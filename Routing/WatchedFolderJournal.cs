using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipsToDiscord;

internal enum RoutingWatchedJournalAdmissionKind
{
    PreparedPlan,
    LegacyKnownExcluded,
    LegacyUploadedExcluded,
    LegacyLocalOnlyExcluded,
    // Read compatibility for v1 journals created before receipt-owned physical deduplication.
    ContentDuplicateNeedsAttention
}

/// <summary>
/// Immutable write-ahead authority for one physical occurrence discovered in an external capture
/// folder. The watched root itself is deliberately not persisted; its native identity and the
/// committed migration source fingerprint bind this relative path to the currently-authorized
/// root without copying a user's absolute path into routing diagnostics.
/// </summary>
internal sealed record RoutingWatchedSourceJournalDocument(
    int SchemaVersion,
    long Generation,
    RoutingWatchedJournalAdmissionKind AdmissionKind,
    string SourceClipId,
    string OccurrenceIdentitySha256,
    string SourceRootIdentitySha256,
    string MarkerSourceFingerprint,
    string MarkerPayloadFingerprint,
    ClipCaptureSource CaptureSource,
    string SourceRelativePath,
    string DisplayFileName,
    string GameName,
    RoutingWatchedNativeFileIdentity FileIdentity,
    string ContentSha256,
    long DurationMilliseconds,
    int Width,
    int Height,
    RoutingPlanProposal? FrozenPlan,
    // Retained so previously-written ContentDuplicateNeedsAttention journals remain readable.
    string? DuplicateOfSourceClipId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

internal enum RoutingWatchedJournalLoadStatus
{
    Missing,
    Loaded,
    Corrupt,
    UnsupportedSchema,
    IdentityMismatch,
    Invalid,
    Unavailable
}

internal sealed record RoutingWatchedJournalLoadResult(
    string SourceClipId,
    RoutingWatchedSourceJournalDocument? Document,
    RoutingWatchedJournalLoadStatus Status)
{
    internal bool LoadedFromDisk =>
        Status == RoutingWatchedJournalLoadStatus.Loaded && Document is not null;
}

internal static class RoutingWatchedJournalModel
{
    internal const string SourcePrefix = "watched:";
    internal const int OccurrenceHashLength = 64;

    internal static RoutingWatchedSourceJournalDocument Create(
        RoutingWatchedJournalAdmissionKind admissionKind,
        string occurrenceIdentitySha256,
        string sourceRootIdentitySha256,
        string markerSourceFingerprint,
        string markerPayloadFingerprint,
        ClipCaptureSource captureSource,
        string sourceRelativePath,
        string displayFileName,
        string gameName,
        RoutingWatchedNativeFileIdentity fileIdentity,
        string contentSha256,
        long durationMilliseconds,
        int width,
        int height,
        RoutingPlanProposal? frozenPlan,
        string? duplicateOfSourceClipId,
        DateTimeOffset now)
    {
        RoutingValidation.RequireSha256(occurrenceIdentitySha256,
            "watched occurrence identity");
        var canonicalOccurrence = occurrenceIdentitySha256.ToLowerInvariant();
        var timestamp = RoutingValidation.Utc(now);
        var document = new RoutingWatchedSourceJournalDocument(
            RoutingWatchedSourceJournalStore.CurrentSchemaVersion,
            Generation: 1,
            admissionKind,
            SourcePrefix + canonicalOccurrence,
            canonicalOccurrence,
            sourceRootIdentitySha256.ToLowerInvariant(),
            markerSourceFingerprint.ToLowerInvariant(),
            markerPayloadFingerprint.ToLowerInvariant(),
            captureSource,
            NormalizePortableRelativePath(sourceRelativePath),
            displayFileName.Normalize(NormalizationForm.FormC),
            UploadedFolder.SanitizeGameFolderName(gameName),
            fileIdentity,
            contentSha256.ToLowerInvariant(),
            durationMilliseconds,
            width,
            height,
            frozenPlan,
            duplicateOfSourceClipId,
            timestamp,
            timestamp);
        Validate(document);
        return document;
    }

    internal static void Validate(RoutingWatchedSourceJournalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(
            document.SchemaVersion == RoutingWatchedSourceJournalStore.CurrentSchemaVersion &&
            document.Generation == 1,
            "The watched source journal schema or immutable generation is invalid.");
        RoutingValidation.Require(Enum.IsDefined(document.AdmissionKind),
            "The watched source journal admission kind is unsupported.");
        ValidateSourceClipId(document.SourceClipId, document.OccurrenceIdentitySha256);
        RequireCanonicalSha256(document.SourceRootIdentitySha256,
            "watched source root identity");
        RequireCanonicalSha256(document.MarkerSourceFingerprint,
            "watched migration source fingerprint");
        RequireCanonicalSha256(document.MarkerPayloadFingerprint,
            "watched migration payload fingerprint");
        RoutingValidation.Require(document.CaptureSource is
                ClipCaptureSource.SteelSeriesGg or ClipCaptureSource.Nvidia,
            "The watched source journal capture adapter is unsupported.");

        var relative = NormalizePortableRelativePath(document.SourceRelativePath);
        RoutingValidation.Require(relative.Equals(
                document.SourceRelativePath, StringComparison.Ordinal),
            "The watched source relative path is not canonical.");
        var components = relative.Split('/');
        var exactLayout = document.CaptureSource switch
        {
            ClipCaptureSource.SteelSeriesGg => components.Length == 1,
            ClipCaptureSource.Nvidia => components.Length == 2 &&
                                        !AppSettings.ManagedChildFolderNames.Contains(
                                            components[0], StringComparer.OrdinalIgnoreCase),
            _ => false
        };
        RoutingValidation.Require(exactLayout &&
                                  Path.GetExtension(components[^1]).Equals(
                                      ".mp4", StringComparison.OrdinalIgnoreCase),
            "The watched source relative path does not match its capture adapter.");
        RoutingValidation.RequireText(document.DisplayFileName, 1, 255,
            "watched source display file name");
        RoutingValidation.Require(document.DisplayFileName.Equals(
                                      components[^1], StringComparison.Ordinal) &&
                                  Path.GetFileName(document.DisplayFileName).Equals(
                                      document.DisplayFileName, StringComparison.Ordinal),
            "The watched source display file name is not its exact leaf name.");
        RoutingValidation.RequireText(document.GameName, 1, 80,
            "watched source game name");
        RoutingValidation.Require(document.GameName.Equals(
                UploadedFolder.SanitizeGameFolderName(document.GameName),
                StringComparison.Ordinal),
            "The watched source game name is not canonical.");
        ArgumentNullException.ThrowIfNull(document.FileIdentity);
        RoutingWatchedNativeFileIdentityModel.Validate(document.FileIdentity);
        RequireCanonicalSha256(document.ContentSha256, "watched source content hash");
        RoutingValidation.RequireUtc(document.CreatedUtc,
            "watched source journal creation timestamp");
        RoutingValidation.RequireUtc(document.UpdatedUtc,
            "watched source journal update timestamp");
        RoutingValidation.Require(document.CreatedUtc == document.UpdatedUtc,
            "An immutable watched source journal cannot contain mutable history.");

        var isPrepared = document.AdmissionKind ==
                         RoutingWatchedJournalAdmissionKind.PreparedPlan;
        var isDuplicate = document.AdmissionKind ==
                          RoutingWatchedJournalAdmissionKind.ContentDuplicateNeedsAttention;
        RoutingValidation.Require(isPrepared == (document.FrozenPlan is not null),
            "The watched source frozen plan does not match its admission state.");
        RoutingValidation.Require(isDuplicate == (document.DuplicateOfSourceClipId is not null),
            "The watched source duplicate evidence is inconsistent.");
        RoutingValidation.Require(isPrepared || isDuplicate
                ? document.DurationMilliseconds > 0 && document.Width > 0 && document.Height > 0
                : document.DurationMilliseconds == 0 && document.Width == 0 &&
                  document.Height == 0,
            "The watched source media facts do not match its admission state.");
        RoutingValidation.RequireOptionalOpaqueId(
            document.DuplicateOfSourceClipId,
            256,
            "duplicate watched source clip id");
        if (document.DuplicateOfSourceClipId is not null)
        {
            RoutingValidation.Require(
                IsWatchedSourceClipId(document.DuplicateOfSourceClipId) &&
                !document.DuplicateOfSourceClipId.Equals(
                    document.SourceClipId, StringComparison.Ordinal),
                "The watched source duplicate reference is invalid.");
        }
        if (document.FrozenPlan is { } plan)
        {
            RoutingValidation.Require(plan.SourceClipId.Equals(
                                      document.SourceClipId, StringComparison.Ordinal),
                "The watched source frozen plan belongs to another occurrence.");
            var synthetic = RoutingOutboxModel.CreateEmpty(document.CreatedUtc);
            _ = RoutingOutboxModel.AppendEvaluatedPlan(
                synthetic,
                plan,
                document.CreatedUtc);
        }
    }

    internal static bool IsWatchedSourceClipId(string? value)
    {
        if (value is null || !value.StartsWith(SourcePrefix, StringComparison.Ordinal) ||
            value.Length != SourcePrefix.Length + OccurrenceHashLength)
        {
            return false;
        }
        return value.AsSpan(SourcePrefix.Length).ToString().All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    internal static string NormalizePortableRelativePath(string value)
    {
        RoutingValidation.RequireText(value, 1, 1_024,
            "watched source relative path");
        var normalized = value.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        RoutingValidation.Require(!Path.IsPathRooted(normalized) &&
                                  normalized.IndexOf(':') < 0 &&
                                  !normalized.StartsWith("/", StringComparison.Ordinal) &&
                                  !normalized.EndsWith("/", StringComparison.Ordinal),
            "The watched source relative path is rooted or malformed.");
        var components = normalized.Split('/');
        RoutingValidation.Require(components.Length is 1 or 2 &&
                                  components.All(component =>
                                      component.Length is > 0 and <= 255 &&
                                      component is not "." and not ".." &&
                                      component.IndexOf('\0') < 0),
            "The watched source relative path has an unsafe component.");
        return normalized;
    }

    internal static void RequireExact(
        RoutingWatchedSourceJournalDocument actual,
        RoutingWatchedSourceJournalDocument expected)
    {
        Validate(actual);
        Validate(expected);
        var actualPayload = JsonSerializer.SerializeToUtf8Bytes(actual, JournalJsonOptions);
        var expectedPayload = JsonSerializer.SerializeToUtf8Bytes(expected, JournalJsonOptions);
        RoutingValidation.Require(actualPayload.AsSpan().SequenceEqual(expectedPayload),
            "The watched source journal conflicts with an existing occurrence.");
    }

    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
        }
    };

    private static void ValidateSourceClipId(string sourceClipId, string occurrenceIdentity)
    {
        RequireCanonicalSha256(occurrenceIdentity, "watched occurrence identity");
        RoutingValidation.RequireOpaqueId(sourceClipId, 256, "watched source clip id");
        RoutingValidation.Require(sourceClipId.Equals(
                SourcePrefix + occurrenceIdentity, StringComparison.Ordinal),
            "The watched source clip id does not match its occurrence identity.");
    }

    private static void RequireCanonicalSha256(string value, string description)
    {
        RoutingValidation.RequireSha256(value, description);
        RoutingValidation.Require(value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"The {description} is not canonical lowercase text.");
    }
}

/// <summary>
/// One immutable bounded file per watched occurrence. A source-key hash controls the on-disk name,
/// while the document reasserts the original source id so a renamed or substituted file fails
/// closed. Existing entries are never updated or overwritten.
/// </summary>
internal sealed class RoutingWatchedSourceJournalStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = 256 * 1024;
    private const int MaximumSourcesPerEnumeration = 10_000;
    private readonly string _root;

    internal RoutingWatchedSourceJournalStore()
        : this(Path.Combine(
            SettingsStore.DataDirectory,
            "routing",
            "watched-journal",
            "v1"))
    {
    }

    internal RoutingWatchedSourceJournalStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    internal string Root => _root;

    internal RoutingWatchedJournalLoadResult Load(
        string sourceClipId,
        CancellationToken cancellationToken = default)
    {
        RoutingValidation.RequireOpaqueId(sourceClipId, 256, "watched source clip id");
        if (!RoutingWatchedJournalModel.IsWatchedSourceClipId(sourceClipId))
        {
            return new RoutingWatchedJournalLoadResult(
                sourceClipId,
                null,
                RoutingWatchedJournalLoadStatus.IdentityMismatch);
        }
        var loaded = StoreFor(sourceClipId).Load(cancellationToken);
        if (!loaded.LoadedFromDisk || loaded.Document is null)
        {
            return new RoutingWatchedJournalLoadResult(
                sourceClipId,
                null,
                MapStatus(loaded.Status));
        }
        return loaded.Document.SourceClipId.Equals(sourceClipId, StringComparison.Ordinal)
            ? new RoutingWatchedJournalLoadResult(
                sourceClipId,
                loaded.Document,
                RoutingWatchedJournalLoadStatus.Loaded)
            : new RoutingWatchedJournalLoadResult(
                sourceClipId,
                null,
                RoutingWatchedJournalLoadStatus.IdentityMismatch);
    }

    internal async Task<RoutingWatchedSourceJournalDocument> PersistExactAsync(
        RoutingWatchedSourceJournalDocument document,
        CancellationToken cancellationToken = default)
    {
        RoutingWatchedJournalModel.Validate(document);
        var store = StoreFor(document.SourceClipId);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = Load(document.SourceClipId, cancellationToken);
            if (existing.LoadedFromDisk && existing.Document is not null)
            {
                RoutingWatchedJournalModel.RequireExact(existing.Document, document);
                return existing.Document;
            }
            if (existing.Status != RoutingWatchedJournalLoadStatus.Missing)
            {
                throw new InvalidDataException(
                    $"The watched source journal cannot be loaded safely ({existing.Status}).");
            }
            try
            {
                return await store.SaveAsync(document, 0, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RoutingConcurrencyException) when (attempt < 3)
            {
                // Another process may have committed the exact same immutable occurrence.
            }
        }
        throw new RoutingConcurrencyException(
            "The watched source journal kept changing while it was created.");
    }

    internal IReadOnlyList<string> EnumerateSourceClipIds(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root)) return [];
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            _root,
            _root,
            requireDirectory: true,
            "watched source journal root");
        var result = new List<string>();
        var shards = Directory.EnumerateDirectories(
                _root, "*", SearchOption.TopDirectoryOnly)
            .ToArray();
        RoutingValidation.Require(shards.Length <= 256,
            "The watched source journal contains too many shard folders.");
        foreach (var shard in shards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureJournalStore.EnsureOrdinaryExistingPath(
                _root,
                shard,
                requireDirectory: true,
                "watched source journal shard");
            var shardName = Path.GetFileName(shard);
            RoutingValidation.Require(shardName.Length == 2 && shardName.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "A watched source journal shard name is not canonical.");
            foreach (var path in Directory.EnumerateFiles(
                         shard, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CaptureJournalStore.EnsureOrdinaryExistingPath(
                    _root,
                    path,
                    requireDirectory: false,
                    "watched source journal file");
                var key = Path.GetFileNameWithoutExtension(path);
                if (key.Length != 64 || key.Any(character =>
                        character is not (>= '0' and <= '9') and
                            not (>= 'a' and <= 'f')) ||
                    !key.StartsWith(shardName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A watched source journal file name is not canonical.");
                }
                using var lease = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan);
                if (lease.Length is <= 0 or > MaximumDocumentBytes)
                {
                    throw new InvalidDataException(
                        "A watched source journal exceeds its bounded file size.");
                }
                using var document = JsonDocument.Parse(lease);
                if (!document.RootElement.TryGetProperty("sourceClipId", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    idElement.GetString() is not { } sourceClipId ||
                    !RoutingWatchedJournalModel.IsWatchedSourceClipId(sourceClipId) ||
                    !SourceKey(sourceClipId).Equals(key, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A watched source journal file does not match its source identity.");
                }
                result.Add(sourceClipId);
                if (result.Count > MaximumSourcesPerEnumeration)
                {
                    throw new InvalidDataException(
                        "The watched source journal exceeds its bounded enumeration capacity.");
                }
            }
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    internal string PathForSource(string sourceClipId)
    {
        var key = SourceKey(sourceClipId);
        return Path.Combine(_root, key[..2], key + ".json");
    }

    internal static string SourceKey(string sourceClipId)
    {
        RoutingValidation.RequireOpaqueId(sourceClipId, 256, "watched source clip id");
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(sourceClipId)))
            .ToLowerInvariant();
    }

    private RoutingAtomicJsonStore<RoutingWatchedSourceJournalDocument> StoreFor(
        string sourceClipId)
    {
        var path = PathForSource(sourceClipId);
        return new RoutingAtomicJsonStore<RoutingWatchedSourceJournalDocument>(
            path,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Generation,
            RoutingWatchedJournalModel.Validate,
            (_, _) => throw new InvalidDataException(
                "Watched source journals are immutable."),
            document =>
            {
                RoutingWatchedJournalModel.Validate(document);
                RoutingValidation.Require(document.Generation == 1,
                    "The first watched source journal generation is not canonical.");
            });
    }

    private static RoutingWatchedJournalLoadStatus MapStatus(
        RoutingDocumentLoadStatus status) => status switch
    {
        RoutingDocumentLoadStatus.Missing => RoutingWatchedJournalLoadStatus.Missing,
        RoutingDocumentLoadStatus.Loaded => RoutingWatchedJournalLoadStatus.Loaded,
        RoutingDocumentLoadStatus.Corrupt => RoutingWatchedJournalLoadStatus.Corrupt,
        RoutingDocumentLoadStatus.UnsupportedSchema =>
            RoutingWatchedJournalLoadStatus.UnsupportedSchema,
        RoutingDocumentLoadStatus.Invalid => RoutingWatchedJournalLoadStatus.Invalid,
        RoutingDocumentLoadStatus.Unavailable => RoutingWatchedJournalLoadStatus.Unavailable,
        _ => RoutingWatchedJournalLoadStatus.Invalid
    };
}

/// <summary>
/// Builds the complete immutable write-ahead record for one adapter-validated source. Legacy
/// exclusions are terminal before media probing. Every other source is probed, revalidated through
/// the same native adapter, evaluated exactly once, and stores the complete proposal before it can
/// enter the outbox.
/// </summary>
internal sealed class RoutingWatchedJournalFactory
{
    private readonly IRoutingWatchedFolderMediaProbe _mediaProbe;
    private readonly Func<Guid> _createPlanId;
    private readonly Func<DateTimeOffset> _utcNow;

    internal RoutingWatchedJournalFactory(
        IRoutingWatchedFolderMediaProbe mediaProbe,
        Func<Guid>? createPlanId = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _mediaProbe = mediaProbe ?? throw new ArgumentNullException(nameof(mediaProbe));
        _createPlanId = createPlanId ?? Guid.NewGuid;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal async Task<RoutingWatchedSourceJournalDocument> CreateAsync(
        RoutingWatchedSourceFile source,
        IRoutingWatchedSourceAdapter adapter,
        LegacyRoutingMigrationMarker marker,
        RoutingSnapshotDocument routingSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(routingSnapshot);
        LegacyRoutingMigrationMarkerModel.Validate(marker);
        RoutingSnapshotModel.Validate(routingSnapshot);
        RoutingValidation.Require(
            marker.Phase == LegacyRoutingMigrationMarkerPhase.Committed,
            "A watched source requires a committed migration marker.");
        RoutingValidation.Require(source.Source == adapter.Source &&
                                  marker.Route is not null &&
                                  routingSnapshot.Routes.Any(route =>
                                      route.RouteId == marker.Route.RouteId &&
                                      LegacyRoutingMigrationPlanner.IsEquivalentMigrationRoute(
                                          route, marker.Route)),
            "The watched source adapter or frozen routing snapshot is outside migration authority.");
        cancellationToken.ThrowIfCancellationRequested();
        var occurrence = CreateOccurrenceIdentity(source, marker.SourceFingerprint);
        var sourceClipId = RoutingWatchedJournalModel.SourcePrefix + occurrence;
        var exclusion = ClassifyLegacyExclusion(marker.ContentHashExclusions, source.ContentSha256);
        if (exclusion is { } excluded)
        {
            return CreateDocument(
                excluded,
                source,
                marker,
                occurrence,
                durationMilliseconds: 0,
                width: 0,
                height: 0,
                frozenPlan: null,
                duplicateOfSourceClipId: null);
        }

        var sourcePath = Path.GetFullPath(Path.Combine(
            source.CanonicalRoot,
            source.PortableRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var media = await _mediaProbe.ProbeAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        if (media.Duration <= TimeSpan.Zero || media.Width <= 0 || media.Height <= 0)
        {
            throw new InvalidDataException(
                "The watched source media probe returned incomplete facts.");
        }
        var revalidated = await adapter.RevalidateAsync(source, cancellationToken)
            .ConfigureAwait(false);
        if (revalidated != source)
        {
            throw new InvalidDataException(
                "The watched source changed while its media facts were collected.");
        }

        var durationMilliseconds = checked((long)media.Duration.TotalMilliseconds);
        var plannedUtc = RoutingValidation.Utc(_utcNow());
        var original = RoutingEvaluator.CreateLogicalOutputReference(
            sourceClipId,
            source.ContentSha256,
            RoutingOutputKind.Original);
        var facts = new RoutingClipFacts(
            sourceClipId,
            RoutingEvaluationEventKind.SourceArrival,
            RoutingClipSource.WatchedFolder,
            RoutingTriggerKind.WatchedFolder,
            CaptureType: null,
            source.GameName,
            ReactionCamera: false,
            durationMilliseconds,
            source.ContentSha256,
            [
                new RoutingClipOutputRevision(
                    original,
                    RoutingOutputAvailability.Ready,
                    FailureCode: null),
                MissingOutput(sourceClipId, source.ContentSha256, RoutingOutputKind.Landscape),
                MissingOutput(sourceClipId, source.ContentSha256, RoutingOutputKind.Portrait)
            ]);
        var proposal = RoutingEvaluator.CreatePlan(
            routingSnapshot,
            facts,
            _createPlanId(),
            deliberateDuplicateAuthorizations: [],
            plannedUtc);

        return CreateDocument(
            RoutingWatchedJournalAdmissionKind.PreparedPlan,
            source,
            marker,
            occurrence,
            durationMilliseconds,
            media.Width,
            media.Height,
            proposal,
            duplicateOfSourceClipId: null);
    }

    internal static string CreateOccurrenceIdentity(
        RoutingWatchedSourceFile source,
        string markerSourceFingerprint)
    {
        ArgumentNullException.ThrowIfNull(source);
        RoutingValidation.RequireSha256(markerSourceFingerprint,
            "watched migration source fingerprint");
        var relative = RoutingWatchedJournalModel.NormalizePortableRelativePath(
                source.PortableRelativePath)
            .ToUpperInvariant();
        var native = source.NativeFileIdentity;
        RoutingWatchedNativeFileIdentityModel.Validate(native);
        var material = string.Join('\n',
        [
            "clipcord-watched-occurrence-v2",
            source.Source.ToString(),
            source.RootIdentitySha256,
            markerSourceFingerprint.ToLowerInvariant(),
            relative,
            native.VolumeSerialNumber.ToString(
                "x8", System.Globalization.CultureInfo.InvariantCulture),
            native.FileIdHex,
            native.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            native.CreationUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            native.LastWriteUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.ContentSha256.ToLowerInvariant()
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private RoutingWatchedSourceJournalDocument CreateDocument(
        RoutingWatchedJournalAdmissionKind admissionKind,
        RoutingWatchedSourceFile source,
        LegacyRoutingMigrationMarker marker,
        string occurrence,
        long durationMilliseconds,
        int width,
        int height,
        RoutingPlanProposal? frozenPlan,
        string? duplicateOfSourceClipId) => RoutingWatchedJournalModel.Create(
        admissionKind,
        occurrence,
        source.RootIdentitySha256,
        marker.SourceFingerprint,
        marker.PayloadFingerprint,
        source.Source,
        source.PortableRelativePath,
        source.DisplayFileName,
        source.GameName,
        source.NativeFileIdentity,
        source.ContentSha256,
        durationMilliseconds,
        width,
        height,
        frozenPlan,
        duplicateOfSourceClipId,
        RoutingValidation.Utc(_utcNow()));

    private static RoutingWatchedJournalAdmissionKind? ClassifyLegacyExclusion(
        LegacyContentHashExclusions exclusions,
        string contentSha256)
    {
        ArgumentNullException.ThrowIfNull(exclusions);
        if (exclusions.Uploaded.Contains(contentSha256, StringComparer.OrdinalIgnoreCase))
        {
            return RoutingWatchedJournalAdmissionKind.LegacyUploadedExcluded;
        }
        if (exclusions.LocalOnly.Contains(contentSha256, StringComparer.OrdinalIgnoreCase))
        {
            return RoutingWatchedJournalAdmissionKind.LegacyLocalOnlyExcluded;
        }
        return exclusions.Known.Contains(contentSha256, StringComparer.OrdinalIgnoreCase)
            ? RoutingWatchedJournalAdmissionKind.LegacyKnownExcluded
            : null;
    }

    private static RoutingClipOutputRevision MissingOutput(
        string sourceClipId,
        string sourceContentSha256,
        RoutingOutputKind kind) => new(
        RoutingEvaluator.CreateLogicalOutputReference(sourceClipId, sourceContentSha256, kind),
        RoutingOutputAvailability.PermanentlyMissing,
        "watched-output-not-produced");
}
