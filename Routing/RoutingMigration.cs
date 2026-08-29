using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

internal enum LegacyRoutingMode
{
    DiscordUpload,
    LocalOnly
}

internal enum LegacyRoutingCutoverScope
{
    FutureClipsOnly
}

internal enum LegacyRoutingCutoverReadinessStatus
{
    Ready,
    LegacyWorkerActive,
    PendingLegacyWork,
    UnreconciledIgnoredFiles,
    InvalidLegacySettings,
    InvalidWatchState,
    MissingDiscordConnection,
    AmbiguousDiscordConnection,
    InvalidDiscordConnection
}

internal enum LegacyRoutingCutoverResultStatus
{
    Committed,
    AlreadyCommitted,
    Blocked,
    MarkerUnavailable,
    RoutingStateUnavailable,
    StateConflict
}

internal enum LegacyRoutingMigrationMarkerPhase
{
    Prepared,
    Committed
}

/// <summary>
/// The connector slice supplies opaque ids. Migration never derives an id from, nor persists,
/// the legacy webhook URL or token.
/// </summary>
internal sealed record LegacyRoutingMigrationInput(
    AppSettings Settings,
    WatchState WatchState,
    bool LegacyWorkerQuiesced,
    IReadOnlyList<string> DiscordConnectionIds);

internal sealed record LegacyContentHashExclusions(
    IReadOnlyList<string> Known,
    IReadOnlyList<string> Uploaded,
    IReadOnlyList<string> LocalOnly)
{
    public bool Equals(LegacyContentHashExclusions? other) =>
        ReferenceEquals(this, other) ||
        other is not null &&
        RoutingStructural.SequenceEqual(Known, other.Known) &&
        RoutingStructural.SequenceEqual(Uploaded, other.Uploaded) &&
        RoutingStructural.SequenceEqual(LocalOnly, other.LocalOnly);

    public override int GetHashCode() => RoutingStructural.Hash(Known, Uploaded, LocalOnly);
}

internal sealed record LegacyRoutingMigrationPlan(
    Guid MigrationId,
    LegacyRoutingMode Mode,
    LegacyRoutingCutoverScope Scope,
    string SourceFingerprint,
    RoutingRoute Route,
    LegacyContentHashExclusions ContentHashExclusions);

internal sealed record LegacyRoutingCutoverReadiness(
    LegacyRoutingCutoverReadinessStatus Status,
    string Reason,
    LegacyRoutingMigrationPlan? Plan)
{
    internal bool CanCommit => Status == LegacyRoutingCutoverReadinessStatus.Ready && Plan is not null;
}

internal sealed record LegacyRoutingCutoverResult(
    LegacyRoutingCutoverResultStatus Status,
    string Reason,
    LegacyRoutingCutoverReadiness Readiness,
    LegacyRoutingMigrationMarker? Marker)
{
    internal bool IsCommitted => Status is LegacyRoutingCutoverResultStatus.Committed or
        LegacyRoutingCutoverResultStatus.AlreadyCommitted;
}

internal sealed record LegacyRoutingMigrationMarker(
    int SchemaVersion,
    long Generation,
    Guid MigrationId,
    LegacyRoutingMigrationMarkerPhase Phase,
    LegacyRoutingMode Mode,
    LegacyRoutingCutoverScope Scope,
    string SourceFingerprint,
    string PayloadFingerprint,
    RoutingRoute Route,
    LegacyContentHashExclusions ContentHashExclusions,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// Pure translation of the 1.x settings and exclusion state. It describes only future clips;
/// hashes already known to 1.x remain exclusions and are never converted into delivery work.
/// </summary>
internal static class LegacyRoutingMigrationPlanner
{
    private const string DiscordRouteName = "Everything else → Friends server";
    private const string LocalRouteName = "Everything else → Local only";
    internal const int MaximumPersistedHashOccurrences = 90_000;

    internal static LegacyRoutingCutoverReadiness Evaluate(
        LegacyRoutingMigrationInput input,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Settings is null || input.WatchState is null || input.DiscordConnectionIds is null)
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.InvalidLegacySettings,
                "The legacy migration input is incomplete.");
        }
        if (!input.LegacyWorkerQuiesced)
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.LegacyWorkerActive,
                "The legacy watcher must be stopped before routing cutover.");
        }

        var state = input.WatchState;
        if (state.PendingMoves is null || state.PendingLocalOnlyMoves is null ||
            state.PendingEditedUploads is null || state.IgnoredFileKeys is null ||
            state.KnownContentHashes is null ||
            state.UploadedContentHashes is null || state.LocalOnlyContentHashes is null)
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.InvalidWatchState,
                "The legacy watcher state is incomplete.");
        }
        if (state.PendingMoves.Count != 0 || state.PendingLocalOnlyMoves.Count != 0 ||
            state.PendingEditedUploads.Count != 0)
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.PendingLegacyWork,
                "All legacy pending upload, archive, and edited-clip queues must drain first.");
        }
        if (state.IgnoredFileKeys.Count != 0)
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.UnreconciledIgnoredFiles,
                "Every legacy ignored baseline file must be reconciled to a content hash before cutover.");
        }

        if (!TryValidateSettingsAndState(input.Settings, state, out var settingsReason))
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.InvalidLegacySettings,
                settingsReason);
        }
        if (!TryCreateExclusions(state, out var exclusions, out var exclusionReason))
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.InvalidWatchState,
                exclusionReason);
        }
        if (exclusions!.Known.Count + exclusions.Uploaded.Count + exclusions.LocalOnly.Count >
            MaximumPersistedHashOccurrences)
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.InvalidWatchState,
                "The legacy exclusion set is too large for the bounded cutover marker.");
        }

        string? connectionId = null;
        if (input.Settings.UploadToDiscord)
        {
            if (!WebhookValidation.IsDiscordWebhook(input.Settings.WebhookUrl))
            {
                return Blocked(LegacyRoutingCutoverReadinessStatus.InvalidLegacySettings,
                    "The legacy Discord destination is not valid.");
            }
            if (input.DiscordConnectionIds.Count == 0)
            {
                return Blocked(LegacyRoutingCutoverReadinessStatus.MissingDiscordConnection,
                    "A pre-created opaque Discord connection is required before cutover.");
            }
            if (input.DiscordConnectionIds.Count != 1)
            {
                return Blocked(LegacyRoutingCutoverReadinessStatus.AmbiguousDiscordConnection,
                    "Exactly one pre-created Discord connection must represent the legacy destination.");
            }
            connectionId = input.DiscordConnectionIds[0];
            try
            {
                RoutingValidation.RequireOpaqueId(connectionId, 128, "legacy Discord connection id");
            }
            catch (InvalidDataException)
            {
                return Blocked(LegacyRoutingCutoverReadinessStatus.InvalidDiscordConnection,
                    "The supplied Discord connection id is not an opaque identifier.");
            }
        }

        var mode = input.Settings.UploadToDiscord
            ? LegacyRoutingMode.DiscordUpload
            : LegacyRoutingMode.LocalOnly;
        var fingerprint = CreateSourceFingerprint(
            mode,
            input.Settings.ClipsFolder,
            input.Settings.CaptureSource,
            connectionId,
            exclusions!);
        var timestamp = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        var routeId = DeterministicGuid(fingerprint, "route");
        var actions = mode == LegacyRoutingMode.DiscordUpload
            ? new RoutingAction[]
            {
                new(
                    DeterministicGuid(fingerprint, "discord-action"),
                    Enabled: true,
                    RoutingActionKind.Deliver,
                    RoutingDestinationKind.Discord,
                    connectionId,
                    RoutingOutputKind.Original,
                    RoutingMissingOutputBehavior.UseOriginal,
                    RoutingDeliveryMode.Automatic,
                    LibraryArea: null,
                    new RoutingDeliverySettings(
                        Message: null,
                        Title: null,
                        Caption: null,
                        RoutingVisibility.Unspecified,
                        NotifyFollowers: false)),
                CreateFileAction(fingerprint, RoutingLibraryArea.Uploaded)
            }
            :
            [
                CreateFileAction(fingerprint, RoutingLibraryArea.LocalOnly)
            ];
        var route = new RoutingRoute(
            routeId,
            mode == LegacyRoutingMode.DiscordUpload ? DiscordRouteName : LocalRouteName,
            Enabled: true,
            Priority: 0,
            Revision: 1,
            RoutingRouteSource.Migration,
            RoutingRouteKind.Fallback,
            RoutingTriggerKind.AnyNewSourceClip,
            new RoutingPrepareSettings(
                Landscape: false,
                Portrait: false,
                RoutingMissingOutputBehavior.UseOriginal),
            Conditions: [],
            Actions: actions,
            CreatedUtc: timestamp,
            ModifiedUtc: timestamp);
        try
        {
            RoutingSnapshotModel.Validate(new RoutingSnapshotDocument(
                RoutingSnapshotStore.CurrentSchemaVersion,
                Generation: 1,
                Routes: [route],
                CreatedUtc: timestamp,
                UpdatedUtc: timestamp));
        }
        catch (InvalidDataException exception)
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.InvalidLegacySettings,
                $"The legacy route could not be represented safely: {exception.Message}");
        }

        var plan = new LegacyRoutingMigrationPlan(
            DeterministicGuid(fingerprint, "migration"),
            mode,
            LegacyRoutingCutoverScope.FutureClipsOnly,
            fingerprint,
            route,
            exclusions!);
        return new LegacyRoutingCutoverReadiness(
            LegacyRoutingCutoverReadinessStatus.Ready,
            "The legacy queues are drained and the cutover plan is ready.",
            plan);
    }

    internal static bool IsEquivalentMigrationRoute(RoutingRoute candidate, RoutingRoute planned)
    {
        if (candidate is null || planned is null) return false;
        return candidate.RouteId == planned.RouteId &&
               candidate.Name == planned.Name &&
               candidate.Enabled == planned.Enabled &&
               candidate.Priority == planned.Priority &&
               candidate.Revision == planned.Revision &&
               candidate.Source == planned.Source &&
               candidate.Kind == planned.Kind &&
               candidate.Trigger == planned.Trigger &&
               candidate.Prepare == planned.Prepare &&
               RoutingStructural.SequenceEqual(candidate.Conditions, planned.Conditions) &&
               RoutingStructural.SequenceEqual(candidate.Actions, planned.Actions);
    }

    private static RoutingAction CreateFileAction(string fingerprint, RoutingLibraryArea area) =>
        new(
            DeterministicGuid(fingerprint, "file-action"),
            Enabled: true,
            RoutingActionKind.FileIntoLibrary,
            Destination: null,
            ConnectionId: null,
            OutputRef: null,
            OnMissingOutput: null,
            RoutingDeliveryMode.Automatic,
            area,
            DeliverySettings: null);

    private static LegacyRoutingCutoverReadiness Blocked(
        LegacyRoutingCutoverReadinessStatus status,
        string reason) => new(status, reason, Plan: null);

    private static bool TryValidateSettingsAndState(
        AppSettings settings,
        WatchState state,
        out string reason)
    {
        reason = string.Empty;
        if (!settings.IsValid || state.Version <= 0 || string.IsNullOrWhiteSpace(settings.ClipsFolder) ||
            string.IsNullOrWhiteSpace(state.ClipsFolder) || !Directory.Exists(settings.ClipsFolder))
        {
            reason = "The legacy clips folder or watcher state is unavailable.";
            return false;
        }
        try
        {
            if (!Path.GetFullPath(settings.ClipsFolder).Equals(
                    Path.GetFullPath(state.ClipsFolder),
                    StringComparison.OrdinalIgnoreCase) ||
                AppSettings.NormalizeCaptureSource(settings.CaptureSource) !=
                AppSettings.NormalizeCaptureSource(state.CaptureSource))
            {
                reason = "The watcher state does not belong to the current clips source.";
                return false;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "The legacy clips folder is not a canonical path.";
            return false;
        }
        return true;
    }

    private static bool TryCreateExclusions(
        WatchState state,
        out LegacyContentHashExclusions? exclusions,
        out string reason)
    {
        exclusions = null;
        reason = string.Empty;
        if (!TryNormalizeHashes(state.KnownContentHashes, out var known) ||
            !TryNormalizeHashes(state.UploadedContentHashes, out var uploaded) ||
            !TryNormalizeHashes(state.LocalOnlyContentHashes, out var localOnly))
        {
            reason = "A legacy content-hash exclusion is invalid.";
            return false;
        }
        if (uploaded!.Intersect(localOnly!, StringComparer.Ordinal).Any())
        {
            reason = "A legacy content hash cannot be both Uploaded and Local only.";
            return false;
        }

        known = known!.Concat(uploaded!).Concat(localOnly!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        exclusions = new LegacyContentHashExclusions(known, uploaded!, localOnly!);
        return true;
    }

    private static bool TryNormalizeHashes(
        IEnumerable<string> values,
        out string[]? normalized)
    {
        normalized = null;
        var result = new List<string>();
        foreach (var value in values)
        {
            if (value is not { Length: 64 } || !value.All(Uri.IsHexDigit)) return false;
            result.Add(value.ToUpperInvariant());
        }
        normalized = result.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    internal static string CreateSourceFingerprint(
        LegacyRoutingMode mode,
        string clipsFolder,
        ClipCaptureSource captureSource,
        string? connectionId,
        LegacyContentHashExclusions exclusions)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "clipcord-legacy-routing-v1");
        Append(hash, mode.ToString());
        Append(hash, Path.GetFullPath(clipsFolder).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar).ToUpperInvariant());
        Append(hash, AppSettings.NormalizeCaptureSource(captureSource).ToString());
        Append(hash, connectionId ?? string.Empty);
        foreach (var value in exclusions.Known) Append(hash, "known:" + value);
        foreach (var value in exclusions.Uploaded) Append(hash, "uploaded:" + value);
        foreach (var value in exclusions.LocalOnly) Append(hash, "local:" + value);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static Guid DeterministicGuid(string fingerprint, string purpose)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, fingerprint);
        Append(hash, purpose);
        var bytes = hash.GetHashAndReset()[..16];
        // Mark this as an RFC 4122 version-5-style application-derived UUID.
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }

    /// <summary>
    /// Recomputable integrity digest for every persisted routing decision in the marker. The
    /// source fingerprint deliberately also covers legacy-only inputs that are not persisted;
    /// this second digest makes valid-shape corruption of the durable route or exclusions fail
    /// closed on load.
    /// </summary>
    internal static string CreatePayloadFingerprint(
        string sourceFingerprint,
        LegacyRoutingMode mode,
        LegacyRoutingCutoverScope scope,
        RoutingRoute route,
        LegacyContentHashExclusions exclusions)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "clipcord-legacy-marker-payload-v1");
        Append(hash, sourceFingerprint);
        Append(hash, mode.ToString());
        Append(hash, scope.ToString());
        Append(hash, route.RouteId.ToString("N"));
        Append(hash, route.Name);
        Append(hash, route.Enabled.ToString(CultureInfo.InvariantCulture));
        Append(hash, route.Priority.ToString(CultureInfo.InvariantCulture));
        Append(hash, route.Revision.ToString(CultureInfo.InvariantCulture));
        Append(hash, route.Source.ToString());
        Append(hash, route.Kind.ToString());
        Append(hash, route.Trigger.ToString());
        Append(hash, route.Prepare.Landscape.ToString(CultureInfo.InvariantCulture));
        Append(hash, route.Prepare.Portrait.ToString(CultureInfo.InvariantCulture));
        Append(hash, route.Prepare.DefaultOnMissing.ToString());
        Append(hash, route.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        Append(hash, route.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture));
        Append(hash, route.Conditions.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var condition in route.Conditions)
        {
            Append(hash, condition.ConditionId.ToString("N"));
            Append(hash, condition.Field.ToString());
            Append(hash, condition.Operator.ToString());
            Append(hash, condition.Value);
        }
        Append(hash, route.Actions.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var action in route.Actions)
        {
            Append(hash, action.ActionId.ToString("N"));
            Append(hash, action.Enabled.ToString(CultureInfo.InvariantCulture));
            Append(hash, action.Kind.ToString());
            AppendNullable(hash, action.Destination?.ToString());
            AppendNullable(hash, action.ConnectionId);
            AppendNullable(hash, action.OutputRef?.ToString());
            AppendNullable(hash, action.OnMissingOutput?.ToString());
            Append(hash, action.Mode.ToString());
            AppendNullable(hash, action.LibraryArea?.ToString());
            if (action.DeliverySettings is null)
            {
                Append(hash, "settings:<null>");
            }
            else
            {
                Append(hash, "settings:present");
                AppendNullable(hash, action.DeliverySettings.Message);
                AppendNullable(hash, action.DeliverySettings.Title);
                AppendNullable(hash, action.DeliverySettings.Caption);
                Append(hash, action.DeliverySettings.Visibility.ToString());
                Append(hash, action.DeliverySettings.NotifyFollowers.ToString(
                    CultureInfo.InvariantCulture));
            }
        }
        foreach (var value in exclusions.Known) Append(hash, "known:" + value);
        foreach (var value in exclusions.Uploaded) Append(hash, "uploaded:" + value);
        foreach (var value in exclusions.LocalOnly) Append(hash, "local:" + value);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendNullable(IncrementalHash hash, string? value)
    {
        Append(hash, value is null ? "nullable:absent" : "nullable:present");
        if (value is not null) Append(hash, value);
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

internal static class LegacyRoutingMigrationMarkerModel
{
    internal static LegacyRoutingMigrationMarker CreatePrepared(
        LegacyRoutingMigrationPlan plan,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var timestamp = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        var marker = new LegacyRoutingMigrationMarker(
            LegacyRoutingMigrationMarkerStore.CurrentSchemaVersion,
            Generation: 1,
            plan.MigrationId,
            LegacyRoutingMigrationMarkerPhase.Prepared,
            plan.Mode,
            plan.Scope,
            plan.SourceFingerprint,
            LegacyRoutingMigrationPlanner.CreatePayloadFingerprint(
                plan.SourceFingerprint,
                plan.Mode,
                plan.Scope,
                plan.Route,
                plan.ContentHashExclusions),
            plan.Route,
            plan.ContentHashExclusions,
            CreatedUtc: timestamp,
            UpdatedUtc: timestamp);
        Validate(marker);
        return marker;
    }

    internal static LegacyRoutingMigrationMarker Commit(
        LegacyRoutingMigrationMarker prepared,
        DateTimeOffset? now = null)
    {
        Validate(prepared);
        RoutingValidation.Require(prepared.Phase == LegacyRoutingMigrationMarkerPhase.Prepared,
            "Only a prepared legacy migration can be committed.");
        var committed = prepared with
        {
            Generation = RoutingValidation.NextGeneration(prepared.Generation),
            Phase = LegacyRoutingMigrationMarkerPhase.Committed,
            UpdatedUtc = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow)
        };
        ValidateSuccessor(prepared, committed);
        return committed;
    }

    internal static void Validate(LegacyRoutingMigrationMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        RoutingValidation.Require(
            marker.SchemaVersion == LegacyRoutingMigrationMarkerStore.CurrentSchemaVersion,
            "The legacy routing marker schema is unsupported.");
        RoutingValidation.Require(marker.Generation > 0 && marker.MigrationId != Guid.Empty,
            "The legacy routing marker identity is invalid.");
        RoutingValidation.Require(Enum.IsDefined(marker.Phase) && Enum.IsDefined(marker.Mode) &&
                                  marker.Scope == LegacyRoutingCutoverScope.FutureClipsOnly,
            "The legacy routing marker state is unsupported.");
        RoutingValidation.RequireSha256(marker.SourceFingerprint,
            "legacy routing source fingerprint");
        RoutingValidation.RequireSha256(marker.PayloadFingerprint,
            "legacy routing payload fingerprint");
        RoutingValidation.RequireUtc(marker.CreatedUtc, "legacy routing marker creation timestamp");
        RoutingValidation.RequireUtc(marker.UpdatedUtc, "legacy routing marker update timestamp");
        RoutingValidation.Require(marker.UpdatedUtc >= marker.CreatedUtc,
            "The legacy routing marker timestamps are inconsistent.");
        RoutingValidation.Require(
            marker.Phase == LegacyRoutingMigrationMarkerPhase.Prepared && marker.Generation == 1 ||
            marker.Phase == LegacyRoutingMigrationMarkerPhase.Committed && marker.Generation == 2,
            "The legacy routing marker phase and generation are inconsistent.");
        ValidateExclusions(marker.ContentHashExclusions);
        ValidateMigrationRoute(marker.Route, marker.Mode);
        RoutingValidation.Require(
            marker.PayloadFingerprint == LegacyRoutingMigrationPlanner.CreatePayloadFingerprint(
                marker.SourceFingerprint,
                marker.Mode,
                marker.Scope,
                marker.Route,
                marker.ContentHashExclusions),
            "The legacy routing marker payload fingerprint does not match its durable plan.");
        RoutingValidation.Require(
            marker.MigrationId == LegacyRoutingMigrationPlanner.DeterministicGuid(
                marker.SourceFingerprint, "migration") &&
            marker.Route.RouteId == LegacyRoutingMigrationPlanner.DeterministicGuid(
                marker.SourceFingerprint, "route") &&
            marker.Route.Actions[^1].ActionId == LegacyRoutingMigrationPlanner.DeterministicGuid(
                marker.SourceFingerprint, "file-action") &&
            (marker.Mode != LegacyRoutingMode.DiscordUpload ||
             marker.Route.Actions[0].ActionId == LegacyRoutingMigrationPlanner.DeterministicGuid(
                 marker.SourceFingerprint, "discord-action")),
            "The legacy routing marker ids are not bound to its exact migration plan.");
    }

    internal static void ValidateSuccessor(
        LegacyRoutingMigrationMarker current,
        LegacyRoutingMigrationMarker candidate)
    {
        Validate(current);
        Validate(candidate);
        RoutingValidation.Require(
            current.Phase == LegacyRoutingMigrationMarkerPhase.Prepared &&
            candidate.Phase == LegacyRoutingMigrationMarkerPhase.Committed &&
            candidate.Generation == current.Generation + 1 &&
            candidate.MigrationId == current.MigrationId &&
            candidate.Mode == current.Mode && candidate.Scope == current.Scope &&
            candidate.SourceFingerprint == current.SourceFingerprint &&
            candidate.PayloadFingerprint == current.PayloadFingerprint &&
            candidate.Route == current.Route &&
            candidate.ContentHashExclusions == current.ContentHashExclusions &&
            candidate.CreatedUtc == current.CreatedUtc && candidate.UpdatedUtc >= current.UpdatedUtc,
            "The legacy routing marker successor changed immutable cutover state.");
    }

    private static void ValidateInitial(LegacyRoutingMigrationMarker marker)
    {
        Validate(marker);
        RoutingValidation.Require(marker.Generation == 1 &&
                                  marker.Phase == LegacyRoutingMigrationMarkerPhase.Prepared &&
                                  marker.CreatedUtc == marker.UpdatedUtc,
            "The initial legacy routing marker must be prepared.");
    }

    internal static Action<LegacyRoutingMigrationMarker> InitialValidator => ValidateInitial;

    private static void ValidateExclusions(LegacyContentHashExclusions exclusions)
    {
        if (exclusions is null)
            throw new InvalidDataException("The legacy content-hash exclusions are missing.");
        ValidateHashList(exclusions.Known, "known");
        ValidateHashList(exclusions.Uploaded, "uploaded");
        ValidateHashList(exclusions.LocalOnly, "local-only");
        RoutingValidation.Require(
            exclusions.Known.Count + exclusions.Uploaded.Count + exclusions.LocalOnly.Count <=
            LegacyRoutingMigrationPlanner.MaximumPersistedHashOccurrences,
            "The legacy content-hash exclusions exceed the bounded marker capacity.");
        RoutingValidation.Require(!exclusions.Uploaded.Intersect(
                                      exclusions.LocalOnly, StringComparer.Ordinal).Any(),
            "A migrated hash cannot be both Uploaded and Local only.");
        var known = exclusions.Known.ToHashSet(StringComparer.Ordinal);
        RoutingValidation.Require(exclusions.Uploaded.All(known.Contains) &&
                                  exclusions.LocalOnly.All(known.Contains),
            "Every classified legacy hash must remain a known-content exclusion.");
    }

    private static void ValidateHashList(IReadOnlyList<string> hashes, string description)
    {
        if (hashes is null)
            throw new InvalidDataException($"The {description} hash exclusions are missing.");
        RoutingValidation.Require(hashes.Count <= 100_000,
            "The legacy routing marker contains too many hash exclusions.");
        string? previous = null;
        foreach (var hash in hashes)
        {
            RoutingValidation.RequireSha256(hash, $"{description} content hash");
            RoutingValidation.Require(hash == hash.ToUpperInvariant() &&
                                      (previous is null || StringComparer.Ordinal.Compare(previous, hash) < 0),
                "Legacy content hashes must be uppercase, unique, and sorted.");
            previous = hash;
        }
    }

    private static void ValidateMigrationRoute(RoutingRoute route, LegacyRoutingMode mode)
    {
        if (route is null) throw new InvalidDataException("The migrated fallback route is missing.");
        var timestamp = route.CreatedUtc;
        RoutingSnapshotModel.Validate(new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            Routes: [route],
            CreatedUtc: timestamp,
            UpdatedUtc: timestamp));
        RoutingValidation.Require(route.Source == RoutingRouteSource.Migration &&
                                  route.Kind == RoutingRouteKind.Fallback &&
                                  route.Trigger == RoutingTriggerKind.AnyNewSourceClip &&
                                  route.Conditions.Count == 0 &&
                                  route.Prepare is
                                  {
                                      Landscape: false,
                                      Portrait: false,
                                      DefaultOnMissing: RoutingMissingOutputBehavior.UseOriginal
                                  },
            "The legacy migration must create one unconditional migration fallback route.");
        if (mode == LegacyRoutingMode.DiscordUpload)
        {
            RoutingValidation.Require(route.Actions.Count == 2 &&
                                      route.Actions[0] is { Kind: RoutingActionKind.Deliver,
                                          Enabled: true,
                                          Destination: RoutingDestinationKind.Discord,
                                          OutputRef: RoutingOutputKind.Original,
                                          OnMissingOutput: RoutingMissingOutputBehavior.UseOriginal,
                                          Mode: RoutingDeliveryMode.Automatic } &&
                                      route.Actions[1] is { Kind: RoutingActionKind.FileIntoLibrary,
                                          Enabled: true,
                                          LibraryArea: RoutingLibraryArea.Uploaded },
                "Discord migration must deliver the original and then file it as Uploaded.");
        }
        else
        {
            RoutingValidation.Require(route.Actions.Count == 1 &&
                                      route.Actions[0] is { Kind: RoutingActionKind.FileIntoLibrary,
                                          Enabled: true,
                                          LibraryArea: RoutingLibraryArea.LocalOnly,
                                          ConnectionId: null },
                "Local-only migration must be a connectionless File into Library route.");
        }
    }
}

/// <summary>
/// A bounded generation-CAS marker. A committed marker is the only future activation gate;
/// neither this store nor the coordinator starts or disables either runtime.
/// </summary>
internal sealed class LegacyRoutingMigrationMarkerStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = 8 * 1024 * 1024;
    internal const string FileName = ".legacy-cutover.json";

    private readonly RoutingAtomicJsonStore<LegacyRoutingMigrationMarker> _store;

    internal LegacyRoutingMigrationMarkerStore()
        : this(System.IO.Path.Combine(SettingsStore.DataDirectory, "routing", FileName))
    {
    }

    internal LegacyRoutingMigrationMarkerStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.GetFileName(canonical).Equals(FileName, StringComparison.Ordinal))
            throw new ArgumentException($"The cutover marker must be named {FileName}.", nameof(path));
        _store = new RoutingAtomicJsonStore<LegacyRoutingMigrationMarker>(
            canonical,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            marker => marker.SchemaVersion,
            marker => marker.Generation,
            LegacyRoutingMigrationMarkerModel.Validate,
            LegacyRoutingMigrationMarkerModel.ValidateSuccessor,
            LegacyRoutingMigrationMarkerModel.InitialValidator);
    }

    internal string Path => _store.Path;

    internal RoutingDocumentLoadResult<LegacyRoutingMigrationMarker> Load(
        CancellationToken cancellationToken = default) => _store.Load(cancellationToken);

    internal Task<LegacyRoutingMigrationMarker> SaveAsync(
        LegacyRoutingMigrationMarker marker,
        long expectedGeneration,
        CancellationToken cancellationToken = default,
        Action? beforeCommit = null) =>
        _store.SaveAsync(marker, expectedGeneration, cancellationToken, beforeCommit);
}

/// <summary>
/// Crash-safe two-phase cutover. Prepared is written before the route; Committed is written only
/// after the exact route is durable. The legacy runtime remains authoritative until a later
/// integration explicitly consumes the committed marker.
/// </summary>
internal sealed class LegacyRoutingMigrationCoordinator
{
    private readonly RoutingSnapshotStore _routes;
    private readonly LegacyRoutingMigrationMarkerStore _markers;

    internal LegacyRoutingMigrationCoordinator(
        RoutingSnapshotStore routes,
        LegacyRoutingMigrationMarkerStore markers)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _markers = markers ?? throw new ArgumentNullException(nameof(markers));
    }

    internal async Task<LegacyRoutingCutoverResult> ExecuteAsync(
        LegacyRoutingMigrationInput input,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var timestamp = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        var readiness = LegacyRoutingMigrationPlanner.Evaluate(input, timestamp);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loadedMarker = _markers.Load(cancellationToken);
            if (loadedMarker.LoadedFromDisk)
            {
                var marker = loadedMarker.Document!;
                if (marker.Phase == LegacyRoutingMigrationMarkerPhase.Committed)
                {
                    return VerifyCommitted(marker, readiness, cancellationToken);
                }
                if (!readiness.CanCommit || readiness.Plan!.SourceFingerprint != marker.SourceFingerprint ||
                    readiness.Plan.MigrationId != marker.MigrationId)
                {
                    return Result(LegacyRoutingCutoverResultStatus.StateConflict,
                        "The prepared cutover no longer matches the quiesced legacy state.",
                        readiness, marker);
                }
            }
            else if (loadedMarker.Status != RoutingDocumentLoadStatus.Missing)
            {
                return Result(LegacyRoutingCutoverResultStatus.MarkerUnavailable,
                    $"The durable cutover marker cannot be used safely ({loadedMarker.Status}).",
                    readiness, null);
            }
            else
            {
                if (!readiness.CanCommit)
                    return Result(LegacyRoutingCutoverResultStatus.Blocked,
                        readiness.Reason, readiness, null);
                var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(readiness.Plan!, timestamp);
                try
                {
                    await _markers.SaveAsync(prepared, expectedGeneration: 0,
                        cancellationToken);
                }
                catch (RoutingConcurrencyException) when (attempt < 7)
                {
                    continue;
                }
            }

            var currentMarker = _markers.Load(cancellationToken);
            if (!currentMarker.LoadedFromDisk ||
                currentMarker.Document!.Phase != LegacyRoutingMigrationMarkerPhase.Prepared)
            {
                continue;
            }
            var durablePrepared = currentMarker.Document;
            var routeResult = await EnsureRouteAsync(durablePrepared.Route, timestamp,
                cancellationToken);
            if (routeResult is not null)
                return Result(routeResult.Value.Status, routeResult.Value.Reason,
                    readiness, durablePrepared);

            var committed = LegacyRoutingMigrationMarkerModel.Commit(durablePrepared, timestamp);
            try
            {
                committed = await _markers.SaveAsync(
                    committed,
                    durablePrepared.Generation,
                    cancellationToken);
                return Result(LegacyRoutingCutoverResultStatus.Committed,
                    "The legacy fallback route and cutover marker are durable.",
                    readiness, committed);
            }
            catch (RoutingConcurrencyException) when (attempt < 7)
            {
                // Another instance either committed the same marker or changed the state.
            }
        }

        return Result(LegacyRoutingCutoverResultStatus.StateConflict,
            "The migration state kept changing during cutover.", readiness, null);
    }

    private LegacyRoutingCutoverResult VerifyCommitted(
        LegacyRoutingMigrationMarker marker,
        LegacyRoutingCutoverReadiness readiness,
        CancellationToken cancellationToken)
    {
        var routes = _routes.Load(cancellationToken);
        if (!routes.LoadedFromDisk)
        {
            return Result(LegacyRoutingCutoverResultStatus.RoutingStateUnavailable,
                $"The committed route snapshot is unavailable ({routes.Status}).",
                readiness, marker);
        }
        var migrationRoute = routes.Document!.Routes.SingleOrDefault(route =>
            route.RouteId == marker.Route.RouteId);
        if (migrationRoute is null ||
            !LegacyRoutingMigrationPlanner.IsEquivalentMigrationRoute(
                migrationRoute, marker.Route))
        {
            return Result(LegacyRoutingCutoverResultStatus.StateConflict,
                "The committed migration marker does not match its durable fallback route.",
                readiness, marker);
        }
        return Result(LegacyRoutingCutoverResultStatus.AlreadyCommitted,
            "The equivalent legacy cutover is already committed.", readiness, marker);
    }

    private async Task<(LegacyRoutingCutoverResultStatus Status, string Reason)?> EnsureRouteAsync(
        RoutingRoute planned,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var loaded = _routes.Load(cancellationToken);
            if (loaded.Status == RoutingDocumentLoadStatus.Missing)
            {
                var initial = new RoutingSnapshotDocument(
                    RoutingSnapshotStore.CurrentSchemaVersion,
                    Generation: 1,
                    Routes: [planned],
                    CreatedUtc: planned.CreatedUtc,
                    UpdatedUtc: planned.ModifiedUtc);
                try
                {
                    await _routes.SaveAsync(initial, expectedGeneration: 0, cancellationToken);
                    return null;
                }
                catch (RoutingConcurrencyException) when (attempt < 5)
                {
                    continue;
                }
            }
            if (!loaded.LoadedFromDisk)
            {
                return (LegacyRoutingCutoverResultStatus.RoutingStateUnavailable,
                    $"The route snapshot cannot be used safely ({loaded.Status}).");
            }

            var document = loaded.Document!;
            if (document.Routes.Count == 1 &&
                LegacyRoutingMigrationPlanner.IsEquivalentMigrationRoute(document.Routes[0], planned))
                return null;
            if (document.Routes.Count != 0)
            {
                return (LegacyRoutingCutoverResultStatus.StateConflict,
                    "Migration will not replace or merge an existing non-equivalent route snapshot.");
            }
            var next = RoutingSnapshotModel.ReplaceRoutes(document, [planned], timestamp);
            try
            {
                await _routes.SaveAsync(next, document.Generation, cancellationToken);
                return null;
            }
            catch (RoutingConcurrencyException) when (attempt < 5)
            {
                // Re-evaluate the winning snapshot rather than overwriting it.
            }
        }
        return (LegacyRoutingCutoverResultStatus.StateConflict,
            "The route snapshot kept changing during cutover.");
    }

    private static LegacyRoutingCutoverResult Result(
        LegacyRoutingCutoverResultStatus status,
        string reason,
        LegacyRoutingCutoverReadiness readiness,
        LegacyRoutingMigrationMarker? marker) => new(status, reason, readiness, marker);
}
