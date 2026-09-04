using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

internal static class XboxDvrGameMatch
{
    internal static bool Matches(
        string actual,
        RoutingConditionOperator comparison,
        string expected)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);
        var actualIdentity = CaptureLibraryLayout.CreateGameIdentity(actual);
        var expectedIdentity = CaptureLibraryLayout.CreateGameIdentity(expected);
        // Canonical identities collapse Xbox filename presentation marks such as ™/® and
        // punctuation. If either side has no semantic identity, retain literal matching so an
        // empty canonical value can never become a universal Contains match.
        var useCanonical = actualIdentity.Length > 0 && expectedIdentity.Length > 0;
        var left = useCanonical ? actualIdentity : actual;
        var right = useCanonical ? expectedIdentity : expected;
        var comparisonKind = useCanonical
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return comparison switch
        {
            RoutingConditionOperator.Equals => left.Equals(right, comparisonKind),
            RoutingConditionOperator.DoesNotEqual => !left.Equals(right, comparisonKind),
            RoutingConditionOperator.Contains => right.Length > 0 &&
                                                   left.Contains(right, comparisonKind),
            _ => throw new InvalidDataException(
                "An Xbox game condition uses an unsupported operator.")
        };
    }
}

internal enum XboxDvrFileNameStatus
{
    Parsed,
    NeedsAttention
}

internal enum XboxDvrNeedsAttentionReason
{
    None,
    UnsupportedFileName,
    InvalidTimestamp,
    InvalidGameName,
    UnsafeReparsePoint,
    CaptureTimeAfterActivation
}

internal sealed record XboxDvrParsedFileName(
    XboxDvrFileNameStatus Status,
    string DisplayFileName,
    string? GameName,
    DateTimeOffset? CapturedUtc,
    XboxDvrNeedsAttentionReason NeedsAttentionReason)
{
    internal bool IsParsed => Status == XboxDvrFileNameStatus.Parsed;
}

/// <summary>
/// Parses the current Xbox OneDrive leaf convention from its fixed suffix. No filesystem
/// timestamp is used: OneDrive may rewrite creation timestamps while populating placeholders.
/// Xbox currently emits the timestamp as UTC, so the result is always offset zero.
/// </summary>
internal static class XboxDvrFileNameParser
{
    private const int TimestampSuffixLength = 20; // -yyyy_MM_dd-HH-mm-ss
    private const string TimestampFormat = "yyyy_MM_dd-HH-mm-ss";

    internal static XboxDvrParsedFileName Parse(string? fileName)
    {
        var display = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(display) ||
            !display.Equals(fileName, StringComparison.Ordinal) ||
            !Path.GetExtension(display).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return NeedsAttention(display, XboxDvrNeedsAttentionReason.UnsupportedFileName);
        }

        var stem = Path.GetFileNameWithoutExtension(display);
        if (stem.Length <= TimestampSuffixLength)
        {
            return NeedsAttention(display, XboxDvrNeedsAttentionReason.UnsupportedFileName);
        }

        var suffixStart = stem.Length - TimestampSuffixLength;
        var timestampText = stem[(suffixStart + 1)..];
        if (stem[suffixStart] != '-' || !HasModernTimestampShape(timestampText))
        {
            return NeedsAttention(display, XboxDvrNeedsAttentionReason.UnsupportedFileName);
        }

        if (!DateTime.TryParseExact(
                timestampText,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
        {
            return NeedsAttention(display, XboxDvrNeedsAttentionReason.InvalidTimestamp);
        }

        var rawGameName = stem[..suffixStart].Normalize(NormalizationForm.FormC).Trim();
        if (rawGameName.Length is 0 or > 160 || rawGameName.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(rawGameName.Trim(' ', '.')))
        {
            return NeedsAttention(display, XboxDvrNeedsAttentionReason.InvalidGameName);
        }
        var gameName = UploadedFolder.SanitizeGameFolderName(rawGameName);
        return new XboxDvrParsedFileName(
            XboxDvrFileNameStatus.Parsed,
            display,
            gameName,
            new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            XboxDvrNeedsAttentionReason.None);
    }

    private static bool HasModernTimestampShape(string value) =>
        value.Length == TimestampSuffixLength - 1 &&
        value[4] == '_' && value[7] == '_' && value[10] == '-' &&
        value[13] == '-' && value[16] == '-' &&
        value.Where((_, index) => index is not (4 or 7 or 10 or 13 or 16))
            .All(character => character is >= '0' and <= '9');

    private static XboxDvrParsedFileName NeedsAttention(
        string display,
        XboxDvrNeedsAttentionReason reason) => new(
        XboxDvrFileNameStatus.NeedsAttention,
        display,
        null,
        null,
        reason);
}

internal enum XboxDvrReparseClassification
{
    Ordinary,
    CloudPlaceholder,
    Rejected
}

/// <summary>
/// Classifies directory-enumeration metadata without opening file content. Cloud Files defines
/// IO_REPARSE_TAG_CLOUD plus CLOUD_1 through CLOUD_F; the state nibble changes as OneDrive
/// hydrates/dehydrates an item, so matching one observed tag is insufficient.
/// </summary>
internal static class XboxDvrCloudReparsePolicy
{
    internal const uint FileAttributeDirectory = 0x00000010;
    internal const uint FileAttributeReparsePoint = 0x00000400;
    internal const uint ReparseTagNameSurrogate = 0x20000000;
    internal const uint CloudTagFamilyMask = 0xffff0fff;
    internal const uint CloudTagFamily = 0x9000001a;

    internal static XboxDvrReparseClassification Classify(
        uint fileAttributes,
        uint reparseTag)
    {
        if ((fileAttributes & FileAttributeDirectory) != 0)
        {
            return XboxDvrReparseClassification.Rejected;
        }

        if ((fileAttributes & FileAttributeReparsePoint) == 0)
        {
            return reparseTag == 0
                ? XboxDvrReparseClassification.Ordinary
                : XboxDvrReparseClassification.Rejected;
        }

        if ((reparseTag & ReparseTagNameSurrogate) != 0)
        {
            return XboxDvrReparseClassification.Rejected;
        }

        return (reparseTag & CloudTagFamilyMask) == CloudTagFamily
            ? XboxDvrReparseClassification.CloudPlaceholder
            : XboxDvrReparseClassification.Rejected;
    }
}

/// <summary>
/// Content-free metadata returned by a platform-specific directory enumerator. The seam exposes
/// no content-open or hydration operation, making route preflight unable to recall cloud bytes.
/// FileId128Hex and ProviderIdentitySha256 must already be privacy-safe canonical digests/ids.
/// </summary>
internal sealed record XboxDvrCandidateMetadata(
    string PortableRelativePath,
    long LogicalBytes,
    long LastWriteUtcTicks,
    ulong VolumeSerialNumber,
    string FileId128Hex,
    string? ProviderIdentitySha256,
    uint FileAttributes,
    uint ReparseTag);

internal interface IXboxDvrMetadataFileSystem
{
    IReadOnlyList<XboxDvrCandidateMetadata> EnumerateMetadata(
        string canonicalRoot,
        CancellationToken cancellationToken);
}

internal enum XboxDvrHistoryWindow
{
    NewOnly,
    Last24Hours,
    Last7Days,
    Custom
}

/// <summary>
/// Immutable activation-time selection. Relative labels are converted to an absolute cutoff once;
/// retries and restarts must persist these exact values rather than recomputing "last week".
/// </summary>
internal sealed record XboxDvrHistoryPolicy(
    XboxDvrHistoryWindow Window,
    DateTimeOffset ActivationUtc,
    DateTimeOffset HistoricalCutoffUtc,
    int MaximumHistoricalClips)
{
    internal const int MaximumAllowedHistoricalClips = 1_000;

    internal static XboxDvrHistoryPolicy Create(
        XboxDvrHistoryWindow window,
        DateTimeOffset activationUtc,
        int maximumHistoricalClips,
        DateTimeOffset? customCutoffUtc = null)
    {
        var activation = RequireUtc(activationUtc, nameof(activationUtc));
        var cutoff = window switch
        {
            XboxDvrHistoryWindow.NewOnly => activation,
            XboxDvrHistoryWindow.Last24Hours => activation.AddHours(-24),
            XboxDvrHistoryWindow.Last7Days => activation.AddDays(-7),
            XboxDvrHistoryWindow.Custom when customCutoffUtc is { } custom =>
                RequireUtc(custom, nameof(customCutoffUtc)),
            XboxDvrHistoryWindow.Custom => throw new ArgumentException(
                "A custom Xbox history window requires an absolute UTC cutoff.",
                nameof(customCutoffUtc)),
            _ => throw new ArgumentOutOfRangeException(nameof(window))
        };
        if (cutoff > activation)
        {
            throw new ArgumentOutOfRangeException(
                nameof(customCutoffUtc),
                "The Xbox history cutoff cannot be after route activation.");
        }
        if (maximumHistoricalClips is < 1 or > MaximumAllowedHistoricalClips)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumHistoricalClips));
        }
        return new XboxDvrHistoryPolicy(window, activation, cutoff, maximumHistoricalClips);
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(Window) || ActivationUtc.Offset != TimeSpan.Zero ||
            HistoricalCutoffUtc.Offset != TimeSpan.Zero ||
            HistoricalCutoffUtc > ActivationUtc ||
            MaximumHistoricalClips is < 1 or > MaximumAllowedHistoricalClips ||
            Window == XboxDvrHistoryWindow.NewOnly && HistoricalCutoffUtc != ActivationUtc ||
            Window == XboxDvrHistoryWindow.Last24Hours &&
                HistoricalCutoffUtc != ActivationUtc.AddHours(-24) ||
            Window == XboxDvrHistoryWindow.Last7Days &&
                HistoricalCutoffUtc != ActivationUtc.AddDays(-7))
        {
            throw new InvalidDataException("The Xbox history policy is not canonical.");
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Xbox history timestamps must use UTC.", parameterName);
        }
        return value;
    }
}

internal enum XboxDvrPreflightDisposition
{
    EligibleHistorical,
    BaselineOnly,
    NeedsAttention
}

internal sealed record XboxDvrPreflightItem(
    string DisplayFileName,
    string? GameName,
    DateTimeOffset? CapturedUtc,
    long LogicalBytes,
    string OccurrenceIdentitySha256,
    string RevisionIdentitySha256,
    XboxDvrReparseClassification ReparseClassification,
    XboxDvrPreflightDisposition Disposition,
    XboxDvrNeedsAttentionReason NeedsAttentionReason);

internal sealed record XboxDvrPreflightPreview(
    string SourceId,
    string CanonicalRoot,
    XboxDvrHistoryPolicy Policy,
    int TotalClipCount,
    long TotalLogicalBytes,
    int ParsedClipCount,
    int WindowMatchCount,
    int EligibleHistoricalCount,
    long EligibleHistoricalBytes,
    int BaselineOnlyCount,
    int NeedsAttentionCount,
    IReadOnlyList<XboxDvrPreflightItem> Items)
{
    internal IReadOnlyList<XboxDvrPreflightItem> EligibleHistorical => Items
        .Where(item => item.Disposition == XboxDvrPreflightDisposition.EligibleHistorical)
        .ToArray();
}

/// <summary>
/// Xbox source setup/preflight only. It lists placeholder metadata, parses capture facts, freezes a
/// bounded history selection, and derives opaque identities. It cannot open or hydrate content.
/// </summary>
internal sealed class XboxDvrSourceAdapter
{
    private readonly IXboxDvrMetadataFileSystem _fileSystem;

    internal XboxDvrSourceAdapter(IXboxDvrMetadataFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    internal XboxDvrPreflightPreview Preflight(
        string sourceId,
        string clipsRoot,
        XboxDvrHistoryPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceId(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clipsRoot);
        if (!Path.IsPathFullyQualified(clipsRoot))
        {
            throw new ArgumentException("The Xbox source root must be fully qualified.", nameof(clipsRoot));
        }
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(clipsRoot));
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = _fileSystem.EnumerateMetadata(canonicalRoot, cancellationToken) ??
                       throw new InvalidDataException("Xbox metadata enumeration returned no collection.");
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Candidate>(metadata.Count);
        foreach (var entry in metadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateMetadata(entry);
            var portable = NormalizePortableLeaf(entry.PortableRelativePath);
            if (!seenPaths.Add(portable))
            {
                throw new InvalidDataException("Xbox metadata enumeration returned a duplicate leaf.");
            }

            var parsed = XboxDvrFileNameParser.Parse(portable);
            var reparse = XboxDvrCloudReparsePolicy.Classify(
                entry.FileAttributes,
                entry.ReparseTag);
            var occurrence = CreateOccurrenceIdentity(sourceId, portable, entry);
            var revision = CreateRevisionIdentity(occurrence, parsed.CapturedUtc, entry);
            var attention = reparse == XboxDvrReparseClassification.Rejected
                ? XboxDvrNeedsAttentionReason.UnsafeReparsePoint
                : parsed.NeedsAttentionReason;
            if (attention == XboxDvrNeedsAttentionReason.None &&
                parsed.CapturedUtc > policy.ActivationUtc)
            {
                attention = XboxDvrNeedsAttentionReason.CaptureTimeAfterActivation;
            }
            candidates.Add(new Candidate(
                portable,
                entry,
                parsed,
                reparse,
                occurrence,
                revision,
                attention));
        }

        var windowMatches = policy.Window == XboxDvrHistoryWindow.NewOnly
            ? []
            : candidates
                .Where(candidate => candidate.Attention == XboxDvrNeedsAttentionReason.None &&
                                    candidate.Parsed.CapturedUtc >= policy.HistoricalCutoffUtc &&
                                    candidate.Parsed.CapturedUtc <= policy.ActivationUtc)
                .OrderByDescending(candidate => candidate.Parsed.CapturedUtc)
                .ThenBy(candidate => candidate.PortableRelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var selectedIds = windowMatches
            .Take(policy.MaximumHistoricalClips)
            .Select(candidate => candidate.OccurrenceIdentitySha256)
            .ToHashSet(StringComparer.Ordinal);

        var items = candidates
            .Select(candidate => CreateItem(candidate, selectedIds))
            .OrderBy(item => item.CapturedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.DisplayFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var eligible = items.Where(item =>
                item.Disposition == XboxDvrPreflightDisposition.EligibleHistorical)
            .ToArray();
        return new XboxDvrPreflightPreview(
            sourceId,
            canonicalRoot,
            policy,
            items.Length,
            CheckedSum(items.Select(item => item.LogicalBytes)),
            items.Count(item => item.CapturedUtc is not null),
            windowMatches.Length,
            eligible.Length,
            CheckedSum(eligible.Select(item => item.LogicalBytes)),
            items.Count(item => item.Disposition == XboxDvrPreflightDisposition.BaselineOnly),
            items.Count(item => item.Disposition == XboxDvrPreflightDisposition.NeedsAttention),
            items);
    }

    internal static string CreateOccurrenceIdentity(
        string sourceId,
        string portableRelativePath,
        XboxDvrCandidateMetadata metadata)
    {
        ValidateSourceId(sourceId);
        ValidateMetadata(metadata);
        var portable = NormalizePortableLeaf(portableRelativePath).Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();
        var material = string.Join('\n',
        [
            "clipcord-xbox-dvr-occurrence-v1",
            sourceId,
            portable,
            metadata.VolumeSerialNumber.ToString("x16", CultureInfo.InvariantCulture),
            metadata.FileId128Hex,
            metadata.ProviderIdentitySha256 ?? "-"
        ]);
        return Hash(material);
    }

    internal static string CreateRevisionIdentity(
        string occurrenceIdentitySha256,
        DateTimeOffset? capturedUtc,
        XboxDvrCandidateMetadata metadata)
    {
        RequireCanonicalSha256(occurrenceIdentitySha256, "Xbox occurrence identity");
        ValidateMetadata(metadata);
        var capturedTicks = capturedUtc is null
            ? "-"
            : capturedUtc.Value.Offset == TimeSpan.Zero
                ? capturedUtc.Value.UtcTicks.ToString(CultureInfo.InvariantCulture)
                : throw new ArgumentException("The Xbox capture timestamp must use UTC.", nameof(capturedUtc));
        var material = string.Join('\n',
        [
            "clipcord-xbox-dvr-revision-v1",
            occurrenceIdentitySha256,
            metadata.LogicalBytes.ToString(CultureInfo.InvariantCulture),
            metadata.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture),
            capturedTicks
        ]);
        return Hash(material);
    }

    private static XboxDvrPreflightItem CreateItem(
        Candidate candidate,
        IReadOnlySet<string> selectedIds)
    {
        var disposition = candidate.Attention != XboxDvrNeedsAttentionReason.None
            ? XboxDvrPreflightDisposition.NeedsAttention
            : selectedIds.Contains(candidate.OccurrenceIdentitySha256)
                ? XboxDvrPreflightDisposition.EligibleHistorical
                : XboxDvrPreflightDisposition.BaselineOnly;
        return new XboxDvrPreflightItem(
            candidate.Parsed.DisplayFileName,
            candidate.Parsed.GameName,
            candidate.Parsed.CapturedUtc,
            candidate.Metadata.LogicalBytes,
            candidate.OccurrenceIdentitySha256,
            candidate.RevisionIdentitySha256,
            candidate.ReparseClassification,
            disposition,
            candidate.Attention);
    }

    private static string NormalizePortableLeaf(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.GetFileName(value).Equals(value, StringComparison.Ordinal) ||
            value is "." or ".." || value.Contains('/') || value.Contains('\\'))
        {
            throw new InvalidDataException("An Xbox metadata entry must be one portable leaf.");
        }
        return value.Normalize(NormalizationForm.FormC);
    }

    private static void ValidateMetadata(XboxDvrCandidateMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _ = NormalizePortableLeaf(metadata.PortableRelativePath);
        if (metadata.LogicalBytes <= 0 || metadata.LastWriteUtcTicks <= 0 ||
            !IsCanonicalHex(metadata.FileId128Hex, 32) ||
            metadata.ProviderIdentitySha256 is { } provider &&
                !IsCanonicalHex(provider, 64))
        {
            throw new InvalidDataException("Xbox source metadata is incomplete or non-canonical.");
        }
    }

    private static void ValidateSourceId(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 128 ||
            sourceId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("The Xbox source id must be one bounded opaque id.", nameof(sourceId));
        }
    }

    private static void RequireCanonicalSha256(string value, string description)
    {
        if (!IsCanonicalHex(value, 64))
        {
            throw new InvalidDataException($"{description} is not canonical SHA-256 text.");
        }
    }

    private static bool IsCanonicalHex(string value, int length) =>
        value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static long CheckedSum(IEnumerable<long> values)
    {
        var result = 0L;
        foreach (var value in values) result = checked(result + value);
        return result;
    }

    private sealed record Candidate(
        string PortableRelativePath,
        XboxDvrCandidateMetadata Metadata,
        XboxDvrParsedFileName Parsed,
        XboxDvrReparseClassification ReparseClassification,
        string OccurrenceIdentitySha256,
        string RevisionIdentitySha256,
        XboxDvrNeedsAttentionReason Attention);
}
