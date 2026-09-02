using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipsToDiscord;

internal sealed record CaptureJournalReconciliationItem(
    string ClipId,
    CaptureJournalLoadStatus Status,
    CaptureJournalDocument? Document,
    IReadOnlyDictionary<string, CaptureJournalArtifactValidationStatus> ArtifactStatuses)
{
    internal bool MediaValidated =>
        Document is not null &&
        ArtifactStatuses.TryGetValue("original", out var original) &&
        original == CaptureJournalArtifactValidationStatus.Valid &&
        Document.Artifacts.All(artifact =>
            ArtifactStatuses.TryGetValue(artifact.Kind, out var status) &&
            status == CaptureJournalArtifactValidationStatus.Valid);

    internal bool CanPlanDeliveries =>
        MediaValidated &&
        Document is not null &&
        Document.State is CaptureJournalState.OriginalCommitted or
            CaptureJournalState.CameraPending or
            CaptureJournalState.RenditionsReady or
            CaptureJournalState.RenditionsFailed;
}

internal interface ICaptureJournalReconciliationHandler
{
    /// <summary>
    /// Receives every readable journal record and every unreadable record identity. Implementors
    /// resume CameraPending work, make OriginalCommitted clips visible to routing, and surface
    /// durable failure states. ArtifactStatuses is always populated for a readable document;
    /// handlers must require MediaValidated/CanPlanDeliveries before planning any output. The
    /// handler must use generation-checked store transitions; the enumerator never silently
    /// rewrites or deletes state.
    /// </summary>
    ValueTask ReconcileAsync(
        CaptureJournalReconciliationItem item,
        CancellationToken cancellationToken);
}

internal sealed record CaptureJournalReconciliationSummary(
    int Inspected,
    int Loaded,
    int Unreadable,
    bool EntryLimitReached,
    string? NextCursor,
    TimeSpan Elapsed);

/// <summary>
/// Bounded startup bridge between durable capture records and routing/render coordinators. It
/// intentionally performs no business transition itself: a crash-safe handler can resume work,
/// mark a stable failure code, or create planned deliveries without the scan re-resolving policy.
/// </summary>
internal static class CaptureJournalStartupReconciler
{
    internal const int DefaultMaximumEntries = 512;
    internal static readonly TimeSpan DefaultMaximumDuration = TimeSpan.FromSeconds(2);

    internal static async Task<CaptureJournalReconciliationSummary> ReconcileAsync(
        string libraryRoot,
        ICaptureJournalReconciliationHandler handler,
        CancellationToken cancellationToken = default,
        int maximumEntries = DefaultMaximumEntries,
        TimeSpan? maximumDuration = null,
        string? afterClipId = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        var duration = maximumDuration ?? DefaultMaximumDuration;
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDuration));

        cancellationToken.ThrowIfCancellationRequested();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var inspected = 0;
        var loaded = 0;
        var unreadable = 0;
        var limitReached = false;
        string? nextCursor = null;

        var page = CaptureJournalStore.ReadClipIdPage(
            libraryRoot,
            maximumEntries,
            afterClipId,
            cancellationToken);
        for (var index = 0; index < page.ClipIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inspected > 0 && started.Elapsed >= duration)
            {
                limitReached = true;
                break;
            }

            var clipId = page.ClipIds[index];
            var load = CaptureJournalStore.Load(libraryRoot, clipId, cancellationToken);
            var artifactStatuses = new Dictionary<
                string,
                CaptureJournalArtifactValidationStatus>(StringComparer.Ordinal);
            if (load.LoadedFromDisk && load.Document is not null)
            {
                var media = new[] { load.Document.Clip.Original }
                    .Concat(load.Document.Artifacts);
                foreach (var artifact in media)
                {
                    var validation = await CaptureJournalStore.ValidateArtifactAsync(
                            libraryRoot,
                            artifact,
                            cancellationToken)
                        .ConfigureAwait(false);
                    artifactStatuses[artifact.Kind] = validation.Status;
                }
            }
            var item = new CaptureJournalReconciliationItem(
                clipId,
                load.Status,
                load.Document,
                artifactStatuses);
            try
            {
                await handler.ReconcileAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or
                    RoutingConcurrencyException)
            {
                // A handler can observe cancellation and then surface a data/CAS failure. Cancellation
                // still owns the operation and must not be converted into a successfully advanced page.
                cancellationToken.ThrowIfCancellationRequested();
                // A corrupt or concurrently-changing routing record is local to this clip. Keep
                // advancing the durable page cursor so one bad item cannot starve every later clip.
                Log.Error(
                    $"ClipCord could not reconcile capture journal {item.ClipId}; startup scanning will continue.",
                    exception);
            }
            inspected++;
            nextCursor = clipId;
            if (load.LoadedFromDisk) loaded++;
            else unreadable++;
        }

        if (inspected < page.ClipIds.Count || page.HasMore) limitReached = true;

        started.Stop();
        return new CaptureJournalReconciliationSummary(
            inspected,
            loaded,
            unreadable,
            limitReached,
            nextCursor,
            started.Elapsed);
    }
}

internal sealed record CaptureJournalPromotionIntent(
    int SchemaVersion,
    string ClipId,
    string Kind,
    long ExpectedGeneration,
    long ProcessingAttemptEpoch,
    string ProcessingAttemptId,
    string StagedRelativePath,
    string DestinationRelativePath,
    CaptureJournalFingerprint Fingerprint,
    DateTimeOffset CreatedUtc);

internal enum CaptureJournalPromotionStatus
{
    Missing,
    MoveRequired,
    DestinationReady,
    AlreadyJournaled,
    SourceMissing,
    FingerprintMismatch,
    DestinationConflict,
    StaleAttempt,
    Invalid,
    Unavailable
}

internal sealed record CaptureJournalPromotionInspection(
    CaptureJournalPromotionStatus Status,
    CaptureJournalPromotionIntent? Intent,
    CaptureJournalArtifact? Artifact);

internal sealed record CaptureJournalOriginalPromotionIntent(
    int SchemaVersion,
    string ClipId,
    CaptureJournalSourceKind SourceKind,
    string GameName,
    DateTimeOffset CapturedUtc,
    long DurationTicks,
    int Width,
    int Height,
    bool ReactionCameraRequested,
    IReadOnlyList<string> RequestedRenditions,
    string StagedRelativePath,
    string DestinationRelativePath,
    CaptureJournalFingerprint Fingerprint,
    DateTimeOffset CreatedUtc,
    string? SourceConnectionId = null,
    string? SourceOccurrenceId = null,
    string? SourceRevisionId = null,
    RoutingLocalOnlyAdmissionSnapshot? LocalOnlyOverride = null);

internal sealed record CaptureJournalOriginalPromotionInspection(
    CaptureJournalPromotionStatus Status,
    CaptureJournalOriginalPromotionIntent? Intent,
    CaptureJournalDocument? Document);

internal sealed record CaptureJournalOriginalPromotionReconciliationItem(
    string ClipId,
    CaptureJournalOriginalPromotionInspection Inspection);

internal interface ICaptureJournalOriginalPromotionReconciliationHandler
{
    ValueTask ReconcileAsync(
        CaptureJournalOriginalPromotionReconciliationItem item,
        CancellationToken cancellationToken);
}

internal sealed record CaptureJournalOriginalPromotionReconciliationSummary(
    int Inspected,
    int Recoverable,
    int NeedsAttention,
    bool EntryLimitReached,
    string? NextCursor,
    TimeSpan Elapsed);

internal static class CaptureJournalOriginalPromotionStartupReconciler
{
    internal static async Task<CaptureJournalOriginalPromotionReconciliationSummary> ReconcileAsync(
        string libraryRoot,
        ICaptureJournalOriginalPromotionReconciliationHandler handler,
        CancellationToken cancellationToken = default,
        int maximumEntries = CaptureJournalStartupReconciler.DefaultMaximumEntries,
        TimeSpan? maximumDuration = null,
        string? afterClipId = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var duration = maximumDuration ?? CaptureJournalStartupReconciler.DefaultMaximumDuration;
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var page = CaptureJournalPromotionIntentStore.ReadOriginalClipIdPage(
            libraryRoot,
            maximumEntries,
            afterClipId,
            cancellationToken);
        var inspected = 0;
        var recoverable = 0;
        var needsAttention = 0;
        string? nextCursor = null;
        var limitReached = false;
        for (var index = 0; index < page.ClipIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inspected > 0 && started.Elapsed >= duration)
            {
                limitReached = true;
                break;
            }
            var clipId = page.ClipIds[index];
            var inspection = await CaptureJournalPromotionIntentStore.InspectOriginalAsync(
                    libraryRoot,
                    clipId,
                    cancellationToken)
                .ConfigureAwait(false);
            await handler.ReconcileAsync(
                    new CaptureJournalOriginalPromotionReconciliationItem(clipId, inspection),
                    cancellationToken)
                .ConfigureAwait(false);
            inspected++;
            nextCursor = clipId;
            if (inspection.Status is CaptureJournalPromotionStatus.MoveRequired or
                CaptureJournalPromotionStatus.DestinationReady or
                CaptureJournalPromotionStatus.AlreadyJournaled)
            {
                recoverable++;
            }
            else
            {
                needsAttention++;
            }
        }
        if (inspected < page.ClipIds.Count || page.HasMore) limitReached = true;
        started.Stop();
        return new CaptureJournalOriginalPromotionReconciliationSummary(
            inspected,
            recoverable,
            needsAttention,
            limitReached,
            nextCursor,
            started.Elapsed);
    }
}

/// <summary>
/// Durable write-ahead seam for the non-atomic gap between publishing an MP4 and attaching its
/// fingerprint to the capture journal. Prepare is written first; Promote performs only the exact
/// staged-to-canonical rename; Complete removes the intent only after the journal contains the
/// same kind, path, and fingerprint. Startup can therefore inspect and resume any crash point.
/// </summary>
internal static class CaptureJournalPromotionIntentStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = 64 * 1024;
    internal const string FolderName = "PromotionIntents";
    internal const string OriginalFolderName = "OriginalPromotionIntents";

    private const string SaveMutexPrefix = @"Local\ClipCord.CapturePromotion.";
    private const string OriginalIntentFileSuffix = ".original.json";
    private static readonly char[] ClipIdAlphabet = "0123456789abcdef".ToCharArray();
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

    internal static string GetDirectory(string libraryRoot) =>
        Path.Combine(
            NormalizeRoot(libraryRoot),
            CaptureLibraryLayout.PrivateDataFolderName,
            CaptureJournalStore.RoutingFolderName,
            FolderName);

    internal static string GetPath(string libraryRoot, string clipId, string kind)
    {
        ValidateIdentity(clipId, kind);
        return Path.Combine(GetDirectory(libraryRoot), $"{clipId}.{kind}.json");
    }

    internal static string GetOriginalDirectory(string libraryRoot) =>
        Path.Combine(
            NormalizeRoot(libraryRoot),
            CaptureLibraryLayout.PrivateDataFolderName,
            CaptureJournalStore.RoutingFolderName,
            OriginalFolderName);

    internal static string GetOriginalPath(string libraryRoot, string clipId)
    {
        if (!CaptureJournalModel.IsClipId(clipId))
        {
            throw new ArgumentException("The original promotion clip id is invalid.", nameof(clipId));
        }
        return Path.Combine(GetOriginalDirectory(libraryRoot), $"{clipId}.original.json");
    }

    internal static CaptureJournalClipIdPage ReadOriginalClipIdPage(
        string libraryRoot,
        int maximumEntries,
        string? afterClipId = null,
        CancellationToken cancellationToken = default)
    {
        if (maximumEntries <= 0 || maximumEntries == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }
        if (afterClipId is not null && !CaptureJournalModel.IsClipId(afterClipId))
        {
            throw new ArgumentException("The original promotion cursor is invalid.", nameof(afterClipId));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var root = NormalizeRoot(libraryRoot);
        var directory = GetOriginalDirectory(root);
        if (!Directory.Exists(directory))
        {
            return new CaptureJournalClipIdPage([], null, HasMore: false);
        }
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root,
            directory,
            requireDirectory: true,
            "original promotion intent folder");

        var target = maximumEntries + 1;
        var collected = new List<string>(target);
        foreach (var first in ClipIdAlphabet)
        {
            CollectOriginalClipIdsForPrefix(
                directory,
                first.ToString(),
                afterClipId,
                target,
                collected,
                cancellationToken);
            if (collected.Count >= target) break;
        }
        collected.Sort(StringComparer.Ordinal);
        var hasMore = collected.Count > maximumEntries;
        var ids = collected.Take(maximumEntries).ToArray();
        return new CaptureJournalClipIdPage(
            ids,
            ids.Length == 0 ? null : ids[^1],
            hasMore);
    }

    /// <summary>
    /// Deletion guard for capture staging cleanup. A prepared original-promotion intent owns its
    /// exact staged MP4 until promotion and journal commit finish. If intent evidence cannot be
    /// read safely, fail closed and preserve the candidate rather than destroying recoverable
    /// media.
    /// </summary>
    internal static bool IsOriginalStageProtected(
        string libraryRoot,
        string stagedPath)
    {
        try
        {
            var root = NormalizeRoot(libraryRoot);
            var candidate = Path.GetFullPath(stagedPath);
            CaptureJournalStore.EnsurePathIsInside(
                root,
                candidate,
                "original promotion staging cleanup candidate");
            string? cursor = null;
            do
            {
                var page = ReadOriginalClipIdPage(
                    root,
                    maximumEntries: 256,
                    afterClipId: cursor,
                    CancellationToken.None);
                foreach (var clipId in page.ClipIds)
                {
                    var intent = LoadOriginal(root, clipId);
                    if (intent is null) continue;
                    ValidateOriginalIntent(root, intent);
                    var protectedPath = ResolveRelative(root, intent.StagedRelativePath);
                    if (protectedPath.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                cursor = page.HasMore ? page.NextCursor : null;
            } while (cursor is not null);
            return false;
        }
        catch
        {
            // Cleanup is optional; preserving an unrelated orphan is safer than deleting the
            // only copy referenced by an unreadable or temporarily unavailable durable intent.
            return true;
        }
    }

    internal static async Task<CaptureJournalOriginalPromotionIntent> PrepareOriginalAsync(
        string libraryRoot,
        string stagedPath,
        string finalGameplayPath,
        CaptureJournalSourceKind sourceKind,
        string gameName,
        DateTimeOffset capturedUtc,
        TimeSpan duration,
        int width,
        int height,
        bool reactionCameraRequested,
        IReadOnlyList<string> requestedRenditions,
        CancellationToken cancellationToken = default,
        DateTimeOffset? now = null,
        string? sourceConnectionId = null,
        string? sourceOccurrenceId = null,
        string? sourceRevisionId = null,
        RoutingLocalOnlyAdmissionSnapshot? localOnlyOverride = null)
    {
        ArgumentNullException.ThrowIfNull(requestedRenditions);
        cancellationToken.ThrowIfCancellationRequested();
        var root = NormalizeRoot(libraryRoot);
        var staged = ValidateOrdinaryExistingFile(root, stagedPath, "original promotion stage");
        var destination = Path.GetFullPath(finalGameplayPath);
        CaptureJournalStore.EnsurePathIsInside(root, destination, "original promotion destination");
        if (!Path.GetExtension(destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            staged.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The original promotion destination is invalid.");
        }
        var destinationDirectory = Path.GetDirectoryName(destination) ??
                                   throw new IOException(
                                       "The original promotion destination folder is invalid.");
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root,
            destinationDirectory,
            requireDirectory: true,
            "original promotion destination folder");
        var destinationRelative = ToRelative(root, destination);
        var clipId = CaptureProjectStore.CreateProjectIdFromRelativeGameplayPath(destinationRelative);
        var fingerprint = await FingerprintAsync(root, staged, cancellationToken).ConfigureAwait(false);
        var requested = requestedRenditions
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var intent = new CaptureJournalOriginalPromotionIntent(
            CurrentSchemaVersion,
            clipId,
            sourceKind,
            gameName.Trim(),
            capturedUtc.ToUniversalTime(),
            duration.Ticks,
            width,
            height,
            reactionCameraRequested,
            requested,
            ToRelative(root, staged),
            destinationRelative,
            fingerprint,
            (now ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            sourceConnectionId?.Trim(),
            sourceOccurrenceId?.Trim().ToLowerInvariant(),
            sourceRevisionId?.Trim().ToLowerInvariant(),
            localOnlyOverride);
        ValidateOriginalIntent(root, intent);

        using var mutex = new Mutex(false, SaveMutexPrefix + clipId + ".original");
        var lockTaken = WaitForMutex(mutex, cancellationToken);
        try
        {
            var directory = EnsureOriginalDirectory(root);
            var path = GetOriginalPath(root, clipId);
            if (File.Exists(path))
            {
                var existing = LoadOriginal(root, clipId);
                if (existing is not null && OriginalIntentMatches(existing, intent)) return existing;
                throw new CaptureJournalConcurrencyException(
                    "A different original promotion intent already owns this clip id.");
            }
            WriteOriginalAtomically(directory, path, intent, cancellationToken);
            return intent;
        }
        finally
        {
            if (lockTaken) mutex.ReleaseMutex();
        }
    }

    internal static async Task<CaptureJournalOriginalPromotionInspection> InspectOriginalAsync(
        string libraryRoot,
        string clipId,
        CancellationToken cancellationToken = default)
    {
        if (!CaptureJournalModel.IsClipId(clipId))
        {
            throw new ArgumentException("The original promotion clip id is invalid.", nameof(clipId));
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var root = NormalizeRoot(libraryRoot);
            var intent = LoadOriginal(root, clipId);
            if (intent is null)
            {
                return new CaptureJournalOriginalPromotionInspection(
                    CaptureJournalPromotionStatus.Missing,
                    null,
                    null);
            }
            ValidateOriginalIntent(root, intent);
            var journal = CaptureJournalStore.Load(root, clipId, cancellationToken);
            if (journal.LoadedFromDisk && journal.Document is not null)
            {
                if (!DocumentMatchesOriginalIntent(journal.Document, intent))
                {
                    return new CaptureJournalOriginalPromotionInspection(
                        CaptureJournalPromotionStatus.DestinationConflict,
                        intent,
                        journal.Document);
                }
                var validation = await CaptureJournalStore.ValidateArtifactAsync(
                        root,
                        journal.Document.Clip.Original,
                        cancellationToken)
                    .ConfigureAwait(false);
                var status = validation.Status switch
                {
                    CaptureJournalArtifactValidationStatus.Valid =>
                        CaptureJournalPromotionStatus.AlreadyJournaled,
                    CaptureJournalArtifactValidationStatus.Missing =>
                        CaptureJournalPromotionStatus.SourceMissing,
                    CaptureJournalArtifactValidationStatus.Mismatch =>
                        CaptureJournalPromotionStatus.FingerprintMismatch,
                    _ => CaptureJournalPromotionStatus.Unavailable
                };
                return new CaptureJournalOriginalPromotionInspection(
                    status,
                    intent,
                    journal.Document);
            }
            if (journal.Status != CaptureJournalLoadStatus.Missing)
            {
                return new CaptureJournalOriginalPromotionInspection(
                    CaptureJournalPromotionStatus.Invalid,
                    intent,
                    null);
            }

            var destination = ResolveRelative(root, intent.DestinationRelativePath);
            if (File.Exists(destination))
            {
                var fingerprint = await FingerprintAsync(root, destination, cancellationToken)
                    .ConfigureAwait(false);
                return new CaptureJournalOriginalPromotionInspection(
                    fingerprint == intent.Fingerprint
                        ? CaptureJournalPromotionStatus.DestinationReady
                        : CaptureJournalPromotionStatus.DestinationConflict,
                    intent,
                    null);
            }
            var staged = ResolveRelative(root, intent.StagedRelativePath);
            if (!File.Exists(staged))
            {
                return new CaptureJournalOriginalPromotionInspection(
                    CaptureJournalPromotionStatus.SourceMissing,
                    intent,
                    null);
            }
            var stagedFingerprint = await FingerprintAsync(root, staged, cancellationToken)
                .ConfigureAwait(false);
            return new CaptureJournalOriginalPromotionInspection(
                stagedFingerprint == intent.Fingerprint
                    ? CaptureJournalPromotionStatus.MoveRequired
                    : CaptureJournalPromotionStatus.FingerprintMismatch,
                intent,
                null);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or JsonException or ArgumentException)
        {
            return new CaptureJournalOriginalPromotionInspection(
                CaptureJournalPromotionStatus.Invalid,
                null,
                null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or NotSupportedException)
        {
            return new CaptureJournalOriginalPromotionInspection(
                CaptureJournalPromotionStatus.Unavailable,
                null,
                null);
        }
    }

    internal static async Task<CaptureJournalOriginalPromotionInspection> PromoteOriginalAsync(
        string libraryRoot,
        string clipId,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectOriginalAsync(libraryRoot, clipId, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.Status != CaptureJournalPromotionStatus.MoveRequired ||
            inspection.Intent is null)
        {
            return inspection;
        }
        var root = NormalizeRoot(libraryRoot);
        return await Task.Run(
                () => PromoteOriginalCore(root, inspection.Intent, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<CaptureJournalDocument> CommitOriginalAsync(
        string libraryRoot,
        string clipId,
        CancellationToken cancellationToken = default,
        DateTimeOffset? now = null)
    {
        var inspection = await InspectOriginalAsync(libraryRoot, clipId, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.Status == CaptureJournalPromotionStatus.AlreadyJournaled &&
            inspection.Document is not null)
        {
            return inspection.Document;
        }
        if (inspection.Status != CaptureJournalPromotionStatus.DestinationReady ||
            inspection.Intent is null)
        {
            throw new InvalidDataException(
                "The original promotion is not ready to create its capture journal.");
        }
        var intent = inspection.Intent;
        var root = NormalizeRoot(libraryRoot);
        var destination = ResolveRelative(root, intent.DestinationRelativePath);
        return await CaptureJournalStore.CommitOriginalAsync(
                root,
                destination,
                intent.SourceKind,
                intent.GameName,
                intent.CapturedUtc,
                TimeSpan.FromTicks(intent.DurationTicks),
                intent.Width,
                intent.Height,
                intent.ReactionCameraRequested,
                intent.RequestedRenditions,
                cancellationToken,
                now,
                intent.Fingerprint,
                intent.SourceConnectionId,
                intent.SourceOccurrenceId,
                intent.SourceRevisionId,
                RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                    intent.LocalOnlyOverride))
            .ConfigureAwait(false);
    }

    internal static async Task CompleteOriginalAsync(
        string libraryRoot,
        string clipId,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectOriginalAsync(libraryRoot, clipId, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.Status != CaptureJournalPromotionStatus.AlreadyJournaled ||
            inspection.Intent is null)
        {
            throw new InvalidDataException(
                "The original promotion intent cannot be cleaned before its journal is durable.");
        }
        var root = NormalizeRoot(libraryRoot);
        await Task.Run(
                () => CompleteOriginalCore(root, inspection.Intent, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<CaptureJournalPromotionIntent> PrepareAsync(
        string libraryRoot,
        string clipId,
        string kind,
        long expectedGeneration,
        string stagedPath,
        CancellationToken cancellationToken = default,
        DateTimeOffset? now = null)
    {
        ValidateIdentity(clipId, kind);
        if (expectedGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var root = NormalizeRoot(libraryRoot);
        var journal = CaptureJournalStore.Load(root, clipId, cancellationToken);
        if (!journal.LoadedFromDisk || journal.Document is null ||
            journal.Document.Generation != expectedGeneration ||
            journal.Document.State != CaptureJournalState.CameraPending)
        {
            throw new CaptureJournalConcurrencyException(
                "The promotion intent does not target the current journal generation.");
        }
        RequireRequestedKind(journal.Document, kind);
        if (CaptureJournalArtifactKinds.Renditions.Contains(kind, StringComparer.Ordinal) &&
            !journal.Document.CameraPresent)
        {
            throw new InvalidDataException(
                "A rendition promotion cannot begin before the camera layer is journaled.");
        }

        var normalizedStaged = ValidateOrdinaryExistingFile(root, stagedPath, "promotion stage");
        var destination = CaptureJournalStore.GetCanonicalArtifactPath(
            root,
            journal.Document.Clip,
            kind);
        EnsureInside(root, destination, "promotion destination");
        if (normalizedStaged.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The promotion stage must be distinct from its canonical destination.");
        }
        var fingerprint = await FingerprintAsync(root, normalizedStaged, cancellationToken)
            .ConfigureAwait(false);
        var createdUtc = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var intent = new CaptureJournalPromotionIntent(
            CurrentSchemaVersion,
            clipId,
            kind,
            expectedGeneration,
            journal.Document.ProcessingAttemptEpoch,
            journal.Document.ProcessingAttemptId ?? throw new InvalidDataException(
                "The pending journal has no processing attempt id."),
            ToRelative(root, normalizedStaged),
            ToRelative(root, destination),
            fingerprint,
            createdUtc);
        ValidateIntent(root, intent, journal.Document);

        using var mutex = new Mutex(false, SaveMutexPrefix + clipId + "." + kind);
        var lockTaken = WaitForMutex(mutex, cancellationToken);
        try
        {
            var directory = EnsureDirectory(root);
            var path = GetPath(root, clipId, kind);
            if (File.Exists(path))
            {
                var existing = Load(root, clipId, kind);
                if (existing is not null && IntentMatches(existing, intent)) return existing;
                if (existing is null ||
                    existing.ProcessingAttemptEpoch == intent.ProcessingAttemptEpoch ||
                    existing.ProcessingAttemptId.Equals(
                        intent.ProcessingAttemptId,
                        StringComparison.Ordinal))
                {
                    throw new CaptureJournalConcurrencyException(
                        "A different promotion intent already owns this artifact kind.");
                }
                File.Delete(path);
            }
            WriteAtomically(directory, path, intent, cancellationToken);
            return intent;
        }
        finally
        {
            if (lockTaken) mutex.ReleaseMutex();
        }
    }

    internal static async Task<CaptureJournalPromotionInspection> InspectAsync(
        string libraryRoot,
        string clipId,
        string kind,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(clipId, kind);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var root = NormalizeRoot(libraryRoot);
            var intent = Load(root, clipId, kind);
            if (intent is null)
            {
                return new CaptureJournalPromotionInspection(
                    CaptureJournalPromotionStatus.Missing,
                    null,
                    null);
            }
            var journal = CaptureJournalStore.Load(root, clipId, cancellationToken);
            if (!journal.LoadedFromDisk || journal.Document is null)
            {
                return new CaptureJournalPromotionInspection(
                    CaptureJournalPromotionStatus.Invalid,
                    intent,
                    null);
            }
            if (!AttemptMatches(intent, journal.Document) ||
                journal.Document.State == CaptureJournalState.RenditionsFailed)
            {
                return new CaptureJournalPromotionInspection(
                    CaptureJournalPromotionStatus.StaleAttempt,
                    intent,
                    null);
            }
            ValidateIntent(root, intent, journal.Document);
            var destination = ResolveRelative(root, intent.DestinationRelativePath);
            var staged = ResolveRelative(root, intent.StagedRelativePath);
            var expectedArtifact = new CaptureJournalArtifact(
                CaptureJournalModel.CreateArtifactId(clipId, kind),
                kind,
                intent.DestinationRelativePath,
                intent.Fingerprint);
            var attached = journal.Document.Artifacts.SingleOrDefault(candidate =>
                candidate.Kind.Equals(kind, StringComparison.Ordinal));
            if (attached is not null)
            {
                var matches = attached == expectedArtifact;
                if (matches)
                {
                    var validation = await CaptureJournalStore.ValidateArtifactAsync(
                            root,
                            attached,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var status = validation.Status switch
                    {
                        CaptureJournalArtifactValidationStatus.Valid =>
                            CaptureJournalPromotionStatus.AlreadyJournaled,
                        CaptureJournalArtifactValidationStatus.Missing =>
                            CaptureJournalPromotionStatus.SourceMissing,
                        CaptureJournalArtifactValidationStatus.Mismatch =>
                            CaptureJournalPromotionStatus.FingerprintMismatch,
                        _ => CaptureJournalPromotionStatus.Unavailable
                    };
                    return new CaptureJournalPromotionInspection(
                        status,
                        intent,
                        status == CaptureJournalPromotionStatus.AlreadyJournaled
                            ? attached
                            : null);
                }
                return new CaptureJournalPromotionInspection(
                    CaptureJournalPromotionStatus.DestinationConflict,
                    intent,
                    null);
            }

            if (File.Exists(destination))
            {
                var destinationFingerprint = await FingerprintAsync(root, destination, cancellationToken)
                    .ConfigureAwait(false);
                var matches = destinationFingerprint == intent.Fingerprint;
                return new CaptureJournalPromotionInspection(
                    matches
                        ? CaptureJournalPromotionStatus.DestinationReady
                        : CaptureJournalPromotionStatus.DestinationConflict,
                    intent,
                    matches ? expectedArtifact : null);
            }
            if (!File.Exists(staged))
            {
                return new CaptureJournalPromotionInspection(
                    CaptureJournalPromotionStatus.SourceMissing,
                    intent,
                    null);
            }
            var stagedFingerprint = await FingerprintAsync(root, staged, cancellationToken)
                .ConfigureAwait(false);
            return new CaptureJournalPromotionInspection(
                stagedFingerprint == intent.Fingerprint
                    ? CaptureJournalPromotionStatus.MoveRequired
                    : CaptureJournalPromotionStatus.FingerprintMismatch,
                intent,
                null);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or JsonException or ArgumentException)
        {
            return new CaptureJournalPromotionInspection(
                CaptureJournalPromotionStatus.Invalid,
                null,
                null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or NotSupportedException or
                PathTooLongException)
        {
            return new CaptureJournalPromotionInspection(
                CaptureJournalPromotionStatus.Unavailable,
                null,
                null);
        }
    }

    internal static async Task<CaptureJournalPromotionInspection> PromoteAsync(
        string libraryRoot,
        string clipId,
        string kind,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(libraryRoot, clipId, kind, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.Status != CaptureJournalPromotionStatus.MoveRequired ||
            inspection.Intent is null)
        {
            return inspection;
        }

        var root = NormalizeRoot(libraryRoot);
        return await Task.Run(
                () => PromoteCore(root, inspection.Intent, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task CompleteAsync(
        string libraryRoot,
        string clipId,
        string kind,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(libraryRoot, clipId, kind, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.Status != CaptureJournalPromotionStatus.AlreadyJournaled)
        {
            throw new InvalidDataException(
                "A promotion intent can be completed only after its exact artifact is journaled.");
        }
        var root = NormalizeRoot(libraryRoot);
        await Task.Run(
                () => CompleteCore(root, inspection.Intent!, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static CaptureJournalOriginalPromotionInspection PromoteOriginalCore(
        string root,
        CaptureJournalOriginalPromotionIntent expectedIntent,
        CancellationToken cancellationToken)
    {
        using var journalMutex = CaptureJournalStore.CreateSaveMutex(expectedIntent.ClipId);
        var journalLockTaken = CaptureJournalStore.WaitForMutex(journalMutex, cancellationToken);
        using var intentMutex = new Mutex(
            false,
            SaveMutexPrefix + expectedIntent.ClipId + ".original");
        var intentLockTaken = WaitForMutex(intentMutex, cancellationToken);
        try
        {
            var currentIntent = LoadOriginal(root, expectedIntent.ClipId);
            if (currentIntent is null || !OriginalIntentMatches(currentIntent, expectedIntent))
            {
                throw new CaptureJournalConcurrencyException(
                    "The original promotion intent changed before its move could commit.");
            }
            ValidateOriginalIntent(root, currentIntent);
            var existingJournal = CaptureJournalStore.Load(
                root,
                currentIntent.ClipId,
                cancellationToken);
            if (existingJournal.LoadedFromDisk && existingJournal.Document is not null)
            {
                return new CaptureJournalOriginalPromotionInspection(
                    DocumentMatchesOriginalIntent(existingJournal.Document, currentIntent)
                        ? CaptureJournalPromotionStatus.AlreadyJournaled
                        : CaptureJournalPromotionStatus.DestinationConflict,
                    currentIntent,
                    existingJournal.Document);
            }
            if (existingJournal.Status != CaptureJournalLoadStatus.Missing)
            {
                return new CaptureJournalOriginalPromotionInspection(
                    CaptureJournalPromotionStatus.Invalid,
                    currentIntent,
                    null);
            }
            var staged = ResolveRelative(root, currentIntent.StagedRelativePath);
            var destination = ResolveRelative(root, currentIntent.DestinationRelativePath);
            var destinationDirectory = Path.GetDirectoryName(destination) ??
                                       throw new IOException(
                                           "The original promotion destination folder is invalid.");
            CaptureJournalStore.EnsureOrdinaryExistingPath(
                root,
                destinationDirectory,
                requireDirectory: true,
                "original promotion destination folder");
            if (File.Exists(destination))
            {
                return new CaptureJournalOriginalPromotionInspection(
                    FingerprintCore(root, destination) == currentIntent.Fingerprint
                        ? CaptureJournalPromotionStatus.DestinationReady
                        : CaptureJournalPromotionStatus.DestinationConflict,
                    currentIntent,
                    null);
            }
            if (FingerprintCore(root, staged) != currentIntent.Fingerprint)
            {
                return new CaptureJournalOriginalPromotionInspection(
                    CaptureJournalPromotionStatus.FingerprintMismatch,
                    currentIntent,
                    null);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, destination, overwrite: false);
            return new CaptureJournalOriginalPromotionInspection(
                CaptureJournalPromotionStatus.DestinationReady,
                currentIntent,
                null);
        }
        finally
        {
            if (intentLockTaken) intentMutex.ReleaseMutex();
            if (journalLockTaken) journalMutex.ReleaseMutex();
        }
    }

    private static void CompleteOriginalCore(
        string root,
        CaptureJournalOriginalPromotionIntent expectedIntent,
        CancellationToken cancellationToken)
    {
        using var journalMutex = CaptureJournalStore.CreateSaveMutex(expectedIntent.ClipId);
        var journalLockTaken = CaptureJournalStore.WaitForMutex(journalMutex, cancellationToken);
        using var intentMutex = new Mutex(
            false,
            SaveMutexPrefix + expectedIntent.ClipId + ".original");
        var intentLockTaken = WaitForMutex(intentMutex, cancellationToken);
        try
        {
            var currentIntent = LoadOriginal(root, expectedIntent.ClipId);
            if (currentIntent is null || !OriginalIntentMatches(currentIntent, expectedIntent))
            {
                throw new CaptureJournalConcurrencyException(
                    "The original promotion intent changed before cleanup acquired its lock.");
            }
            var journal = CaptureJournalStore.Load(
                root,
                currentIntent.ClipId,
                cancellationToken);
            if (!journal.LoadedFromDisk || journal.Document is null ||
                !DocumentMatchesOriginalIntent(journal.Document, currentIntent) ||
                FingerprintCore(
                    root,
                    ResolveRelative(root, currentIntent.DestinationRelativePath)) !=
                currentIntent.Fingerprint)
            {
                throw new InvalidDataException(
                    "The exact promoted original is not durably journaled and intact.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(GetOriginalPath(root, currentIntent.ClipId));
        }
        finally
        {
            if (intentLockTaken) intentMutex.ReleaseMutex();
            if (journalLockTaken) journalMutex.ReleaseMutex();
        }
    }

    private static CaptureJournalPromotionInspection PromoteCore(
        string root,
        CaptureJournalPromotionIntent expectedIntent,
        CancellationToken cancellationToken)
    {
        using var journalMutex = CaptureJournalStore.CreateSaveMutex(expectedIntent.ClipId);
        var journalLockTaken = CaptureJournalStore.WaitForMutex(journalMutex, cancellationToken);
        using var intentMutex = new Mutex(
            false,
            SaveMutexPrefix + expectedIntent.ClipId + "." + expectedIntent.Kind);
        var intentLockTaken = WaitForMutex(intentMutex, cancellationToken);
        try
        {
            var currentIntent = Load(root, expectedIntent.ClipId, expectedIntent.Kind);
            if (currentIntent is null || !IntentMatches(currentIntent, expectedIntent))
            {
                throw new CaptureJournalConcurrencyException(
                    "The promotion intent changed before its move could commit.");
            }
            var journal = CaptureJournalStore.Load(
                root,
                expectedIntent.ClipId,
                cancellationToken);
            if (!journal.LoadedFromDisk || journal.Document is null ||
                journal.Document.State != CaptureJournalState.CameraPending ||
                !AttemptMatches(currentIntent, journal.Document))
            {
                return new CaptureJournalPromotionInspection(
                    CaptureJournalPromotionStatus.StaleAttempt,
                    currentIntent,
                    null);
            }
            ValidateIntent(root, currentIntent, journal.Document);
            var staged = ResolveRelative(root, currentIntent.StagedRelativePath);
            var destination = ResolveRelative(root, currentIntent.DestinationRelativePath);
            var destinationDirectory = Path.GetDirectoryName(destination) ??
                                       throw new IOException(
                                           "The promotion destination folder is invalid.");
            CaptureJournalStore.EnsureOrdinaryExistingPath(
                root,
                destinationDirectory,
                requireDirectory: true,
                "promotion destination folder");
            if (File.Exists(destination))
            {
                var existing = FingerprintCore(root, destination);
                var ready = existing == currentIntent.Fingerprint;
                return new CaptureJournalPromotionInspection(
                    ready
                        ? CaptureJournalPromotionStatus.DestinationReady
                        : CaptureJournalPromotionStatus.DestinationConflict,
                    currentIntent,
                    ready ? CreateExpectedArtifact(currentIntent) : null);
            }
            var stagedFingerprint = FingerprintCore(root, staged);
            if (stagedFingerprint != currentIntent.Fingerprint)
            {
                return new CaptureJournalPromotionInspection(
                    CaptureJournalPromotionStatus.FingerprintMismatch,
                    currentIntent,
                    null);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, destination, overwrite: false);
            return new CaptureJournalPromotionInspection(
                CaptureJournalPromotionStatus.DestinationReady,
                currentIntent,
                CreateExpectedArtifact(currentIntent));
        }
        finally
        {
            if (intentLockTaken) intentMutex.ReleaseMutex();
            if (journalLockTaken) journalMutex.ReleaseMutex();
        }
    }

    private static void CompleteCore(
        string root,
        CaptureJournalPromotionIntent expectedIntent,
        CancellationToken cancellationToken)
    {
        using var journalMutex = CaptureJournalStore.CreateSaveMutex(expectedIntent.ClipId);
        var journalLockTaken = CaptureJournalStore.WaitForMutex(journalMutex, cancellationToken);
        using var intentMutex = new Mutex(
            false,
            SaveMutexPrefix + expectedIntent.ClipId + "." + expectedIntent.Kind);
        var intentLockTaken = WaitForMutex(intentMutex, cancellationToken);
        try
        {
            var currentIntent = Load(root, expectedIntent.ClipId, expectedIntent.Kind);
            if (currentIntent is null || !IntentMatches(currentIntent, expectedIntent))
            {
                throw new CaptureJournalConcurrencyException(
                    "The promotion intent changed before cleanup acquired its lock.");
            }
            var journal = CaptureJournalStore.Load(
                root,
                currentIntent.ClipId,
                cancellationToken);
            if (!journal.LoadedFromDisk || journal.Document is null ||
                !AttemptMatches(currentIntent, journal.Document))
            {
                throw new InvalidDataException(
                    "The promotion intent no longer belongs to the active processing attempt.");
            }
            ValidateIntent(root, currentIntent, journal.Document);
            var expectedArtifact = CreateExpectedArtifact(currentIntent);
            var attached = journal.Document.Artifacts.SingleOrDefault(candidate =>
                candidate.Kind.Equals(currentIntent.Kind, StringComparison.Ordinal));
            if (attached != expectedArtifact ||
                FingerprintCore(
                    root,
                    ResolveRelative(root, currentIntent.DestinationRelativePath)) !=
                currentIntent.Fingerprint)
            {
                throw new InvalidDataException(
                    "The exact promoted artifact is not durably journaled and intact.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(GetPath(root, currentIntent.ClipId, currentIntent.Kind));
        }
        finally
        {
            if (intentLockTaken) intentMutex.ReleaseMutex();
            if (journalLockTaken) journalMutex.ReleaseMutex();
        }
    }

    private static CaptureJournalOriginalPromotionIntent? LoadOriginal(
        string root,
        string clipId)
    {
        var path = GetOriginalPath(root, clipId);
        if (!File.Exists(path)) return null;
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root,
            path,
            requireDirectory: false,
            "original promotion intent");
        var file = new FileInfo(path);
        if (file.Length <= 0 || file.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                "The original promotion intent has an invalid size.");
        }
        var bytes = File.ReadAllBytes(path);
        return JsonSerializer.Deserialize<CaptureJournalOriginalPromotionIntent>(bytes, JsonOptions) ??
               throw new InvalidDataException("The original promotion intent is empty.");
    }

    private static void ValidateOriginalIntent(
        string root,
        CaptureJournalOriginalPromotionIntent intent)
    {
        if (intent.SchemaVersion != CurrentSchemaVersion ||
            !CaptureJournalModel.IsClipId(intent.ClipId) ||
            !Enum.IsDefined(intent.SourceKind) ||
            string.IsNullOrWhiteSpace(intent.GameName) || intent.GameName.Length > 160 ||
            intent.CapturedUtc.Offset != TimeSpan.Zero ||
            intent.CreatedUtc.Offset != TimeSpan.Zero ||
            intent.DurationTicks <= 0 || intent.Width <= 0 || intent.Height <= 0 ||
            intent.RequestedRenditions is null ||
            intent.RequestedRenditions.Distinct(StringComparer.Ordinal).Count() !=
            intent.RequestedRenditions.Count ||
            intent.RequestedRenditions.Any(value =>
                !CaptureJournalArtifactKinds.Renditions.Contains(value, StringComparer.Ordinal)) ||
            intent.ReactionCameraRequested != (intent.RequestedRenditions.Count > 0) ||
            intent.Fingerprint is not { ByteLength: > 0 } ||
            !CaptureJournalModel.IsSha256(intent.Fingerprint.Sha256))
        {
            throw new InvalidDataException("The original promotion intent is invalid.");
        }
        if (intent.LocalOnlyOverride is not null)
            RoutingLocalOnlyAdmissionSnapshot.Validate(intent.LocalOnlyOverride);
        if (intent.SourceKind == CaptureJournalSourceKind.XboxGameDvr)
        {
            RoutingValidation.RequireOpaqueId(
                intent.SourceConnectionId, 128, "Xbox source connection id");
            if (!CaptureJournalModel.IsSha256(intent.SourceOccurrenceId) ||
                !intent.SourceOccurrenceId!.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                !CaptureJournalModel.IsSha256(intent.SourceRevisionId) ||
                !intent.SourceRevisionId!.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                intent.ReactionCameraRequested)
            {
                throw new InvalidDataException("The Xbox promotion provenance is invalid.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(intent.SourceConnectionId) ||
                 !string.IsNullOrWhiteSpace(intent.SourceOccurrenceId) ||
                 !string.IsNullOrWhiteSpace(intent.SourceRevisionId))
        {
            throw new InvalidDataException(
                "A ClipCord capture promotion cannot carry external-source provenance.");
        }
        CaptureJournalModel.ValidateRelativePath(intent.DestinationRelativePath);
        var staged = ResolveRelative(root, intent.StagedRelativePath);
        var destination = ResolveRelative(root, intent.DestinationRelativePath);
        EnsureOrdinaryParent(root, staged, "original promotion stage folder");
        EnsureOrdinaryParent(root, destination, "original promotion destination folder");
        if (staged.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            !CaptureProjectStore.CreateProjectIdFromRelativeGameplayPath(
                    intent.DestinationRelativePath)
                .Equals(intent.ClipId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The original promotion destination does not match its clip identity.");
        }
    }

    private static bool OriginalIntentMatches(
        CaptureJournalOriginalPromotionIntent left,
        CaptureJournalOriginalPromotionIntent right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.ClipId.Equals(right.ClipId, StringComparison.Ordinal) &&
        left.SourceKind == right.SourceKind &&
        left.GameName.Equals(right.GameName, StringComparison.Ordinal) &&
        left.CapturedUtc == right.CapturedUtc &&
        left.DurationTicks == right.DurationTicks &&
        left.Width == right.Width &&
        left.Height == right.Height &&
        left.ReactionCameraRequested == right.ReactionCameraRequested &&
        left.RequestedRenditions.SequenceEqual(right.RequestedRenditions, StringComparer.Ordinal) &&
        left.StagedRelativePath.Equals(right.StagedRelativePath, StringComparison.Ordinal) &&
        left.DestinationRelativePath.Equals(right.DestinationRelativePath, StringComparison.Ordinal) &&
        left.Fingerprint == right.Fingerprint &&
        left.CreatedUtc == right.CreatedUtc &&
        left.SourceConnectionId == right.SourceConnectionId &&
        left.SourceOccurrenceId == right.SourceOccurrenceId &&
        left.SourceRevisionId == right.SourceRevisionId &&
        left.LocalOnlyOverride == right.LocalOnlyOverride;

    private static bool DocumentMatchesOriginalIntent(
        CaptureJournalDocument document,
        CaptureJournalOriginalPromotionIntent intent) =>
        document.Clip.ClipId.Equals(intent.ClipId, StringComparison.Ordinal) &&
        document.Clip.SourceKind == intent.SourceKind &&
        document.Clip.GameName.Equals(intent.GameName, StringComparison.Ordinal) &&
        document.Clip.CapturedUtc == intent.CapturedUtc &&
        document.Clip.DurationTicks == intent.DurationTicks &&
        document.Clip.Width == intent.Width &&
        document.Clip.Height == intent.Height &&
        document.Clip.ReactionCameraRequested == intent.ReactionCameraRequested &&
        document.Clip.RequestedRenditions.SequenceEqual(
            intent.RequestedRenditions,
            StringComparer.Ordinal) &&
        document.Clip.SourceConnectionId == intent.SourceConnectionId &&
        document.Clip.SourceOccurrenceId == intent.SourceOccurrenceId &&
        document.Clip.SourceRevisionId == intent.SourceRevisionId &&
        document.Clip.LocalOnlyOverride ==
            RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                intent.LocalOnlyOverride) &&
        document.Clip.Original.RelativePath.Equals(
            intent.DestinationRelativePath,
            StringComparison.Ordinal) &&
        document.Clip.Original.Fingerprint == intent.Fingerprint;

    private static CaptureJournalPromotionIntent? Load(
        string root,
        string clipId,
        string kind)
    {
        var path = GetPath(root, clipId, kind);
        if (!File.Exists(path)) return null;
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root,
            path,
            requireDirectory: false,
            "promotion intent");
        var file = new FileInfo(path);
        if (file.Length <= 0 || file.Length > MaximumDocumentBytes ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The promotion intent has an invalid file identity.");
        }
        var bytes = File.ReadAllBytes(path);
        return JsonSerializer.Deserialize<CaptureJournalPromotionIntent>(bytes, JsonOptions) ??
               throw new InvalidDataException("The promotion intent is empty.");
    }

    private static void ValidateIntent(
        string root,
        CaptureJournalPromotionIntent intent,
        CaptureJournalDocument journal)
    {
        if (intent.SchemaVersion != CurrentSchemaVersion ||
            !intent.ClipId.Equals(journal.Clip.ClipId, StringComparison.Ordinal) ||
            intent.ExpectedGeneration <= 0 ||
            intent.ExpectedGeneration > journal.Generation ||
            intent.ProcessingAttemptEpoch <= 0 ||
            !CaptureJournalModel.IsClipId(intent.ProcessingAttemptId) ||
            intent.CreatedUtc.Offset != TimeSpan.Zero ||
            intent.Fingerprint is not { ByteLength: > 0 } ||
            !CaptureJournalModel.IsSha256(intent.Fingerprint.Sha256))
        {
            throw new InvalidDataException("The promotion intent is invalid.");
        }
        ValidateIdentity(intent.ClipId, intent.Kind);
        if (!AttemptMatches(intent, journal))
        {
            throw new InvalidDataException(
                "The promotion intent does not match the active processing attempt.");
        }
        RequireRequestedKind(journal, intent.Kind);
        CaptureJournalModel.ValidateRelativePath(intent.DestinationRelativePath);
        var staged = ResolveRelative(root, intent.StagedRelativePath);
        var destination = ResolveRelative(root, intent.DestinationRelativePath);
        EnsureOrdinaryParent(root, staged, "promotion stage folder");
        EnsureOrdinaryParent(root, destination, "promotion destination folder");
        if (staged.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            !destination.Equals(
                CaptureJournalStore.GetCanonicalArtifactPath(root, journal.Clip, intent.Kind),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The promotion intent does not target the canonical artifact path.");
        }
    }

    private static void RequireRequestedKind(
        CaptureJournalDocument journal,
        string kind)
    {
        var requested = kind.Equals(
                CaptureJournalArtifactKinds.ReactionCamera,
                StringComparison.Ordinal)
            ? journal.Clip.ReactionCameraRequested
            : journal.Clip.RequestedRenditions.Contains(kind, StringComparer.Ordinal);
        if (!requested)
        {
            throw new InvalidDataException(
                "The promotion intent artifact was not requested by the capture snapshot.");
        }
    }

    private static bool IntentMatches(
        CaptureJournalPromotionIntent left,
        CaptureJournalPromotionIntent right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.ClipId.Equals(right.ClipId, StringComparison.Ordinal) &&
        left.Kind.Equals(right.Kind, StringComparison.Ordinal) &&
        left.ExpectedGeneration == right.ExpectedGeneration &&
        left.ProcessingAttemptEpoch == right.ProcessingAttemptEpoch &&
        left.ProcessingAttemptId.Equals(right.ProcessingAttemptId, StringComparison.Ordinal) &&
        left.StagedRelativePath.Equals(right.StagedRelativePath, StringComparison.Ordinal) &&
        left.DestinationRelativePath.Equals(right.DestinationRelativePath, StringComparison.Ordinal) &&
        left.Fingerprint == right.Fingerprint &&
        left.CreatedUtc == right.CreatedUtc;

    private static bool AttemptMatches(
        CaptureJournalPromotionIntent intent,
        CaptureJournalDocument journal) =>
        intent.ProcessingAttemptEpoch == journal.ProcessingAttemptEpoch &&
        intent.ProcessingAttemptId.Equals(
            journal.ProcessingAttemptId,
            StringComparison.Ordinal);

    private static async Task<CaptureJournalFingerprint> FingerprintAsync(
        string root,
        string path,
        CancellationToken cancellationToken)
    {
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root,
            path,
            requireDirectory: false,
            "promotion artifact");
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length <= 0) throw new InvalidDataException("The promotion artifact is empty.");
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(
                stream,
                cancellationToken)
            .ConfigureAwait(false);
        return new CaptureJournalFingerprint(
            length,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static CaptureJournalFingerprint FingerprintCore(string root, string path)
    {
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root,
            path,
            requireDirectory: false,
            "promotion artifact");
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        var length = stream.Length;
        if (length <= 0) throw new InvalidDataException("The promotion artifact is empty.");
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return new CaptureJournalFingerprint(
            length,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static CaptureJournalArtifact CreateExpectedArtifact(
        CaptureJournalPromotionIntent intent) => new(
        CaptureJournalModel.CreateArtifactId(intent.ClipId, intent.Kind),
        intent.Kind,
        intent.DestinationRelativePath,
        intent.Fingerprint);

    private static string EnsureDirectory(string root)
    {
        var directory = GetDirectory(root);
        CaptureJournalStore.EnsurePathIsInside(root, directory, "promotion intent folder");
        CaptureJournalStore.EnsureOrdinaryAncestorsBeforeCreate(root, directory);
        Directory.CreateDirectory(directory);
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root,
            directory,
            requireDirectory: true,
            "promotion intent folder");
        return directory;
    }

    private static string EnsureOriginalDirectory(string root)
    {
        var directory = GetOriginalDirectory(root);
        CaptureJournalStore.EnsurePathIsInside(
            root,
            directory,
            "original promotion intent folder");
        CaptureJournalStore.EnsureOrdinaryAncestorsBeforeCreate(root, directory);
        Directory.CreateDirectory(directory);
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root,
            directory,
            requireDirectory: true,
            "original promotion intent folder");
        return directory;
    }

    private static void WriteAtomically(
        string directory,
        string destination,
        CaptureJournalPromotionIntent intent,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(intent, JsonOptions);
        if (bytes.Length <= 0 || bytes.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException("The promotion intent is too large.");
        }
        var temporary = Path.Combine(
            directory,
            $".{intent.ClipId}.{intent.Kind}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    private static void WriteOriginalAtomically(
        string directory,
        string destination,
        CaptureJournalOriginalPromotionIntent intent,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(intent, JsonOptions);
        if (bytes.Length <= 0 || bytes.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException("The original promotion intent is too large.");
        }
        var temporary = Path.Combine(
            directory,
            $".{intent.ClipId}.original.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    private static string ValidateOrdinaryExistingFile(
        string root,
        string path,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.GetFullPath(path);
        CaptureJournalStore.EnsurePathIsInside(root, normalized, description);
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root,
            normalized,
            requireDirectory: false,
            description);
        if (new FileInfo(normalized).Length <= 0)
        {
            throw new InvalidDataException($"The {description} is unavailable or redirected.");
        }
        return normalized;
    }

    private static string ResolveRelative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("A promotion intent path is invalid.");
        }
        var normalized = Path.GetFullPath(Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var components = relative.Replace('\\', '/').Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components.Any(component =>
                component is "." or ".." || component.Contains(':')))
        {
            throw new InvalidDataException("A promotion intent path is invalid.");
        }
        CaptureJournalStore.EnsurePathIsInside(root, normalized, "promotion intent path");
        return normalized;
    }

    private static void EnsureOrdinaryParent(string root, string path, string description)
    {
        var parent = Path.GetDirectoryName(path) ??
                     throw new IOException($"The {description} is invalid.");
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root,
            parent,
            requireDirectory: true,
            description);
    }

    private static void CollectOriginalClipIdsForPrefix(
        string directory,
        string prefix,
        string? afterClipId,
        int target,
        List<string> collected,
        CancellationToken cancellationToken)
    {
        if (collected.Count >= target) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (afterClipId is not null)
        {
            var relation = string.CompareOrdinal(
                prefix,
                afterClipId[..Math.Min(prefix.Length, afterClipId.Length)]);
            if (relation < 0) return;
            if (relation == 0 && prefix.Length < afterClipId.Length)
            {
                foreach (var next in ClipIdAlphabet)
                {
                    CollectOriginalClipIdsForPrefix(
                        directory,
                        prefix + next,
                        afterClipId,
                        target,
                        collected,
                        cancellationToken);
                    if (collected.Count >= target) return;
                }
                return;
            }
            if (relation == 0 && prefix.Length == afterClipId.Length) return;
        }

        if (prefix.Length == 32)
        {
            if (File.Exists(Path.Combine(directory, prefix + OriginalIntentFileSuffix)))
            {
                collected.Add(prefix);
            }
            return;
        }

        var remaining = target - collected.Count;
        var candidates = Directory
            .EnumerateFiles(
                directory,
                prefix + "*" + OriginalIntentFileSuffix,
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name.EndsWith(OriginalIntentFileSuffix, StringComparison.Ordinal))
            .Select(name => name[..^OriginalIntentFileSuffix.Length])
            .Where(candidate =>
                CaptureJournalModel.IsClipId(candidate) &&
                (afterClipId is null || string.CompareOrdinal(candidate, afterClipId) > 0))
            .Take(remaining + 1)
            .ToArray();
        if (candidates.Length <= remaining)
        {
            Array.Sort(candidates, StringComparer.Ordinal);
            collected.AddRange(candidates);
            return;
        }
        foreach (var next in ClipIdAlphabet)
        {
            CollectOriginalClipIdsForPrefix(
                directory,
                prefix + next,
                afterClipId,
                target,
                collected,
                cancellationToken);
            if (collected.Count >= target) return;
        }
    }

    private static string ToRelative(string root, string path)
    {
        CaptureJournalStore.EnsurePathIsInside(root, path, "promotion intent path");
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative.Length is <= 0 or > 1024 ||
            relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(component => component is "." or ".." || component.Contains(':')))
        {
            throw new InvalidDataException("A promotion intent path is invalid.");
        }
        return relative;
    }

    private static string NormalizeRoot(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        var root = CaptureJournalStore.NormalizeLibraryRoot(libraryRoot);
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root,
            root,
            requireDirectory: true,
            "ClipCord library");
        return root;
    }

    private static void EnsureInside(string root, string candidate, string description)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"The {description} must remain inside the ClipCord library.");
        }
    }

    private static void ValidateIdentity(string clipId, string kind)
    {
        if (!CaptureJournalModel.IsClipId(clipId))
        {
            throw new ArgumentException("The promotion clip id is invalid.", nameof(clipId));
        }
        if (!CaptureJournalArtifactKinds.IsKnown(kind))
        {
            throw new ArgumentException("The promotion artifact kind is invalid.", nameof(kind));
        }
    }

    private static bool WaitForMutex(Mutex mutex, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (mutex.WaitOne(TimeSpan.FromMilliseconds(100))) return true;
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }
    }
}
