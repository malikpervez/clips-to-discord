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
    NoLegacyUpgradeEvidence,
    NoFreshProfileEvidence,
    LegacyWorkerActive,
    PendingLegacyWork,
    UnreconciledIgnoredFiles,
    InvalidLegacySettings,
    InvalidWatchState,
    MissingDiscordConnection,
    AmbiguousDiscordConnection,
    InvalidDiscordConnection,
    InvalidFreshRoute
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
    Aborting,
    Committed
}

/// <summary>
/// Persisted discriminator for the two ways Routing can acquire initial authority. Legacy is
/// deliberately zero so markers written before this field existed deserialize to their original
/// meaning without changing their schema or payload fingerprint.
/// </summary>
internal enum RoutingActivationOrigin
{
    LegacyMigration = 0,
    FreshSetup = 1
}

/// <summary>
/// The connector slice supplies opaque ids. Migration never derives an id from, nor persists,
/// the legacy webhook URL or token.
/// </summary>
internal sealed record LegacyRoutingMigrationInput(
    AppSettings Settings,
    WatchState WatchState,
    bool LegacyWorkerQuiesced,
    IReadOnlyList<string> DiscordConnectionIds,
    RoutingCaptureLibraryBinding CaptureLibraryBinding,
    LegacyRoutingMigrationAdmission Admission,
    int ImportedRouteLabelVersion =
        LegacyRoutingMigrationPlanner.CurrentImportedRouteLabelVersion);

/// <summary>
/// Explicit new-profile setup input. Unlike legacy migration, the route is supplied by the user
/// and no legacy destination is inferred or imported. The root identity must come from the same
/// stable native-source inspection that produced the supplied watcher baseline.
/// </summary>
internal sealed record FreshRoutingSetupInput(
    AppSettings Settings,
    WatchState WatchState,
    bool LegacyWorkerQuiesced,
    RoutingRouteDraft FirstRoute,
    RoutingCaptureLibraryBinding CaptureLibraryBinding,
    string WatchedRootIdentitySha256,
    LegacyRoutingMigrationAdmission Admission);

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
    RoutingCaptureLibraryBinding CaptureLibraryBinding,
    RoutingRoute Route,
    LegacyContentHashExclusions ContentHashExclusions,
    RoutingActivationOrigin Origin = RoutingActivationOrigin.LegacyMigration,
    int? ImportedRouteLabelVersion = null);

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
    RoutingCaptureLibraryBinding CaptureLibraryBinding,
    RoutingRoute Route,
    LegacyContentHashExclusions ContentHashExclusions,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    RoutingActivationOrigin Origin = RoutingActivationOrigin.LegacyMigration,
    int? ImportedRouteLabelVersion = null);

/// <summary>
/// Pure translation of the 1.x settings and exclusion state. It describes only future clips;
/// hashes already known to 1.x remain exclusions and are never converted into delivery work.
/// </summary>
internal static class LegacyRoutingMigrationPlanner
{
    internal const string ImportedRouteLabel = "Imported from ClipCord 1.x";
    internal const int LegacyImportedRouteLabelVersion = 1;
    internal const int CurrentImportedRouteLabelVersion = 2;
    private const string DiscordRouteName = "Everything else → Friends server";
    private const string LocalRouteName = "Everything else → Local only";
    internal const int MaximumPersistedHashOccurrences = 90_000;

    internal static LegacyRoutingCutoverReadiness Evaluate(
        LegacyRoutingMigrationInput input,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Settings is null || input.WatchState is null ||
            input.DiscordConnectionIds is null || input.CaptureLibraryBinding is null)
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.InvalidLegacySettings,
                "The legacy migration input is incomplete.");
        }
        if (input.Admission != LegacyRoutingMigrationAdmission.ValidLegacyUpgrade)
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.NoLegacyUpgradeEvidence,
                "No valid pre-2.0 ClipCord settings and watcher state were found. Set up Routes as a new profile.");
        }
        if (input.ImportedRouteLabelVersion is not
            (LegacyImportedRouteLabelVersion or CurrentImportedRouteLabelVersion))
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.InvalidLegacySettings,
                "The imported route label version is unsupported.");
        }
        if (!input.LegacyWorkerQuiesced)
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.LegacyWorkerActive,
                "The legacy watcher must be stopped before routing cutover.");
        }

        try
        {
            RoutingCaptureLibraryBindingModel.Validate(input.CaptureLibraryBinding);
        }
        catch (InvalidDataException)
        {
            return Blocked(LegacyRoutingCutoverReadinessStatus.InvalidLegacySettings,
                "The Capture library identity required for Routing is invalid.");
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
            input.CaptureLibraryBinding,
            exclusions!,
            input.ImportedRouteLabelVersion);
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
            input.ImportedRouteLabelVersion == CurrentImportedRouteLabelVersion
                ? ImportedRouteLabel
                : mode == LegacyRoutingMode.DiscordUpload ? DiscordRouteName : LocalRouteName,
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
            input.CaptureLibraryBinding,
            route,
            exclusions!,
            RoutingActivationOrigin.LegacyMigration,
            input.ImportedRouteLabelVersion);
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

    internal static bool TryValidateSettingsAndState(
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

    internal static bool TryCreateExclusions(
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
        RoutingCaptureLibraryBinding captureLibraryBinding,
        LegacyContentHashExclusions exclusions,
        int importedRouteLabelVersion = LegacyImportedRouteLabelVersion)
    {
        RoutingCaptureLibraryBindingModel.Validate(captureLibraryBinding);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "clipcord-legacy-routing-v2");
        Append(hash, mode.ToString());
        Append(hash, Path.GetFullPath(clipsFolder).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar).ToUpperInvariant());
        Append(hash, AppSettings.NormalizeCaptureSource(captureSource).ToString());
        Append(hash, connectionId ?? string.Empty);
        Append(hash, captureLibraryBinding.CanonicalPathFingerprint);
        Append(hash, captureLibraryBinding.NativeDirectoryIdentityFingerprint);
        if (importedRouteLabelVersion == CurrentImportedRouteLabelVersion)
            Append(hash, "imported-route-label-v2");
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
        RoutingCaptureLibraryBinding captureLibraryBinding,
        RoutingRoute route,
        LegacyContentHashExclusions exclusions,
        int? importedRouteLabelVersion = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "clipcord-legacy-marker-payload-v2");
        Append(hash, sourceFingerprint);
        Append(hash, mode.ToString());
        Append(hash, scope.ToString());
        Append(hash, captureLibraryBinding.CanonicalPathFingerprint);
        Append(hash, captureLibraryBinding.NativeDirectoryIdentityFingerprint);
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
        // Null and v1 deliberately append nothing: markers written before this field existed
        // retain their exact payload digest. New imported-label markers bind the persisted choice.
        if (importedRouteLabelVersion == CurrentImportedRouteLabelVersion)
            Append(hash, "imported-route-label-version:2");
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

/// <summary>
/// Pure planner for an explicitly submitted first route on a durably fresh profile. It reuses
/// the legacy watch-state baseline only as safety evidence; it never creates a migration route,
/// imports a destination, or assigns an imported-route label.
/// </summary>
internal static class FreshRoutingSetupPlanner
{
    private const string SourceFingerprintDomain = "clipcord-fresh-routing-source-v2";
    private const string RouteDefinitionFingerprintDomain =
        "clipcord-fresh-routing-route-definition-v1";
    private const string SetupFingerprintDomain = "clipcord-fresh-routing-setup-v1";
    private const string PayloadFingerprintDomain = "clipcord-fresh-routing-payload-v1";

    internal static LegacyRoutingCutoverReadiness Evaluate(
        FreshRoutingSetupInput input,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Settings is null || input.WatchState is null || input.FirstRoute is null ||
            input.CaptureLibraryBinding is null)
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.InvalidFreshRoute,
                "The fresh Routing setup input is incomplete.");
        }
        if (input.Admission != LegacyRoutingMigrationAdmission.FreshOrInvalidProfile)
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.NoFreshProfileEvidence,
                "Fresh Routing setup requires a durable negative legacy-migration decision.");
        }
        if (!input.LegacyWorkerQuiesced)
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.LegacyWorkerActive,
                "The compatibility watcher must be stopped before fresh Routing setup commits.");
        }

        try
        {
            RoutingCaptureLibraryBindingModel.Validate(input.CaptureLibraryBinding);
        }
        catch (InvalidDataException)
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.InvalidLegacySettings,
                "The Capture library identity required for fresh Routing setup is invalid.");
        }
        try
        {
            RoutingValidation.RequireSha256(
                input.WatchedRootIdentitySha256,
                "fresh watched-source root identity");
        }
        catch (InvalidDataException)
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.InvalidWatchState,
                "The native watched-source identity required for fresh Routing setup is invalid.");
        }

        var state = input.WatchState;
        if (state.PendingMoves is null || state.PendingLocalOnlyMoves is null ||
            state.PendingEditedUploads is null || state.IgnoredFileKeys is null ||
            state.KnownContentHashes is null || state.UploadedContentHashes is null ||
            state.LocalOnlyContentHashes is null)
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.InvalidWatchState,
                "The compatibility watcher state is incomplete.");
        }
        if (state.PendingMoves.Count != 0 || state.PendingLocalOnlyMoves.Count != 0 ||
            state.PendingEditedUploads.Count != 0)
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.PendingLegacyWork,
                "All compatibility-watcher queues must drain before fresh Routing setup.");
        }
        if (state.IgnoredFileKeys.Count != 0)
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.UnreconciledIgnoredFiles,
                "Every compatibility-watcher baseline file must be reconciled before fresh Routing setup.");
        }
        if (!LegacyRoutingMigrationPlanner.TryValidateSettingsAndState(
                input.Settings,
                state,
                out var settingsReason))
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.InvalidLegacySettings,
                settingsReason);
        }
        if (!LegacyRoutingMigrationPlanner.TryCreateExclusions(
                state,
                out var exclusions,
                out var exclusionReason))
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.InvalidWatchState,
                exclusionReason);
        }
        if (exclusions!.Known.Count + exclusions.Uploaded.Count + exclusions.LocalOnly.Count >
            LegacyRoutingMigrationPlanner.MaximumPersistedHashOccurrences)
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.InvalidWatchState,
                "The fresh baseline exclusion set is too large for the bounded activation marker.");
        }

        try
        {
            RoutingRouteManager.ValidateDraft(input.FirstRoute);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or OverflowException)
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.InvalidFreshRoute,
                $"The first route is invalid: {exception.Message}");
        }

        var timestamp = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        var sourceFingerprint = CreateSourceFingerprint(
            input.Settings,
            input.CaptureLibraryBinding,
            exclusions,
            input.WatchedRootIdentitySha256);
        var routeDefinitionFingerprint = CreateRouteDefinitionFingerprint(input.FirstRoute);
        var setupFingerprint = CreateSetupFingerprint(
            sourceFingerprint,
            routeDefinitionFingerprint);
        var actionCount = (input.FirstRoute.Destination is null ? 0 : 1) +
                          (input.FirstRoute.FileIntoLibrary ? 1 : 0);
        var conditionCount = (string.IsNullOrWhiteSpace(input.FirstRoute.Game) ? 0 : 1) +
                             (input.FirstRoute.WatchedSourceId is null ? 0 : 1) +
                             (input.FirstRoute.EarliestCapturedUtc is null ? 0 : 1);
        var route = RoutingRouteManager.CreateRoute(
            input.FirstRoute,
            LegacyRoutingMigrationPlanner.DeterministicGuid(
                setupFingerprint,
                "fresh-route"),
            Enumerable.Range(0, actionCount)
                .Select(index => LegacyRoutingMigrationPlanner.DeterministicGuid(
                    setupFingerprint,
                    $"fresh-action-{index}"))
                .ToArray(),
            Enumerable.Range(0, conditionCount)
                .Select(index => LegacyRoutingMigrationPlanner.DeterministicGuid(
                    setupFingerprint,
                    $"fresh-condition-{index}"))
                .ToArray(),
            priority: 0,
            timestamp);
        try
        {
            ValidateFreshRoute(route, sourceFingerprint);
        }
        catch (InvalidDataException exception)
        {
            return Blocked(
                LegacyRoutingCutoverReadinessStatus.InvalidFreshRoute,
                $"The first route could not be represented safely: {exception.Message}");
        }

        var plan = new LegacyRoutingMigrationPlan(
            LegacyRoutingMigrationPlanner.DeterministicGuid(
                setupFingerprint,
                "fresh-setup"),
            // This legacy-shaped field is a compatibility placeholder only. Fresh marker
            // semantics are selected exclusively by Origin and the user-authored route.
            LegacyRoutingMode.LocalOnly,
            LegacyRoutingCutoverScope.FutureClipsOnly,
            sourceFingerprint,
            input.CaptureLibraryBinding,
            route,
            exclusions,
            RoutingActivationOrigin.FreshSetup,
            ImportedRouteLabelVersion: null);
        return new LegacyRoutingCutoverReadiness(
            LegacyRoutingCutoverReadinessStatus.Ready,
            "The fresh first-route plan is ready.",
            plan);
    }

    internal static LegacyRoutingMigrationPlan ReconstructPlan(
        LegacyRoutingMigrationMarker marker)
    {
        LegacyRoutingMigrationMarkerModel.Validate(marker);
        RoutingValidation.Require(
            marker.Origin == RoutingActivationOrigin.FreshSetup,
            "Only a fresh setup marker can reconstruct a fresh plan.");
        return new LegacyRoutingMigrationPlan(
            marker.MigrationId,
            marker.Mode,
            marker.Scope,
            marker.SourceFingerprint,
            marker.CaptureLibraryBinding,
            marker.Route,
            marker.ContentHashExclusions,
            marker.Origin,
            marker.ImportedRouteLabelVersion);
    }

    /// <summary>
    /// Revalidates the durable, route-independent evidence for a fresh activation after restart.
    /// The marker's frozen first route remains payload-bound proof of what was initially created,
    /// but it is intentionally not compared with the live route snapshot: after authority commits,
    /// ordinary user edits and deletion are legitimate.
    /// </summary>
    internal static LegacyRoutingMigrationPlan ReconstructCurrentPlan(
        LegacyRoutingMigrationMarker marker,
        AppSettings settings,
        WatchState watchState,
        RoutingCaptureLibraryBinding captureLibraryBinding,
        string watchedRootIdentitySha256)
    {
        LegacyRoutingMigrationMarkerModel.Validate(marker);
        RoutingValidation.Require(
            marker.Origin == RoutingActivationOrigin.FreshSetup,
            "Only a fresh setup marker can be revalidated as fresh evidence.");
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(watchState);
        RoutingCaptureLibraryBindingModel.RequireExact(
            marker.CaptureLibraryBinding,
            captureLibraryBinding);
        var pendingMoves = watchState.PendingMoves ?? throw new InvalidDataException(
            "The compatibility watcher state is incomplete.");
        var pendingLocalOnlyMoves = watchState.PendingLocalOnlyMoves ??
                                    throw new InvalidDataException(
                                        "The compatibility watcher state is incomplete.");
        var pendingEditedUploads = watchState.PendingEditedUploads ??
                                   throw new InvalidDataException(
                                       "The compatibility watcher state is incomplete.");
        var ignoredFileKeys = watchState.IgnoredFileKeys ?? throw new InvalidDataException(
            "The compatibility watcher state is incomplete.");
        RoutingValidation.Require(
            watchState.KnownContentHashes is not null &&
            watchState.UploadedContentHashes is not null &&
            watchState.LocalOnlyContentHashes is not null,
            "The compatibility watcher state is incomplete.");
        RoutingValidation.Require(
            pendingMoves.Count == 0 &&
            pendingLocalOnlyMoves.Count == 0 &&
            pendingEditedUploads.Count == 0,
            "Fresh Routing evidence cannot retain compatibility-watcher work.");
        RoutingValidation.Require(
            ignoredFileKeys.Count == 0,
            "Fresh Routing evidence cannot retain an unreconciled compatibility baseline.");
        RoutingValidation.Require(
            LegacyRoutingMigrationPlanner.TryValidateSettingsAndState(
                settings,
                watchState,
                out var settingsReason),
            settingsReason);
        RoutingValidation.Require(
            LegacyRoutingMigrationPlanner.TryCreateExclusions(
                watchState,
                out var exclusions,
                out var exclusionReason),
            exclusionReason);
        RoutingValidation.Require(
            exclusions == marker.ContentHashExclusions,
            "The current compatibility exclusions do not match the fresh activation marker.");
        RoutingValidation.Require(
            CreateSourceFingerprint(
                settings,
                captureLibraryBinding,
                exclusions!,
                watchedRootIdentitySha256) ==
            marker.SourceFingerprint,
            "The current settings, watched source, or compatibility state do not match the fresh activation marker.");
        return ReconstructPlan(marker);
    }

    internal static string CreatePayloadFingerprint(
        string sourceFingerprint,
        RoutingCaptureLibraryBinding captureLibraryBinding,
        RoutingRoute route,
        LegacyContentHashExclusions exclusions)
    {
        RoutingValidation.RequireSha256(
            sourceFingerprint,
            "fresh Routing source fingerprint");
        RoutingCaptureLibraryBindingModel.Validate(captureLibraryBinding);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, PayloadFingerprintDomain);
        Append(hash, RoutingActivationOrigin.FreshSetup.ToString());
        Append(hash, sourceFingerprint);
        Append(hash, captureLibraryBinding.CanonicalPathFingerprint);
        Append(hash, captureLibraryBinding.NativeDirectoryIdentityFingerprint);
        AppendRoute(hash, route);
        AppendExclusions(hash, exclusions);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void ValidateFreshRoute(RoutingRoute route, string sourceFingerprint)
    {
        ArgumentNullException.ThrowIfNull(route);
        RoutingValidation.RequireSha256(
            sourceFingerprint,
            "fresh Routing source fingerprint");
        RoutingSnapshotModel.Validate(new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            Routes: [route],
            route.CreatedUtc,
            route.ModifiedUtc));
        var setupFingerprint = CreateSetupFingerprint(
            sourceFingerprint,
            CreateRouteDefinitionFingerprint(route));
        RoutingValidation.Require(
            route.Source == RoutingRouteSource.User && route.Enabled &&
            route.Priority == 0 && route.Revision == 1 &&
            route.CreatedUtc == route.ModifiedUtc &&
            route.RouteId == LegacyRoutingMigrationPlanner.DeterministicGuid(
                setupFingerprint,
                "fresh-route") &&
            route.Actions.Select((action, index) => action.ActionId ==
                    LegacyRoutingMigrationPlanner.DeterministicGuid(
                        setupFingerprint,
                        $"fresh-action-{index}"))
                .All(matches => matches) &&
            route.Conditions.Select((condition, index) => condition.ConditionId ==
                    LegacyRoutingMigrationPlanner.DeterministicGuid(
                        setupFingerprint,
                        $"fresh-condition-{index}"))
                .All(matches => matches),
            "A fresh setup marker requires one exact deterministic user route.");
    }

    internal static Guid CreateSetupId(
        string sourceFingerprint,
        RoutingRoute route) => LegacyRoutingMigrationPlanner.DeterministicGuid(
            CreateSetupFingerprint(
                sourceFingerprint,
                CreateRouteDefinitionFingerprint(route)),
            "fresh-setup");

    internal static string CreateSourceFingerprint(
        AppSettings settings,
        RoutingCaptureLibraryBinding captureLibraryBinding,
        LegacyContentHashExclusions exclusions,
        string watchedRootIdentitySha256)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RoutingCaptureLibraryBindingModel.Validate(captureLibraryBinding);
        RoutingValidation.RequireSha256(
            watchedRootIdentitySha256,
            "fresh watched-source root identity");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, SourceFingerprintDomain);
        Append(hash, Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.ClipsFolder))
            .ToUpperInvariant());
        Append(hash, AppSettings.NormalizeCaptureSource(settings.CaptureSource).ToString());
        Append(hash, watchedRootIdentitySha256.ToLowerInvariant());
        Append(hash, captureLibraryBinding.CanonicalPathFingerprint);
        Append(hash, captureLibraryBinding.NativeDirectoryIdentityFingerprint);
        AppendExclusions(hash, exclusions);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string CreateRouteDefinitionFingerprint(RoutingRouteDraft draft)
    {
        var actionCount = (draft.Destination is null ? 0 : 1) +
                          (draft.FileIntoLibrary ? 1 : 0);
        var conditionCount = (string.IsNullOrWhiteSpace(draft.Game) ? 0 : 1) +
                             (draft.WatchedSourceId is null ? 0 : 1) +
                             (draft.EarliestCapturedUtc is null ? 0 : 1);
        var canonical = RoutingRouteManager.CreateRoute(
            draft,
            Guid.Empty,
            Enumerable.Repeat(Guid.Empty, actionCount).ToArray(),
            Enumerable.Repeat(Guid.Empty, conditionCount).ToArray(),
            priority: 0,
            DateTimeOffset.UnixEpoch);
        return CreateRouteDefinitionFingerprint(canonical);
    }

    private static string CreateRouteDefinitionFingerprint(RoutingRoute route)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, RouteDefinitionFingerprintDomain);
        Append(hash, route.Name);
        Append(hash, route.Kind.ToString());
        Append(hash, route.Trigger.ToString());
        Append(hash, route.Prepare.Landscape.ToString(CultureInfo.InvariantCulture));
        Append(hash, route.Prepare.Portrait.ToString(CultureInfo.InvariantCulture));
        Append(hash, route.Prepare.DefaultOnMissing.ToString());
        Append(hash, route.Conditions.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var condition in route.Conditions)
        {
            Append(hash, condition.Field.ToString());
            Append(hash, condition.Operator.ToString());
            Append(hash, condition.Value);
        }
        Append(hash, route.Actions.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var action in route.Actions)
        {
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
        if (route.XboxHistorySelection is null)
        {
            Append(hash, "route-xbox-history:absent");
        }
        else
        {
            Append(hash, "route-xbox-history:present");
            Append(hash, route.XboxHistorySelection.ActivationUtc.ToString(
                "O",
                CultureInfo.InvariantCulture));
            foreach (var occurrence in route.XboxHistorySelection.HistoricalOccurrences)
            {
                Append(hash, occurrence.OccurrenceId);
                Append(hash, occurrence.RevisionId);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string CreateSetupFingerprint(
        string sourceFingerprint,
        string routeDefinitionFingerprint)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, SetupFingerprintDomain);
        Append(hash, sourceFingerprint);
        Append(hash, routeDefinitionFingerprint);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendRoute(IncrementalHash hash, RoutingRoute route)
    {
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
        if (route.XboxHistorySelection is null)
        {
            Append(hash, "route-xbox-history:absent");
        }
        else
        {
            Append(hash, "route-xbox-history:present");
            Append(hash, route.XboxHistorySelection.ActivationUtc.ToString(
                "O",
                CultureInfo.InvariantCulture));
            foreach (var occurrence in route.XboxHistorySelection.HistoricalOccurrences)
            {
                Append(hash, occurrence.OccurrenceId);
                Append(hash, occurrence.RevisionId);
            }
        }
    }

    private static void AppendExclusions(
        IncrementalHash hash,
        LegacyContentHashExclusions exclusions)
    {
        ArgumentNullException.ThrowIfNull(exclusions);
        foreach (var value in exclusions.Known) Append(hash, "known:" + value);
        foreach (var value in exclusions.Uploaded) Append(hash, "uploaded:" + value);
        foreach (var value in exclusions.LocalOnly) Append(hash, "local:" + value);
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

    private static LegacyRoutingCutoverReadiness Blocked(
        LegacyRoutingCutoverReadinessStatus status,
        string reason) => new(status, reason, Plan: null);
}

internal static class LegacyRoutingMigrationMarkerModel
{
    internal static LegacyRoutingMigrationMarker CreatePrepared(
        LegacyRoutingMigrationPlan plan,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RoutingValidation.Require(Enum.IsDefined(plan.Origin),
            "The Routing activation origin is unsupported.");
        var timestamp = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        var marker = new LegacyRoutingMigrationMarker(
            LegacyRoutingMigrationMarkerStore.CurrentSchemaVersion,
            Generation: 1,
            plan.MigrationId,
            LegacyRoutingMigrationMarkerPhase.Prepared,
            plan.Mode,
            plan.Scope,
            plan.SourceFingerprint,
            CreatePayloadFingerprint(plan),
            plan.CaptureLibraryBinding,
            plan.Route,
            plan.ContentHashExclusions,
            CreatedUtc: timestamp,
            UpdatedUtc: timestamp,
            plan.Origin,
            plan.ImportedRouteLabelVersion);
        Validate(marker);
        return marker;
    }

    private static string CreatePayloadFingerprint(LegacyRoutingMigrationPlan plan) =>
        plan.Origin switch
        {
            RoutingActivationOrigin.LegacyMigration =>
                LegacyRoutingMigrationPlanner.CreatePayloadFingerprint(
                    plan.SourceFingerprint,
                    plan.Mode,
                    plan.Scope,
                    plan.CaptureLibraryBinding,
                    plan.Route,
                    plan.ContentHashExclusions,
                    plan.ImportedRouteLabelVersion),
            RoutingActivationOrigin.FreshSetup =>
                FreshRoutingSetupPlanner.CreatePayloadFingerprint(
                    plan.SourceFingerprint,
                    plan.CaptureLibraryBinding,
                    plan.Route,
                    plan.ContentHashExclusions),
            _ => throw new InvalidDataException(
                "The Routing activation plan origin is unsupported.")
        };

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

    internal static LegacyRoutingMigrationMarker BeginAbort(
        LegacyRoutingMigrationMarker prepared,
        DateTimeOffset? now = null)
    {
        Validate(prepared);
        RoutingValidation.Require(prepared.Phase == LegacyRoutingMigrationMarkerPhase.Prepared,
            "Only a prepared legacy migration can begin rollback.");
        var aborting = prepared with
        {
            Generation = RoutingValidation.NextGeneration(prepared.Generation),
            Phase = LegacyRoutingMigrationMarkerPhase.Aborting,
            UpdatedUtc = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow)
        };
        ValidateSuccessor(prepared, aborting);
        return aborting;
    }

    internal static LegacyRoutingMigrationMarker RestartPrepared(
        LegacyRoutingMigrationMarker aborting,
        LegacyRoutingMigrationPlan plan,
        DateTimeOffset? now = null)
    {
        Validate(aborting);
        ArgumentNullException.ThrowIfNull(plan);
        RoutingValidation.Require(aborting.Phase == LegacyRoutingMigrationMarkerPhase.Aborting,
            "Only a rolled-back legacy migration can prepare a replacement plan.");
        var timestamp = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        var prepared = new LegacyRoutingMigrationMarker(
            LegacyRoutingMigrationMarkerStore.CurrentSchemaVersion,
            RoutingValidation.NextGeneration(aborting.Generation),
            plan.MigrationId,
            LegacyRoutingMigrationMarkerPhase.Prepared,
            plan.Mode,
            plan.Scope,
            plan.SourceFingerprint,
            CreatePayloadFingerprint(plan),
            plan.CaptureLibraryBinding,
            plan.Route,
            plan.ContentHashExclusions,
            aborting.CreatedUtc,
            timestamp,
            plan.Origin,
            plan.ImportedRouteLabelVersion);
        ValidateSuccessor(aborting, prepared);
        return prepared;
    }

    internal static void Validate(LegacyRoutingMigrationMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        RoutingValidation.Require(
            marker.SchemaVersion == LegacyRoutingMigrationMarkerStore.CurrentSchemaVersion,
            "The legacy routing marker schema is unsupported.");
        RoutingValidation.Require(marker.Generation > 0 && marker.MigrationId != Guid.Empty,
            "The legacy routing marker identity is invalid.");
        RoutingValidation.Require(Enum.IsDefined(marker.Origin) &&
                                  Enum.IsDefined(marker.Phase) && Enum.IsDefined(marker.Mode) &&
                                  marker.Scope == LegacyRoutingCutoverScope.FutureClipsOnly,
            "The Routing activation marker state is unsupported.");
        RoutingValidation.RequireSha256(marker.SourceFingerprint,
            "legacy routing source fingerprint");
        RoutingValidation.RequireSha256(marker.PayloadFingerprint,
            "legacy routing payload fingerprint");
        RoutingCaptureLibraryBindingModel.Validate(marker.CaptureLibraryBinding);
        RoutingValidation.RequireUtc(marker.CreatedUtc, "legacy routing marker creation timestamp");
        RoutingValidation.RequireUtc(marker.UpdatedUtc, "legacy routing marker update timestamp");
        RoutingValidation.Require(marker.UpdatedUtc >= marker.CreatedUtc,
            "The legacy routing marker timestamps are inconsistent.");
        RoutingValidation.Require(
            marker.Phase == LegacyRoutingMigrationMarkerPhase.Prepared && marker.Generation >= 1 ||
            marker.Phase == LegacyRoutingMigrationMarkerPhase.Aborting && marker.Generation >= 2 ||
            marker.Phase == LegacyRoutingMigrationMarkerPhase.Committed && marker.Generation >= 2,
            "The legacy routing marker phase and generation are inconsistent.");
        ValidateExclusions(marker.ContentHashExclusions);
        if (marker.Origin == RoutingActivationOrigin.LegacyMigration)
        {
            var importedRouteLabelVersion = marker.ImportedRouteLabelVersion ??
                                            LegacyRoutingMigrationPlanner
                                                .LegacyImportedRouteLabelVersion;
            RoutingValidation.Require(importedRouteLabelVersion is
                    LegacyRoutingMigrationPlanner.LegacyImportedRouteLabelVersion or
                    LegacyRoutingMigrationPlanner.CurrentImportedRouteLabelVersion,
                "The persisted imported-route label version is unsupported.");
            ValidateMigrationRoute(marker.Route, marker.Mode, importedRouteLabelVersion);
            RoutingValidation.Require(
                marker.PayloadFingerprint ==
                LegacyRoutingMigrationPlanner.CreatePayloadFingerprint(
                    marker.SourceFingerprint,
                    marker.Mode,
                    marker.Scope,
                    marker.CaptureLibraryBinding,
                    marker.Route,
                    marker.ContentHashExclusions,
                    marker.ImportedRouteLabelVersion),
                "The legacy routing marker payload fingerprint does not match its durable plan.");
            RoutingValidation.Require(
                marker.MigrationId == LegacyRoutingMigrationPlanner.DeterministicGuid(
                    marker.SourceFingerprint, "migration") &&
                marker.Route.RouteId == LegacyRoutingMigrationPlanner.DeterministicGuid(
                    marker.SourceFingerprint, "route") &&
                marker.Route.Actions[^1].ActionId ==
                LegacyRoutingMigrationPlanner.DeterministicGuid(
                    marker.SourceFingerprint, "file-action") &&
                (marker.Mode != LegacyRoutingMode.DiscordUpload ||
                 marker.Route.Actions[0].ActionId ==
                 LegacyRoutingMigrationPlanner.DeterministicGuid(
                     marker.SourceFingerprint, "discord-action")),
                "The legacy routing marker ids are not bound to its exact migration plan.");
            return;
        }

        RoutingValidation.Require(
            marker.ImportedRouteLabelVersion is null && marker.Mode == LegacyRoutingMode.LocalOnly,
            "Fresh setup cannot carry legacy import presentation or mode evidence.");
        FreshRoutingSetupPlanner.ValidateFreshRoute(marker.Route, marker.SourceFingerprint);
        RoutingValidation.Require(
            marker.PayloadFingerprint == FreshRoutingSetupPlanner.CreatePayloadFingerprint(
                marker.SourceFingerprint,
                marker.CaptureLibraryBinding,
                marker.Route,
                marker.ContentHashExclusions),
            "The fresh Routing marker payload fingerprint does not match its durable plan.");
        RoutingValidation.Require(
            marker.MigrationId == FreshRoutingSetupPlanner.CreateSetupId(
                marker.SourceFingerprint,
                marker.Route),
            "The fresh Routing marker id is not bound to its exact setup plan.");
    }

    internal static void ValidateSuccessor(
        LegacyRoutingMigrationMarker current,
        LegacyRoutingMigrationMarker candidate)
    {
        Validate(current);
        Validate(candidate);
        var committing =
            current.Phase == LegacyRoutingMigrationMarkerPhase.Prepared &&
            candidate.Phase == LegacyRoutingMigrationMarkerPhase.Committed &&
            candidate.Generation == current.Generation + 1 &&
            candidate.MigrationId == current.MigrationId &&
            candidate.Origin == current.Origin &&
            candidate.ImportedRouteLabelVersion == current.ImportedRouteLabelVersion &&
            candidate.Mode == current.Mode && candidate.Scope == current.Scope &&
            candidate.SourceFingerprint == current.SourceFingerprint &&
            candidate.PayloadFingerprint == current.PayloadFingerprint &&
            candidate.CaptureLibraryBinding == current.CaptureLibraryBinding &&
            candidate.Route == current.Route &&
            candidate.ContentHashExclusions == current.ContentHashExclusions &&
            candidate.CreatedUtc == current.CreatedUtc && candidate.UpdatedUtc >= current.UpdatedUtc;
        var beginningAbort =
            current.Phase == LegacyRoutingMigrationMarkerPhase.Prepared &&
            candidate.Phase == LegacyRoutingMigrationMarkerPhase.Aborting &&
            candidate.Generation == current.Generation + 1 &&
            candidate.MigrationId == current.MigrationId &&
            candidate.Origin == current.Origin &&
            candidate.ImportedRouteLabelVersion == current.ImportedRouteLabelVersion &&
            candidate.Mode == current.Mode && candidate.Scope == current.Scope &&
            candidate.SourceFingerprint == current.SourceFingerprint &&
            candidate.PayloadFingerprint == current.PayloadFingerprint &&
            candidate.CaptureLibraryBinding == current.CaptureLibraryBinding &&
            candidate.Route == current.Route &&
            candidate.ContentHashExclusions == current.ContentHashExclusions &&
            candidate.CreatedUtc == current.CreatedUtc && candidate.UpdatedUtc >= current.UpdatedUtc;
        var restartingPrepared =
            current.Phase == LegacyRoutingMigrationMarkerPhase.Aborting &&
            candidate.Phase == LegacyRoutingMigrationMarkerPhase.Prepared &&
            candidate.Generation == current.Generation + 1 &&
            candidate.Origin == current.Origin &&
            candidate.CreatedUtc == current.CreatedUtc && candidate.UpdatedUtc >= current.UpdatedUtc;
        RoutingValidation.Require(
            committing || beginningAbort || restartingPrepared,
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

    private static void ValidateMigrationRoute(
        RoutingRoute route,
        LegacyRoutingMode mode,
        int importedRouteLabelVersion)
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
        var expectedName = importedRouteLabelVersion ==
                           LegacyRoutingMigrationPlanner.CurrentImportedRouteLabelVersion
            ? LegacyRoutingMigrationPlanner.ImportedRouteLabel
            : mode == LegacyRoutingMode.DiscordUpload
                ? "Everything else → Friends server"
                : "Everything else → Local only";
        RoutingValidation.Require(route.Name.Equals(expectedName, StringComparison.Ordinal),
            "The legacy migration route label does not match its persisted label version.");
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
    internal const int CurrentSchemaVersion = 2;
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
/// Crash-safe cutover. Prepared is written before the route; Committed is written only after the
/// exact route is durable and becomes a no-Legacy recovery fence. A stale reversible preparation
/// moves through Aborting while its exact route is removed before the latest legacy state is
/// prepared again.
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
        return await ExecuteAsync(
                readiness,
                RoutingActivationOrigin.LegacyMigration,
                timestamp,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<LegacyRoutingCutoverResult> ExecuteFreshAsync(
        FreshRoutingSetupInput input,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var timestamp = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        var readiness = FreshRoutingSetupPlanner.Evaluate(input, timestamp);
        return await ExecuteAsync(
                readiness,
                RoutingActivationOrigin.FreshSetup,
                timestamp,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a durably fenced fresh-profile activation after process restart. The original
    /// editor draft is intentionally not required: the marker owns the exact first route, while
    /// the current settings, watcher baseline, native watched-root identity, and Capture library
    /// identity must still reproduce its route-independent source evidence before any prepared
    /// transaction can continue.
    /// </summary>
    internal async Task<LegacyRoutingCutoverResult> ResumeFreshAsync(
        AppSettings settings,
        WatchState watchState,
        RoutingCaptureLibraryBinding captureLibraryBinding,
        string watchedRootIdentitySha256,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(watchState);
        ArgumentNullException.ThrowIfNull(captureLibraryBinding);
        var timestamp = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        var loadedMarker = _markers.Load(cancellationToken);
        if (!loadedMarker.LoadedFromDisk || loadedMarker.Document is not
            { Origin: RoutingActivationOrigin.FreshSetup } marker)
        {
            var reason = loadedMarker.Status == RoutingDocumentLoadStatus.Missing
                ? "No durable fresh activation marker is available to resume."
                : $"The durable fresh activation marker cannot be used safely ({loadedMarker.Status}).";
            var blocked = new LegacyRoutingCutoverReadiness(
                LegacyRoutingCutoverReadinessStatus.NoFreshProfileEvidence,
                reason,
                null);
            return Result(
                loadedMarker.Status == RoutingDocumentLoadStatus.Missing
                    ? LegacyRoutingCutoverResultStatus.StateConflict
                    : LegacyRoutingCutoverResultStatus.MarkerUnavailable,
                reason,
                blocked,
                loadedMarker.Document);
        }

        LegacyRoutingMigrationPlan plan;
        try
        {
            plan = FreshRoutingSetupPlanner.ReconstructCurrentPlan(
                marker,
                settings,
                watchState,
                captureLibraryBinding,
                watchedRootIdentitySha256);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or OverflowException)
        {
            var blocked = new LegacyRoutingCutoverReadiness(
                LegacyRoutingCutoverReadinessStatus.NoFreshProfileEvidence,
                exception.Message,
                null);
            return Result(
                LegacyRoutingCutoverResultStatus.Blocked,
                exception.Message,
                blocked,
                marker);
        }

        var readiness = new LegacyRoutingCutoverReadiness(
            LegacyRoutingCutoverReadinessStatus.Ready,
            "The durable fresh activation evidence is ready to resume.",
            plan);
        return await ExecuteAsync(
                readiness,
                RoutingActivationOrigin.FreshSetup,
                timestamp,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<LegacyRoutingCutoverResult> ExecuteAsync(
        LegacyRoutingCutoverReadiness readiness,
        RoutingActivationOrigin requestedOrigin,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loadedMarker = _markers.Load(cancellationToken);
            if (loadedMarker.LoadedFromDisk)
            {
                var marker = loadedMarker.Document!;
                if (marker.Origin != requestedOrigin)
                {
                    return Result(
                        LegacyRoutingCutoverResultStatus.StateConflict,
                        "The durable activation marker belongs to a different setup origin.",
                        readiness,
                        marker);
                }
                if (marker.Phase == LegacyRoutingMigrationMarkerPhase.Committed)
                {
                    return VerifyCommitted(marker, readiness, cancellationToken);
                }
                if (marker.Phase == LegacyRoutingMigrationMarkerPhase.Aborting ||
                    !readiness.CanCommit ||
                    readiness.Plan!.Origin != marker.Origin ||
                    readiness.Plan.ImportedRouteLabelVersion !=
                    marker.ImportedRouteLabelVersion ||
                    readiness.Plan!.SourceFingerprint != marker.SourceFingerprint ||
                    readiness.Plan.MigrationId != marker.MigrationId)
                {
                    try
                    {
                        var rollback = await RollBackAndReprepareAsync(
                                marker,
                                readiness,
                                timestamp,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (rollback is not null)
                        {
                            return Result(
                                rollback.Value.Status,
                                rollback.Value.Reason,
                                readiness,
                                rollback.Value.Marker);
                        }
                        continue;
                    }
                    catch (RoutingConcurrencyException) when (attempt < 7)
                    {
                        continue;
                    }
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
            var routeResult = await EnsureRouteAsync(durablePrepared, timestamp,
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
                    durablePrepared.Origin == RoutingActivationOrigin.FreshSetup
                        ? "The explicit first route and fresh activation marker are durable."
                        : "The legacy fallback route and cutover marker are durable.",
                    readiness, committed);
            }
            catch (RoutingConcurrencyException) when (attempt < 7)
            {
                // Another instance either committed the same marker or changed the state.
            }
        }

        return Result(LegacyRoutingCutoverResultStatus.StateConflict,
            "The Routing activation state kept changing during cutover.", readiness, null);
    }

    /// <summary>
    /// Prepared is not execution authority. If Legacy resumed after a crash and its exact state
    /// changed, first persist an Aborting fence, then remove only the exact uncommitted migration
    /// route, and finally prepare the newly drained state. Every crash point is replayable:
    /// Aborting never permits route creation or Routing activation.
    /// </summary>
    private async Task<(
        LegacyRoutingCutoverResultStatus Status,
        string Reason,
        LegacyRoutingMigrationMarker Marker)?> RollBackAndReprepareAsync(
        LegacyRoutingMigrationMarker marker,
        LegacyRoutingCutoverReadiness readiness,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var aborting = marker;
        if (marker.Phase == LegacyRoutingMigrationMarkerPhase.Prepared)
        {
            aborting = await _markers.SaveAsync(
                    LegacyRoutingMigrationMarkerModel.BeginAbort(marker, timestamp),
                    marker.Generation,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (marker.Phase != LegacyRoutingMigrationMarkerPhase.Aborting)
        {
            return (
                LegacyRoutingCutoverResultStatus.StateConflict,
                "The migration marker cannot be rolled back safely.",
                marker);
        }

        var cleanup = await RemoveExactPreparedRouteAsync(
                aborting,
                timestamp,
                cancellationToken)
            .ConfigureAwait(false);
        if (cleanup is not null)
        {
            return (cleanup.Value.Status, cleanup.Value.Reason, aborting);
        }
        if (!readiness.CanCommit)
        {
            return (
                LegacyRoutingCutoverResultStatus.Blocked,
                readiness.Reason,
                aborting);
        }

        _ = await _markers.SaveAsync(
                LegacyRoutingMigrationMarkerModel.RestartPrepared(
                    aborting,
                    readiness.Plan!,
                    timestamp),
                aborting.Generation,
                cancellationToken)
            .ConfigureAwait(false);
        return null;
    }

    private async Task<(LegacyRoutingCutoverResultStatus Status, string Reason)?>
        RemoveExactPreparedRouteAsync(
            LegacyRoutingMigrationMarker abortingMarker,
            DateTimeOffset timestamp,
            CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var loaded = _routes.Load(cancellationToken);
            if (loaded.Status == RoutingDocumentLoadStatus.Missing) return null;
            if (!loaded.LoadedFromDisk)
            {
                return (
                    LegacyRoutingCutoverResultStatus.RoutingStateUnavailable,
                    $"The route snapshot cannot be rolled back safely ({loaded.Status}).");
            }

            var document = loaded.Document!;
            if (document.Routes.Count == 0) return null;
            if (document.Routes.Count != 1 ||
                !IsEquivalentPreparedRoute(
                    abortingMarker,
                    document.Routes[0]))
            {
                return (
                    LegacyRoutingCutoverResultStatus.StateConflict,
                    "Migration rollback will not remove a non-equivalent route snapshot.");
            }
            var empty = RoutingSnapshotModel.ReplaceRoutes(
                document,
                [],
                timestamp);
            try
            {
                _ = await _routes.SaveAsync(
                        empty,
                        document.Generation,
                        cancellationToken,
                        beforeCommit: () => RequireMarkerCurrent(
                            abortingMarker,
                            LegacyRoutingMigrationMarkerPhase.Aborting))
                    .ConfigureAwait(false);
                return null;
            }
            catch (RoutingConcurrencyException) when (attempt < 5)
            {
                // Re-evaluate the winning route snapshot before removing anything.
            }
        }
        return (
            LegacyRoutingCutoverResultStatus.StateConflict,
            "The route snapshot kept changing during migration rollback.");
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
        if (marker.Origin == RoutingActivationOrigin.FreshSetup)
        {
            // The marker proves the exact first route that established authority. Once committed,
            // the live snapshot is user-owned and may legitimately contain revisions, replacement
            // routes, or no routes at all. Loading a structurally valid snapshot is therefore the
            // only route-store invariant that remains here.
            return Result(
                LegacyRoutingCutoverResultStatus.AlreadyCommitted,
                "The fresh first-route activation is already committed.",
                readiness,
                marker);
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
            "The equivalent legacy cutover is already committed.",
            readiness,
            marker);
    }

    private async Task<(LegacyRoutingCutoverResultStatus Status, string Reason)?> EnsureRouteAsync(
        LegacyRoutingMigrationMarker preparedMarker,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var planned = preparedMarker.Route;
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
                    await _routes.SaveAsync(
                        initial,
                        expectedGeneration: 0,
                        cancellationToken,
                        beforeCommit: () => RequireMarkerCurrent(
                            preparedMarker,
                            LegacyRoutingMigrationMarkerPhase.Prepared));
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
                IsEquivalentPreparedRoute(preparedMarker, document.Routes[0]))
                return null;
            if (document.Routes.Count != 0)
            {
                return (LegacyRoutingCutoverResultStatus.StateConflict,
                    "Migration will not replace or merge an existing non-equivalent route snapshot.");
            }
            var next = RoutingSnapshotModel.ReplaceRoutes(document, [planned], timestamp);
            try
            {
                await _routes.SaveAsync(
                    next,
                    document.Generation,
                    cancellationToken,
                    beforeCommit: () => RequireMarkerCurrent(
                        preparedMarker,
                        LegacyRoutingMigrationMarkerPhase.Prepared));
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

    private static bool IsEquivalentPreparedRoute(
        LegacyRoutingMigrationMarker marker,
        RoutingRoute candidate) => marker.Origin == RoutingActivationOrigin.FreshSetup
        ? candidate == marker.Route
        : LegacyRoutingMigrationPlanner.IsEquivalentMigrationRoute(candidate, marker.Route);

    private void RequireMarkerCurrent(
        LegacyRoutingMigrationMarker expected,
        LegacyRoutingMigrationMarkerPhase phase)
    {
        var current = _markers.Load(CancellationToken.None);
        if (!current.LoadedFromDisk || current.Document is not
            {
                Phase: var currentPhase,
                Generation: var currentGeneration,
                MigrationId: var currentMigrationId,
                Origin: var currentOrigin,
                PayloadFingerprint: var currentPayload
            } ||
            currentPhase != phase ||
            currentGeneration != expected.Generation ||
            currentMigrationId != expected.MigrationId ||
            currentOrigin != expected.Origin ||
            !currentPayload.Equals(expected.PayloadFingerprint, StringComparison.Ordinal))
        {
            throw new RoutingConcurrencyException(
                "The migration marker changed before its route transaction committed.");
        }
    }

    private static LegacyRoutingCutoverResult Result(
        LegacyRoutingCutoverResultStatus status,
        string reason,
        LegacyRoutingCutoverReadiness readiness,
        LegacyRoutingMigrationMarker? marker) => new(status, reason, readiness, marker);
}
