namespace ClipsToDiscord;

internal enum RoutingCaptureLibraryPermitState
{
    Allowed,
    Mismatch,
    Unavailable
}

internal sealed record RoutingCaptureLibraryPermitInspection(
    RoutingCaptureLibraryPermitState State,
    RoutingCaptureLibraryBinding ExpectedBinding,
    RoutingCaptureLibraryBinding? CurrentBinding,
    Exception? Error = null)
{
    internal bool Allowed => State == RoutingCaptureLibraryPermitState.Allowed;
}

internal sealed class RoutingCaptureLibraryPermitException(
    string message,
    RoutingCaptureLibraryPermitInspection inspection) : InvalidOperationException(
        message,
        inspection.Error)
{
    internal RoutingCaptureLibraryPermitInspection Inspection { get; } = inspection;
}

/// <summary>
/// One immutable expected Capture-library identity plus a strict live probe. Every consumer uses
/// the same instance so loss of the durable settings evidence, a path redirect, or replacement of
/// the Windows directory object revokes Capture reads and Routing mutations together. Inspection
/// is status-bearing and fail-closed; it never exposes the Capture path.
/// </summary>
internal sealed class RoutingCaptureLibraryPermit
{
    private readonly RoutingCaptureLibraryBinding _expectedBinding;
    private readonly Func<RoutingCaptureLibraryBinding> _currentBinding;
    private RoutingCaptureLibraryPermitInspection? _revocation;

    internal RoutingCaptureLibraryPermit(
        RoutingCaptureLibraryBinding expectedBinding,
        Func<RoutingCaptureLibraryBinding> currentBinding)
    {
        RoutingCaptureLibraryBindingModel.Validate(expectedBinding);
        _expectedBinding = expectedBinding;
        _currentBinding = currentBinding ?? throw new ArgumentNullException(nameof(currentBinding));
    }

    internal RoutingCaptureLibraryBinding ExpectedBinding => _expectedBinding;

    internal RoutingCaptureLibraryPermitInspection Inspect()
    {
        var revoked = Volatile.Read(ref _revocation);
        if (revoked is not null) return revoked;

        RoutingCaptureLibraryPermitInspection inspection;
        try
        {
            var current = _currentBinding() ?? throw new InvalidDataException(
                "The current Capture library identity is missing.");
            RoutingCaptureLibraryBindingModel.Validate(current);
            inspection = new RoutingCaptureLibraryPermitInspection(
                current == _expectedBinding
                    ? RoutingCaptureLibraryPermitState.Allowed
                    : RoutingCaptureLibraryPermitState.Mismatch,
                _expectedBinding,
                current);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or PathTooLongException or
                System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            inspection = new RoutingCaptureLibraryPermitInspection(
                RoutingCaptureLibraryPermitState.Unavailable,
                _expectedBinding,
                CurrentBinding: null,
                exception);
        }

        if (!inspection.Allowed)
        {
            return Interlocked.CompareExchange(
                       ref _revocation,
                       inspection,
                       comparand: null) ?? inspection;
        }
        return Volatile.Read(ref _revocation) ?? inspection;
    }

    internal RoutingCaptureLibraryPermitInspection RequireCurrent(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var inspection = Inspect();
        if (inspection.Allowed) return inspection;
        throw new RoutingCaptureLibraryPermitException(
            $"The Capture library permit was revoked before {operation} " +
            $"({inspection.State}).",
            inspection);
    }
}
