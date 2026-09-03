using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

internal enum RoutingCaptureLibrarySwitchPhase
{
    Prepared,
    Stable
}

/// <summary>
/// A path-free successor record for the Capture library after Routing has committed. The original
/// migration marker remains immutable evidence of how Routing first acquired ownership; this
/// document records only later, explicitly requested Capture-library binding changes.
/// </summary>
internal sealed record RoutingCaptureLibrarySwitchDocument(
    int SchemaVersion,
    long Generation,
    Guid AuthorityActivationId,
    string AuthorityIntegrityFingerprint,
    Guid OperationId,
    RoutingCaptureLibrarySwitchPhase Phase,
    RoutingCaptureLibraryBinding EffectiveBinding,
    RoutingCaptureLibraryBinding? PendingBinding,
    DateTimeOffset UpdatedUtc,
    string IntegrityFingerprint);

internal static class RoutingCaptureLibrarySwitchModel
{
    private const string IntegrityDomain = "clipcord-capture-library-switch-v1";

    internal static RoutingCaptureLibrarySwitchDocument Prepare(
        long generation,
        RoutingExecutionAuthorityDocument authority,
        RoutingCaptureLibraryBinding effectiveBinding,
        RoutingCaptureLibraryBinding pendingBinding,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(authority);
        RoutingExecutionAuthorityModel.Validate(authority);
        RoutingCaptureLibraryBindingModel.Validate(effectiveBinding);
        RoutingCaptureLibraryBindingModel.Validate(pendingBinding);
        RoutingValidation.Require(effectiveBinding != pendingBinding,
            "The replacement Capture library must differ from the current library.");
        var operationId = Guid.NewGuid();
        var updatedUtc = RoutingValidation.Utc(now);
        var document = new RoutingCaptureLibrarySwitchDocument(
            RoutingCaptureLibrarySwitchStore.CurrentSchemaVersion,
            generation,
            authority.ActivationId,
            authority.IntegrityFingerprint,
            operationId,
            RoutingCaptureLibrarySwitchPhase.Prepared,
            effectiveBinding,
            pendingBinding,
            updatedUtc,
            CreateIntegrityFingerprint(
                RoutingCaptureLibrarySwitchStore.CurrentSchemaVersion,
                generation,
                authority.ActivationId,
                authority.IntegrityFingerprint,
                operationId,
                RoutingCaptureLibrarySwitchPhase.Prepared,
                effectiveBinding,
                pendingBinding,
                updatedUtc));
        Validate(document);
        return document;
    }

    internal static RoutingCaptureLibrarySwitchDocument Stabilize(
        RoutingCaptureLibrarySwitchDocument prepared,
        RoutingCaptureLibraryBinding effectiveBinding,
        DateTimeOffset now)
    {
        Validate(prepared);
        RoutingValidation.Require(
            prepared.Phase == RoutingCaptureLibrarySwitchPhase.Prepared &&
            prepared.PendingBinding is not null,
            "Only a prepared Capture-library switch can be stabilized.");
        RoutingCaptureLibraryBindingModel.Validate(effectiveBinding);
        RoutingValidation.Require(
            effectiveBinding == prepared.EffectiveBinding ||
            effectiveBinding == prepared.PendingBinding,
            "The stabilized Capture library is not part of the prepared switch.");
        var updatedUtc = RoutingValidation.Utc(now);
        var document = prepared with
        {
            Generation = checked(prepared.Generation + 1),
            Phase = RoutingCaptureLibrarySwitchPhase.Stable,
            EffectiveBinding = effectiveBinding,
            PendingBinding = null,
            UpdatedUtc = updatedUtc,
            IntegrityFingerprint = CreateIntegrityFingerprint(
                prepared.SchemaVersion,
                checked(prepared.Generation + 1),
                prepared.AuthorityActivationId,
                prepared.AuthorityIntegrityFingerprint,
                prepared.OperationId,
                RoutingCaptureLibrarySwitchPhase.Stable,
                effectiveBinding,
                pendingBinding: null,
                updatedUtc)
        };
        Validate(document);
        return document;
    }

    internal static void Validate(RoutingCaptureLibrarySwitchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(
            document.SchemaVersion == RoutingCaptureLibrarySwitchStore.CurrentSchemaVersion &&
            document.Generation >= 1 &&
            document.AuthorityActivationId != Guid.Empty &&
            document.OperationId != Guid.Empty,
            "The Capture-library switch identity is invalid.");
        RoutingValidation.RequireSha256(
            document.AuthorityIntegrityFingerprint,
            "Capture-library switch authority fingerprint");
        RoutingCaptureLibraryBindingModel.Validate(document.EffectiveBinding);
        if (document.Phase == RoutingCaptureLibrarySwitchPhase.Prepared)
        {
            RoutingValidation.Require(document.PendingBinding is not null,
                "A prepared Capture-library switch is missing its replacement binding.");
            RoutingCaptureLibraryBindingModel.Validate(document.PendingBinding!);
            RoutingValidation.Require(document.PendingBinding != document.EffectiveBinding,
                "A prepared Capture-library switch must change the binding.");
        }
        else
        {
            RoutingValidation.Require(
                document.Phase == RoutingCaptureLibrarySwitchPhase.Stable &&
                document.PendingBinding is null,
                "A stable Capture-library switch cannot retain a pending binding.");
        }
        RoutingValidation.RequireUtc(document.UpdatedUtc, "Capture-library switch timestamp");
        RoutingValidation.RequireSha256(
            document.IntegrityFingerprint,
            "Capture-library switch integrity fingerprint");
        RoutingValidation.Require(
            document.AuthorityIntegrityFingerprint ==
                document.AuthorityIntegrityFingerprint.ToUpperInvariant() &&
            document.IntegrityFingerprint == document.IntegrityFingerprint.ToUpperInvariant() &&
            document.IntegrityFingerprint.Equals(
                CreateIntegrityFingerprint(
                    document.SchemaVersion,
                    document.Generation,
                    document.AuthorityActivationId,
                    document.AuthorityIntegrityFingerprint,
                    document.OperationId,
                    document.Phase,
                    document.EffectiveBinding,
                    document.PendingBinding,
                    document.UpdatedUtc),
                StringComparison.Ordinal),
            "The Capture-library switch integrity fingerprint is invalid.");
    }

    internal static void ValidateInitial(RoutingCaptureLibrarySwitchDocument document)
    {
        Validate(document);
        RoutingValidation.Require(
            document.Generation == 1 &&
            document.Phase == RoutingCaptureLibrarySwitchPhase.Prepared,
            "The first Capture-library switch must be a prepared generation-one document.");
    }

    internal static void ValidateSuccessor(
        RoutingCaptureLibrarySwitchDocument current,
        RoutingCaptureLibrarySwitchDocument candidate)
    {
        Validate(current);
        Validate(candidate);
        RoutingValidation.Require(
            candidate.Generation == checked(current.Generation + 1) &&
            candidate.AuthorityActivationId == current.AuthorityActivationId &&
            candidate.AuthorityIntegrityFingerprint.Equals(
                current.AuthorityIntegrityFingerprint,
                StringComparison.Ordinal),
            "The Capture-library switch successor changed its authority lineage.");
        if (current.Phase == RoutingCaptureLibrarySwitchPhase.Prepared)
        {
            RoutingValidation.Require(
                candidate.Phase == RoutingCaptureLibrarySwitchPhase.Stable &&
                candidate.OperationId == current.OperationId &&
                candidate.PendingBinding is null &&
                (candidate.EffectiveBinding == current.EffectiveBinding ||
                 candidate.EffectiveBinding == current.PendingBinding),
                "A prepared Capture-library switch may only commit or abort exactly once.");
            return;
        }
        RoutingValidation.Require(
            candidate.Phase == RoutingCaptureLibrarySwitchPhase.Prepared &&
            candidate.OperationId != current.OperationId &&
            candidate.EffectiveBinding == current.EffectiveBinding &&
            candidate.PendingBinding is not null &&
            candidate.PendingBinding != current.EffectiveBinding,
            "A stable Capture-library binding may only advance through a new prepared switch.");
    }

    internal static void RequireAuthority(
        RoutingCaptureLibrarySwitchDocument document,
        RoutingExecutionAuthorityDocument authority)
    {
        Validate(document);
        RoutingExecutionAuthorityModel.Validate(authority);
        RoutingValidation.Require(
            document.AuthorityActivationId == authority.ActivationId &&
            document.AuthorityIntegrityFingerprint.Equals(
                authority.IntegrityFingerprint,
                StringComparison.Ordinal),
            "The Capture-library switch belongs to different Routing authority.");
    }

    private static string CreateIntegrityFingerprint(
        int schemaVersion,
        long generation,
        Guid authorityActivationId,
        string authorityIntegrityFingerprint,
        Guid operationId,
        RoutingCaptureLibrarySwitchPhase phase,
        RoutingCaptureLibraryBinding effectiveBinding,
        RoutingCaptureLibraryBinding? pendingBinding,
        DateTimeOffset updatedUtc)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, IntegrityDomain);
        Append(hash, schemaVersion.ToString(CultureInfo.InvariantCulture));
        Append(hash, generation.ToString(CultureInfo.InvariantCulture));
        Append(hash, authorityActivationId.ToString("N"));
        Append(hash, authorityIntegrityFingerprint);
        Append(hash, operationId.ToString("N"));
        Append(hash, phase.ToString());
        AppendBinding(hash, effectiveBinding);
        Append(hash, pendingBinding is null ? "none" : "pending");
        if (pendingBinding is not null) AppendBinding(hash, pendingBinding);
        Append(hash, updatedUtc.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendBinding(
        IncrementalHash hash,
        RoutingCaptureLibraryBinding binding)
    {
        Append(hash, binding.CanonicalPathFingerprint);
        Append(hash, binding.NativeDirectoryIdentityFingerprint);
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

/// <summary>
/// Crash-safe two-phase handoff for a user-requested Capture-library change. Capture settings are
/// the path-bearing side of the transaction. A prepared record resolves to the old binding when
/// settings still name the old root, or to the replacement when settings already name the new root.
/// </summary>
internal sealed class RoutingCaptureLibrarySwitchStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = 16 * 1024;
    internal const string FileName = ".capture-library-switch.json";

    private readonly RoutingAtomicJsonStore<RoutingCaptureLibrarySwitchDocument> _store;
    private readonly Func<DateTimeOffset> _utcNow;

    internal RoutingCaptureLibrarySwitchStore()
        : this(System.IO.Path.Combine(SettingsStore.DataDirectory, "routing", FileName))
    {
    }

    internal RoutingCaptureLibrarySwitchStore(
        string path,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.GetFileName(canonical).Equals(FileName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The Capture-library switch record must be named {FileName}.",
                nameof(path));
        }
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _store = new RoutingAtomicJsonStore<RoutingCaptureLibrarySwitchDocument>(
            canonical,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Generation,
            RoutingCaptureLibrarySwitchModel.Validate,
            RoutingCaptureLibrarySwitchModel.ValidateSuccessor,
            RoutingCaptureLibrarySwitchModel.ValidateInitial);
    }

    internal string Path => _store.Path;

    internal RoutingDocumentLoadResult<RoutingCaptureLibrarySwitchDocument> Load(
        CancellationToken cancellationToken = default) => _store.Load(cancellationToken);

    internal RoutingCaptureLibraryBinding Resolve(
        RoutingExecutionAuthorityDocument authority,
        RoutingCaptureLibraryBinding currentSettingsBinding,
        CancellationToken cancellationToken = default)
    {
        RoutingExecutionAuthorityModel.Validate(authority);
        RoutingCaptureLibraryBindingModel.Validate(currentSettingsBinding);
        var loaded = Load(cancellationToken);
        if (loaded.Status == RoutingDocumentLoadStatus.Missing)
        {
            RoutingCaptureLibraryBindingModel.RequireExact(
                authority.CaptureLibraryBinding,
                currentSettingsBinding);
            return authority.CaptureLibraryBinding;
        }
        if (!loaded.LoadedFromDisk || loaded.Document is null)
        {
            throw new InvalidDataException(
                $"Capture-library switch authority cannot be loaded safely ({loaded.Status}).");
        }

        var document = loaded.Document;
        RoutingCaptureLibrarySwitchModel.RequireAuthority(document, authority);
        if (document.Phase == RoutingCaptureLibrarySwitchPhase.Stable)
        {
            RoutingCaptureLibraryBindingModel.RequireExact(
                document.EffectiveBinding,
                currentSettingsBinding);
            return document.EffectiveBinding;
        }

        var resolved = currentSettingsBinding == document.PendingBinding
            ? document.PendingBinding!
            : currentSettingsBinding == document.EffectiveBinding
                ? document.EffectiveBinding
                : throw new InvalidDataException(
                    "Capture settings do not match either side of the prepared library switch.");
        var stable = RoutingCaptureLibrarySwitchModel.Stabilize(
            document,
            resolved,
            _utcNow());
        _ = _store.SaveAsync(stable, document.Generation, cancellationToken)
            .GetAwaiter()
            .GetResult();
        var verified = Load(cancellationToken);
        if (!verified.LoadedFromDisk || verified.Document != stable)
        {
            throw new InvalidDataException(
                "The recovered Capture-library switch could not be verified.");
        }
        return stable.EffectiveBinding;
    }

    internal RoutingCaptureLibrarySwitchDocument Prepare(
        RoutingExecutionAuthorityDocument authority,
        RoutingCaptureLibraryBinding currentBinding,
        RoutingCaptureLibraryBinding replacementBinding,
        CancellationToken cancellationToken = default)
    {
        RoutingExecutionAuthorityModel.Validate(authority);
        RoutingCaptureLibraryBindingModel.Validate(currentBinding);
        RoutingCaptureLibraryBindingModel.Validate(replacementBinding);
        var effective = Resolve(authority, currentBinding, cancellationToken);
        RoutingCaptureLibraryBindingModel.RequireExact(effective, currentBinding);
        var loaded = Load(cancellationToken);
        if (loaded.Status is not (RoutingDocumentLoadStatus.Missing or
            RoutingDocumentLoadStatus.Loaded) ||
            loaded.Document is { Phase: not RoutingCaptureLibrarySwitchPhase.Stable })
        {
            throw new InvalidDataException(
                "The current Capture-library switch state is not stable.");
        }
        var generation = checked((loaded.Document?.Generation ?? 0) + 1);
        var prepared = RoutingCaptureLibrarySwitchModel.Prepare(
            generation,
            authority,
            currentBinding,
            replacementBinding,
            _utcNow());
        _ = _store.SaveAsync(
                prepared,
                expectedGeneration: generation - 1,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
        return RequireExactLoaded(prepared, cancellationToken);
    }

    internal RoutingCaptureLibraryBinding Commit(
        RoutingCaptureLibrarySwitchDocument prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        RoutingCaptureLibrarySwitchModel.Validate(prepared);
        var committed = RoutingCaptureLibrarySwitchModel.Stabilize(
            prepared,
            prepared.PendingBinding ?? throw new InvalidDataException(
                "The prepared Capture-library switch has no replacement."),
            _utcNow());
        _ = _store.SaveAsync(committed, prepared.Generation, cancellationToken)
            .GetAwaiter()
            .GetResult();
        return RequireExactLoaded(committed, cancellationToken).EffectiveBinding;
    }

    internal RoutingCaptureLibraryBinding Abort(
        RoutingCaptureLibrarySwitchDocument prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        RoutingCaptureLibrarySwitchModel.Validate(prepared);
        var aborted = RoutingCaptureLibrarySwitchModel.Stabilize(
            prepared,
            prepared.EffectiveBinding,
            _utcNow());
        _ = _store.SaveAsync(aborted, prepared.Generation, cancellationToken)
            .GetAwaiter()
            .GetResult();
        return RequireExactLoaded(aborted, cancellationToken).EffectiveBinding;
    }

    internal RoutingCaptureLibraryBinding RollBack(
        RoutingExecutionAuthorityDocument authority,
        RoutingCaptureLibrarySwitchDocument prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(prepared);
        RoutingCaptureLibrarySwitchModel.RequireAuthority(prepared, authority);
        var loaded = Load(cancellationToken);
        if (!loaded.LoadedFromDisk || loaded.Document is null)
        {
            throw new InvalidDataException(
                $"The Capture-library switch cannot be rolled back safely ({loaded.Status}).");
        }
        var current = loaded.Document;
        RoutingCaptureLibrarySwitchModel.RequireAuthority(current, authority);
        if (current.OperationId == prepared.OperationId &&
            current.Phase == RoutingCaptureLibrarySwitchPhase.Prepared)
        {
            return Abort(current, cancellationToken);
        }
        if (current.OperationId == prepared.OperationId &&
            current.Phase == RoutingCaptureLibrarySwitchPhase.Stable &&
            current.EffectiveBinding == prepared.EffectiveBinding)
        {
            return current.EffectiveBinding;
        }
        if (current.OperationId == prepared.OperationId &&
            current.Phase == RoutingCaptureLibrarySwitchPhase.Stable &&
            current.EffectiveBinding == prepared.PendingBinding)
        {
            var reverse = Prepare(
                authority,
                current.EffectiveBinding,
                prepared.EffectiveBinding,
                cancellationToken);
            return Commit(reverse, cancellationToken);
        }
        throw new InvalidDataException(
            "Capture-library switch authority changed before rollback completed.");
    }

    private RoutingCaptureLibrarySwitchDocument RequireExactLoaded(
        RoutingCaptureLibrarySwitchDocument expected,
        CancellationToken cancellationToken)
    {
        var loaded = Load(cancellationToken);
        if (!loaded.LoadedFromDisk || loaded.Document != expected)
        {
            throw new InvalidDataException(
                $"The Capture-library switch write could not be verified ({loaded.Status}).");
        }
        return loaded.Document;
    }
}
