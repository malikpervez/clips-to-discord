using System.Text.Json.Serialization;

namespace ClipsToDiscord;

internal enum RoutingRouteSource { User, Migration }
internal enum RoutingRouteKind { Specific, Fallback }
internal enum RoutingTriggerKind { AnyNewSourceClip, InstantReplay, ManualRecording, WatchedFolder }
internal enum RoutingClipSource { ClipCordCapture, WatchedFolder, ManualImport, XboxOneDrive }
internal enum RoutingCaptureType { InstantReplay, ManualRecording }
internal enum RoutingConditionField
{
    Game,
    ClipSource,
    ReactionCamera,
    CaptureType,
    Duration,
    SourceConnection,
    CapturedAt
}
internal enum RoutingConditionOperator { Equals, DoesNotEqual, Contains, GreaterThanOrEqual, LessThanOrEqual }
internal enum RoutingActionKind { Deliver, FileIntoLibrary }
internal enum RoutingDestinationKind { Discord, YouTube, TikTok }
internal enum RoutingOutputKind { Original, Landscape, Portrait }
internal enum RoutingMissingOutputBehavior { UseOriginal, Skip, NeedsAttention }
internal enum RoutingDeliveryMode { Automatic, Approval }
internal enum RoutingVisibility { Unspecified, Public, Unlisted, Private }
internal enum RoutingLibraryArea { LocalOnly, Uploaded }

/// <summary>
/// A condition is plain data, never an expression, path, URI, or secret. Duration values are
/// invariant-culture whole milliseconds, ReactionCamera values are true/false, SourceConnection
/// values are canonical source-catalog ids, and CapturedAt values are round-trip UTC timestamps.
/// </summary>
internal sealed record RoutingCondition(
    Guid ConditionId,
    RoutingConditionField Field,
    RoutingConditionOperator Operator,
    string Value);

/// <summary>
/// Capture owns output availability. A delivery selects an output and explicitly says what to do
/// if Capture did not produce it for this source clip.
/// </summary>
internal sealed record RoutingPrepareSettings(
    bool Landscape,
    bool Portrait,
    RoutingMissingOutputBehavior DefaultOnMissing);

internal sealed record RoutingDeliverySettings(
    string? Message,
    string? Title,
    string? Caption,
    RoutingVisibility Visibility,
    bool NotifyFollowers);

/// <summary>
/// Deliver uses Destination, ConnectionId, OutputRef, OnMissingOutput, Mode, and DeliverySettings.
/// FileIntoLibrary uses LibraryArea and is the route's terminal source disposition.
/// </summary>
internal sealed record RoutingAction(
    Guid ActionId,
    bool Enabled,
    RoutingActionKind Kind,
    RoutingDestinationKind? Destination,
    string? ConnectionId,
    RoutingOutputKind? OutputRef,
    RoutingMissingOutputBehavior? OnMissingOutput,
    RoutingDeliveryMode Mode,
    RoutingLibraryArea? LibraryArea,
    RoutingDeliverySettings? DeliverySettings)
{
    [JsonIgnore]
    internal bool IsTerminalSourceDisposition => Kind == RoutingActionKind.FileIntoLibrary;
}

/// <summary>
/// Freezes the exact pre-existing Xbox occurrences a user approved when activating a history
/// window. Clips captured after ActivationUtc remain normal new arrivals; a clip at or before that
/// boundary is eligible only when its opaque occurrence id is in this immutable selection.
/// </summary>
internal sealed record RoutingXboxHistoricalOccurrence(
    string OccurrenceId,
    string RevisionId);

internal sealed record RoutingXboxHistorySelection(
    DateTimeOffset ActivationUtc,
    IReadOnlyList<RoutingXboxHistoricalOccurrence> HistoricalOccurrences)
{
    public bool Equals(RoutingXboxHistorySelection? other) =>
        ReferenceEquals(this, other) ||
        other is not null && ActivationUtc == other.ActivationUtc &&
        RoutingStructural.SequenceEqual(HistoricalOccurrences, other.HistoricalOccurrences);

    public override int GetHashCode() => RoutingStructural.Hash(
        ActivationUtc, HistoricalOccurrences);
}

internal sealed record RoutingRoute(
    Guid RouteId,
    string Name,
    bool Enabled,
    int Priority,
    long Revision,
    RoutingRouteSource Source,
    RoutingRouteKind Kind,
    RoutingTriggerKind Trigger,
    RoutingPrepareSettings Prepare,
    IReadOnlyList<RoutingCondition> Conditions,
    IReadOnlyList<RoutingAction> Actions,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    RoutingXboxHistorySelection? XboxHistorySelection = null)
{
    public bool Equals(RoutingRoute? other) =>
        ReferenceEquals(this, other) ||
        other is not null &&
        RouteId == other.RouteId && Name == other.Name && Enabled == other.Enabled &&
        Priority == other.Priority && Revision == other.Revision && Source == other.Source &&
        Kind == other.Kind && Trigger == other.Trigger && Prepare == other.Prepare &&
        CreatedUtc == other.CreatedUtc &&
        ModifiedUtc == other.ModifiedUtc && XboxHistorySelection == other.XboxHistorySelection &&
        RoutingStructural.SequenceEqual(Conditions, other.Conditions) &&
        RoutingStructural.SequenceEqual(Actions, other.Actions);

    public override int GetHashCode() => RoutingStructural.Hash(
        RouteId, Name, Enabled, Priority, Revision, Source, Kind, Trigger, Prepare,
        Conditions, Actions, CreatedUtc, ModifiedUtc, XboxHistorySelection);
}

internal sealed record RoutingSnapshotDocument(
    int SchemaVersion,
    long Generation,
    IReadOnlyList<RoutingRoute> Routes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public bool Equals(RoutingSnapshotDocument? other) =>
        ReferenceEquals(this, other) ||
        other is not null && SchemaVersion == other.SchemaVersion &&
        Generation == other.Generation && CreatedUtc == other.CreatedUtc &&
        UpdatedUtc == other.UpdatedUtc && RoutingStructural.SequenceEqual(Routes, other.Routes);

    public override int GetHashCode() =>
        RoutingStructural.Hash(SchemaVersion, Generation, Routes, CreatedUtc, UpdatedUtc);
}

internal sealed record RoutingRouteSnapshotReference(
    long RoutingGeneration,
    Guid RouteId,
    long RouteRevision,
    string RouteName,
    Guid ActionId,
    int Priority,
    int Order);

/// <summary>Logical artifact identity, independent of a mutable filesystem path.</summary>
internal sealed record RoutingOutputReference(
    string ClipId,
    RoutingOutputKind Kind,
    string Revision);

/// <summary>A typed duplicate key with no concatenation or delimiter ambiguity.</summary>
internal sealed record RoutingDeliveryKey(
    string ConnectionId,
    RoutingOutputReference OutputRef);

internal enum IntentionalDuplicateDecision { UserConfirmedDeliverTwice }

/// <summary>
/// Durable proof for exactly two otherwise-duplicate route actions. Both members carry the same
/// exact typed key and exact two route ids approved by the user.
/// </summary>
internal sealed record IntentionalDuplicateProvenance(
    Guid GroupId,
    IntentionalDuplicateDecision Decision,
    RoutingDeliveryKey DeliveryKey,
    Guid FirstRouteId,
    Guid SecondRouteId,
    DateTimeOffset ConfirmedUtc);

internal enum PlannedDeliveryState
{
    WaitingForArtifact,
    WaitingForApproval,
    Ready,
    Sending,
    Delivered,
    Failed,
    DeliveryUnknown,
    NeedsAttention,
    Skipped,
    Cancelled,
    Expired
}

internal enum RoutingMissingArtifactOutcome
{
    RequestedOutput,
    OriginalFallback,
    Skipped,
    NeedsAttention,
    DuplicateSuppressed
}

internal enum RoutingNeedsAttentionResolution
{
    UseOriginal,
    Skip,
    RetryOrRebuild
}

/// <summary>Immutable evidence for an output decision made before a plan first became visible.</summary>
internal sealed record RoutingImmediateMissingResolution(
    Guid DeliveryId,
    RoutingOutputReference RequestedOutput,
    RoutingMissingArtifactOutcome Outcome,
    RoutingOutputReference EffectiveOutput,
    PlannedDeliveryState State,
    string ErrorCode);

/// <summary>
/// Durable header for every source evaluation, including a no-match/no-work decision. It pins the
/// route generation and exact matched route ids so restart never re-evaluates a source against
/// newer configuration. Latent duplicate authorization is retained for collisions that appear
/// only after pending or missing outputs fall back to the original.
/// </summary>
internal sealed record RoutingPlanDecision(
    Guid PlanId,
    string SourceClipId,
    long RoutingGeneration,
    IReadOnlyList<Guid> MatchedRouteIds,
    IReadOnlyList<RoutingImmediateMissingResolution> InitialMissingResolutions,
    IReadOnlyList<IntentionalDuplicateProvenance> LatentDuplicateAuthorizations,
    DateTimeOffset CreatedUtc,
    RoutingLocalOnlyAdmissionSnapshot? LocalOnlyOverride = null)
{
    public bool Equals(RoutingPlanDecision? other) =>
        ReferenceEquals(this, other) ||
        other is not null && PlanId == other.PlanId && SourceClipId == other.SourceClipId &&
        RoutingGeneration == other.RoutingGeneration && CreatedUtc == other.CreatedUtc &&
        LocalOnlyOverride == other.LocalOnlyOverride &&
        RoutingStructural.SequenceEqual(MatchedRouteIds, other.MatchedRouteIds) &&
        RoutingStructural.SequenceEqual(InitialMissingResolutions, other.InitialMissingResolutions) &&
        RoutingStructural.SequenceEqual(
            LatentDuplicateAuthorizations, other.LatentDuplicateAuthorizations);

    public override int GetHashCode() => RoutingStructural.Hash(
        PlanId, SourceClipId, RoutingGeneration, MatchedRouteIds, InitialMissingResolutions,
        LatentDuplicateAuthorizations, CreatedUtc, LocalOnlyOverride);
}

internal sealed record PlannedDelivery(
    Guid DeliveryId,
    Guid PlanId,
    string SourceClipId,
    RoutingRouteSnapshotReference Route,
    RoutingDestinationKind Destination,
    string ConnectionId,
    RoutingOutputReference RequestedOutput,
    RoutingOutputReference Output,
    RoutingOutputReference OriginalOutput,
    RoutingMissingOutputBehavior OnMissingOutput,
    RoutingMissingArtifactOutcome ArtifactOutcome,
    string? ArtifactErrorCode,
    RoutingDeliveryMode Mode,
    RoutingDeliverySettings Settings,
    PlannedDeliveryState State,
    int Attempts,
    Guid? CurrentAttemptId,
    string? ProviderResumeReference,
    string? RemoteReceiptReference,
    string? ErrorCode,
    DateTimeOffset? ApprovedUtc,
    DateTimeOffset? AttemptStartedUtc,
    DateTimeOffset? CompletedUtc,
    DateTimeOffset? DuplicateRiskAcceptedUtc,
    IntentionalDuplicateProvenance? IntentionalDuplicate,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    [JsonIgnore]
    internal RoutingDeliveryKey DeliveryKey => new(ConnectionId, Output);

    [JsonIgnore]
    internal string ProviderIdempotencyKey => DeliveryId.ToString("N");
}

internal enum PlannedFileDispositionState
{
    WaitingForDependencies,
    Ready,
    Moving,
    RecoveryPending,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// The one terminal disposition for a plan/source. Dependencies must equal all sibling deliveries.
/// </summary>
internal sealed record PlannedFileDisposition(
    Guid DispositionId,
    Guid PlanId,
    string SourceClipId,
    string SourceContentSha256,
    RoutingRouteSnapshotReference Route,
    RoutingLibraryArea LibraryArea,
    IReadOnlyList<Guid> PrerequisiteDeliveryIds,
    PlannedFileDispositionState State,
    int Attempts,
    Guid? CurrentAttemptId,
    string? FinalLibraryItemReference,
    string? ErrorCode,
    DateTimeOffset? AttemptStartedUtc,
    DateTimeOffset? CompletedUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public bool Equals(PlannedFileDisposition? other) =>
        ReferenceEquals(this, other) ||
        other is not null && DispositionId == other.DispositionId && PlanId == other.PlanId &&
        SourceClipId == other.SourceClipId && SourceContentSha256 == other.SourceContentSha256 &&
        Route == other.Route && LibraryArea == other.LibraryArea && State == other.State &&
        Attempts == other.Attempts && CurrentAttemptId == other.CurrentAttemptId &&
        FinalLibraryItemReference == other.FinalLibraryItemReference && ErrorCode == other.ErrorCode &&
        AttemptStartedUtc == other.AttemptStartedUtc && CompletedUtc == other.CompletedUtc &&
        CreatedUtc == other.CreatedUtc && UpdatedUtc == other.UpdatedUtc &&
        RoutingStructural.SequenceEqual(PrerequisiteDeliveryIds, other.PrerequisiteDeliveryIds);

    public override int GetHashCode() => RoutingStructural.Hash(
        DispositionId, PlanId, SourceClipId, SourceContentSha256, Route, LibraryArea,
        PrerequisiteDeliveryIds, State, Attempts, CurrentAttemptId, FinalLibraryItemReference,
        ErrorCode, AttemptStartedUtc, CompletedUtc, CreatedUtc, UpdatedUtc);
}

internal sealed record RoutingOutboxDocument(
    int SchemaVersion,
    long Generation,
    IReadOnlyList<RoutingPlanDecision> Plans,
    IReadOnlyList<PlannedDelivery> Deliveries,
    IReadOnlyList<PlannedFileDisposition> FileDispositions,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public bool Equals(RoutingOutboxDocument? other) =>
        ReferenceEquals(this, other) ||
        other is not null && SchemaVersion == other.SchemaVersion &&
        Generation == other.Generation && CreatedUtc == other.CreatedUtc &&
        UpdatedUtc == other.UpdatedUtc &&
        RoutingStructural.SequenceEqual(Plans, other.Plans) &&
        RoutingStructural.SequenceEqual(Deliveries, other.Deliveries) &&
        RoutingStructural.SequenceEqual(FileDispositions, other.FileDispositions);

    public override int GetHashCode() => RoutingStructural.Hash(
        SchemaVersion, Generation, Plans, Deliveries, FileDispositions, CreatedUtc, UpdatedUtc);
}

internal enum RoutingDocumentLoadStatus
{
    Missing,
    Loaded,
    Corrupt,
    UnsupportedSchema,
    Invalid,
    Unavailable
}

internal sealed record RoutingDocumentLoadResult<T>(T? Document, RoutingDocumentLoadStatus Status)
    where T : class
{
    internal bool LoadedFromDisk =>
        Status == RoutingDocumentLoadStatus.Loaded && Document is not null;
}

internal sealed class RoutingConcurrencyException : InvalidOperationException
{
    internal RoutingConcurrencyException(string message) : base(message) { }
}

internal static class RoutingStructural
{
    internal static bool SequenceEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right) =>
        ReferenceEquals(left, right) ||
        left is not null && right is not null && left.SequenceEqual(right);

    internal static int Hash(params object?[] values)
    {
        var hash = new HashCode();
        foreach (var value in values)
        {
            if (value is System.Collections.IEnumerable sequence and not string)
            {
                foreach (var item in sequence) hash.Add(item);
            }
            else
            {
                hash.Add(value);
            }
        }
        return hash.ToHashCode();
    }
}
