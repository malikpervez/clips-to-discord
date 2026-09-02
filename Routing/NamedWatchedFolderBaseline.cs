using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

internal sealed record RoutingNamedWatchedBaselineDocument(
    int SchemaVersion,
    long Generation,
    string SourceId,
    RoutingInputSourceKind Kind,
    string RootIdentitySha256,
    IReadOnlyList<string> IgnoredOccurrenceIds,
    DateTimeOffset CreatedUtc);

/// <summary>
/// Immutable per-source "from now" boundary. Registration snapshots only native occurrence
/// metadata; it never reads clip content. A later file revision has a different occurrence id and
/// is therefore eligible, while every file present when the source was added remains ignored.
/// </summary>
internal sealed class RoutingNamedWatchedBaselineStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumOccurrences = 10_000;
    internal const int MaximumDocumentBytes = 2 * 1024 * 1024;

    private readonly string _root;

    internal RoutingNamedWatchedBaselineStore()
        : this(Path.Combine(
            SettingsStore.DataDirectory,
            "routing",
            "named-watched-baselines",
            "v1"))
    {
    }

    internal RoutingNamedWatchedBaselineStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    internal RoutingDocumentLoadResult<RoutingNamedWatchedBaselineDocument> Load(
        string sourceId,
        CancellationToken cancellationToken = default) =>
        StoreFor(sourceId).Load(cancellationToken);

    internal async Task<RoutingNamedWatchedBaselineDocument> EnsureAsync(
        RoutingInputSourceRecord source,
        IRoutingWatchedSourceAdapter adapter,
        CancellationToken cancellationToken = default)
    {
        RequireNamedSource(source, adapter);
        RequireCurrentRootAuthority(source, adapter);
        var existing = Load(source.SourceId, cancellationToken);
        if (existing.LoadedFromDisk && existing.Document is { } document)
        {
            RequireMatches(document, source);
            return document;
        }
        if (existing.Status != RoutingDocumentLoadStatus.Missing)
        {
            throw new InvalidDataException(
                $"The named watched-source baseline cannot be trusted ({existing.Status}).");
        }

        var candidates = adapter.EnumerateCandidates(source.CanonicalRoot, cancellationToken);
        if (candidates.Count > MaximumOccurrences)
        {
            throw new InvalidDataException(
                "The watched folder contains too many existing clips to establish a safe baseline.");
        }
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var occurrence = await adapter.InspectOccurrenceAsync(
                    source.CanonicalRoot,
                    path,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!occurrence.RootIdentitySha256.Equals(
                    source.RootIdentitySha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The watched folder changed while its safe baseline was created.");
            }
            ignored.Add(CreateOccurrenceId(source.SourceId, occurrence));
        }
        RequireCurrentRootAuthority(source, adapter);

        var candidate = new RoutingNamedWatchedBaselineDocument(
            CurrentSchemaVersion,
            Generation: 1,
            source.SourceId,
            source.Kind,
            source.RootIdentitySha256,
            ignored.Order(StringComparer.Ordinal).ToArray(),
            RoutingValidation.Utc(DateTimeOffset.UtcNow));
        var store = StoreFor(source.SourceId);
        try
        {
            return await store.SaveAsync(candidate, 0, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RoutingConcurrencyException)
        {
            var concurrent = store.Load(cancellationToken);
            if (!concurrent.LoadedFromDisk || concurrent.Document is null)
                throw;
            RequireMatches(concurrent.Document, source);
            return concurrent.Document;
        }
    }

    internal bool IsIgnored(
        RoutingNamedWatchedBaselineDocument baseline,
        RoutingInputSourceRecord source,
        RoutingWatchedSourceOccurrence occurrence)
    {
        RequireMatches(baseline, source);
        return baseline.IgnoredOccurrenceIds.Contains(
            CreateOccurrenceId(source.SourceId, occurrence),
            StringComparer.Ordinal);
    }

    internal static string CreateOccurrenceId(
        string sourceId,
        RoutingWatchedSourceOccurrence occurrence)
    {
        RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
        ArgumentNullException.ThrowIfNull(occurrence);
        RoutingWatchedNativeFileIdentityModel.Validate(occurrence.NativeFileIdentity);
        var native = occurrence.NativeFileIdentity;
        var material = string.Join('\n',
        [
            "clipcord-named-watched-baseline-occurrence-v1",
            sourceId,
            occurrence.RootIdentitySha256.ToLowerInvariant(),
            RoutingWatchedJournalModel.NormalizePortableRelativePath(
                    occurrence.PortableRelativePath)
                .ToUpperInvariant(),
            native.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
            native.FileIdHex,
            native.ByteLength.ToString(CultureInfo.InvariantCulture),
            native.CreationUtcTicks.ToString(CultureInfo.InvariantCulture),
            native.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture)
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private RoutingAtomicJsonStore<RoutingNamedWatchedBaselineDocument> StoreFor(
        string sourceId)
    {
        RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
        var leaf = sourceId[RoutingInputSourceCatalogModel.SourceIdPrefix.Length..];
        return new RoutingAtomicJsonStore<RoutingNamedWatchedBaselineDocument>(
            Path.Combine(_root, $"{leaf}.json"),
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Generation,
            Validate,
            static (_, _) => throw new InvalidDataException(
                "A named watched-source baseline is immutable."),
            Validate);
    }

    private static void Validate(RoutingNamedWatchedBaselineDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(
            document.SchemaVersion == CurrentSchemaVersion && document.Generation == 1,
            "The named watched-source baseline schema or generation is invalid.");
        RoutingInputSourceCatalogModel.ValidateSourceId(document.SourceId);
        RoutingValidation.Require(
            document.Kind is RoutingInputSourceKind.SteelSeriesGg or
                RoutingInputSourceKind.Nvidia,
            "The named watched-source baseline kind is unsupported.");
        RoutingValidation.RequireSha256(
            document.RootIdentitySha256, "named watched-source root identity");
        var occurrences = document.IgnoredOccurrenceIds ??
                          throw new InvalidDataException(
                              "The named watched-source baseline occurrences are missing.");
        RoutingValidation.Require(
            occurrences.Count <= MaximumOccurrences &&
            occurrences.SequenceEqual(occurrences.Order(StringComparer.Ordinal)) &&
            occurrences.Distinct(StringComparer.Ordinal).Count() == occurrences.Count,
            "The named watched-source baseline occurrences are not canonical.");
        foreach (var occurrence in occurrences)
        {
            RoutingValidation.RequireSha256(
                occurrence, "named watched-source baseline occurrence");
            RoutingValidation.Require(
                occurrence.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "A named watched-source baseline occurrence is not lowercase hexadecimal.");
        }
        RoutingValidation.RequireUtc(document.CreatedUtc,
            "named watched-source baseline creation timestamp");
    }

    private static void RequireMatches(
        RoutingNamedWatchedBaselineDocument baseline,
        RoutingInputSourceRecord source)
    {
        Validate(baseline);
        RoutingValidation.Require(
            baseline.SourceId.Equals(source.SourceId, StringComparison.Ordinal) &&
            baseline.Kind == source.Kind &&
            baseline.RootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal),
            "The named watched-source baseline does not match its current source authority.");
    }

    private static void RequireNamedSource(
        RoutingInputSourceRecord source,
        IRoutingWatchedSourceAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adapter);
        var expected = source.Kind switch
        {
            RoutingInputSourceKind.SteelSeriesGg => ClipCaptureSource.SteelSeriesGg,
            RoutingInputSourceKind.Nvidia => ClipCaptureSource.Nvidia,
            _ => throw new InvalidDataException(
                "Only a named SteelSeries or NVIDIA source can have a watched baseline.")
        };
        RoutingValidation.Require(
            adapter.Source == expected && !source.Retired &&
            source.Health == RoutingInputSourceHealth.Ready,
            "The named watched source cannot establish a baseline in its current state.");
    }

    private static void RequireCurrentRootAuthority(
        RoutingInputSourceRecord source,
        IRoutingWatchedSourceAdapter adapter)
    {
        var currentRootIdentity = adapter.InspectRootIdentity(source.CanonicalRoot);
        RoutingValidation.Require(
            currentRootIdentity.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal),
            "The named watched-source root does not match its registered native authority.");
    }
}
