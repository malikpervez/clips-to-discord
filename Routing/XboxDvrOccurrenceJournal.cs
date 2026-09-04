namespace ClipsToDiscord;

internal enum XboxDvrOccurrenceState
{
    Selected,
    Importing,
    Imported,
    NeedsAttention,
    Skipped,
    RouteRevoked
}

/// <summary>
/// Durable admission evidence for one Xbox DVR item. The source leaf and metadata are frozen
/// before ClipCord opens cloud content; Imported binds that source revision to the immutable
/// Capture Library journal that owns all later delivery and retry work.
/// </summary>
internal sealed record XboxDvrOccurrenceEntry(
    string OccurrenceId,
    string RevisionId,
    string PortableRelativePath,
    string GameName,
    DateTimeOffset CapturedUtc,
    long LogicalBytes,
    long LastWriteUtcTicks,
    XboxDvrOccurrenceState State,
    int ImportAttempts,
    string? ClipId,
    string? ContentSha256,
    string? ErrorCode,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    RoutingLocalOnlyAdmissionSnapshot? LocalOnlyOverride = null);

internal sealed record XboxDvrOccurrenceDocument(
    int SchemaVersion,
    long Generation,
    string SourceId,
    string RootIdentitySha256,
    IReadOnlyList<XboxDvrOccurrenceEntry> Occurrences,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

internal static class XboxDvrOccurrenceModel
{
    internal const int MaximumOccurrences = 50_000;

    internal static XboxDvrOccurrenceDocument CreateEmpty(
        string sourceId,
        string rootIdentitySha256,
        DateTimeOffset now)
    {
        var document = new XboxDvrOccurrenceDocument(
            XboxDvrOccurrenceStore.CurrentSchemaVersion,
            Generation: 1,
            sourceId,
            rootIdentitySha256,
            [],
            RoutingValidation.Utc(now),
            RoutingValidation.Utc(now));
        Validate(document);
        return document;
    }

    internal static void Validate(XboxDvrOccurrenceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(
            document.SchemaVersion == XboxDvrOccurrenceStore.CurrentSchemaVersion &&
            document.Generation > 0,
            "The Xbox occurrence journal header is invalid.");
        RoutingValidation.RequireOpaqueId(document.SourceId, 128, "Xbox source id");
        RequireCanonicalSha256(document.RootIdentitySha256, "Xbox root identity");
        RoutingValidation.RequireUtc(document.CreatedUtc, "Xbox journal creation time");
        RoutingValidation.RequireUtc(document.UpdatedUtc, "Xbox journal update time");
        RoutingValidation.Require(document.UpdatedUtc >= document.CreatedUtc,
            "The Xbox occurrence journal timestamps are invalid.");
        var occurrences = document.Occurrences ??
            throw new InvalidDataException("The Xbox occurrence journal entries are missing.");
        RoutingValidation.Require(occurrences.Count <= MaximumOccurrences,
            "The Xbox occurrence journal reached its safe capacity.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in occurrences)
        {
            if (occurrence is null)
                throw new InvalidDataException("An Xbox occurrence journal entry is missing.");
            RequireCanonicalSha256(occurrence.OccurrenceId, "Xbox occurrence id");
            RequireCanonicalSha256(occurrence.RevisionId, "Xbox occurrence revision");
            RoutingValidation.Require(ids.Add(occurrence.OccurrenceId),
                "Xbox occurrence ids must be unique within one source.");
            RoutingValidation.RequireText(
                occurrence.PortableRelativePath, 1, 240, "Xbox source leaf");
            RoutingValidation.Require(
                Path.GetFileName(occurrence.PortableRelativePath).Equals(
                    occurrence.PortableRelativePath, StringComparison.Ordinal) &&
                !occurrence.PortableRelativePath.Contains('/') &&
                !occurrence.PortableRelativePath.Contains('\\'),
                "An Xbox occurrence must reference one source leaf.");
            RoutingValidation.RequireText(occurrence.GameName, 1, 160, "Xbox game name");
            RoutingValidation.RequireUtc(occurrence.CapturedUtc, "Xbox capture time");
            RoutingValidation.Require(occurrence.LogicalBytes > 0 &&
                                      occurrence.LastWriteUtcTicks > 0 &&
                                      occurrence.ImportAttempts >= 0 &&
                                      Enum.IsDefined(occurrence.State),
                "An Xbox occurrence contains invalid metadata.");
            RoutingValidation.RequireUtc(occurrence.CreatedUtc, "Xbox occurrence creation time");
            RoutingValidation.RequireUtc(occurrence.UpdatedUtc, "Xbox occurrence update time");
            RoutingValidation.Require(occurrence.UpdatedUtc >= occurrence.CreatedUtc,
                "The Xbox occurrence timestamps are invalid.");
            if (occurrence.LocalOnlyOverride is not null)
                RoutingLocalOnlyAdmissionSnapshot.Validate(occurrence.LocalOnlyOverride);
            if (occurrence.State == XboxDvrOccurrenceState.Imported)
            {
                RoutingValidation.Require(
                    CaptureJournalModel.IsClipId(occurrence.ClipId) &&
                    IsCanonicalSha256(occurrence.ContentSha256) &&
                    occurrence.ErrorCode is null,
                    "An imported Xbox occurrence lacks immutable library evidence.");
            }
            else
            {
                RoutingValidation.Require(occurrence.ClipId is null &&
                                          occurrence.ContentSha256 is null,
                    "An uncommitted Xbox occurrence cannot reference a library clip.");
                if (occurrence.State is XboxDvrOccurrenceState.NeedsAttention or
                    XboxDvrOccurrenceState.Skipped)
                {
                    RoutingValidation.RequireErrorCode(occurrence.ErrorCode ?? string.Empty);
                }
                else if (occurrence.State == XboxDvrOccurrenceState.RouteRevoked)
                {
                    RoutingValidation.Require(
                        occurrence.ErrorCode == "route-revoked",
                        "A route-revoked Xbox occurrence must retain its terminal reason.");
                }
                else
                {
                    RoutingValidation.Require(occurrence.ErrorCode is null,
                        "A pending Xbox import cannot carry a terminal error.");
                }
            }
        }
    }

    internal static void ValidateSuccessor(
        XboxDvrOccurrenceDocument current,
        XboxDvrOccurrenceDocument candidate)
    {
        Validate(current);
        Validate(candidate);
        RoutingValidation.Require(
            candidate.SchemaVersion == current.SchemaVersion &&
            candidate.SourceId == current.SourceId &&
            candidate.RootIdentitySha256 == current.RootIdentitySha256 &&
            candidate.CreatedUtc == current.CreatedUtc &&
            candidate.Generation == RoutingValidation.NextGeneration(current.Generation) &&
            candidate.UpdatedUtc >= current.UpdatedUtc,
            "The Xbox occurrence journal successor changed immutable authority.");
        var nextById = candidate.Occurrences.ToDictionary(item => item.OccurrenceId);
        foreach (var prior in current.Occurrences)
        {
            RoutingValidation.Require(nextById.TryGetValue(prior.OccurrenceId, out var next),
                "An Xbox occurrence cannot disappear from durable history.");
            RoutingValidation.Require(
                next!.RevisionId == prior.RevisionId &&
                next.PortableRelativePath == prior.PortableRelativePath &&
                next.GameName == prior.GameName &&
                next.CapturedUtc == prior.CapturedUtc &&
                next.LogicalBytes == prior.LogicalBytes &&
                next.LastWriteUtcTicks == prior.LastWriteUtcTicks &&
                next.CreatedUtc == prior.CreatedUtc &&
                next.LocalOnlyOverride == prior.LocalOnlyOverride &&
                next.ImportAttempts >= prior.ImportAttempts,
                "An Xbox occurrence successor changed frozen source evidence.");
            RoutingValidation.Require(
                IsAllowedTransition(prior.State, next.State) &&
                (prior.State != XboxDvrOccurrenceState.NeedsAttention ||
                 next.State != XboxDvrOccurrenceState.Importing ||
                 prior.ErrorCode is "invalid-media" or "library-conflict"),
                "An Xbox occurrence used an invalid state transition.");
            if (prior.State is XboxDvrOccurrenceState.Imported or
                XboxDvrOccurrenceState.Skipped)
            {
                RoutingValidation.Require(next == prior,
                    "A terminal Xbox occurrence is immutable.");
            }
        }
    }

    private static bool IsAllowedTransition(
        XboxDvrOccurrenceState current,
        XboxDvrOccurrenceState candidate) => current switch
        {
            XboxDvrOccurrenceState.Selected =>
                candidate is XboxDvrOccurrenceState.Selected or
                    XboxDvrOccurrenceState.Importing or
                    XboxDvrOccurrenceState.NeedsAttention or
                    XboxDvrOccurrenceState.RouteRevoked,
            XboxDvrOccurrenceState.Importing =>
                candidate is XboxDvrOccurrenceState.Importing or
                    XboxDvrOccurrenceState.Selected or
                    XboxDvrOccurrenceState.Imported or
                    XboxDvrOccurrenceState.NeedsAttention,
            XboxDvrOccurrenceState.NeedsAttention =>
                candidate is XboxDvrOccurrenceState.NeedsAttention or
                    XboxDvrOccurrenceState.Importing or
                    XboxDvrOccurrenceState.Selected or
                    XboxDvrOccurrenceState.Skipped,
            XboxDvrOccurrenceState.Imported => candidate == XboxDvrOccurrenceState.Imported,
            XboxDvrOccurrenceState.Skipped => candidate == XboxDvrOccurrenceState.Skipped,
            XboxDvrOccurrenceState.RouteRevoked =>
                candidate is XboxDvrOccurrenceState.RouteRevoked or
                    XboxDvrOccurrenceState.Selected or
                    XboxDvrOccurrenceState.NeedsAttention,
            _ => false
        };

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireCanonicalSha256(string? value, string description) =>
        RoutingValidation.Require(IsCanonicalSha256(value),
            $"The {description} must use canonical lowercase SHA-256 text.");
}

internal sealed class XboxDvrOccurrenceStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = 8 * 1024 * 1024;
    internal const string FolderName = "xbox-journal";

    private readonly RoutingAtomicJsonStore<XboxDvrOccurrenceDocument> _store;

    internal XboxDvrOccurrenceStore(string routingRoot, string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routingRoot);
        RoutingValidation.RequireOpaqueId(sourceId, 128, "Xbox source id");
        var sourceFile = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(sourceId)))
            .ToLowerInvariant();
        _store = new RoutingAtomicJsonStore<XboxDvrOccurrenceDocument>(
            System.IO.Path.Combine(
                System.IO.Path.GetFullPath(routingRoot), FolderName, "v1", $"{sourceFile}.json"),
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Generation,
            XboxDvrOccurrenceModel.Validate,
            XboxDvrOccurrenceModel.ValidateSuccessor,
            document =>
            {
                XboxDvrOccurrenceModel.Validate(document);
                RoutingValidation.Require(document.Generation == 1,
                    "The initial Xbox occurrence journal generation must be one.");
            });
    }

    internal string Path => _store.Path;

    internal RoutingDocumentLoadResult<XboxDvrOccurrenceDocument> Load(
        CancellationToken cancellationToken = default) => _store.Load(cancellationToken);

    internal Task<XboxDvrOccurrenceDocument> LoadOrCreateAsync(
        string sourceId,
        string rootIdentitySha256,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        _store.LoadOrCreateAsync(
            () => XboxDvrOccurrenceModel.CreateEmpty(sourceId, rootIdentitySha256, now),
            cancellationToken);

    internal Task<XboxDvrOccurrenceDocument> SaveAsync(
        XboxDvrOccurrenceDocument document,
        long expectedGeneration,
        CancellationToken cancellationToken = default) =>
        _store.SaveAsync(document, expectedGeneration, cancellationToken);
}

internal sealed class XboxDvrOccurrenceJournal
{
    private const int MaximumSaveAttempts = 6;
    private readonly XboxDvrOccurrenceStore _store;
    private readonly string _sourceId;
    private readonly string _rootIdentitySha256;
    private readonly Func<DateTimeOffset> _clock;

    internal XboxDvrOccurrenceJournal(
        XboxDvrOccurrenceStore store,
        string sourceId,
        string rootIdentitySha256,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
        RoutingValidation.RequireSha256(rootIdentitySha256, "Xbox root identity");
        _sourceId = sourceId;
        _rootIdentitySha256 = rootIdentitySha256.ToLowerInvariant();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal RoutingDocumentLoadResult<XboxDvrOccurrenceDocument> Load(
        CancellationToken cancellationToken = default) => _store.Load(cancellationToken);

    internal async Task<XboxDvrOccurrenceEntry> SelectAsync(
        string occurrenceId,
        string revisionId,
        string portableRelativePath,
        string gameName,
        DateTimeOffset capturedUtc,
        long logicalBytes,
        long lastWriteUtcTicks,
        CancellationToken cancellationToken = default,
        RoutingLocalOnlyAdmissionSnapshot? localOnlyOverride = null)
    {
        var now = RoutingValidation.Utc(_clock());
        return await MutateAsync(
            current =>
            {
                var existing = current.Occurrences.SingleOrDefault(item =>
                    item.OccurrenceId.Equals(occurrenceId, StringComparison.Ordinal));
                if (existing is not null)
                {
                    if (existing.State is XboxDvrOccurrenceState.Imported or
                        XboxDvrOccurrenceState.Skipped)
                    {
                        return current;
                    }
                    if (!existing.RevisionId.Equals(revisionId, StringComparison.Ordinal))
                    {
                        return ReplaceEntry(current, existing, existing with
                        {
                            State = XboxDvrOccurrenceState.NeedsAttention,
                            ErrorCode = "source-replaced",
                            UpdatedUtc = MutationTime(current, now)
                        });
                    }
                    if (existing.State == XboxDvrOccurrenceState.RouteRevoked)
                    {
                        return ReplaceEntry(current, existing, existing with
                        {
                            State = XboxDvrOccurrenceState.Selected,
                            ErrorCode = null,
                            UpdatedUtc = MutationTime(current, now)
                        });
                    }
                    return current;
                }
                RoutingValidation.Require(
                    current.Occurrences.Count < XboxDvrOccurrenceModel.MaximumOccurrences,
                    "The Xbox occurrence journal reached its safe capacity.");
                var timestamp = MutationTime(current, now);
                var admission = RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                    localOnlyOverride);
                var added = new XboxDvrOccurrenceEntry(
                    occurrenceId,
                    revisionId,
                    portableRelativePath,
                    gameName.Trim(),
                    RoutingValidation.Utc(capturedUtc),
                    logicalBytes,
                    lastWriteUtcTicks,
                    XboxDvrOccurrenceState.Selected,
                    ImportAttempts: 0,
                    ClipId: null,
                    ContentSha256: null,
                    ErrorCode: null,
                    timestamp,
                    timestamp,
                    admission);
                return Next(current, current.Occurrences.Concat([added]).ToArray(), timestamp);
            },
            occurrenceId,
            cancellationToken).ConfigureAwait(false);
    }

    internal Task<XboxDvrOccurrenceEntry> BeginImportAsync(
        string occurrenceId,
        string revisionId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            occurrenceId,
            revisionId,
            entry => entry.State is XboxDvrOccurrenceState.Selected or
                    XboxDvrOccurrenceState.Importing
                ? entry with
                {
                    State = XboxDvrOccurrenceState.Importing,
                    ImportAttempts = checked(entry.ImportAttempts + 1),
                    ErrorCode = null
                }
                : throw new InvalidOperationException(
                    "Only a selected Xbox occurrence can begin importing."),
            cancellationToken);

    internal Task<XboxDvrOccurrenceEntry> ResetInterruptedAsync(
        string occurrenceId,
        string revisionId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            occurrenceId,
            revisionId,
            entry => entry.State == XboxDvrOccurrenceState.Importing
                ? entry with
                {
                    State = XboxDvrOccurrenceState.Selected,
                    ErrorCode = null
                }
                : entry,
            cancellationToken);

    internal Task<XboxDvrOccurrenceEntry> CompleteAsync(
        string occurrenceId,
        string revisionId,
        string clipId,
        string contentSha256,
        CancellationToken cancellationToken = default)
    {
        RoutingValidation.Require(
            CaptureJournalModel.IsClipId(clipId), "The imported Xbox clip id is invalid.");
        RoutingValidation.RequireSha256(contentSha256, "Xbox imported content hash");
        return TransitionAsync(
            occurrenceId,
            revisionId,
            entry => entry.State == XboxDvrOccurrenceState.Imported
                ? entry.ClipId == clipId &&
                  entry.ContentSha256!.Equals(contentSha256, StringComparison.OrdinalIgnoreCase)
                    ? entry
                    : throw new InvalidDataException(
                        "An Xbox occurrence is already bound to another Library clip.")
                : entry.State == XboxDvrOccurrenceState.Importing
                    ? entry with
                    {
                        State = XboxDvrOccurrenceState.Imported,
                        ClipId = clipId,
                        ContentSha256 = contentSha256.ToLowerInvariant(),
                        ErrorCode = null
                    }
                    : throw new InvalidOperationException(
                        "Only an importing Xbox occurrence can be committed."),
            cancellationToken);
    }

    internal Task<XboxDvrOccurrenceEntry> MarkNeedsAttentionAsync(
        string occurrenceId,
        string revisionId,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        RoutingValidation.RequireErrorCode(errorCode);
        return TransitionAsync(
            occurrenceId,
            revisionId,
            entry => entry.State == XboxDvrOccurrenceState.Imported
                ? throw new InvalidOperationException(
                    "An imported Xbox occurrence cannot become uncommitted.")
                : entry with
                {
                    State = XboxDvrOccurrenceState.NeedsAttention,
                    ErrorCode = errorCode
                },
            cancellationToken);
    }

    /// <summary>
    /// Terminates frozen metadata-only work whose admitting route was explicitly disabled or
    /// deleted before content access. The external Xbox file is never opened or changed.
    /// </summary>
    internal Task<XboxDvrOccurrenceEntry> DismissRouteRevokedAsync(
        string occurrenceId,
        string revisionId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            occurrenceId,
            revisionId,
            entry => entry.State == XboxDvrOccurrenceState.Selected
                ? entry with
                {
                    State = XboxDvrOccurrenceState.RouteRevoked,
                    ErrorCode = "route-revoked"
                }
                : throw new InvalidOperationException(
                    "Only a selected Xbox occurrence can be route-revoked."),
            cancellationToken);

    /// <summary>
    /// Restores only a source-replacement occurrence after a metadata-only recovery pass has
    /// proven that the exact frozen occurrence and revision are present again.
    /// </summary>
    internal Task<XboxDvrOccurrenceEntry> RestoreSourceRevisionAsync(
        string occurrenceId,
        string revisionId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            occurrenceId,
            revisionId,
            entry => entry.State == XboxDvrOccurrenceState.NeedsAttention &&
                     entry.ErrorCode == "source-replaced"
                ? entry with
                {
                    State = XboxDvrOccurrenceState.Selected,
                    ErrorCode = null
                }
                : throw new InvalidOperationException(
                    "Only an exact source-replacement occurrence can be restored."),
            cancellationToken);

    internal Task<XboxDvrOccurrenceEntry> SkipBlockedAsync(
        string occurrenceId,
        string revisionId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            occurrenceId,
            revisionId,
            entry => entry.State == XboxDvrOccurrenceState.NeedsAttention &&
                     entry.ErrorCode is "invalid-media" or "library-conflict"
                ? entry with
                {
                    State = XboxDvrOccurrenceState.Skipped,
                    ErrorCode = "skipped-" + entry.ErrorCode
                }
                : throw new InvalidOperationException(
                    "Only a known blocked Xbox occurrence can be skipped."),
            cancellationToken);

    /// <summary>
    /// Re-enters the recoverable Importing state without incrementing the import-attempt count.
    /// The runtime must first look for an already-committed Capture Journal or durable promotion
    /// intent. Only when neither exists may it discard the owned stage and begin a distinguishable
    /// fresh import attempt. Repeating this exact retry is intentionally idempotent so a lost UI
    /// response or catalog-write failure can converge safely.
    /// </summary>
    internal Task<XboxDvrOccurrenceEntry> RetryBlockedAsync(
        string occurrenceId,
        string revisionId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            occurrenceId,
            revisionId,
            entry => entry.State == XboxDvrOccurrenceState.Importing
                ? entry
                : entry.State == XboxDvrOccurrenceState.NeedsAttention &&
                  entry.ErrorCode is "invalid-media" or "library-conflict"
                    ? entry with
                    {
                        State = XboxDvrOccurrenceState.Importing,
                        ErrorCode = null
                    }
                    : throw new InvalidOperationException(
                        "Only a known blocked Xbox occurrence can be retried."),
            cancellationToken);

    private Task<XboxDvrOccurrenceEntry> TransitionAsync(
        string occurrenceId,
        string revisionId,
        Func<XboxDvrOccurrenceEntry, XboxDvrOccurrenceEntry> transition,
        CancellationToken cancellationToken) => MutateAsync(
        current =>
        {
            var existing = current.Occurrences.SingleOrDefault(item =>
                item.OccurrenceId.Equals(occurrenceId, StringComparison.Ordinal)) ??
                           throw new InvalidOperationException(
                               "The Xbox occurrence is not selected for this source.");
            if (!existing.RevisionId.Equals(revisionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The Xbox occurrence changed after route admission.");
            }
            var changed = transition(existing);
            if (ReferenceEquals(changed, existing) || changed == existing) return current;
            var timestamp = MutationTime(current, RoutingValidation.Utc(_clock()));
            changed = changed with { UpdatedUtc = timestamp };
            return ReplaceEntry(current, existing, changed);
        },
        occurrenceId,
        cancellationToken);

    private async Task<XboxDvrOccurrenceEntry> MutateAsync(
        Func<XboxDvrOccurrenceDocument, XboxDvrOccurrenceDocument> mutation,
        string occurrenceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await _store.LoadOrCreateAsync(
                    _sourceId,
                    _rootIdentitySha256,
                    RoutingValidation.Utc(_clock()),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!current.SourceId.Equals(_sourceId, StringComparison.Ordinal) ||
                !current.RootIdentitySha256.Equals(
                    _rootIdentitySha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The Xbox occurrence journal belongs to another source authority.");
            }
            var candidate = mutation(current);
            if (ReferenceEquals(candidate, current))
            {
                return current.Occurrences.Single(item =>
                    item.OccurrenceId.Equals(occurrenceId, StringComparison.Ordinal));
            }
            try
            {
                var saved = await _store.SaveAsync(
                        candidate, current.Generation, cancellationToken)
                    .ConfigureAwait(false);
                return saved.Occurrences.Single(item =>
                    item.OccurrenceId.Equals(occurrenceId, StringComparison.Ordinal));
            }
            catch (RoutingConcurrencyException) when (attempt + 1 < MaximumSaveAttempts)
            {
                // Reapply the exact transition to the winning generation.
            }
        }
        throw new RoutingConcurrencyException(
            "The Xbox occurrence journal kept changing during an import transition.");
    }

    private static XboxDvrOccurrenceDocument ReplaceEntry(
        XboxDvrOccurrenceDocument current,
        XboxDvrOccurrenceEntry existing,
        XboxDvrOccurrenceEntry changed)
    {
        var timestamp = MutationTime(current, changed.UpdatedUtc);
        return Next(
            current,
            current.Occurrences.Select(item =>
                    ReferenceEquals(item, existing) || item.OccurrenceId == existing.OccurrenceId
                        ? changed with { UpdatedUtc = timestamp }
                        : item)
                .ToArray(),
            timestamp);
    }

    private static XboxDvrOccurrenceDocument Next(
        XboxDvrOccurrenceDocument current,
        IReadOnlyList<XboxDvrOccurrenceEntry> occurrences,
        DateTimeOffset timestamp) => current with
    {
        Generation = RoutingValidation.NextGeneration(current.Generation),
        Occurrences = occurrences,
        UpdatedUtc = MutationTime(current, timestamp)
    };

    private static DateTimeOffset MutationTime(
        XboxDvrOccurrenceDocument current,
        DateTimeOffset proposed) => proposed < current.UpdatedUtc
        ? current.UpdatedUtc
        : proposed;
}
