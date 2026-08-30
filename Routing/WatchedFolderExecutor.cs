using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

/// <summary>Resolves only immutable, journal-backed watched-folder source occurrences.</summary>
internal sealed class WatchedFolderRoutingArtifactResolver : IRoutingArtifactResolver
{
    private readonly string _clipsRoot;
    private readonly RoutingWatchedSourceJournalStore _journals;

    internal WatchedFolderRoutingArtifactResolver(
        string clipsRoot,
        RoutingWatchedSourceJournalStore journals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipsRoot);
        _clipsRoot = Path.GetFullPath(clipsRoot);
        _journals = journals ?? throw new ArgumentNullException(nameof(journals));
    }

    public async Task<RoutingResolvedArtifact> ResolveAsync(
        PlannedDelivery delivery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var journal = WatchedFolderRoutingEvidence.LoadPrepared(_journals, delivery.SourceClipId,
            cancellationToken);
        WatchedFolderRoutingEvidence.RequireFrozenDelivery(journal, delivery);
        var expected = RoutingEvaluator.CreateLogicalOutputReference(
            journal.SourceClipId, journal.ContentSha256, RoutingOutputKind.Original);
        if (delivery.Output != expected || delivery.OriginalOutput != expected)
        {
            throw new InvalidDataException(
                "A watched-folder delivery does not resolve to its frozen original artifact.");
        }

        var current = await WatchedFolderRoutingEvidence.RevalidateSourceAsync(
                _clipsRoot, journal, cancellationToken)
            .ConfigureAwait(false);
        var path = WatchedFolderRoutingEvidence.SourcePath(_clipsRoot, journal);
        var opened = RoutingWatchedFileSystem.OpenOrdinaryFile(path, current.CanonicalRoot);
        try
        {
            if (opened.Identity != journal.FileIdentity)
            {
                throw new InvalidDataException(
                    "The watched-folder artifact changed before its read lease was acquired.");
            }
            opened.Stream.Position = 0;
            var hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(opened.Stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!hash.Equals(journal.ContentSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The watched-folder artifact content changed before delivery.");
            }
            opened.Stream.Position = 0;
            return new RoutingResolvedArtifact(
                path,
                journal.DisplayFileName,
                journal.GameName,
                opened.Identity.ByteLength,
                hash,
                opened.Stream);
        }
        catch
        {
            await opened.Stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Performs one deterministic, non-overwriting move for a journal-backed watched occurrence.
/// Recovery classifies the two exact paths; it never invents another name or guesses ownership.
/// </summary>
internal enum WatchedFolderRoutingInspectionPoint
{
    BeforeMove,
    AfterMoveAttempt,
    Recovery
}

internal sealed class WatchedFolderRoutingLibraryFiler : IRoutingLibraryFiler
{
    private readonly string _clipsRoot;
    private readonly RoutingWatchedSourceJournalStore _journals;
    private readonly Action<WatchedFolderRoutingInspectionPoint>? _beforeInspection;

    internal WatchedFolderRoutingLibraryFiler(
        string clipsRoot,
        RoutingWatchedSourceJournalStore journals,
        Action<WatchedFolderRoutingInspectionPoint>? beforeInspection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipsRoot);
        _clipsRoot = Path.GetFullPath(clipsRoot);
        _journals = journals ?? throw new ArgumentNullException(nameof(journals));
        _beforeInspection = beforeInspection;
    }

    public async Task<RoutingFileAttemptResult> FileAsync(
        PlannedFileDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        var journal = WatchedFolderRoutingEvidence.LoadPrepared(
            _journals, disposition.SourceClipId, cancellationToken);
        WatchedFolderRoutingEvidence.RequireFrozenDisposition(journal, disposition);
        var moveAttempted = false;
        try
        {
            var rootLease = WatchedFolderRoutingEvidence.OpenValidatedRoot(_clipsRoot, journal);
            using var heldRoot = rootLease.Handle;
            var before = await InspectAsync(
                    journal,
                    disposition,
                    WatchedFolderRoutingInspectionPoint.BeforeMove,
                    cancellationToken)
                .ConfigureAwait(false);
            if (before.Source == WatchedPathState.Missing &&
                before.Destination == WatchedPathState.Exact)
            {
                return RoutingFileAttemptResult.Confirmed(
                    WatchedFolderLibraryLayout.LibraryReference(disposition.DispositionId));
            }
            if (before.Source != WatchedPathState.Exact ||
                before.Destination != WatchedPathState.Missing)
            {
                return RoutingFileAttemptResult.Unknown("watched-file-state-ambiguous");
            }

            var destination = WatchedFolderLibraryLayout.GetDestinationPath(
                _clipsRoot, journal, disposition, createDirectories: true);
            var source = WatchedFolderRoutingEvidence.SourcePath(_clipsRoot, journal);
            try
            {
                moveAttempted = true;
                File.Move(source, destination, overwrite: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var afterFailure = await InspectAsync(
                        journal,
                        disposition,
                        WatchedFolderRoutingInspectionPoint.AfterMoveAttempt,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return afterFailure.Source == WatchedPathState.Missing &&
                       afterFailure.Destination == WatchedPathState.Exact
                    ? RoutingFileAttemptResult.Confirmed(
                        WatchedFolderLibraryLayout.LibraryReference(disposition.DispositionId))
                    : RoutingFileAttemptResult.Unknown("watched-file-move-unknown");
            }

            var after = await InspectAsync(
                    journal,
                    disposition,
                    WatchedFolderRoutingInspectionPoint.AfterMoveAttempt,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return after.Source == WatchedPathState.Missing &&
                   after.Destination == WatchedPathState.Exact
                ? RoutingFileAttemptResult.Confirmed(
                    WatchedFolderLibraryLayout.LibraryReference(disposition.DispositionId))
                : RoutingFileAttemptResult.Unknown("watched-file-move-unknown");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return moveAttempted
                ? RoutingFileAttemptResult.Unknown("watched-file-move-unknown")
                : RoutingFileAttemptResult.Failed("watched-file-evidence-invalid");
        }
    }

    public async Task<RoutingFileRecoveryResult> ReconcileAsync(
        PlannedFileDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        try
        {
            var journal = WatchedFolderRoutingEvidence.LoadPrepared(
                _journals, disposition.SourceClipId, cancellationToken);
            WatchedFolderRoutingEvidence.RequireFrozenDisposition(journal, disposition);
            var rootLease = WatchedFolderRoutingEvidence.OpenValidatedRoot(_clipsRoot, journal);
            using var heldRoot = rootLease.Handle;
            var state = await InspectAsync(
                    journal,
                    disposition,
                    WatchedFolderRoutingInspectionPoint.Recovery,
                    cancellationToken)
                .ConfigureAwait(false);
            if (state.Source == WatchedPathState.Exact &&
                state.Destination == WatchedPathState.Missing)
            {
                return RoutingFileRecoveryResult.RetrySafe;
            }
            if (state.Source == WatchedPathState.Missing &&
                state.Destination == WatchedPathState.Exact)
            {
                return RoutingFileRecoveryResult.Completed(
                    WatchedFolderLibraryLayout.LibraryReference(disposition.DispositionId));
            }
            return RoutingFileRecoveryResult.Unresolved;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return RoutingFileRecoveryResult.Unresolved;
        }
    }

    private async Task<WatchedInspection> InspectAsync(
        RoutingWatchedSourceJournalDocument journal,
        PlannedFileDisposition disposition,
        WatchedFolderRoutingInspectionPoint point,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _beforeInspection?.Invoke(point);
        WatchedFolderRoutingEvidence.RequireRoot(_clipsRoot, journal);
        var source = await WatchedFolderRoutingEvidence.InspectSourceAsync(
                _clipsRoot, journal, cancellationToken)
            .ConfigureAwait(false);
        var destinationPath = WatchedFolderLibraryLayout.GetDestinationPath(
            _clipsRoot, journal, disposition, createDirectories: false);
        var destination = await WatchedFolderRoutingEvidence.InspectContentAsync(
                _clipsRoot, destinationPath, journal.ContentSha256, cancellationToken)
            .ConfigureAwait(false);
        return new WatchedInspection(source, destination);
    }

    private sealed record WatchedInspection(WatchedPathState Source, WatchedPathState Destination);
}

internal sealed class RoutingArtifactResolverMux : IRoutingArtifactResolver
{
    private readonly IRoutingArtifactResolver _capture;
    private readonly IRoutingArtifactResolver _watched;

    internal RoutingArtifactResolverMux(
        IRoutingArtifactResolver capture,
        IRoutingArtifactResolver watched)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _watched = watched ?? throw new ArgumentNullException(nameof(watched));
    }

    public Task<RoutingResolvedArtifact> ResolveAsync(
        PlannedDelivery delivery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return Select(delivery.SourceClipId).ResolveAsync(delivery, cancellationToken);
    }

    private IRoutingArtifactResolver Select(string sourceClipId) =>
        RoutingWatchedJournalModel.IsWatchedSourceClipId(sourceClipId) ? _watched :
        CaptureJournalModel.IsClipId(sourceClipId) ? _capture :
        throw new InvalidDataException("No artifact resolver owns this source identity.");
}

internal sealed class RoutingLibraryFilerMux : IRoutingLibraryFiler
{
    private readonly IRoutingLibraryFiler _capture;
    private readonly IRoutingLibraryFiler _watched;

    internal RoutingLibraryFilerMux(IRoutingLibraryFiler capture, IRoutingLibraryFiler watched)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _watched = watched ?? throw new ArgumentNullException(nameof(watched));
    }

    public Task<RoutingFileAttemptResult> FileAsync(
        PlannedFileDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        return Select(disposition.SourceClipId).FileAsync(disposition, cancellationToken);
    }

    public Task<RoutingFileRecoveryResult> ReconcileAsync(
        PlannedFileDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        return Select(disposition.SourceClipId).ReconcileAsync(disposition, cancellationToken);
    }

    private IRoutingLibraryFiler Select(string sourceClipId) =>
        RoutingWatchedJournalModel.IsWatchedSourceClipId(sourceClipId) ? _watched :
        CaptureJournalModel.IsClipId(sourceClipId) ? _capture :
        throw new InvalidDataException("No library filer owns this source identity.");
}

internal static class WatchedFolderLibraryLayout
{
    private const int MaximumLeafLength = 240;

    internal static string GetDestinationPath(
        string clipsRoot,
        RoutingWatchedSourceJournalDocument journal,
        PlannedFileDisposition disposition,
        bool createDirectories)
    {
        var root = Path.GetFullPath(clipsRoot);
        var areaName = disposition.LibraryArea switch
        {
            RoutingLibraryArea.LocalOnly => "local-only",
            RoutingLibraryArea.Uploaded => "uploaded",
            _ => throw new InvalidDataException("The watched library area is unsupported.")
        };
        var area = ResolveChild(root, areaName, createDirectories);
        var game = ResolveChild(area, journal.GameName, createDirectories);
        return Path.Combine(game, CreateLeaf(journal.DisplayFileName, disposition.DispositionId));
    }

    internal static string LibraryReference(Guid dispositionId) =>
        "watched-file:" + dispositionId.ToString("N");

    private static string CreateLeaf(string displayFileName, Guid dispositionId)
    {
        var suffix = "__cc-" + dispositionId.ToString("N") + ".mp4";
        var stem = Path.GetFileNameWithoutExtension(displayFileName)
            .Normalize(NormalizationForm.FormC)
            .TrimEnd(' ', '.');
        if (stem.Length > MaximumLeafLength - suffix.Length)
        {
            stem = stem[..(MaximumLeafLength - suffix.Length)].TrimEnd(' ', '.');
        }
        if (stem.Length == 0) stem = "clip";
        return stem + suffix;
    }

    private static string ResolveChild(string parent, string name, bool create)
    {
        var matches = Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray()
            : [];
        if (matches.Length > 1)
        {
            throw new InvalidDataException("The watched library contains ambiguous folder names.");
        }
        var path = matches.Length == 1 ? matches[0] : Path.Combine(parent, name);
        if (!Directory.Exists(path))
        {
            if (!create) return path;
            Directory.CreateDirectory(path);
        }
        if (RoutingWatchedFileSystem.IsReparsePoint(path))
        {
            throw new InvalidDataException("The watched library uses a redirected folder.");
        }
        return Path.GetFullPath(path);
    }
}

internal enum WatchedPathState { Missing, Exact, Conflict }

internal static class WatchedFolderRoutingEvidence
{
    internal static RoutingWatchedSourceJournalDocument LoadPrepared(
        RoutingWatchedSourceJournalStore journals,
        string sourceClipId,
        CancellationToken cancellationToken)
    {
        var loaded = journals.Load(sourceClipId, cancellationToken);
        if (!loaded.LoadedFromDisk || loaded.Document is not { } document ||
            document.AdmissionKind != RoutingWatchedJournalAdmissionKind.PreparedPlan ||
            document.FrozenPlan is null)
        {
            throw new InvalidDataException("A prepared watched source journal is unavailable.");
        }
        return document;
    }

    internal static void RequireFrozenDelivery(
        RoutingWatchedSourceJournalDocument journal,
        PlannedDelivery delivery)
    {
        var frozen = journal.FrozenPlan!.Deliveries.SingleOrDefault(candidate =>
            candidate.DeliveryId == delivery.DeliveryId);
        if (frozen is null || frozen.PlanId != delivery.PlanId ||
            frozen.SourceClipId != delivery.SourceClipId || frozen.Route != delivery.Route ||
            frozen.Destination != delivery.Destination || frozen.ConnectionId != delivery.ConnectionId ||
            frozen.RequestedOutput != delivery.RequestedOutput || frozen.Output != delivery.Output ||
            frozen.OriginalOutput != delivery.OriginalOutput ||
            frozen.OnMissingOutput != delivery.OnMissingOutput ||
            frozen.ArtifactOutcome != delivery.ArtifactOutcome ||
            frozen.ArtifactErrorCode != delivery.ArtifactErrorCode ||
            frozen.Mode != delivery.Mode || frozen.Settings != delivery.Settings ||
            frozen.IntentionalDuplicate != delivery.IntentionalDuplicate)
        {
            throw new InvalidDataException("The watched delivery differs from its frozen plan.");
        }
    }

    internal static void RequireFrozenDisposition(
        RoutingWatchedSourceJournalDocument journal,
        PlannedFileDisposition disposition)
    {
        var frozen = journal.FrozenPlan!.FileDisposition;
        if (frozen is null || frozen.DispositionId != disposition.DispositionId ||
            frozen.PlanId != disposition.PlanId || frozen.SourceClipId != disposition.SourceClipId ||
            frozen.SourceContentSha256 != disposition.SourceContentSha256 ||
            disposition.SourceContentSha256 != journal.ContentSha256 ||
            frozen.Route != disposition.Route || frozen.LibraryArea != disposition.LibraryArea ||
            !frozen.PrerequisiteDeliveryIds.SequenceEqual(disposition.PrerequisiteDeliveryIds))
        {
            throw new InvalidDataException("The watched file disposition differs from its frozen plan.");
        }
    }

    internal static string SourcePath(
        string clipsRoot,
        RoutingWatchedSourceJournalDocument journal) =>
        Path.GetFullPath(Path.Combine(
            clipsRoot,
            journal.SourceRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    internal static async Task<RoutingWatchedSourceFile> RevalidateSourceAsync(
        string clipsRoot,
        RoutingWatchedSourceJournalDocument journal,
        CancellationToken cancellationToken)
    {
        RequireRoot(clipsRoot, journal);
        var prior = new RoutingWatchedSourceFile(
            journal.CaptureSource,
            Path.GetFullPath(clipsRoot),
            journal.SourceRelativePath,
            journal.GameName,
            journal.DisplayFileName,
            journal.SourceRootIdentitySha256,
            journal.FileIdentity,
            journal.ContentSha256);
        return await RoutingWatchedSourceAdapters.Get(journal.CaptureSource)
            .RevalidateAsync(prior, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<WatchedPathState> InspectSourceAsync(
        string clipsRoot,
        RoutingWatchedSourceJournalDocument journal,
        CancellationToken cancellationToken)
    {
        var sourcePath = SourcePath(clipsRoot, journal);
        var parent = Path.GetDirectoryName(sourcePath) ??
                     throw new InvalidDataException("The watched source parent is missing.");
        RoutingWatchedFileSystem.EnsureOrdinaryExistingPath(
            clipsRoot, parent, requireDirectory: true, "watched source parent");
        if (!ExistsOrdinaryFile(sourcePath)) return WatchedPathState.Missing;
        try
        {
            await RevalidateSourceAsync(clipsRoot, journal, cancellationToken).ConfigureAwait(false);
            return WatchedPathState.Exact;
        }
        catch (FileNotFoundException) { return WatchedPathState.Missing; }
        catch (DirectoryNotFoundException) { return WatchedPathState.Missing; }
        catch (InvalidDataException) { return WatchedPathState.Conflict; }
    }

    internal static async Task<WatchedPathState> InspectContentAsync(
        string clipsRoot,
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (!ExistsOrdinaryFile(path)) return WatchedPathState.Missing;
        var opened = RoutingWatchedFileSystem.OpenOrdinaryFile(path, clipsRoot);
        await using var stream = opened.Stream;
        var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        return hash.Equals(expectedHash, StringComparison.Ordinal)
            ? WatchedPathState.Exact
            : WatchedPathState.Conflict;
    }

    internal static void RequireRoot(
        string clipsRoot,
        RoutingWatchedSourceJournalDocument journal)
    {
        var root = OpenValidatedRoot(clipsRoot, journal);
        root.Handle.Dispose();
    }

    internal static RoutingWatchedRootHandle OpenValidatedRoot(
        string clipsRoot,
        RoutingWatchedSourceJournalDocument journal)
    {
        var root = RoutingWatchedFileSystem.OpenOrdinaryRoot(clipsRoot);
        try
        {
            var material = string.Create(
                CultureInfo.InvariantCulture,
                $"clipcord-watched-root-v1\n{journal.CaptureSource}\n" +
                $"{root.CanonicalPath.Normalize(NormalizationForm.FormC).ToUpperInvariant()}\n" +
                $"{root.Identity.VolumeSerialNumber:x8}\n{root.Identity.FileIdHex}");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
                .ToLowerInvariant();
            if (!identity.Equals(journal.SourceRootIdentitySha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The watched source root identity changed.");
            }
            return root;
        }
        catch
        {
            root.Handle.Dispose();
            throw;
        }
    }

    private static bool ExistsOrdinaryFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(
                    "A watched file path has an unexpected or redirected filesystem type.");
            }
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
