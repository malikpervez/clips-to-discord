namespace ClipsToDiscord;

internal sealed record RoutingLocalOnlyOverrideDocument(
    int SchemaVersion,
    long Revision,
    bool Enabled,
    string HotkeyBinding,
    bool FirstRunNoticeDismissed,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// The exact global override sampled when one source clip is admitted. A positive revision names
/// durable user state. Revision zero is reserved for fail-safe behavior when that state cannot be
/// trusted; it may suppress external work, but can never authorize it.
/// </summary>
internal sealed record RoutingLocalOnlyAdmissionSnapshot(
    bool Enabled,
    long StateRevision,
    bool FailSafe)
{
    internal static RoutingLocalOnlyAdmissionSnapshot FailSafeSnapshot { get; } = new(
        Enabled: true,
        StateRevision: 0,
        FailSafe: true);

    internal static RoutingLocalOnlyAdmissionSnapshot PersistedOrFailSafe(
        RoutingLocalOnlyAdmissionSnapshot? snapshot)
    {
        var effective = snapshot ?? FailSafeSnapshot;
        Validate(effective);
        return effective;
    }

    internal static void Validate(RoutingLocalOnlyAdmissionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RoutingValidation.Require(
            snapshot.StateRevision > 0 && !snapshot.FailSafe ||
            snapshot.StateRevision == 0 && snapshot.Enabled && snapshot.FailSafe,
            "The local-only admission snapshot is invalid.");
    }
}

internal sealed record RoutingLocalOnlyOverrideInspection(
    RoutingDocumentLoadStatus Status,
    RoutingLocalOnlyOverrideDocument? Document,
    bool EffectiveEnabled,
    string EffectiveHotkeyBinding,
    RoutingLocalOnlyAdmissionSnapshot AdmissionSnapshot)
{
    internal bool LoadedFromDisk =>
        Status == RoutingDocumentLoadStatus.Loaded && Document is not null;
}

internal static class RoutingLocalOnlyOverrideModel
{
    internal static RoutingLocalOnlyOverrideDocument CreateMigrated(
        bool legacyUploadToDiscord,
        string? legacyHotkeyBinding,
        DateTimeOffset now)
    {
        var timestamp = RoutingValidation.Utc(now);
        var document = new RoutingLocalOnlyOverrideDocument(
            RoutingLocalOnlyOverrideStore.CurrentSchemaVersion,
            Revision: 1,
            Enabled: !legacyUploadToDiscord,
            AppSettings.NormalizeModeToggleHotkey(legacyHotkeyBinding),
            FirstRunNoticeDismissed: false,
            timestamp,
            timestamp);
        Validate(document);
        return document;
    }

    internal static RoutingLocalOnlyOverrideDocument SetEnabled(
        RoutingLocalOnlyOverrideDocument current,
        bool enabled,
        DateTimeOffset now) => enabled == current.Enabled
        ? current
        : Advance(current, now) with { Enabled = enabled };

    internal static RoutingLocalOnlyOverrideDocument SetHotkey(
        RoutingLocalOnlyOverrideDocument current,
        string? hotkeyBinding,
        DateTimeOffset now)
    {
        Validate(current);
        var normalized = AppSettings.NormalizeModeToggleHotkey(hotkeyBinding);
        if (!string.IsNullOrWhiteSpace(hotkeyBinding) &&
            !GlobalHotkeyBinding.TryParse(hotkeyBinding, out _))
        {
            throw new ArgumentException(
                "The local-only shortcut is not a supported global hotkey.",
                nameof(hotkeyBinding));
        }
        return normalized.Equals(current.HotkeyBinding, StringComparison.Ordinal)
            ? current
            : Advance(current, now) with { HotkeyBinding = normalized };
    }

    internal static RoutingLocalOnlyOverrideDocument DismissFirstRunNotice(
        RoutingLocalOnlyOverrideDocument current,
        DateTimeOffset now) => current.FirstRunNoticeDismissed
        ? current
        : Advance(current, now) with { FirstRunNoticeDismissed = true };

    internal static void Validate(RoutingLocalOnlyOverrideDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(
            document.SchemaVersion == RoutingLocalOnlyOverrideStore.CurrentSchemaVersion &&
            document.Revision > 0,
            "The local-only override schema or revision is invalid.");
        RoutingValidation.RequireUtc(document.CreatedUtc, "local-only override creation timestamp");
        RoutingValidation.RequireUtc(document.UpdatedUtc, "local-only override update timestamp");
        RoutingValidation.Require(document.UpdatedUtc >= document.CreatedUtc,
            "The local-only override timestamps are inconsistent.");
        var normalized = AppSettings.NormalizeModeToggleHotkey(document.HotkeyBinding);
        RoutingValidation.Require(
            document.HotkeyBinding is not null &&
            normalized.Equals(document.HotkeyBinding, StringComparison.Ordinal) &&
            (document.HotkeyBinding.Length == 0 ||
             GlobalHotkeyBinding.TryParse(document.HotkeyBinding, out _)),
            "The local-only override shortcut is invalid or not canonical.");
    }

    internal static void ValidateSuccessor(
        RoutingLocalOnlyOverrideDocument current,
        RoutingLocalOnlyOverrideDocument candidate)
    {
        Validate(current);
        Validate(candidate);
        RoutingValidation.Require(
            candidate.Revision == checked(current.Revision + 1) &&
            candidate.CreatedUtc == current.CreatedUtc &&
            candidate.UpdatedUtc >= current.UpdatedUtc,
            "The local-only override did not advance one immutable revision.");
    }

    internal static void ValidateInitial(RoutingLocalOnlyOverrideDocument document)
    {
        Validate(document);
        RoutingValidation.Require(document.Revision == 1 &&
                                  document.CreatedUtc == document.UpdatedUtc,
            "The first local-only override document is not canonical.");
    }

    private static RoutingLocalOnlyOverrideDocument Advance(
        RoutingLocalOnlyOverrideDocument current,
        DateTimeOffset now)
    {
        Validate(current);
        var timestamp = RoutingValidation.Utc(now);
        if (timestamp < current.UpdatedUtc) timestamp = current.UpdatedUtc;
        return current with
        {
            Revision = RoutingValidation.NextGeneration(current.Revision),
            UpdatedUtc = timestamp
        };
    }
}

internal sealed class RoutingLocalOnlyOverrideStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = 32 * 1024;
    internal const string FileName = "local-only-override.v1.json";
    private readonly RoutingAtomicJsonStore<RoutingLocalOnlyOverrideDocument> _store;

    internal RoutingLocalOnlyOverrideStore()
        : this(System.IO.Path.Combine(SettingsStore.DataDirectory, "routing", FileName))
    {
    }

    internal RoutingLocalOnlyOverrideStore(string path)
    {
        _store = new RoutingAtomicJsonStore<RoutingLocalOnlyOverrideDocument>(
            path,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Revision,
            RoutingLocalOnlyOverrideModel.Validate,
            RoutingLocalOnlyOverrideModel.ValidateSuccessor,
            RoutingLocalOnlyOverrideModel.ValidateInitial);
    }

    internal string Path => _store.Path;

    internal RoutingDocumentLoadResult<RoutingLocalOnlyOverrideDocument> Load(
        CancellationToken cancellationToken = default) => _store.Load(cancellationToken);

    internal Task<RoutingLocalOnlyOverrideDocument> SaveAsync(
        RoutingLocalOnlyOverrideDocument document,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        _store.SaveAsync(document, expectedRevision, cancellationToken);
}

/// <summary>
/// Routing-owned mutable preference authority. It never rewrites routes or existing outbox plans;
/// callers sample AdmissionSnapshot once and persist that sample with a newly-admitted clip.
/// </summary>
internal sealed class RoutingLocalOnlyOverrideState
{
    private const int MaximumSaveAttempts = 4;
    private readonly RoutingLocalOnlyOverrideStore _store;
    private readonly Func<DateTimeOffset> _utcNow;

    internal RoutingLocalOnlyOverrideState(
        RoutingLocalOnlyOverrideStore? store = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _store = store ?? new RoutingLocalOnlyOverrideStore();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal RoutingLocalOnlyOverrideInspection Inspect(
        CancellationToken cancellationToken = default) =>
        ToInspection(_store.Load(cancellationToken));

    internal RoutingLocalOnlyAdmissionSnapshot CaptureAdmissionSnapshot(
        CancellationToken cancellationToken = default) =>
        Inspect(cancellationToken).AdmissionSnapshot;

    internal async Task<RoutingLocalOnlyOverrideInspection> EnsureMigratedAsync(
        bool legacyUploadToDiscord,
        string? legacyHotkeyBinding,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = _store.Load(cancellationToken);
            if (loaded.LoadedFromDisk || loaded.Status != RoutingDocumentLoadStatus.Missing)
                return ToInspection(loaded);
            try
            {
                var saved = await _store.SaveAsync(
                        RoutingLocalOnlyOverrideModel.CreateMigrated(
                            legacyUploadToDiscord,
                            legacyHotkeyBinding,
                            _utcNow()),
                        expectedRevision: 0,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Loaded(saved);
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                // Another process initialized the state. Reload rather than replacing it.
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return FailSafe(RoutingDocumentLoadStatus.Unavailable);
            }
        }
        throw new RoutingConcurrencyException(
            "The local-only override kept changing while it was initialized.");
    }

    internal Task<RoutingLocalOnlyOverrideInspection> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) => MutateAsync(
            current => RoutingLocalOnlyOverrideModel.SetEnabled(current, enabled, _utcNow()),
            cancellationToken);

    internal Task<RoutingLocalOnlyOverrideInspection> SetHotkeyAsync(
        string? hotkeyBinding,
        CancellationToken cancellationToken = default) => MutateAsync(
            current => RoutingLocalOnlyOverrideModel.SetHotkey(
                current, hotkeyBinding, _utcNow()),
            cancellationToken);

    internal Task<RoutingLocalOnlyOverrideInspection> DismissFirstRunNoticeAsync(
        CancellationToken cancellationToken = default) => MutateAsync(
            current => RoutingLocalOnlyOverrideModel.DismissFirstRunNotice(current, _utcNow()),
            cancellationToken);

    private async Task<RoutingLocalOnlyOverrideInspection> MutateAsync(
        Func<RoutingLocalOnlyOverrideDocument, RoutingLocalOnlyOverrideDocument> mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = _store.Load(cancellationToken);
            if (!loaded.LoadedFromDisk || loaded.Document is null)
            {
                throw new InvalidDataException(
                    $"The local-only override cannot be changed safely ({loaded.Status}).");
            }
            var candidate = mutation(loaded.Document);
            if (ReferenceEquals(candidate, loaded.Document)) return Loaded(loaded.Document);
            try
            {
                var saved = await _store.SaveAsync(
                        candidate,
                        loaded.Document.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Loaded(saved);
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                // Reload and apply the caller's intent to the newest durable revision.
            }
        }
        throw new RoutingConcurrencyException(
            "The local-only override kept changing while it was updated.");
    }

    private static RoutingLocalOnlyOverrideInspection ToInspection(
        RoutingDocumentLoadResult<RoutingLocalOnlyOverrideDocument> loaded) =>
        loaded.LoadedFromDisk && loaded.Document is not null
            ? Loaded(loaded.Document)
            : FailSafe(loaded.Status);

    private static RoutingLocalOnlyOverrideInspection Loaded(
        RoutingLocalOnlyOverrideDocument document)
    {
        RoutingLocalOnlyOverrideModel.Validate(document);
        return new RoutingLocalOnlyOverrideInspection(
            RoutingDocumentLoadStatus.Loaded,
            document,
            document.Enabled,
            document.HotkeyBinding,
            new RoutingLocalOnlyAdmissionSnapshot(
                document.Enabled,
                document.Revision,
                FailSafe: false));
    }

    private static RoutingLocalOnlyOverrideInspection FailSafe(
        RoutingDocumentLoadStatus status) => new(
        status,
        Document: null,
        EffectiveEnabled: true,
        GlobalHotkeyBinding.DefaultDisplayText,
        RoutingLocalOnlyAdmissionSnapshot.FailSafeSnapshot);
}

internal static class RoutingLocalOnlyPlanPolicy
{
    internal static readonly Guid SyntheticRouteId =
        Guid.Parse("9d31e263-70d0-4f57-9cf9-83b120a34ad3");
    internal static readonly Guid SyntheticActionId =
        Guid.Parse("a8c806bf-1e1f-48ff-ab35-76b7ee77ff3f");

    internal static RoutingRouteSnapshotReference CreateRouteReference(long routingGeneration) =>
        new(
            routingGeneration,
            SyntheticRouteId,
            RouteRevision: 1,
            "Local-only safety mode",
            SyntheticActionId,
            Priority: 0,
            Order: 0);

    internal static bool IsSyntheticDisposition(PlannedFileDisposition disposition) =>
        disposition.Route.RouteId == SyntheticRouteId &&
        disposition.Route.ActionId == SyntheticActionId &&
        disposition.Route.RouteRevision == 1 &&
        disposition.Route.RouteName.Equals("Local-only safety mode", StringComparison.Ordinal) &&
        disposition.Route.Priority == 0 &&
        disposition.Route.Order == 0 &&
        disposition.LibraryArea == RoutingLibraryArea.LocalOnly &&
        disposition.PrerequisiteDeliveryIds.Count == 0;
}
