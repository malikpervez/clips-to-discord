namespace ClipsToDiscord;

/// <summary>
/// Narrow recovery transaction for a blocked Capture settings document. The candidate directory
/// is opened read-only and must match both privacy-safe authority fingerprints before any settings
/// write occurs. Capture remains stopped after success; a fresh process performs normal bootstrap.
/// </summary>
internal static class RoutingCaptureLibraryRepair
{
    internal static CaptureSettings RestoreExactRoot(
        CaptureSettings currentSettings,
        string candidateRoot,
        RoutingCaptureLibraryBinding expectedBinding,
        Action<CaptureSettings> persist,
        Func<string, RoutingCaptureLibraryBinding> verifyPersistedRoot,
        Action? rollbackPersistedSettings = null)
    {
        ArgumentNullException.ThrowIfNull(currentSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRoot);
        ArgumentNullException.ThrowIfNull(expectedBinding);
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(verifyPersistedRoot);

        var canonicalCandidate = CaptureJournalStore.NormalizeLibraryRoot(candidateRoot);
        var candidateBinding = RoutingCaptureLibraryBindingModel.Create(canonicalCandidate);
        RoutingCaptureLibraryBindingModel.RequireExact(expectedBinding, candidateBinding);

        var repaired = CaptureSettings.Normalize(currentSettings with
        {
            LibraryRoot = canonicalCandidate
        });
        var persisted = false;
        try
        {
            persist(repaired);
            persisted = true;
            var verified = verifyPersistedRoot(canonicalCandidate);
            RoutingCaptureLibraryBindingModel.RequireExact(expectedBinding, verified);
            return repaired;
        }
        catch (Exception repairException)
        {
            if (persisted && rollbackPersistedSettings is not null)
            {
                try
                {
                    rollbackPersistedSettings();
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Capture-library repair failed and its settings rollback also failed.",
                        repairException,
                        rollbackException);
                }
            }
            throw;
        }
    }
}
