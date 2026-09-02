using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipsToDiscord;

internal enum SilhouetteMatteState
{
    Pending,
    Processing,
    Committing,
    Ready,
    Failed
}

internal enum SilhouetteOutputState
{
    Disabled,
    Pending,
    Rendering,
    Committing,
    Ready,
    Failed
}

internal enum SilhouetteRenditionAggregateState
{
    Disabled,
    Pending,
    Processing,
    PartiallyReady,
    Ready,
    Failed
}

internal sealed record SilhouetteMatteRendition(
    SilhouetteMatteState State,
    int Attempts,
    long AttemptGeneration,
    DateTimeOffset? StartedUtc,
    DateTimeOffset UpdatedUtc,
    string? ErrorCode,
    CaptureMediaFingerprint? ExpectedTemporaryArtifact,
    CaptureMediaFingerprint? FinalArtifact);

internal sealed record SilhouetteOutputRendition(
    string OrientationId,
    SilhouetteOutputState State,
    int Attempts,
    long AttemptGeneration,
    long MatteGeneration,
    DateTimeOffset? StartedUtc,
    DateTimeOffset UpdatedUtc,
    string? ErrorCode,
    CaptureMediaFingerprint? ExpectedTemporaryArtifact,
    CaptureMediaFingerprint? FinalArtifact);

/// <summary>
/// Durable orchestration state only. Media paths and model output bytes deliberately do not
/// belong here; a validated capture project and content fingerprints own those identities.
/// Generation is a compare-and-swap revision, while AttemptGeneration prevents a completion
/// from an abandoned matte/render attempt from publishing into a later attempt.
/// </summary>
internal sealed record SilhouetteRenditionDocument(
    int SchemaVersion,
    string ProjectId,
    long Generation,
    long CompositionRevision,
    string CompositionSha256,
    string ModelId,
    string ModelSha256,
    CaptureMediaFingerprint GameplaySource,
    CaptureMediaFingerprint CameraSource,
    SilhouetteMatteRendition Matte,
    IReadOnlyList<SilhouetteOutputRendition> Outputs,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    [JsonIgnore]
    internal SilhouetteRenditionAggregateState AggregateState =>
        SilhouetteRenditionModel.GetAggregateState(this);
}

internal sealed class SilhouetteRenditionConcurrencyException : InvalidOperationException
{
    internal SilhouetteRenditionConcurrencyException(string message) : base(message)
    {
    }
}

internal static class SilhouetteRenditionModel
{
    internal const string DefaultErrorCode = "processing-failed";
    internal const int MaximumErrorCodeLength = 64;
    private static IReadOnlySet<string> AllowedErrorCodes { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            DefaultErrorCode,
            "model-unavailable",
            "model-validation-failed",
            "source-invalid",
            "matte-processing-failed",
            "render-failed",
            "commit-failed",
            "output-invalid",
            "insufficient-storage"
        };

    internal static SilhouetteRenditionDocument StartMatte(
        SilhouetteRenditionDocument document,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        Require(document.Matte.State == SilhouetteMatteState.Pending,
            "Only a pending matte may start processing.");
        var generation = NextGeneration(document);
        var utcNow = NormalizeTimestamp(now);
        var matte = document.Matte with
        {
            State = SilhouetteMatteState.Processing,
            Attempts = checked(document.Matte.Attempts + 1),
            AttemptGeneration = generation,
            StartedUtc = utcNow,
            UpdatedUtc = utcNow,
            ErrorCode = null,
            ExpectedTemporaryArtifact = null,
            FinalArtifact = null
        };
        return Next(document, generation, utcNow, matte, document.Outputs);
    }

    internal static SilhouetteRenditionDocument BeginMatteCommit(
        SilhouetteRenditionDocument document,
        long attemptGeneration,
        CaptureMediaFingerprint expectedTemporaryArtifact,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        RequireCurrentAttempt(
            document.Matte.State == SilhouetteMatteState.Processing,
            document.Matte.AttemptGeneration,
            attemptGeneration,
            "matte");
        var artifact = NormalizeFingerprint(expectedTemporaryArtifact, "matte artifact");
        var generation = NextGeneration(document);
        var utcNow = NormalizeTimestamp(now);
        var matte = document.Matte with
        {
            State = SilhouetteMatteState.Committing,
            UpdatedUtc = utcNow,
            ExpectedTemporaryArtifact = artifact,
            FinalArtifact = null,
            ErrorCode = null
        };
        return Next(document, generation, utcNow, matte, document.Outputs);
    }

    internal static SilhouetteRenditionDocument CompleteMatte(
        SilhouetteRenditionDocument document,
        long attemptGeneration,
        CaptureMediaFingerprint verifiedFinalArtifact,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        RequireCurrentAttempt(
            document.Matte.State == SilhouetteMatteState.Committing,
            document.Matte.AttemptGeneration,
            attemptGeneration,
            "matte");
        var finalArtifact = NormalizeFingerprint(verifiedFinalArtifact, "matte artifact");
        Require(FingerprintsEqual(
                document.Matte.ExpectedTemporaryArtifact,
                finalArtifact),
            "The committed matte does not match the expected temporary artifact.");
        var generation = NextGeneration(document);
        var utcNow = NormalizeTimestamp(now);
        var matte = document.Matte with
        {
            State = SilhouetteMatteState.Ready,
            UpdatedUtc = utcNow,
            ExpectedTemporaryArtifact = null,
            FinalArtifact = finalArtifact,
            ErrorCode = null
        };
        return Next(document, generation, utcNow, matte, document.Outputs);
    }

    internal static SilhouetteRenditionDocument FailMatte(
        SilhouetteRenditionDocument document,
        long attemptGeneration,
        string? errorCode,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        RequireCurrentAttempt(
            document.Matte.State is SilhouetteMatteState.Processing or
                SilhouetteMatteState.Committing,
            document.Matte.AttemptGeneration,
            attemptGeneration,
            "matte");
        var generation = NextGeneration(document);
        var utcNow = NormalizeTimestamp(now);
        var matte = document.Matte with
        {
            State = SilhouetteMatteState.Failed,
            UpdatedUtc = utcNow,
            ErrorCode = SanitizeErrorCode(errorCode),
            ExpectedTemporaryArtifact = null,
            FinalArtifact = null
        };
        return Next(document, generation, utcNow, matte, document.Outputs);
    }

    internal static SilhouetteRenditionDocument RetryMatte(
        SilhouetteRenditionDocument document,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        Require(document.Matte.State == SilhouetteMatteState.Failed,
            "Only a failed matte may be retried.");
        var generation = NextGeneration(document);
        var utcNow = NormalizeTimestamp(now);
        var matte = ToPending(document.Matte, utcNow);
        return Next(document, generation, utcNow, matte, document.Outputs);
    }

    internal static SilhouetteRenditionDocument StartOutput(
        SilhouetteRenditionDocument document,
        string orientationId,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        Require(document.Matte.State == SilhouetteMatteState.Ready &&
                document.Matte.AttemptGeneration > 0,
            "A rendition cannot start until the shared matte is ready.");
        var (outputs, index, output) = FindOutput(document, orientationId);
        Require(output.State == SilhouetteOutputState.Pending,
            "Only a pending rendition may start rendering.");
        var generation = NextGeneration(document);
        var utcNow = NormalizeTimestamp(now);
        outputs[index] = output with
        {
            State = SilhouetteOutputState.Rendering,
            Attempts = checked(output.Attempts + 1),
            AttemptGeneration = generation,
            MatteGeneration = document.Matte.AttemptGeneration,
            StartedUtc = utcNow,
            UpdatedUtc = utcNow,
            ErrorCode = null,
            ExpectedTemporaryArtifact = null,
            FinalArtifact = null
        };
        return Next(document, generation, utcNow, document.Matte, outputs);
    }

    internal static SilhouetteRenditionDocument BeginOutputCommit(
        SilhouetteRenditionDocument document,
        string orientationId,
        long attemptGeneration,
        CaptureMediaFingerprint expectedTemporaryArtifact,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        var (outputs, index, output) = FindOutput(document, orientationId);
        RequireCurrentAttempt(
            output.State == SilhouetteOutputState.Rendering,
            output.AttemptGeneration,
            attemptGeneration,
            "rendition");
        Require(document.Matte.State == SilhouetteMatteState.Ready &&
                output.MatteGeneration == document.Matte.AttemptGeneration,
            "The rendition is not bound to the current shared matte.");
        var artifact = NormalizeFingerprint(expectedTemporaryArtifact, "rendition artifact");
        var generation = NextGeneration(document);
        var utcNow = NormalizeTimestamp(now);
        outputs[index] = output with
        {
            State = SilhouetteOutputState.Committing,
            UpdatedUtc = utcNow,
            ErrorCode = null,
            ExpectedTemporaryArtifact = artifact,
            FinalArtifact = null
        };
        return Next(document, generation, utcNow, document.Matte, outputs);
    }

    internal static SilhouetteRenditionDocument CompleteOutput(
        SilhouetteRenditionDocument document,
        string orientationId,
        long attemptGeneration,
        CaptureMediaFingerprint verifiedFinalArtifact,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        var (outputs, index, output) = FindOutput(document, orientationId);
        RequireCurrentAttempt(
            output.State == SilhouetteOutputState.Committing,
            output.AttemptGeneration,
            attemptGeneration,
            "rendition");
        Require(document.Matte.State == SilhouetteMatteState.Ready &&
                output.MatteGeneration == document.Matte.AttemptGeneration,
            "The rendition is not bound to the current shared matte.");
        var finalArtifact = NormalizeFingerprint(verifiedFinalArtifact, "rendition artifact");
        // Deliberate second check: the artifact store verifies promotion first, while the
        // state model independently refuses to publish Ready against stale commit evidence.
        Require(FingerprintsEqual(output.ExpectedTemporaryArtifact, finalArtifact),
            "The committed rendition does not match the expected temporary artifact.");
        var generation = NextGeneration(document);
        var utcNow = NormalizeTimestamp(now);
        outputs[index] = output with
        {
            State = SilhouetteOutputState.Ready,
            UpdatedUtc = utcNow,
            ErrorCode = null,
            ExpectedTemporaryArtifact = null,
            FinalArtifact = finalArtifact
        };
        return Next(document, generation, utcNow, document.Matte, outputs);
    }

    /// <summary>
    /// Invalidates one published output after the fixed artifact store has proved that the file
    /// is definitively missing (null) or no longer matches its durable fingerprint. The ready
    /// shared matte and any ready sibling output are deliberately preserved for a targeted retry.
    /// </summary>
    internal static SilhouetteRenditionDocument InvalidateReadyOutput(
        SilhouetteRenditionDocument document,
        string orientationId,
        DateTimeOffset now) =>
        InvalidateReadyOutput(document, orientationId, observedArtifact: null, now);

    internal static SilhouetteRenditionDocument InvalidateReadyOutput(
        SilhouetteRenditionDocument document,
        string orientationId,
        CaptureMediaFingerprint? observedArtifact,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        var (outputs, index, output) = FindOutput(document, orientationId);
        Require(output.State == SilhouetteOutputState.Ready,
            "Only a ready rendition may be invalidated after artifact validation.");
        if (observedArtifact is not null)
        {
            var normalizedObserved = NormalizeFingerprint(
                observedArtifact,
                "observed rendition artifact");
            Require(!FingerprintsEqual(output.FinalArtifact, normalizedObserved),
                "A rendition whose artifact still matches cannot be invalidated.");
        }

        var utcNow = NormalizeTimestamp(now);
        outputs[index] = output with
        {
            State = SilhouetteOutputState.Failed,
            UpdatedUtc = utcNow,
            ErrorCode = "output-invalid",
            ExpectedTemporaryArtifact = null,
            FinalArtifact = null
        };
        return Next(
            document,
            NextGeneration(document),
            utcNow,
            document.Matte,
            outputs);
    }

    internal static SilhouetteRenditionDocument FailOutput(
        SilhouetteRenditionDocument document,
        string orientationId,
        long attemptGeneration,
        string? errorCode,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        var (outputs, index, output) = FindOutput(document, orientationId);
        RequireCurrentAttempt(
            output.State is SilhouetteOutputState.Rendering or
                SilhouetteOutputState.Committing,
            output.AttemptGeneration,
            attemptGeneration,
            "rendition");
        var generation = NextGeneration(document);
        var utcNow = NormalizeTimestamp(now);
        outputs[index] = output with
        {
            State = SilhouetteOutputState.Failed,
            UpdatedUtc = utcNow,
            ErrorCode = SanitizeErrorCode(errorCode),
            ExpectedTemporaryArtifact = null,
            FinalArtifact = null
        };
        return Next(document, generation, utcNow, document.Matte, outputs);
    }

    internal static SilhouetteRenditionDocument RetryOutput(
        SilhouetteRenditionDocument document,
        string orientationId,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        var (outputs, index, output) = FindOutput(document, orientationId);
        Require(output.State == SilhouetteOutputState.Failed,
            "Only a failed rendition may be retried.");
        var generation = NextGeneration(document);
        var utcNow = NormalizeTimestamp(now);
        outputs[index] = ToPending(output, utcNow);
        return Next(document, generation, utcNow, document.Matte, outputs);
    }

    /// <summary>
    /// Startup recovery may retry work that never reached its commit boundary. Committing
    /// states are intentionally untouched here: they require an explicit fingerprint-backed
    /// adoption decision through RecoverMatteCommit or RecoverOutputCommit.
    /// </summary>
    internal static SilhouetteRenditionDocument RecoverAbandonedWork(
        SilhouetteRenditionDocument document,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        var utcNow = NormalizeTimestamp(now);
        var changed = false;
        var matte = document.Matte;
        if (matte.State == SilhouetteMatteState.Processing)
        {
            matte = ToPending(matte, utcNow);
            changed = true;
        }

        var outputs = document.Outputs.ToArray();
        for (var index = 0; index < outputs.Length; index++)
        {
            if (outputs[index].State != SilhouetteOutputState.Rendering) continue;
            outputs[index] = ToPending(outputs[index], utcNow);
            changed = true;
        }

        if (!changed) return document;
        return Next(document, NextGeneration(document), utcNow, matte, outputs);
    }

    internal static SilhouetteRenditionDocument RecoverMatteCommit(
        SilhouetteRenditionDocument document,
        long attemptGeneration,
        CaptureMediaFingerprint? verifiedFinalArtifact,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        RequireCurrentAttempt(
            document.Matte.State == SilhouetteMatteState.Committing,
            document.Matte.AttemptGeneration,
            attemptGeneration,
            "matte");
        if (verifiedFinalArtifact is not null &&
            FingerprintsEqual(
                document.Matte.ExpectedTemporaryArtifact,
                NormalizeFingerprint(verifiedFinalArtifact, "matte artifact")))
        {
            return CompleteMatte(document, attemptGeneration, verifiedFinalArtifact, now);
        }

        var utcNow = NormalizeTimestamp(now);
        return Next(
            document,
            NextGeneration(document),
            utcNow,
            ToPending(document.Matte, utcNow),
            document.Outputs);
    }

    internal static SilhouetteRenditionDocument RecoverOutputCommit(
        SilhouetteRenditionDocument document,
        string orientationId,
        long attemptGeneration,
        CaptureMediaFingerprint? verifiedFinalArtifact,
        DateTimeOffset now)
    {
        ValidateForTransition(document);
        var (outputs, index, output) = FindOutput(document, orientationId);
        RequireCurrentAttempt(
            output.State == SilhouetteOutputState.Committing,
            output.AttemptGeneration,
            attemptGeneration,
            "rendition");
        if (verifiedFinalArtifact is not null &&
            FingerprintsEqual(
                output.ExpectedTemporaryArtifact,
                NormalizeFingerprint(verifiedFinalArtifact, "rendition artifact")))
        {
            return CompleteOutput(
                document,
                orientationId,
                attemptGeneration,
                verifiedFinalArtifact,
                now);
        }

        var utcNow = NormalizeTimestamp(now);
        outputs[index] = ToPending(output, utcNow);
        return Next(
            document,
            NextGeneration(document),
            utcNow,
            document.Matte,
            outputs);
    }

    internal static SilhouetteRenditionAggregateState GetAggregateState(
        SilhouetteRenditionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var enabled = document.Outputs?
            .Where(output => output is not null && output.State != SilhouetteOutputState.Disabled)
            .ToArray() ?? [];
        if (enabled.Length == 0) return SilhouetteRenditionAggregateState.Disabled;
        if (document.Matte.State is SilhouetteMatteState.Processing or
            SilhouetteMatteState.Committing ||
            enabled.Any(output => output.State is SilhouetteOutputState.Rendering or
                SilhouetteOutputState.Committing))
        {
            return SilhouetteRenditionAggregateState.Processing;
        }
        if (enabled.All(output => output.State == SilhouetteOutputState.Ready))
        {
            return SilhouetteRenditionAggregateState.Ready;
        }
        if (enabled.Any(output => output.State == SilhouetteOutputState.Ready))
        {
            return SilhouetteRenditionAggregateState.PartiallyReady;
        }
        if (document.Matte.State == SilhouetteMatteState.Failed ||
            enabled.Any(output => output.State == SilhouetteOutputState.Failed))
        {
            return SilhouetteRenditionAggregateState.Failed;
        }
        return SilhouetteRenditionAggregateState.Pending;
    }

    internal static string SanitizeErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode)) return DefaultErrorCode;
        // Failure state is public orchestration metadata, not a log sink. Only fixed,
        // reviewed codes cross this boundary; messages, paths, device names and usernames
        // all collapse to a generic code instead of being lossy-escaped and leaked.
        var normalized = errorCode.Trim().ToLowerInvariant();
        return normalized.Length <= MaximumErrorCodeLength &&
               AllowedErrorCodes.Contains(normalized)
            ? normalized
            : DefaultErrorCode;
    }

    internal static void ValidateDocumentShape(SilhouetteRenditionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Require(document.SchemaVersion == SilhouetteRenditionStore.CurrentSchemaVersion,
            "The silhouette rendition schema is not supported.");
        Require(IsProjectId(document.ProjectId), "The silhouette project id is invalid.");
        Require(document.Generation > 0, "The silhouette generation is invalid.");
        Require(document.CompositionRevision > 0,
            "The silhouette composition revision is invalid.");
        Require(IsSha256(document.CompositionSha256),
            "The silhouette composition hash is invalid.");
        Require(IsSafeModelId(document.ModelId), "The silhouette model id is invalid.");
        Require(IsSha256(document.ModelSha256), "The silhouette model hash is invalid.");
        _ = NormalizeFingerprint(document.GameplaySource, "gameplay source");
        _ = NormalizeFingerprint(document.CameraSource, "camera source");
        var matte = document.Matte ??
            throw new InvalidDataException("The shared matte state is missing.");
        var outputs = document.Outputs ??
            throw new InvalidDataException("The rendition list is missing.");
        Require(document.CreatedUtc.Offset == TimeSpan.Zero &&
                document.UpdatedUtc.Offset == TimeSpan.Zero &&
                document.UpdatedUtc >= document.CreatedUtc,
            "The silhouette document timestamps are invalid.");
        ValidateMatte(matte, document);

        Require(outputs.Count == CompositionOrientationIds.All.Count,
            "The rendition list does not contain both orientations.");
        Require(outputs.Select(output => output?.OrientationId)
                .SequenceEqual(CompositionOrientationIds.All),
            "The rendition list is not in canonical orientation order.");
        var supplied = new Dictionary<string, SilhouetteOutputRendition>(StringComparer.Ordinal);
        foreach (var candidate in outputs)
        {
            var output = candidate ??
                throw new InvalidDataException("A rendition orientation is missing.");
            Require(CompositionOrientationIds.All.Contains(output.OrientationId, StringComparer.Ordinal) &&
                    supplied.TryAdd(output.OrientationId, output),
                "A rendition orientation is missing, duplicated, or unsupported.");
            ValidateOutput(output, document);
        }
        Require(CompositionOrientationIds.All.All(supplied.ContainsKey),
            "A rendition orientation is missing.");
        Require(supplied.Values.Any(output => output.State != SilhouetteOutputState.Disabled),
            "At least one silhouette rendition must be enabled.");
    }

    private static SilhouetteMatteRendition ToPending(
        SilhouetteMatteRendition matte,
        DateTimeOffset now) => matte with
        {
            State = SilhouetteMatteState.Pending,
            AttemptGeneration = 0,
            StartedUtc = null,
            UpdatedUtc = now,
            ErrorCode = null,
            ExpectedTemporaryArtifact = null,
            FinalArtifact = null
        };

    private static SilhouetteOutputRendition ToPending(
        SilhouetteOutputRendition output,
        DateTimeOffset now) => output with
        {
            State = SilhouetteOutputState.Pending,
            AttemptGeneration = 0,
            MatteGeneration = 0,
            StartedUtc = null,
            UpdatedUtc = now,
            ErrorCode = null,
            ExpectedTemporaryArtifact = null,
            FinalArtifact = null
        };

    private static (SilhouetteOutputRendition[] Outputs, int Index, SilhouetteOutputRendition Output)
        FindOutput(SilhouetteRenditionDocument document, string orientationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orientationId);
        var outputs = document.Outputs.ToArray();
        var index = Array.FindIndex(outputs, candidate =>
            candidate.OrientationId.Equals(orientationId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new ArgumentException(
                "The silhouette rendition orientation is not supported.",
                nameof(orientationId));
        }
        return (outputs, index, outputs[index]);
    }

    private static SilhouetteRenditionDocument Next(
        SilhouetteRenditionDocument document,
        long generation,
        DateTimeOffset now,
        SilhouetteMatteRendition matte,
        IReadOnlyList<SilhouetteOutputRendition> outputs)
    {
        Require(now >= document.UpdatedUtc,
            "A silhouette transition timestamp cannot move backwards.");
        var next = document with
        {
            Generation = generation,
            Matte = matte,
            Outputs = outputs.ToArray(),
            UpdatedUtc = now
        };
        ValidateDocumentShape(next);
        return next;
    }

    private static long NextGeneration(SilhouetteRenditionDocument document)
    {
        if (document.Generation == long.MaxValue)
        {
            throw new InvalidOperationException("The silhouette generation cannot advance.");
        }
        return document.Generation + 1;
    }

    private static void ValidateForTransition(SilhouetteRenditionDocument document) =>
        ValidateDocumentShape(document);

    private static void ValidateMatte(
        SilhouetteMatteRendition matte,
        SilhouetteRenditionDocument document)
    {
        Require(Enum.IsDefined(matte.State) && matte.Attempts >= 0 &&
                matte.UpdatedUtc.Offset == TimeSpan.Zero &&
                matte.UpdatedUtc >= document.CreatedUtc &&
                matte.UpdatedUtc <= document.UpdatedUtc,
            "The shared matte state is invalid.");
        ValidateStatePayload(
            matte.State switch
            {
                SilhouetteMatteState.Pending => StatePayloadKind.Pending,
                SilhouetteMatteState.Processing => StatePayloadKind.Active,
                SilhouetteMatteState.Committing => StatePayloadKind.Committing,
                SilhouetteMatteState.Ready => StatePayloadKind.Ready,
                SilhouetteMatteState.Failed => StatePayloadKind.Failed,
                _ => throw new InvalidDataException("The shared matte state is invalid.")
            },
            matte.Attempts,
            matte.AttemptGeneration,
            matte.StartedUtc,
            matte.UpdatedUtc,
            matte.ErrorCode,
            matte.ExpectedTemporaryArtifact,
            matte.FinalArtifact,
            document.Generation,
            "shared matte");
    }

    private static void ValidateOutput(
        SilhouetteOutputRendition output,
        SilhouetteRenditionDocument document)
    {
        Require(Enum.IsDefined(output.State) && output.Attempts >= 0 &&
                output.UpdatedUtc.Offset == TimeSpan.Zero &&
                output.UpdatedUtc >= document.CreatedUtc &&
                output.UpdatedUtc <= document.UpdatedUtc,
            "A silhouette rendition state is invalid.");
        var kind = output.State switch
        {
            SilhouetteOutputState.Disabled => StatePayloadKind.Disabled,
            SilhouetteOutputState.Pending => StatePayloadKind.Pending,
            SilhouetteOutputState.Rendering => StatePayloadKind.Active,
            SilhouetteOutputState.Committing => StatePayloadKind.Committing,
            SilhouetteOutputState.Ready => StatePayloadKind.Ready,
            SilhouetteOutputState.Failed => StatePayloadKind.Failed,
            _ => throw new InvalidDataException("A silhouette rendition state is invalid.")
        };
        ValidateStatePayload(
            kind,
            output.Attempts,
            output.AttemptGeneration,
            output.StartedUtc,
            output.UpdatedUtc,
            output.ErrorCode,
            output.ExpectedTemporaryArtifact,
            output.FinalArtifact,
            document.Generation,
            "silhouette rendition");

        if (kind is StatePayloadKind.Disabled or StatePayloadKind.Pending)
        {
            Require(output.MatteGeneration == 0,
                "An inactive rendition cannot bind a matte generation.");
        }
        else
        {
            Require(output.MatteGeneration > 0 &&
                    output.MatteGeneration <= document.Generation &&
                    document.Matte.State == SilhouetteMatteState.Ready &&
                    output.MatteGeneration == document.Matte.AttemptGeneration,
                "A rendition must bind the ready shared matte generation.");
        }
    }

    private static void ValidateStatePayload(
        StatePayloadKind kind,
        int attempts,
        long attemptGeneration,
        DateTimeOffset? startedUtc,
        DateTimeOffset updatedUtc,
        string? errorCode,
        CaptureMediaFingerprint? expectedTemporaryArtifact,
        CaptureMediaFingerprint? finalArtifact,
        long documentGeneration,
        string description)
    {
        if (startedUtc is not null)
        {
            Require(startedUtc.Value.Offset == TimeSpan.Zero && startedUtc <= updatedUtc,
                $"The {description} start time is invalid.");
        }
        switch (kind)
        {
            case StatePayloadKind.Disabled:
                Require(attempts == 0 && attemptGeneration == 0 && startedUtc is null &&
                        errorCode is null && expectedTemporaryArtifact is null && finalArtifact is null,
                    $"The disabled {description} contains work state.");
                break;
            case StatePayloadKind.Pending:
                Require(attemptGeneration == 0 && startedUtc is null && errorCode is null &&
                        expectedTemporaryArtifact is null && finalArtifact is null,
                    $"The pending {description} contains committed work state.");
                break;
            case StatePayloadKind.Active:
                Require(attempts > 0 && attemptGeneration > 0 &&
                        attemptGeneration <= documentGeneration && startedUtc is not null &&
                        errorCode is null && expectedTemporaryArtifact is null && finalArtifact is null,
                    $"The active {description} state is invalid.");
                break;
            case StatePayloadKind.Committing:
                Require(attempts > 0 && attemptGeneration > 0 &&
                        attemptGeneration <= documentGeneration && startedUtc is not null &&
                        errorCode is null && expectedTemporaryArtifact is not null && finalArtifact is null,
                    $"The committing {description} state is invalid.");
                _ = NormalizeFingerprint(expectedTemporaryArtifact!, $"{description} temporary artifact");
                break;
            case StatePayloadKind.Ready:
                Require(attempts > 0 && attemptGeneration > 0 &&
                        attemptGeneration <= documentGeneration && startedUtc is not null &&
                        errorCode is null && expectedTemporaryArtifact is null && finalArtifact is not null,
                    $"The ready {description} state is invalid.");
                _ = NormalizeFingerprint(finalArtifact!, $"{description} final artifact");
                break;
            case StatePayloadKind.Failed:
                Require(attempts > 0 && attemptGeneration > 0 &&
                        attemptGeneration <= documentGeneration && startedUtc is not null &&
                        IsSanitizedErrorCode(errorCode) && expectedTemporaryArtifact is null &&
                        finalArtifact is null,
                    $"The failed {description} state is invalid.");
                break;
        }
    }

    private static bool IsSanitizedErrorCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumErrorCodeLength &&
        value.Equals(SanitizeErrorCode(value), StringComparison.Ordinal);

    internal static CaptureMediaFingerprint NormalizeFingerprint(
        CaptureMediaFingerprint fingerprint,
        string description)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        Require(fingerprint.ByteLength > 0 && IsSha256(fingerprint.Sha256),
            $"The {description} fingerprint is invalid.");
        return fingerprint with { Sha256 = fingerprint.Sha256.ToLowerInvariant() };
    }

    internal static bool FingerprintsEqual(
        CaptureMediaFingerprint? left,
        CaptureMediaFingerprint? right) =>
        left is not null && right is not null &&
        left.Sha256 is not null && right.Sha256 is not null &&
        left.ByteLength == right.ByteLength &&
        left.Sha256.Equals(right.Sha256, StringComparison.OrdinalIgnoreCase);

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    internal static bool IsSafeModelId(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '.' or '_' or '-');

    private static bool IsProjectId(string? value) =>
        value is { Length: 32 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime();

    private static void RequireCurrentAttempt(
        bool correctState,
        long currentAttemptGeneration,
        long suppliedAttemptGeneration,
        string description)
    {
        Require(correctState, $"The {description} is not in the required state.");
        Require(suppliedAttemptGeneration > 0 &&
                suppliedAttemptGeneration == currentAttemptGeneration,
            $"The {description} completion belongs to a stale attempt.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private enum StatePayloadKind
    {
        Disabled,
        Pending,
        Active,
        Committing,
        Ready,
        Failed
    }
}

internal enum SilhouetteRenditionLoadStatus
{
    Missing,
    Loaded,
    Corrupt,
    UnsupportedSchema,
    ProjectMismatch,
    CompositionMismatch,
    Invalid,
    Unavailable
}

internal sealed record SilhouetteRenditionLoadResult(
    SilhouetteRenditionDocument? Document,
    SilhouetteRenditionLoadStatus Status)
{
    internal bool LoadedFromDisk =>
        Status == SilhouetteRenditionLoadStatus.Loaded && Document is not null;
}

internal static class SilhouetteRenditionStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const string FileName = "renditions.json";
    internal const int MaximumDocumentBytes = 128 * 1024;
    private const string SaveMutexPrefix = @"Local\ClipCord.SilhouetteRenditions.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string GetPath(string libraryRoot, string projectId) =>
        Path.Combine(CaptureProjectStore.GetProjectDirectory(libraryRoot, projectId), FileName);

    internal static SilhouetteRenditionDocument CreateInitial(
        string libraryRoot,
        string projectId,
        string modelId,
        string modelSha256,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = ValidateProject(libraryRoot, projectId, cancellationToken);
        if (!SilhouetteRenditionModel.IsSafeModelId(modelId))
        {
            throw new ArgumentException("The silhouette model id is invalid.", nameof(modelId));
        }
        if (!SilhouetteRenditionModel.IsSha256(modelSha256))
        {
            throw new ArgumentException("The silhouette model hash is invalid.", nameof(modelSha256));
        }
        var utcNow = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var outputs = CompositionOrientationIds.All.Select(orientationId =>
        {
            var enabled = project.EnabledOrientations.Contains(orientationId);
            return new SilhouetteOutputRendition(
                orientationId,
                enabled ? SilhouetteOutputState.Pending : SilhouetteOutputState.Disabled,
                Attempts: 0,
                AttemptGeneration: 0,
                MatteGeneration: 0,
                StartedUtc: null,
                UpdatedUtc: utcNow,
                ErrorCode: null,
                ExpectedTemporaryArtifact: null,
                FinalArtifact: null);
        }).ToArray();
        var document = new SilhouetteRenditionDocument(
            CurrentSchemaVersion,
            projectId,
            Generation: 1,
            project.CompositionRevision,
            project.CompositionSha256,
            modelId,
            modelSha256.ToLowerInvariant(),
            project.Manifest.GameplayFingerprint with
            {
                Sha256 = project.Manifest.GameplayFingerprint.Sha256.ToLowerInvariant()
            },
            project.Manifest.CameraLayerFingerprint with
            {
                Sha256 = project.Manifest.CameraLayerFingerprint.Sha256.ToLowerInvariant()
            },
            new SilhouetteMatteRendition(
                SilhouetteMatteState.Pending,
                Attempts: 0,
                AttemptGeneration: 0,
                StartedUtc: null,
                UpdatedUtc: utcNow,
                ErrorCode: null,
                ExpectedTemporaryArtifact: null,
                FinalArtifact: null),
            outputs,
            CreatedUtc: utcNow,
            UpdatedUtc: utcNow);
        ValidateAgainstProject(document, project);
        return document;
    }

    internal static SilhouetteRenditionLoadResult Load(
        string libraryRoot,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatedRenditionProject project;
        try
        {
            project = ValidateProject(libraryRoot, projectId, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return new SilhouetteRenditionLoadResult(
                null,
                SilhouetteRenditionLoadStatus.Invalid);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SilhouetteRenditionLoadResult(
                null,
                SilhouetteRenditionLoadStatus.Unavailable);
        }

        return LoadValidatedProject(project, projectId, cancellationToken);
    }

    private static SilhouetteRenditionLoadResult LoadValidatedProject(
        ValidatedRenditionProject project,
        string projectId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(project.Directory, FileName);
        if (!File.Exists(path))
        {
            return new SilhouetteRenditionLoadResult(
                null,
                SilhouetteRenditionLoadStatus.Missing);
        }
        try
        {
            EnsureOrdinaryExistingPath(project.Root, path, requireDirectory: false,
                "silhouette rendition state");
            var json = CaptureProjectStore.ReadBoundedUtf8File(
                path,
                MaximumDocumentBytes,
                "silhouette rendition state");
            int schemaVersion;
            using (var parsed = JsonDocument.Parse(json))
            {
                if (!parsed.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                    !schema.TryGetInt32(out schemaVersion))
                {
                    return new SilhouetteRenditionLoadResult(
                        null,
                        SilhouetteRenditionLoadStatus.Invalid);
                }
                if (schemaVersion != CurrentSchemaVersion)
                {
                    return new SilhouetteRenditionLoadResult(
                        null,
                        SilhouetteRenditionLoadStatus.UnsupportedSchema);
                }
            }

            var document = JsonSerializer.Deserialize<SilhouetteRenditionDocument>(
                json,
                JsonOptions);
            if (document is null)
            {
                return new SilhouetteRenditionLoadResult(
                    null,
                    SilhouetteRenditionLoadStatus.Corrupt);
            }
            // Validate the complete typed shape before dereferencing any deserialized field.
            // Nullable annotations do not stop hostile JSON from supplying null for a required
            // string, fingerprint member, or orientation id.
            SilhouetteRenditionModel.ValidateDocumentShape(document);
            if (!string.Equals(document.ProjectId, projectId, StringComparison.Ordinal) ||
                !SilhouetteRenditionModel.FingerprintsEqual(
                    document.GameplaySource,
                    project.Manifest.GameplayFingerprint) ||
                !SilhouetteRenditionModel.FingerprintsEqual(
                    document.CameraSource,
                    project.Manifest.CameraLayerFingerprint))
            {
                return new SilhouetteRenditionLoadResult(
                    null,
                    SilhouetteRenditionLoadStatus.ProjectMismatch);
            }
            if (document.CompositionRevision != project.CompositionRevision ||
                !string.Equals(
                    document.CompositionSha256,
                    project.CompositionSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !OutputsMatchComposition(document.Outputs, project.EnabledOrientations))
            {
                return new SilhouetteRenditionLoadResult(
                    null,
                    SilhouetteRenditionLoadStatus.CompositionMismatch);
            }
            return new SilhouetteRenditionLoadResult(
                document,
                SilhouetteRenditionLoadStatus.Loaded);
        }
        catch (JsonException)
        {
            return new SilhouetteRenditionLoadResult(
                null,
                SilhouetteRenditionLoadStatus.Corrupt);
        }
        catch (InvalidDataException)
        {
            return new SilhouetteRenditionLoadResult(
                null,
                SilhouetteRenditionLoadStatus.Invalid);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new SilhouetteRenditionLoadResult(
                null,
                SilhouetteRenditionLoadStatus.Unavailable);
        }
    }

    internal static Task<SilhouetteRenditionDocument> SaveAsync(
        string libraryRoot,
        string projectId,
        SilhouetteRenditionDocument document,
        long expectedGeneration,
        CancellationToken cancellationToken = default,
        Action? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (expectedGeneration < 0) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => SaveCore(
                libraryRoot,
                projectId,
                document,
                expectedGeneration,
                cancellationToken,
                beforeCommit),
            cancellationToken);
    }

    private static SilhouetteRenditionDocument SaveCore(
        string libraryRoot,
        string projectId,
        SilhouetteRenditionDocument document,
        long expectedGeneration,
        CancellationToken cancellationToken,
        Action? beforeCommit)
    {
        var project = ValidateProject(libraryRoot, projectId, cancellationToken);
        ValidateAgainstProject(document, project);
        if (document.Generation != checked(expectedGeneration + 1))
        {
            throw new SilhouetteRenditionConcurrencyException(
                "The silhouette state does not advance the expected generation.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        using var saveMutex = new Mutex(
            initiallyOwned: false,
            GetSaveMutexName(projectId));
        var lockTaken = false;
        try
        {
            while (!lockTaken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    lockTaken = saveMutex.WaitOne(TimeSpan.FromMilliseconds(100));
                }
                catch (AbandonedMutexException)
                {
                    lockTaken = true;
                }
            }

            var path = Path.Combine(project.Directory, FileName);
            var replacingExisting = File.Exists(path);
            if (!replacingExisting)
            {
                if (expectedGeneration != 0)
                {
                    throw new SilhouetteRenditionConcurrencyException(
                        "The silhouette state disappeared before it could be updated.");
                }
                ValidateInitialPersistence(document);
            }
            else
            {
                EnsureOrdinaryExistingPath(project.Root, path, requireDirectory: false,
                    "silhouette rendition state");
                // Reuse the project/source validation already completed for this save. Calling
                // public Load here would hash both media sources a second time under every CAS.
                var current = LoadValidatedProject(project, projectId, cancellationToken);
                if (!current.LoadedFromDisk || current.Document is null)
                {
                    throw new InvalidDataException(
                        "The existing silhouette state cannot be replaced safely.");
                }
                if (current.Document.Generation != expectedGeneration)
                {
                    throw new SilhouetteRenditionConcurrencyException(
                        "A newer silhouette state has already been saved.");
                }
                ValidateLegalSuccessor(current.Document, document);
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (bytes.Length <= 0 || bytes.Length > MaximumDocumentBytes)
            {
                throw new InvalidDataException("The silhouette rendition state is too large.");
            }
            var temporaryPath = Path.Combine(
                project.Directory,
                $".{FileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                beforeCommit?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, overwrite: replacingExisting);
                return document;
            }
            finally
            {
                TryDeleteOwnedTemporaryFile(project.Directory, temporaryPath);
            }
        }
        finally
        {
            if (lockTaken) saveMutex.ReleaseMutex();
        }
    }

    internal static string GetSaveMutexName(string projectId) =>
        SaveMutexPrefix + projectId;

    private static void ValidateInitialPersistence(SilhouetteRenditionDocument document)
    {
        var validMatte = document.Matte.State == SilhouetteMatteState.Pending &&
                         document.Matte.Attempts == 0 &&
                         document.Matte.AttemptGeneration == 0 &&
                         document.Matte.StartedUtc is null &&
                         document.Matte.UpdatedUtc == document.CreatedUtc &&
                         document.Matte.ErrorCode is null &&
                         document.Matte.ExpectedTemporaryArtifact is null &&
                         document.Matte.FinalArtifact is null;
        var validOutputs = document.Outputs.All(output =>
            output.State is SilhouetteOutputState.Pending or SilhouetteOutputState.Disabled &&
            output.Attempts == 0 &&
            output.AttemptGeneration == 0 &&
            output.MatteGeneration == 0 &&
            output.StartedUtc is null &&
            output.UpdatedUtc == document.CreatedUtc &&
            output.ErrorCode is null &&
            output.ExpectedTemporaryArtifact is null &&
            output.FinalArtifact is null);
        if (document.Generation != 1 || document.UpdatedUtc != document.CreatedUtc ||
            !validMatte || !validOutputs)
        {
            throw new InvalidDataException(
                "The first persisted silhouette state must be the canonical pending document.");
        }
    }

    /// <summary>
    /// The state records are internal but still cross process/crash boundaries. Generation CAS
    /// prevents lost updates; this gate additionally proves that the candidate is exactly one
    /// transition produced by the reviewed model and cannot skip work, reset attempt history, or
    /// swap immutable model/source identity.
    /// </summary>
    private static void ValidateLegalSuccessor(
        SilhouetteRenditionDocument current,
        SilhouetteRenditionDocument candidate)
    {
        if (!HasSameImmutableIdentity(current, candidate))
        {
            throw new InvalidDataException(
                "A silhouette transition cannot change immutable project or model identity.");
        }

        var now = candidate.UpdatedUtc;
        var legal = false;
        void Match(Func<SilhouetteRenditionDocument> transition)
        {
            if (legal) return;
            try
            {
                legal = DocumentsEqual(transition(), candidate);
            }
            catch (InvalidDataException)
            {
                // The candidate does not satisfy this transition's preconditions. Other legal
                // transitions are still considered below.
            }
        }

        switch (current.Matte.State)
        {
            case SilhouetteMatteState.Pending:
                Match(() => SilhouetteRenditionModel.StartMatte(current, now));
                break;
            case SilhouetteMatteState.Processing:
                if (candidate.Matte.ExpectedTemporaryArtifact is { } matteTemporary)
                {
                    Match(() => SilhouetteRenditionModel.BeginMatteCommit(
                        current,
                        current.Matte.AttemptGeneration,
                        matteTemporary,
                        now));
                }
                Match(() => SilhouetteRenditionModel.FailMatte(
                    current,
                    current.Matte.AttemptGeneration,
                    candidate.Matte.ErrorCode,
                    now));
                break;
            case SilhouetteMatteState.Committing:
                if (candidate.Matte.FinalArtifact is { } matteFinal)
                {
                    Match(() => SilhouetteRenditionModel.CompleteMatte(
                        current,
                        current.Matte.AttemptGeneration,
                        matteFinal,
                        now));
                    Match(() => SilhouetteRenditionModel.RecoverMatteCommit(
                        current,
                        current.Matte.AttemptGeneration,
                        matteFinal,
                        now));
                }
                Match(() => SilhouetteRenditionModel.FailMatte(
                    current,
                    current.Matte.AttemptGeneration,
                    candidate.Matte.ErrorCode,
                    now));
                Match(() => SilhouetteRenditionModel.RecoverMatteCommit(
                    current,
                    current.Matte.AttemptGeneration,
                    verifiedFinalArtifact: null,
                    now));
                break;
            case SilhouetteMatteState.Failed:
                Match(() => SilhouetteRenditionModel.RetryMatte(current, now));
                break;
        }

        foreach (var output in current.Outputs)
        {
            switch (output.State)
            {
                case SilhouetteOutputState.Pending:
                    Match(() => SilhouetteRenditionModel.StartOutput(
                        current,
                        output.OrientationId,
                        now));
                    break;
                case SilhouetteOutputState.Rendering:
                    var candidateRenderingOutput = candidate.Outputs.Single(candidateOutput =>
                        candidateOutput.OrientationId.Equals(
                            output.OrientationId,
                            StringComparison.Ordinal));
                    if (candidateRenderingOutput.ExpectedTemporaryArtifact is { } outputTemporary)
                    {
                        Match(() => SilhouetteRenditionModel.BeginOutputCommit(
                            current,
                            output.OrientationId,
                            output.AttemptGeneration,
                            outputTemporary,
                            now));
                    }
                    Match(() => SilhouetteRenditionModel.FailOutput(
                        current,
                        output.OrientationId,
                        output.AttemptGeneration,
                        candidateRenderingOutput.ErrorCode,
                        now));
                    break;
                case SilhouetteOutputState.Committing:
                    var candidateCommittingOutput = candidate.Outputs.Single(candidateOutput =>
                        candidateOutput.OrientationId.Equals(
                            output.OrientationId,
                            StringComparison.Ordinal));
                    if (candidateCommittingOutput.FinalArtifact is { } outputFinal)
                    {
                        Match(() => SilhouetteRenditionModel.CompleteOutput(
                            current,
                            output.OrientationId,
                            output.AttemptGeneration,
                            outputFinal,
                            now));
                        Match(() => SilhouetteRenditionModel.RecoverOutputCommit(
                            current,
                            output.OrientationId,
                            output.AttemptGeneration,
                            outputFinal,
                            now));
                    }
                    Match(() => SilhouetteRenditionModel.FailOutput(
                        current,
                        output.OrientationId,
                        output.AttemptGeneration,
                        candidateCommittingOutput.ErrorCode,
                        now));
                    Match(() => SilhouetteRenditionModel.RecoverOutputCommit(
                        current,
                        output.OrientationId,
                        output.AttemptGeneration,
                        verifiedFinalArtifact: null,
                        now));
                    break;
                case SilhouetteOutputState.Ready:
                    Match(() => SilhouetteRenditionModel.InvalidateReadyOutput(
                        current,
                        output.OrientationId,
                        observedArtifact: null,
                        now));
                    break;
                case SilhouetteOutputState.Failed:
                    Match(() => SilhouetteRenditionModel.RetryOutput(
                        current,
                        output.OrientationId,
                        now));
                    break;
            }
        }

        // Recovery may reset a processing matte and multiple rendering outputs together, so it
        // is the one reviewed transition that can alter more than one work item at once.
        Match(() => SilhouetteRenditionModel.RecoverAbandonedWork(current, now));
        if (!legal)
        {
            throw new InvalidDataException(
                "The silhouette state is not a legal successor of the persisted generation.");
        }
    }

    private static bool HasSameImmutableIdentity(
        SilhouetteRenditionDocument left,
        SilhouetteRenditionDocument right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.ProjectId.Equals(right.ProjectId, StringComparison.Ordinal) &&
        left.CompositionRevision == right.CompositionRevision &&
        left.CompositionSha256.Equals(right.CompositionSha256, StringComparison.Ordinal) &&
        left.ModelId.Equals(right.ModelId, StringComparison.Ordinal) &&
        left.ModelSha256.Equals(right.ModelSha256, StringComparison.Ordinal) &&
        left.GameplaySource == right.GameplaySource &&
        left.CameraSource == right.CameraSource &&
        left.CreatedUtc == right.CreatedUtc;

    private static bool DocumentsEqual(
        SilhouetteRenditionDocument left,
        SilhouetteRenditionDocument right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.ProjectId == right.ProjectId &&
        left.Generation == right.Generation &&
        left.CompositionRevision == right.CompositionRevision &&
        left.CompositionSha256 == right.CompositionSha256 &&
        left.ModelId == right.ModelId &&
        left.ModelSha256 == right.ModelSha256 &&
        left.GameplaySource == right.GameplaySource &&
        left.CameraSource == right.CameraSource &&
        left.Matte == right.Matte &&
        left.Outputs.SequenceEqual(right.Outputs) &&
        left.CreatedUtc == right.CreatedUtc &&
        left.UpdatedUtc == right.UpdatedUtc;

    private static ValidatedRenditionProject ValidateProject(
        string libraryRoot,
        string projectId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot.Trim()));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The ClipCord library folder does not exist.");
        }
        EnsureOrdinaryExistingPath(root, root, requireDirectory: true, "ClipCord library folder");
        var projectDirectory = Path.GetFullPath(
            CaptureProjectStore.GetProjectDirectory(root, projectId));
        EnsurePathIsInside(root, projectDirectory, "capture project folder");
        EnsureOrdinaryExistingPath(
            root,
            projectDirectory,
            requireDirectory: true,
            "capture project folder");

        var manifestPath = CaptureProjectStore.GetManifestPath(root, projectId);
        EnsureOrdinaryExistingPath(
            root,
            manifestPath,
            requireDirectory: false,
            "capture project manifest");
        CaptureProjectManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CaptureProjectManifest>(
                           CaptureProjectStore.ReadBoundedManifestFile(manifestPath),
                           ManifestJsonOptions)
                       ?? throw new InvalidDataException("The capture project manifest is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The capture project manifest is invalid.", exception);
        }
        _ = CaptureProjectStore.ValidateCommittedMediaIdentity(
            root,
            projectId,
            manifest,
            cancellationToken);

        var compositionPath = CaptureCompositionStore.GetPath(root, projectId);
        EnsureOrdinaryExistingPath(
            root,
            compositionPath,
            requireDirectory: false,
            "capture composition snapshot");
        var compositionJson = CaptureProjectStore.ReadBoundedUtf8File(
            compositionPath,
            CaptureCompositionStore.MaximumDocumentBytes,
            "capture composition snapshot");
        var compositionLoad = CaptureCompositionStore.LoadOrDefault(root, projectId);
        if (!compositionLoad.LoadedFromDisk)
        {
            throw new InvalidDataException("The capture composition snapshot is invalid.");
        }
        var composition = compositionLoad.Document;
        var enabled = composition.SilhouetteLayouts
            .Where(layout => layout.Enabled)
            .Select(layout => layout.ProfileId)
            .ToHashSet(StringComparer.Ordinal);
        if (enabled.Count == 0 ||
            enabled.Any(id => !CompositionOrientationIds.All.Contains(id, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("The capture composition output selection is invalid.");
        }
        var compositionHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(compositionJson)))
            .ToLowerInvariant();
        return new ValidatedRenditionProject(
            root,
            projectDirectory,
            manifest,
            enabled,
            composition.ModifiedUtc.UtcDateTime.Ticks,
            compositionHash);
    }

    private static void ValidateAgainstProject(
        SilhouetteRenditionDocument document,
        ValidatedRenditionProject project)
    {
        SilhouetteRenditionModel.ValidateDocumentShape(document);
        if (!document.ProjectId.Equals(project.Manifest.ProjectId, StringComparison.Ordinal) ||
            document.CompositionRevision != project.CompositionRevision ||
            !document.CompositionSha256.Equals(project.CompositionSha256, StringComparison.OrdinalIgnoreCase) ||
            !SilhouetteRenditionModel.FingerprintsEqual(
                document.GameplaySource,
                project.Manifest.GameplayFingerprint) ||
            !SilhouetteRenditionModel.FingerprintsEqual(
                document.CameraSource,
                project.Manifest.CameraLayerFingerprint) ||
            !OutputsMatchComposition(document.Outputs, project.EnabledOrientations))
        {
            throw new InvalidDataException(
                "The silhouette state does not match its immutable capture project snapshot.");
        }
    }

    private static bool OutputsMatchComposition(
        IReadOnlyList<SilhouetteOutputRendition>? outputs,
        IReadOnlySet<string> enabledOrientations)
    {
        if (outputs is null || outputs.Count != CompositionOrientationIds.All.Count) return false;
        foreach (var orientationId in CompositionOrientationIds.All)
        {
            var matches = outputs.Where(output =>
                    output is not null &&
                    string.Equals(
                        output.OrientationId,
                        orientationId,
                        StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1) return false;
            var expectedEnabled = enabledOrientations.Contains(orientationId);
            var actuallyEnabled = matches[0].State != SilhouetteOutputState.Disabled;
            if (expectedEnabled != actuallyEnabled) return false;
        }
        return true;
    }

    private static void EnsureOrdinaryExistingPath(
        string allowedRoot,
        string candidate,
        bool requireDirectory,
        string description)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (!normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            EnsurePathIsInside(normalizedRoot, normalizedCandidate, description);
        }
        EnsureNotReparsePoint(normalizedRoot, description);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        var current = normalizedRoot;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                throw new IOException($"The {description} does not exist.");
            }
            EnsureNotReparsePoint(current, description);
        }
        if (requireDirectory ? !Directory.Exists(normalizedCandidate) : !File.Exists(normalizedCandidate))
        {
            throw new IOException($"The {description} has an unexpected file-system type.");
        }
    }

    private static void EnsurePathIsInside(string allowedRoot, string candidate, string description)
    {
        var relative = Path.GetRelativePath(allowedRoot, candidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"The {description} must remain inside ClipCord's library.");
        }
    }

    private static void EnsureNotReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The {description} cannot be a symbolic link or junction.");
        }
    }

    private static void TryDeleteOwnedTemporaryFile(
        string projectDirectory,
        string temporaryPath)
    {
        try
        {
            var file = new FileInfo(temporaryPath);
            if (!file.Exists || file.Directory is null ||
                !file.Directory.FullName.Equals(projectDirectory, StringComparison.OrdinalIgnoreCase) ||
                !file.Name.StartsWith($".{FileName}.", StringComparison.Ordinal) ||
                !file.Name.EndsWith(".tmp", StringComparison.Ordinal) ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }
            file.Delete();
        }
        catch
        {
            // Cleanup is restricted to the one exact owned temporary file. No directory is
            // ever recursively traversed or removed by this state store.
        }
    }

    private sealed record ValidatedRenditionProject(
        string Root,
        string Directory,
        CaptureProjectManifest Manifest,
        IReadOnlySet<string> EnabledOrientations,
        long CompositionRevision,
        string CompositionSha256);
}
