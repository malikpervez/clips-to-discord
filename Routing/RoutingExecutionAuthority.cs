using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

internal enum RoutingExecutionAuthorityOwner
{
    Routing
}

/// <summary>
/// The irreversible commit point for the watched-folder routing runtime. A committed document
/// means a later process must recover Routing and must not start the legacy watcher. It stores
/// only opaque migration/source evidence; paths and connection secrets remain in their existing
/// protected stores.
/// </summary>
internal sealed record RoutingExecutionAuthorityDocument(
    int SchemaVersion,
    long Generation,
    Guid ActivationId,
    RoutingExecutionAuthorityOwner Owner,
    Guid MigrationId,
    string MigrationPayloadFingerprint,
    string SourceFingerprint,
    RoutingCaptureLibraryBinding CaptureLibraryBinding,
    ClipCaptureSource RequiredLegacySource,
    DateTimeOffset ActivatedUtc,
    string IntegrityFingerprint);

internal static class RoutingExecutionAuthorityModel
{
    private const string FingerprintDomain = "clipcord-routing-execution-authority-v2";

    internal static RoutingExecutionAuthorityDocument Create(
        LegacyRoutingMigrationMarker committedMigration,
        ClipCaptureSource requiredLegacySource,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(committedMigration);
        LegacyRoutingMigrationMarkerModel.Validate(committedMigration);
        RoutingValidation.Require(
            committedMigration.Phase == LegacyRoutingMigrationMarkerPhase.Committed,
            "Routing authority requires a committed legacy migration.");
        requiredLegacySource = RequireCanonicalSource(requiredLegacySource);
        var activatedUtc = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        var activationId = LegacyRoutingMigrationPlanner.DeterministicGuid(
            committedMigration.PayloadFingerprint,
            "routing-execution-authority");
        var document = new RoutingExecutionAuthorityDocument(
            RoutingExecutionAuthorityStore.CurrentSchemaVersion,
            Generation: 1,
            activationId,
            RoutingExecutionAuthorityOwner.Routing,
            committedMigration.MigrationId,
            committedMigration.PayloadFingerprint,
            committedMigration.SourceFingerprint,
            committedMigration.CaptureLibraryBinding,
            requiredLegacySource,
            activatedUtc,
            CreateIntegrityFingerprint(
                RoutingExecutionAuthorityStore.CurrentSchemaVersion,
                generation: 1,
                activationId,
                RoutingExecutionAuthorityOwner.Routing,
                committedMigration.MigrationId,
                committedMigration.PayloadFingerprint,
                committedMigration.SourceFingerprint,
                committedMigration.CaptureLibraryBinding,
                requiredLegacySource,
                activatedUtc));
        Validate(document);
        return document;
    }

    internal static void Validate(RoutingExecutionAuthorityDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(
            document.SchemaVersion == RoutingExecutionAuthorityStore.CurrentSchemaVersion,
            "The routing execution authority schema is unsupported.");
        RoutingValidation.Require(
            document.Generation == 1 && document.ActivationId != Guid.Empty &&
            document.MigrationId != Guid.Empty,
            "The routing execution authority identity is invalid.");
        RoutingValidation.Require(
            document.Owner == RoutingExecutionAuthorityOwner.Routing,
            "The routing execution authority owner is unsupported.");
        RoutingValidation.RequireSha256(
            document.MigrationPayloadFingerprint,
            "routing authority migration payload fingerprint");
        RoutingValidation.RequireSha256(
            document.SourceFingerprint,
            "routing authority source fingerprint");
        RoutingCaptureLibraryBindingModel.Validate(document.CaptureLibraryBinding);
        RoutingValidation.RequireSha256(
            document.IntegrityFingerprint,
            "routing authority integrity fingerprint");
        RoutingValidation.Require(
            document.MigrationPayloadFingerprint ==
            document.MigrationPayloadFingerprint.ToUpperInvariant() &&
            document.SourceFingerprint == document.SourceFingerprint.ToUpperInvariant() &&
            document.IntegrityFingerprint == document.IntegrityFingerprint.ToUpperInvariant(),
            "Routing execution authority fingerprints must use canonical uppercase SHA-256 text.");
        _ = RequireCanonicalSource(document.RequiredLegacySource);
        RoutingValidation.RequireUtc(
            document.ActivatedUtc,
            "routing authority activation timestamp");
        RoutingValidation.Require(
            document.ActivationId == LegacyRoutingMigrationPlanner.DeterministicGuid(
                document.MigrationPayloadFingerprint,
                "routing-execution-authority"),
            "The routing execution authority id is not bound to its migration.");
        RoutingValidation.Require(
            document.IntegrityFingerprint.Equals(
                CreateIntegrityFingerprint(
                    document.SchemaVersion,
                    document.Generation,
                    document.ActivationId,
                    document.Owner,
                    document.MigrationId,
                    document.MigrationPayloadFingerprint,
                    document.SourceFingerprint,
                    document.CaptureLibraryBinding,
                    document.RequiredLegacySource,
                    document.ActivatedUtc),
                StringComparison.Ordinal),
            "The routing execution authority integrity fingerprint is invalid.");
    }

    /// <summary>
    /// Authority is intentionally immutable. Returning to Legacy requires a future, separate
    /// drain-and-supersede protocol; it is never represented as a successor to this document.
    /// </summary>
    internal static void ValidateSuccessor(
        RoutingExecutionAuthorityDocument current,
        RoutingExecutionAuthorityDocument candidate)
    {
        Validate(current);
        Validate(candidate);
        throw new InvalidDataException(
            "Routing execution authority is sticky and cannot be replaced or downgraded.");
    }

    internal static Action<RoutingExecutionAuthorityDocument> InitialValidator =>
        document =>
        {
            Validate(document);
            RoutingValidation.Require(document.Generation == 1,
                "The first routing execution authority is not canonical.");
        };

    private static ClipCaptureSource RequireCanonicalSource(ClipCaptureSource source)
    {
        RoutingValidation.Require(
            Enum.IsDefined(source) && AppSettings.NormalizeCaptureSource(source) == source,
            "The routing execution authority source is unsupported.");
        return source;
    }

    private static string CreateIntegrityFingerprint(
        int schemaVersion,
        long generation,
        Guid activationId,
        RoutingExecutionAuthorityOwner owner,
        Guid migrationId,
        string migrationPayloadFingerprint,
        string sourceFingerprint,
        RoutingCaptureLibraryBinding captureLibraryBinding,
        ClipCaptureSource requiredLegacySource,
        DateTimeOffset activatedUtc)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, FingerprintDomain);
        Append(hash, schemaVersion.ToString(CultureInfo.InvariantCulture));
        Append(hash, generation.ToString(CultureInfo.InvariantCulture));
        Append(hash, activationId.ToString("N"));
        Append(hash, owner.ToString());
        Append(hash, migrationId.ToString("N"));
        Append(hash, migrationPayloadFingerprint);
        Append(hash, sourceFingerprint);
        Append(hash, captureLibraryBinding.CanonicalPathFingerprint);
        Append(hash, captureLibraryBinding.NativeDirectoryIdentityFingerprint);
        Append(hash, requiredLegacySource.ToString());
        Append(hash, activatedUtc.ToString("O", CultureInfo.InvariantCulture));
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

internal enum RoutingExecutionAuthorityInspectionState
{
    LegacyPermitted,
    RoutingRequired,
    Blocked
}

internal sealed record RoutingExecutionAuthorityInspection(
    RoutingExecutionAuthorityInspectionState State,
    RoutingDocumentLoadStatus LoadStatus,
    RoutingExecutionAuthorityDocument? Document)
{
    internal bool LegacyPermitted => State == RoutingExecutionAuthorityInspectionState.LegacyPermitted;
    internal bool RoutingRequired => State == RoutingExecutionAuthorityInspectionState.RoutingRequired;
    internal bool Blocked => State == RoutingExecutionAuthorityInspectionState.Blocked;
}

internal enum RoutingExecutionAuthorityCommitStatus
{
    Committed,
    AlreadyCommitted
}

internal sealed record RoutingExecutionAuthorityCommitResult(
    RoutingExecutionAuthorityCommitStatus Status,
    RoutingExecutionAuthorityDocument Document);

internal sealed class RoutingExecutionAuthorityConflictException(string message)
    : InvalidOperationException(message);

/// <summary>
/// Initial-only durable authority store. Missing is the only state that permits Legacy. Any
/// malformed, inaccessible, or unexpected filesystem state blocks both runtimes. Commit ignores
/// cancellation after entry and verifies the exact bytes through a fresh validated load before it
/// reports success.
/// </summary>
internal sealed class RoutingExecutionAuthorityStore
{
    internal const int CurrentSchemaVersion = 2;
    internal const int MaximumDocumentBytes = 64 * 1024;
    internal const string FileName = ".runtime-authority.json";
    private const int MaximumCommitAttempts = 4;

    private readonly RoutingAtomicJsonStore<RoutingExecutionAuthorityDocument> _store;

    internal RoutingExecutionAuthorityStore()
        : this(System.IO.Path.Combine(SettingsStore.DataDirectory, "routing", FileName))
    {
    }

    internal RoutingExecutionAuthorityStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.GetFileName(canonical).Equals(FileName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The routing execution authority must be named {FileName}.",
                nameof(path));
        }
        _store = new RoutingAtomicJsonStore<RoutingExecutionAuthorityDocument>(
            canonical,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Generation,
            RoutingExecutionAuthorityModel.Validate,
            RoutingExecutionAuthorityModel.ValidateSuccessor,
            RoutingExecutionAuthorityModel.InitialValidator);
    }

    internal string Path => _store.Path;

    internal RoutingDocumentLoadResult<RoutingExecutionAuthorityDocument> Load(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // RoutingAtomicJsonStore treats a directory occupying a missing file path as Missing.
        // That is acceptable for replaceable documents, but authority must fail closed.
        if (Directory.Exists(Path))
        {
            return new RoutingDocumentLoadResult<RoutingExecutionAuthorityDocument>(
                null,
                RoutingDocumentLoadStatus.Invalid);
        }
        return _store.Load(cancellationToken);
    }

    internal RoutingExecutionAuthorityInspection Inspect(
        CancellationToken cancellationToken = default)
    {
        var loaded = Load(cancellationToken);
        return loaded.Status switch
        {
            RoutingDocumentLoadStatus.Missing => new RoutingExecutionAuthorityInspection(
                RoutingExecutionAuthorityInspectionState.LegacyPermitted,
                loaded.Status,
                null),
            RoutingDocumentLoadStatus.Loaded when loaded.Document is not null =>
                new RoutingExecutionAuthorityInspection(
                    RoutingExecutionAuthorityInspectionState.RoutingRequired,
                    loaded.Status,
                    loaded.Document),
            _ => new RoutingExecutionAuthorityInspection(
                RoutingExecutionAuthorityInspectionState.Blocked,
                loaded.Status,
                null)
        };
    }

    internal async Task<RoutingExecutionAuthorityCommitResult> CommitAsync(
        RoutingExecutionAuthorityDocument document,
        CancellationToken cancellationToken = default,
        Action? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingExecutionAuthorityModel.Validate(document);
        cancellationToken.ThrowIfCancellationRequested();

        // Crossing this method boundary is the irreversible commit operation. Caller
        // cancellation is deliberately not allowed to make its outcome ambiguous.
        for (var attempt = 0; attempt < MaximumCommitAttempts; attempt++)
        {
            var existing = Load(CancellationToken.None);
            if (existing.LoadedFromDisk && existing.Document is not null)
            {
                return ExistingResult(existing.Document, document);
            }
            if (existing.Status != RoutingDocumentLoadStatus.Missing)
            {
                throw new InvalidDataException(
                    $"Routing execution authority cannot be committed safely ({existing.Status}).");
            }

            try
            {
                _ = await _store.SaveAsync(
                        document,
                        expectedGeneration: 0,
                        CancellationToken.None,
                        beforeCommit)
                    .ConfigureAwait(false);
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumCommitAttempts - 1)
            {
                continue;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                var afterFailure = Load(CancellationToken.None);
                if (afterFailure.LoadedFromDisk && afterFailure.Document is not null)
                {
                    return ExistingResult(afterFailure.Document, document);
                }
                if (afterFailure.Status != RoutingDocumentLoadStatus.Missing)
                {
                    throw new InvalidDataException(
                        $"The routing authority commit outcome cannot be established safely ({afterFailure.Status}).",
                        exception);
                }
                throw;
            }

            var verified = Load(CancellationToken.None);
            if (!verified.LoadedFromDisk || verified.Document is null)
            {
                throw new InvalidDataException(
                    $"The committed routing authority could not be verified ({verified.Status}).");
            }
            RequireExact(verified.Document, document);
            return new RoutingExecutionAuthorityCommitResult(
                RoutingExecutionAuthorityCommitStatus.Committed,
                verified.Document);
        }

        throw new RoutingConcurrencyException(
            "Routing execution authority kept changing while it was committed.");
    }

    private static RoutingExecutionAuthorityCommitResult ExistingResult(
        RoutingExecutionAuthorityDocument existing,
        RoutingExecutionAuthorityDocument candidate)
    {
        RequireExact(existing, candidate);
        return new RoutingExecutionAuthorityCommitResult(
            RoutingExecutionAuthorityCommitStatus.AlreadyCommitted,
            existing);
    }

    private static void RequireExact(
        RoutingExecutionAuthorityDocument existing,
        RoutingExecutionAuthorityDocument candidate)
    {
        RoutingExecutionAuthorityModel.Validate(existing);
        RoutingExecutionAuthorityModel.Validate(candidate);
        if (existing != candidate)
        {
            throw new RoutingExecutionAuthorityConflictException(
                "A different routing execution authority is already committed.");
        }
    }
}
