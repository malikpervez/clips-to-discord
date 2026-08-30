namespace ClipsToDiscord;

internal enum RoutingDeliveryClaimState
{
    Claimed,
    Confirmed,
    Unknown,
    Released
}

internal sealed record RoutingDeliveryClaimKey(
    RoutingDestinationKind Destination,
    string ConnectionId,
    string ArtifactSha256,
    string ClaimSlot);

internal sealed record RoutingDeliveryClaim(
    RoutingDeliveryClaimKey Key,
    long Epoch,
    Guid ClaimId,
    Guid OwnerDeliveryId,
    string OwnerSourceClipId,
    Guid OwnerAttemptId,
    RoutingDeliveryClaimState State,
    string? RemoteReceiptReference,
    string? ErrorCode,
    DateTimeOffset? DuplicateRiskAcceptedUtc,
    DateTimeOffset ClaimedUtc,
    DateTimeOffset UpdatedUtc);

internal sealed record RoutingDeliveryReceiptDocument(
    int SchemaVersion,
    long Generation,
    IReadOnlyList<RoutingDeliveryClaim> Claims,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public bool Equals(RoutingDeliveryReceiptDocument? other) =>
        ReferenceEquals(this, other) ||
        other is not null && SchemaVersion == other.SchemaVersion &&
        Generation == other.Generation && CreatedUtc == other.CreatedUtc &&
        UpdatedUtc == other.UpdatedUtc &&
        RoutingStructural.SequenceEqual(Claims, other.Claims);

    public override int GetHashCode() => RoutingStructural.Hash(
        SchemaVersion, Generation, Claims, CreatedUtc, UpdatedUtc);
}

internal static class RoutingDeliveryReceiptModel
{
    internal const int MaximumClaims = 10_000;

    internal static RoutingDeliveryReceiptDocument CreateEmpty(DateTimeOffset now)
    {
        var timestamp = RoutingValidation.Utc(now);
        return new RoutingDeliveryReceiptDocument(
            RoutingDeliveryReceiptStore.CurrentSchemaVersion,
            Generation: 1,
            Claims: [],
            CreatedUtc: timestamp,
            UpdatedUtc: timestamp);
    }

    internal static RoutingDeliveryReceiptDocument BeginClaim(
        RoutingDeliveryReceiptDocument current,
        RoutingDeliveryClaimKey key,
        Guid claimId,
        Guid ownerDeliveryId,
        string ownerSourceClipId,
        Guid ownerAttemptId,
        DateTimeOffset? duplicateRiskAcceptedUtc,
        DateTimeOffset now)
    {
        Validate(current);
        ValidateKey(key);
        RoutingValidation.Require(claimId != Guid.Empty && ownerDeliveryId != Guid.Empty &&
                                  ownerAttemptId != Guid.Empty,
            "A routing content-delivery claim identity is missing.");
        RoutingValidation.RequireOpaqueId(ownerSourceClipId, 256,
            "routing content-delivery source id");
        var existing = current.Claims.SingleOrDefault(claim => claim.Key == key);
        var replacingAmbiguous = existing is
            { State: RoutingDeliveryClaimState.Claimed or RoutingDeliveryClaimState.Unknown };
        var riskAcceptedUtc = duplicateRiskAcceptedUtc is null
            ? (DateTimeOffset?)null
            : RoutingValidation.Utc(duplicateRiskAcceptedUtc.Value);
        RoutingValidation.Require(existing is null ||
                                  existing.State == RoutingDeliveryClaimState.Released ||
                                  replacingAmbiguous &&
                                  existing!.OwnerDeliveryId == ownerDeliveryId &&
                                  existing.OwnerSourceClipId.Equals(
                                      ownerSourceClipId, StringComparison.Ordinal) &&
                                  HasFreshDuplicateRiskAcceptance(existing, riskAcceptedUtc),
            "A live or confirmed content-delivery claim cannot be replaced.");
        var timestamp = MaxUtc(current.UpdatedUtc, now);
        if (riskAcceptedUtc is { } acceptedUtc && acceptedUtc > timestamp)
        {
            timestamp = acceptedUtc;
        }
        var claim = new RoutingDeliveryClaim(
            key,
            existing is null ? 1 : checked(existing.Epoch + 1),
            claimId,
            ownerDeliveryId,
            ownerSourceClipId,
            ownerAttemptId,
            RoutingDeliveryClaimState.Claimed,
            RemoteReceiptReference: null,
            ErrorCode: null,
            DuplicateRiskAcceptedUtc: replacingAmbiguous ? riskAcceptedUtc : null,
            ClaimedUtc: timestamp,
            UpdatedUtc: timestamp);
        var claims = existing is null
            ? current.Claims.Append(claim).ToArray()
            : current.Claims.Select(item => item.Key == key ? claim : item).ToArray();
        var candidate = current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            Claims = claims,
            UpdatedUtc = timestamp
        };
        ValidateSuccessor(current, candidate);
        return candidate;
    }

    internal static RoutingDeliveryReceiptDocument CompleteClaim(
        RoutingDeliveryReceiptDocument current,
        RoutingDeliveryClaimKey key,
        Guid claimId,
        Guid ownerAttemptId,
        RoutingDeliveryAttemptResult outcome,
        DateTimeOffset now)
    {
        Validate(current);
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(outcome);
        ValidateOutcome(outcome);
        var existing = current.Claims.SingleOrDefault(claim => claim.Key == key) ??
                       throw new InvalidDataException(
                           "The content-delivery claim disappeared before completion.");
        RoutingValidation.Require(existing.ClaimId == claimId &&
                                  existing.OwnerAttemptId == ownerAttemptId &&
                                  existing.State == RoutingDeliveryClaimState.Claimed,
            "The content-delivery result does not own the current claim fence.");
        var timestamp = MaxUtc(current.UpdatedUtc, now);
        var completed = outcome.Outcome switch
        {
            RoutingDeliveryAttemptOutcome.Confirmed => existing with
            {
                State = RoutingDeliveryClaimState.Confirmed,
                RemoteReceiptReference = RequireReceipt(outcome.RemoteReceiptReference),
                ErrorCode = null,
                UpdatedUtc = timestamp
            },
            RoutingDeliveryAttemptOutcome.Failed => existing with
            {
                State = RoutingDeliveryClaimState.Released,
                RemoteReceiptReference = null,
                ErrorCode = RequireError(outcome.ErrorCode),
                UpdatedUtc = timestamp
            },
            RoutingDeliveryAttemptOutcome.Unknown => existing with
            {
                State = RoutingDeliveryClaimState.Unknown,
                RemoteReceiptReference = null,
                ErrorCode = RequireError(outcome.ErrorCode),
                UpdatedUtc = timestamp
            },
            _ => throw new InvalidDataException(
                "The content-delivery provider outcome is unsupported.")
        };
        var candidate = current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            Claims = current.Claims.Select(item => item.Key == key ? completed : item).ToArray(),
            UpdatedUtc = timestamp
        };
        ValidateSuccessor(current, candidate);
        return candidate;
    }

    internal static bool MatchesCompletion(
        RoutingDeliveryClaim claim,
        RoutingDeliveryAttemptResult outcome)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(outcome);
        ValidateClaim(claim);
        ValidateOutcome(outcome);
        return outcome.Outcome switch
        {
            RoutingDeliveryAttemptOutcome.Confirmed =>
                claim.State == RoutingDeliveryClaimState.Confirmed &&
                claim.RemoteReceiptReference == outcome.RemoteReceiptReference,
            RoutingDeliveryAttemptOutcome.Failed =>
                claim.State == RoutingDeliveryClaimState.Released &&
                claim.ErrorCode == outcome.ErrorCode,
            RoutingDeliveryAttemptOutcome.Unknown =>
                claim.State == RoutingDeliveryClaimState.Unknown &&
                claim.ErrorCode == outcome.ErrorCode,
            _ => false
        };
    }

    internal static bool HasFreshDuplicateRiskAcceptance(
        RoutingDeliveryClaim existing,
        DateTimeOffset? acceptedUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (acceptedUtc is not { } accepted) return false;
        RoutingValidation.RequireUtc(accepted,
            "routing duplicate-risk confirmation timestamp");
        return accepted >= existing.UpdatedUtc &&
               (existing.DuplicateRiskAcceptedUtc is null ||
                accepted > existing.DuplicateRiskAcceptedUtc.Value);
    }

    internal static void Validate(RoutingDeliveryReceiptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(
            document.SchemaVersion == RoutingDeliveryReceiptStore.CurrentSchemaVersion &&
            document.Generation > 0,
            "The routing delivery-receipt schema or generation is invalid.");
        var claims = document.Claims ?? throw new InvalidDataException(
            "Routing delivery claims are missing.");
        RoutingValidation.Require(claims.Count <= MaximumClaims &&
                                  claims.Select(claim => claim.Key).Distinct().Count() == claims.Count,
            "Routing delivery claims exceed capacity or contain duplicate keys.");
        RoutingValidation.RequireUtc(document.CreatedUtc,
            "routing delivery-receipt creation timestamp");
        RoutingValidation.RequireUtc(document.UpdatedUtc,
            "routing delivery-receipt update timestamp");
        RoutingValidation.Require(document.UpdatedUtc >= document.CreatedUtc,
            "Routing delivery-receipt timestamps are inconsistent.");
        foreach (var claim in claims) ValidateClaim(claim);
    }

    internal static void ValidateSuccessor(
        RoutingDeliveryReceiptDocument current,
        RoutingDeliveryReceiptDocument candidate)
    {
        Validate(current);
        Validate(candidate);
        RoutingValidation.Require(candidate.Generation == current.Generation + 1 &&
                                  candidate.CreatedUtc == current.CreatedUtc &&
                                  candidate.UpdatedUtc >= current.UpdatedUtc &&
                                  candidate.Claims.Count <= current.Claims.Count + 1,
            "The routing delivery-receipt successor has an invalid envelope.");
        var currentByKey = current.Claims.ToDictionary(claim => claim.Key);
        var candidateByKey = candidate.Claims.ToDictionary(claim => claim.Key);
        var changedClaims = candidate.Claims.Count(next =>
            !currentByKey.TryGetValue(next.Key, out var previous) || previous != next);
        RoutingValidation.Require(changedClaims == 1,
            "A routing delivery-receipt successor must change exactly one claim.");
        foreach (var added in candidate.Claims.Where(claim => !currentByKey.ContainsKey(claim.Key)))
        {
            RoutingValidation.Require(
                added.Epoch == 1 && added.State == RoutingDeliveryClaimState.Claimed &&
                added.RemoteReceiptReference is null && added.ErrorCode is null &&
                added.DuplicateRiskAcceptedUtc is null,
                "A new durable content-delivery claim must begin as an unconfirmed first claim.");
        }
        foreach (var previous in current.Claims)
        {
            RoutingValidation.Require(candidateByKey.TryGetValue(previous.Key, out var next),
                "A durable content-delivery claim disappeared.");
            if (previous == next) continue;
            RoutingValidation.Require(
                previous.State switch
                {
                    RoutingDeliveryClaimState.Claimed =>
                        IsCompletion(previous, next!) || IsAmbiguousReclaim(previous, next!),
                    RoutingDeliveryClaimState.Unknown => IsAmbiguousReclaim(previous, next!),
                    RoutingDeliveryClaimState.Released => IsReleasedReclaim(previous, next!),
                    RoutingDeliveryClaimState.Confirmed => false,
                    _ => false
                },
                "A durable content-delivery claim changed outside its legal state machine.");
        }
    }

    internal static RoutingDeliveryClaimKey CreateKey(
        PlannedDelivery delivery,
        RoutingResolvedArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(artifact);
        var slot = "default";
        if (delivery.IntentionalDuplicate is { } intentional)
        {
            RoutingValidation.Require(
                intentional.GroupId != Guid.Empty &&
                intentional.Decision == IntentionalDuplicateDecision.UserConfirmedDeliverTwice &&
                intentional.FirstRouteId != Guid.Empty &&
                intentional.SecondRouteId != Guid.Empty &&
                intentional.FirstRouteId != intentional.SecondRouteId &&
                intentional.DeliveryKey == delivery.DeliveryKey,
                "Intentional duplicate claim separation is not bound to this delivery.");
            RoutingValidation.RequireUtc(intentional.ConfirmedUtc,
                "intentional duplicate claim confirmation timestamp");
            var member = delivery.Route.RouteId == intentional.FirstRouteId
                ? "first"
                : delivery.Route.RouteId == intentional.SecondRouteId
                    ? "second"
                    : throw new InvalidDataException(
                        "An intentional duplicate delivery is outside its approved pair.");
            slot = $"intentional.{intentional.GroupId:N}.{member}";
        }
        var key = new RoutingDeliveryClaimKey(
            delivery.Destination,
            delivery.ConnectionId,
            artifact.Sha256.ToLowerInvariant(),
            slot);
        ValidateKey(key);
        return key;
    }

    private static void ValidateClaim(RoutingDeliveryClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ValidateKey(claim.Key);
        RoutingValidation.Require(claim.Epoch > 0 && claim.ClaimId != Guid.Empty &&
                                  claim.OwnerDeliveryId != Guid.Empty &&
                                  claim.OwnerAttemptId != Guid.Empty &&
                                  Enum.IsDefined(claim.State),
            "A routing content-delivery claim identity or state is invalid.");
        RoutingValidation.RequireOpaqueId(claim.OwnerSourceClipId, 256,
            "routing content-delivery source id");
        RoutingValidation.RequireOptionalOpaqueId(claim.RemoteReceiptReference, 256,
            "routing content-delivery remote receipt");
        if (claim.ErrorCode is not null) RoutingValidation.RequireErrorCode(claim.ErrorCode);
        RoutingValidation.RequireOptionalUtc(claim.DuplicateRiskAcceptedUtc,
            "routing duplicate-risk confirmation timestamp");
        RoutingValidation.RequireUtc(claim.ClaimedUtc,
            "routing content-delivery claim timestamp");
        RoutingValidation.RequireUtc(claim.UpdatedUtc,
            "routing content-delivery update timestamp");
        RoutingValidation.Require(claim.UpdatedUtc >= claim.ClaimedUtc,
            "Routing content-delivery claim timestamps are inconsistent.");
        RoutingValidation.Require(claim.DuplicateRiskAcceptedUtc is null ||
                                  claim.DuplicateRiskAcceptedUtc <= claim.ClaimedUtc,
            "Routing duplicate-risk confirmation must precede its replacement claim.");
        RoutingValidation.Require(claim.State switch
        {
            RoutingDeliveryClaimState.Claimed =>
                claim.RemoteReceiptReference is null && claim.ErrorCode is null &&
                claim.UpdatedUtc == claim.ClaimedUtc,
            RoutingDeliveryClaimState.Confirmed =>
                claim.RemoteReceiptReference is not null && claim.ErrorCode is null,
            RoutingDeliveryClaimState.Unknown or RoutingDeliveryClaimState.Released =>
                claim.RemoteReceiptReference is null && claim.ErrorCode is not null,
            _ => false
        }, "A routing content-delivery claim payload does not match its state.");
    }

    private static bool IsCompletion(
        RoutingDeliveryClaim previous,
        RoutingDeliveryClaim next) =>
        next.Epoch == previous.Epoch &&
        next.ClaimId == previous.ClaimId &&
        next.OwnerDeliveryId == previous.OwnerDeliveryId &&
        next.OwnerSourceClipId == previous.OwnerSourceClipId &&
        next.OwnerAttemptId == previous.OwnerAttemptId &&
        next.DuplicateRiskAcceptedUtc == previous.DuplicateRiskAcceptedUtc &&
        next.ClaimedUtc == previous.ClaimedUtc &&
        next.State is RoutingDeliveryClaimState.Confirmed or
            RoutingDeliveryClaimState.Unknown or RoutingDeliveryClaimState.Released;

    private static bool IsAmbiguousReclaim(
        RoutingDeliveryClaim previous,
        RoutingDeliveryClaim next) =>
        IsReplacementClaim(previous, next) &&
        next.OwnerDeliveryId == previous.OwnerDeliveryId &&
        next.OwnerSourceClipId == previous.OwnerSourceClipId &&
        HasFreshDuplicateRiskAcceptance(previous, next.DuplicateRiskAcceptedUtc);

    private static bool IsReleasedReclaim(
        RoutingDeliveryClaim previous,
        RoutingDeliveryClaim next) =>
        IsReplacementClaim(previous, next) && next.DuplicateRiskAcceptedUtc is null;

    private static bool IsReplacementClaim(
        RoutingDeliveryClaim previous,
        RoutingDeliveryClaim next) =>
        next.State == RoutingDeliveryClaimState.Claimed &&
        next.Epoch == previous.Epoch + 1 &&
        next.ClaimId != previous.ClaimId &&
        next.OwnerAttemptId != previous.OwnerAttemptId &&
        next.RemoteReceiptReference is null && next.ErrorCode is null &&
        next.ClaimedUtc >= previous.UpdatedUtc;

    private static void ValidateKey(RoutingDeliveryClaimKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        RoutingValidation.Require(Enum.IsDefined(key.Destination),
            "The routing delivery-claim destination is unsupported.");
        RoutingValidation.RequireOpaqueId(key.ConnectionId, 128,
            "routing delivery-claim connection id");
        RoutingValidation.RequireSha256(key.ArtifactSha256,
            "routing delivery-claim artifact hash");
        RoutingValidation.Require(key.ArtifactSha256.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "The routing delivery-claim artifact hash is not canonical lowercase text.");
        RoutingValidation.RequireOpaqueId(key.ClaimSlot, 128,
            "routing delivery-claim slot");
    }

    private static DateTimeOffset MaxUtc(DateTimeOffset current, DateTimeOffset requested)
    {
        var timestamp = RoutingValidation.Utc(requested);
        return timestamp < current ? current : timestamp;
    }

    private static string RequireReceipt(string? value)
    {
        RoutingValidation.RequireOpaqueId(value, 256,
            "routing content-delivery remote receipt");
        return value!;
    }

    private static string RequireError(string? value)
    {
        if (value is null) throw new InvalidDataException(
            "A routing content-delivery outcome has no safe error code.");
        RoutingValidation.RequireErrorCode(value);
        return value;
    }

    private static void ValidateOutcome(RoutingDeliveryAttemptResult outcome)
    {
        switch (outcome.Outcome)
        {
            case RoutingDeliveryAttemptOutcome.Confirmed:
                _ = RequireReceipt(outcome.RemoteReceiptReference);
                RoutingValidation.Require(outcome.ErrorCode is null &&
                                          outcome.ProviderResumeReference is null,
                    "A confirmed content-delivery result contains failure state.");
                break;
            case RoutingDeliveryAttemptOutcome.Failed:
                RoutingValidation.Require(outcome.RemoteReceiptReference is null,
                    "A failed content-delivery result contains a receipt.");
                _ = RequireError(outcome.ErrorCode);
                RoutingValidation.RequireOptionalOpaqueId(
                    outcome.ProviderResumeReference,
                    256,
                    "content-delivery provider resume reference");
                break;
            case RoutingDeliveryAttemptOutcome.Unknown:
                RoutingValidation.Require(outcome.RemoteReceiptReference is null &&
                                          outcome.ProviderResumeReference is null,
                    "An unknown content-delivery result contains completion state.");
                _ = RequireError(outcome.ErrorCode);
                break;
            default:
                throw new InvalidDataException(
                    "The content-delivery provider outcome is unsupported.");
        }
    }
}

internal sealed class RoutingDeliveryReceiptStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = 4 * 1024 * 1024;
    internal const string FileName = "delivery-receipts.json";
    private readonly RoutingAtomicJsonStore<RoutingDeliveryReceiptDocument> _store;

    internal RoutingDeliveryReceiptStore()
        : this(System.IO.Path.Combine(SettingsStore.DataDirectory, "routing", FileName))
    {
    }

    internal RoutingDeliveryReceiptStore(string path)
    {
        _store = new RoutingAtomicJsonStore<RoutingDeliveryReceiptDocument>(
            path,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Generation,
            RoutingDeliveryReceiptModel.Validate,
            RoutingDeliveryReceiptModel.ValidateSuccessor,
            document =>
            {
                RoutingDeliveryReceiptModel.Validate(document);
                RoutingValidation.Require(document.Generation == 1 &&
                                          document.Claims.Count == 0 &&
                                          document.CreatedUtc == document.UpdatedUtc,
                    "The first routing delivery-receipt document is not canonical.");
            });
    }

    internal string Path => _store.Path;
    internal RoutingDocumentLoadResult<RoutingDeliveryReceiptDocument> Load(
        CancellationToken cancellationToken = default) => _store.Load(cancellationToken);
    internal Task<RoutingDeliveryReceiptDocument> LoadOrCreateAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) => _store.LoadOrCreateAsync(
        () => RoutingDeliveryReceiptModel.CreateEmpty(now), cancellationToken);
    internal Task<RoutingDeliveryReceiptDocument> SaveAsync(
        RoutingDeliveryReceiptDocument document,
        long expectedGeneration,
        CancellationToken cancellationToken = default) => _store.SaveAsync(
        document, expectedGeneration, cancellationToken);
}

internal enum RoutingDeliveryClaimAcquireStatus
{
    Acquired,
    ReusedConfirmed,
    BlockedUnknown
}

internal sealed record RoutingDeliveryClaimAcquireResult(
    RoutingDeliveryClaimAcquireStatus Status,
    RoutingDeliveryClaim Claim);

internal sealed class RoutingDeliveryReceiptGuard
{
    private const int MaximumSaveAttempts = 4;
    private readonly RoutingDeliveryReceiptStore _store;
    private readonly Func<Guid> _createClaimId;
    private readonly Func<DateTimeOffset> _utcNow;

    internal RoutingDeliveryReceiptGuard(
        RoutingDeliveryReceiptStore store,
        Func<Guid>? createClaimId = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _createClaimId = createClaimId ?? Guid.NewGuid;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal async Task<RoutingDeliveryClaimAcquireResult> AcquireAsync(
        PlannedDelivery delivery,
        RoutingResolvedArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(artifact);
        var attemptId = delivery.CurrentAttemptId ?? throw new InvalidDataException(
            "A provider-fenced delivery has no current attempt id.");
        RoutingValidation.Require(
            delivery.State == PlannedDeliveryState.Sending && delivery.Attempts > 0 &&
            delivery.AttemptStartedUtc is not null,
            "Only a durably started delivery may acquire a provider claim.");
        RoutingValidation.RequireOptionalUtc(delivery.DuplicateRiskAcceptedUtc,
            "delivery duplicate-risk confirmation timestamp");
        var key = RoutingDeliveryReceiptModel.CreateKey(delivery, artifact);
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await _store.LoadOrCreateAsync(_utcNow(), cancellationToken)
                .ConfigureAwait(false);
            var existing = current.Claims.SingleOrDefault(claim => claim.Key == key);
            if (existing is { State: RoutingDeliveryClaimState.Confirmed })
            {
                return new RoutingDeliveryClaimAcquireResult(
                    RoutingDeliveryClaimAcquireStatus.ReusedConfirmed,
                    existing);
            }
            if (existing is
                { State: RoutingDeliveryClaimState.Claimed or RoutingDeliveryClaimState.Unknown } &&
                (existing.OwnerDeliveryId != delivery.DeliveryId ||
                 !existing.OwnerSourceClipId.Equals(
                     delivery.SourceClipId, StringComparison.Ordinal) ||
                 !RoutingDeliveryReceiptModel.HasFreshDuplicateRiskAcceptance(
                     existing, delivery.DuplicateRiskAcceptedUtc)))
            {
                return new RoutingDeliveryClaimAcquireResult(
                    RoutingDeliveryClaimAcquireStatus.BlockedUnknown,
                    existing);
            }

            var candidate = RoutingDeliveryReceiptModel.BeginClaim(
                current,
                key,
                _createClaimId(),
                delivery.DeliveryId,
                delivery.SourceClipId,
                attemptId,
                delivery.DuplicateRiskAcceptedUtc,
                _utcNow());
            try
            {
                var saved = await _store.SaveAsync(
                        candidate,
                        current.Generation,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new RoutingDeliveryClaimAcquireResult(
                    RoutingDeliveryClaimAcquireStatus.Acquired,
                    saved.Claims.Single(claim => claim.Key == key));
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                // Reclassify against the winning durable claim.
            }
        }
        throw new RoutingConcurrencyException(
            "The routing delivery-receipt store kept changing while a claim was acquired.");
    }

    internal async Task<RoutingDeliveryClaim> CompleteAsync(
        RoutingDeliveryClaim claim,
        RoutingDeliveryAttemptResult outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(outcome);
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            var load = _store.Load(cancellationToken);
            if (!load.LoadedFromDisk || load.Document is null)
            {
                throw new InvalidDataException(
                    $"The routing delivery-receipt store cannot be trusted ({load.Status}).");
            }
            var current = load.Document;
            var existing = current.Claims.SingleOrDefault(item => item.Key == claim.Key) ??
                           throw new InvalidDataException(
                               "The routing content-delivery claim disappeared.");
            if (existing.State != RoutingDeliveryClaimState.Claimed)
            {
                if (existing.ClaimId == claim.ClaimId &&
                    existing.OwnerAttemptId == claim.OwnerAttemptId)
                {
                    RoutingValidation.Require(
                        RoutingDeliveryReceiptModel.MatchesCompletion(existing, outcome),
                        "A completed content-delivery claim received a conflicting result.");
                    return existing;
                }
                throw new InvalidDataException(
                    "Another content-delivery attempt replaced the completion fence.");
            }
            var candidate = RoutingDeliveryReceiptModel.CompleteClaim(
                current,
                claim.Key,
                claim.ClaimId,
                claim.OwnerAttemptId,
                outcome,
                _utcNow());
            try
            {
                var saved = await _store.SaveAsync(
                        candidate,
                        current.Generation,
                        cancellationToken)
                    .ConfigureAwait(false);
                return saved.Claims.Single(item => item.Key == claim.Key);
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                // Reload the exact owner fence and retry its terminal transition.
            }
        }
        throw new RoutingConcurrencyException(
            "The routing delivery-receipt store kept changing while a claim completed.");
    }
}

/// <summary>
/// Provider decorator that makes physical-content delivery claims durable before any connector
/// call. Confirmed duplicates reuse durable receipt evidence without invoking the connector;
/// in-flight/unknown claims remain unknown and require explicit user recovery rather than a resend.
/// </summary>
internal sealed class RoutingReceiptGuardedDeliveryProvider(
    IRoutingDeliveryProvider inner,
    RoutingDeliveryReceiptGuard receipts) : IRoutingDeliveryProvider
{
    private readonly IRoutingDeliveryProvider _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly RoutingDeliveryReceiptGuard _receipts =
        receipts ?? throw new ArgumentNullException(nameof(receipts));

    public bool Supports(RoutingDestinationKind destination) => _inner.Supports(destination);

    public async Task<RoutingDeliveryAttemptResult> SendAsync(
        PlannedDelivery delivery,
        RoutingResolvedArtifact artifact,
        CancellationToken cancellationToken)
    {
        var claim = await _receipts.AcquireAsync(delivery, artifact, cancellationToken)
            .ConfigureAwait(false);
        if (claim.Status == RoutingDeliveryClaimAcquireStatus.ReusedConfirmed)
        {
            return RoutingDeliveryAttemptResult.Confirmed(
                $"dedup.{claim.Claim.ClaimId:N}");
        }
        if (claim.Status == RoutingDeliveryClaimAcquireStatus.BlockedUnknown)
        {
            return RoutingDeliveryAttemptResult.Unknown(
                "content-delivery-claim-unknown");
        }

        RoutingDeliveryAttemptResult outcome;
        try
        {
            outcome = await _inner.SendAsync(delivery, artifact, cancellationToken)
                .ConfigureAwait(false) ??
                      RoutingDeliveryAttemptResult.Unknown("provider-result-missing");
        }
        catch
        {
            outcome = RoutingDeliveryAttemptResult.Unknown("provider-result-unknown");
        }
        _ = await _receipts.CompleteAsync(
                claim.Claim,
                outcome,
                CancellationToken.None)
            .ConfigureAwait(false);
        return outcome;
    }
}
