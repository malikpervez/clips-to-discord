namespace ClipsToDiscord;

internal enum LegacyRoutingMigrationAdmission
{
    Deferred,
    FreshOrInvalidProfile,
    ValidLegacyUpgrade
}

internal enum LegacyRoutingSettingsPresence
{
    Missing,
    Present,
    Unavailable
}

internal sealed record LegacyRoutingMigrationAdmissionDocument(
    int SchemaVersion,
    long Generation,
    LegacyRoutingMigrationAdmission Admission,
    int ImportedRouteLabelVersion,
    DateTimeOffset CreatedUtc);

internal sealed record LegacyRoutingMigrationAdmissionInspection(
    RoutingDocumentLoadStatus Status,
    LegacyRoutingMigrationAdmissionDocument? Document)
{
    internal bool Loaded => Status == RoutingDocumentLoadStatus.Loaded && Document is not null;
    internal bool AllowsLegacyImport =>
        Loaded && Document!.Admission == LegacyRoutingMigrationAdmission.ValidLegacyUpgrade;
    internal bool LegacyRuntimeAllowed => AllowsLegacyImport;
    internal LegacyRoutingMigrationAdmission EffectiveAdmission => Loaded
        ? Document!.Admission
        : LegacyRoutingMigrationAdmission.Deferred;
    internal int ImportedRouteLabelVersion => Loaded
        ? Document!.ImportedRouteLabelVersion
        : LegacyRoutingMigrationPlanner.CurrentImportedRouteLabelVersion;
}

/// <summary>
/// Records a terminal answer when 2.0 can prove either a fresh profile or a compatible 1.x
/// settings/state pair. Terminal decisions are distribution-neutral and immutable, so later 2.0
/// setup writes can never turn a clearly fresh profile into an apparent upgrade. Ambiguous or
/// transient evidence remains unrecorded and fail-closed until a later launch can prove an answer.
/// </summary>
internal sealed class LegacyRoutingMigrationAdmissionStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = 4 * 1024;
    internal const string FileName = ".legacy-routing-admission.json";
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(5);

    private readonly RoutingAtomicJsonStore<LegacyRoutingMigrationAdmissionDocument> _store;

    internal LegacyRoutingMigrationAdmissionStore()
        : this(System.IO.Path.Combine(SettingsStore.DataDirectory, "routing", FileName))
    {
    }

    internal LegacyRoutingMigrationAdmissionStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.GetFileName(canonical).Equals(FileName, StringComparison.Ordinal))
            throw new ArgumentException(
                $"The migration admission record must be named {FileName}.",
                nameof(path));
        _store = new RoutingAtomicJsonStore<LegacyRoutingMigrationAdmissionDocument>(
            canonical,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Generation,
            Validate,
            ValidateSuccessor,
            ValidateInitial);
    }

    internal string Path => _store.Path;

    internal LegacyRoutingMigrationAdmissionInspection Inspect(
        CancellationToken cancellationToken = default)
    {
        var loaded = _store.Load(cancellationToken);
        return new LegacyRoutingMigrationAdmissionInspection(loaded.Status, loaded.Document);
    }

    internal LegacyRoutingMigrationAdmissionInspection Resolve(
        AppSettings settings,
        WatchStateRoutingProbe legacyState,
        LegacyRoutingSettingsPresence settingsPresence,
        bool preserveExistingMigrationLabel = false,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(legacyState);
        var existing = Inspect(cancellationToken);
        if (existing.Status != RoutingDocumentLoadStatus.Missing) return existing;

        var admission = Classify(settings, legacyState, settingsPresence);
        if (admission == LegacyRoutingMigrationAdmission.Deferred)
        {
            // Ambiguous or transient evidence remains denied for this launch, but is deliberately
            // not persisted. A later launch can retry once a locked file is released, damaged
            // evidence is repaired, or a watched drive mounts. Deferred never authorizes a worker.
            return existing;
        }

        var timestamp = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        var candidate = new LegacyRoutingMigrationAdmissionDocument(
            CurrentSchemaVersion,
            Generation: 1,
            admission,
            preserveExistingMigrationLabel
                ? LegacyRoutingMigrationPlanner.LegacyImportedRouteLabelVersion
                : LegacyRoutingMigrationPlanner.CurrentImportedRouteLabelVersion,
            timestamp);
        using var saveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        saveTimeout.CancelAfter(SaveTimeout);
        try
        {
            _ = _store.SaveAsync(candidate, expectedGeneration: 0, saveTimeout.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (RoutingConcurrencyException)
        {
            // Another process or recovery path won the create-only decision. Its validated
            // document is authoritative; never overwrite it with a later view of the profile.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LegacyRoutingMigrationAdmissionInspection(
                RoutingDocumentLoadStatus.Unavailable,
                Document: null);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Admission is a startup safety gate. Any unexpected persistence failure must deny
            // migration without taking down the tray process or manufacturing a durable answer.
            return new LegacyRoutingMigrationAdmissionInspection(
                RoutingDocumentLoadStatus.Unavailable,
                Document: null);
        }
        return Inspect(cancellationToken);
    }

    internal static LegacyRoutingMigrationAdmission Classify(
        AppSettings settings,
        WatchStateRoutingProbe legacyState,
        LegacyRoutingSettingsPresence settingsPresence)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(legacyState);
        if (settingsPresence == LegacyRoutingSettingsPresence.Present &&
            HasPositiveLegacyEvidence(settings, legacyState))
            return LegacyRoutingMigrationAdmission.ValidLegacyUpgrade;
        if (settingsPresence == LegacyRoutingSettingsPresence.Unavailable ||
            legacyState.Status is WatchStateRoutingProbeStatus.Corrupt or
                WatchStateRoutingProbeStatus.UnsupportedVersion or
                WatchStateRoutingProbeStatus.Unavailable)
        {
            return LegacyRoutingMigrationAdmission.Deferred;
        }
        return LegacyRoutingMigrationAdmission.FreshOrInvalidProfile;
    }

    internal static bool HasPositiveLegacyEvidence(
        AppSettings settings,
        WatchStateRoutingProbe legacyState)
    {
        if (settings is null || !HasValidLegacySettingsShape(settings) ||
            legacyState.State is null)
        {
            return false;
        }

        try
        {
            var releasedVersion = legacyState.Loaded &&
                                  legacyState.State.Version is >=
                                      WatchStateStore.MinimumCompatibleVersion and
                                      <= WatchStateStore.CurrentVersion;
            var releasedUnversioned = legacyState.Status ==
                                      WatchStateRoutingProbeStatus.ReleasedUnversioned &&
                                      legacyState.State.Version == 0;
            return (releasedVersion || releasedUnversioned) &&
                   System.IO.Path.TrimEndingDirectorySeparator(
                       System.IO.Path.GetFullPath(settings.ClipsFolder))
                       .Equals(
                           System.IO.Path.TrimEndingDirectorySeparator(
                               System.IO.Path.GetFullPath(legacyState.State.ClipsFolder)),
                           StringComparison.OrdinalIgnoreCase) &&
                   AppSettings.NormalizeCaptureSource(settings.CaptureSource) ==
                   legacyState.State.CaptureSource;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool HasValidLegacySettingsShape(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ClipsFolder) ||
            !System.IO.Path.IsPathFullyQualified(settings.ClipsFolder) ||
            !Enum.IsDefined(settings.CaptureSource) ||
            settings.CompressionTargetMb is < 1 or > 100 ||
            (settings.UploadToDiscord &&
             (!WebhookValidation.IsDiscordWebhook(settings.WebhookUrl) ||
              string.IsNullOrWhiteSpace(settings.UploaderName) ||
              settings.UploaderName.Length > AppSettings.MaximumUploaderNameLength)))
        {
            return false;
        }
        return true;
    }

    private static void Validate(LegacyRoutingMigrationAdmissionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(
            document.SchemaVersion == CurrentSchemaVersion && document.Generation == 1,
            "The legacy migration admission schema or generation is invalid.");
        RoutingValidation.Require(document.Admission is
                LegacyRoutingMigrationAdmission.FreshOrInvalidProfile or
                LegacyRoutingMigrationAdmission.ValidLegacyUpgrade,
            "The legacy migration admission decision is invalid.");
        RoutingValidation.Require(document.ImportedRouteLabelVersion is
                LegacyRoutingMigrationPlanner.LegacyImportedRouteLabelVersion or
                LegacyRoutingMigrationPlanner.CurrentImportedRouteLabelVersion,
            "The imported route label version is invalid.");
        RoutingValidation.RequireUtc(document.CreatedUtc,
            "legacy migration admission timestamp");
    }

    private static void ValidateInitial(LegacyRoutingMigrationAdmissionDocument document) =>
        Validate(document);

    private static void ValidateSuccessor(
        LegacyRoutingMigrationAdmissionDocument current,
        LegacyRoutingMigrationAdmissionDocument candidate)
    {
        Validate(current);
        Validate(candidate);
        RoutingValidation.Require(candidate == current,
            "The legacy migration admission decision is immutable.");
    }
}
