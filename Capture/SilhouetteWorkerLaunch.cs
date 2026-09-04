using System.Diagnostics;
using System.Globalization;

namespace ClipsToDiscord;

internal sealed record SilhouetteWorkerLaunchOptions(
    string LibraryRoot,
    string ProjectId,
    bool ForceCpu,
    int ParentProcessId,
    ulong ParentCreationTimeFileTime,
    RoutingCaptureLibraryBinding? ExpectedLibraryBinding)
{
    // Retained only so existing internal callers fail closed at RunAsync rather than failing to
    // compile. Production launches always use TryParse/CreateStartInfo and therefore carry both
    // pieces of durable authority evidence.
    internal SilhouetteWorkerLaunchOptions(
        string LibraryRoot,
        string ProjectId,
        bool ForceCpu,
        int ParentProcessId)
        : this(
            LibraryRoot,
            ProjectId,
            ForceCpu,
            ParentProcessId,
            ParentCreationTimeFileTime: 0,
            ExpectedLibraryBinding: null)
    {
    }

    internal const string WorkerArgument = "--silhouette-worker";
    internal const string LibraryRootArgument = "--library-root";
    internal const string ProjectIdArgument = "--project-id";
    internal const string ParentArgument = "--parent-pid";
    internal const string ParentCreationArgument = "--parent-created-filetime";
    internal const string LibraryPathFingerprintArgument = "--library-path-fingerprint";
    internal const string LibraryIdentityFingerprintArgument =
        "--library-identity-fingerprint";
    internal const string CpuArgument = "--cpu";

    internal static bool TryParse(
        string[] args,
        out SilhouetteWorkerLaunchOptions? options)
    {
        options = null;
        if (args.Length is not (13 or 14) ||
            !args[0].Equals(WorkerArgument, StringComparison.Ordinal) ||
            !args[1].Equals(LibraryRootArgument, StringComparison.Ordinal) ||
            !args[3].Equals(ProjectIdArgument, StringComparison.Ordinal) ||
            !args[5].Equals(ParentArgument, StringComparison.Ordinal) ||
            !args[7].Equals(ParentCreationArgument, StringComparison.Ordinal) ||
            !args[9].Equals(LibraryPathFingerprintArgument, StringComparison.Ordinal) ||
            !args[11].Equals(LibraryIdentityFingerprintArgument, StringComparison.Ordinal) ||
            (args.Length == 14 &&
             !args[13].Equals(CpuArgument, StringComparison.Ordinal)))
        {
            return false;
        }

        var libraryRoot = args[2];
        var projectId = args[4];
        if (string.IsNullOrWhiteSpace(libraryRoot) ||
            !Path.IsPathFullyQualified(libraryRoot) ||
            !IsProjectId(projectId) ||
            !TryParseCanonicalPositiveInt32(args[6], out var parentProcessId) ||
            parentProcessId == Environment.ProcessId ||
            !TryParseCanonicalPositiveUInt64(
                args[8],
                out var parentCreationTimeFileTime) ||
            !IsCanonicalSha256(args[10]) ||
            !IsCanonicalSha256(args[12]))
        {
            return false;
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(libraryRoot.Trim());
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        options = new SilhouetteWorkerLaunchOptions(
            normalizedRoot,
            projectId,
            ForceCpu: args.Length == 14,
            parentProcessId,
            parentCreationTimeFileTime,
            new RoutingCaptureLibraryBinding(args[10], args[12]));
        return true;
    }

    internal static bool IsProjectId(string? projectId) =>
        projectId is { Length: 32 } && projectId.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryParseCanonicalPositiveInt32(string value, out int parsed) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out parsed) &&
        parsed > 0 &&
        value.Equals(parsed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static bool TryParseCanonicalPositiveUInt64(string value, out ulong parsed) =>
        ulong.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out parsed) &&
        parsed > 0 &&
        value.Equals(parsed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static bool IsCanonicalSha256(string value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

internal static class SilhouetteWorkerLaunch
{
    internal static ProcessStartInfo CreateStartInfo(
        string libraryRoot,
        string projectId,
        bool forceCpu = false,
        string? executablePath = null,
        int? parentProcessId = null,
        ulong? parentCreationTimeFileTime = null,
        RoutingCaptureLibraryBinding? expectedLibraryBinding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        if (!Path.IsPathFullyQualified(libraryRoot))
        {
            throw new ArgumentException(
                "The silhouette library root must be an absolute path.",
                nameof(libraryRoot));
        }
        if (!SilhouetteWorkerLaunchOptions.IsProjectId(projectId))
        {
            throw new ArgumentException(
                "The silhouette project id is invalid.",
                nameof(projectId));
        }
        var validatedParentProcessId = parentProcessId ?? Environment.ProcessId;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(validatedParentProcessId);

        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(libraryRoot.Trim()));
        var validatedParentCreationTime = parentCreationTimeFileTime ??
            SilhouetteWorkerParentHandle.ReadCreationTimeFileTime(
                validatedParentProcessId);
        if (validatedParentCreationTime == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parentCreationTimeFileTime),
                "The silhouette parent creation identity must be positive.");
        }
        var validatedBinding = expectedLibraryBinding ??
                               RoutingCaptureLibraryBindingModel.Create(normalizedRoot);
        RoutingCaptureLibraryBindingModel.Validate(validatedBinding);

        var normalizedExecutable = Path.GetFullPath(
            executablePath ?? Environment.ProcessPath ??
            throw new InvalidOperationException(
                "ClipCord could not resolve its silhouette worker executable."));
        var info = new ProcessStartInfo(normalizedExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(normalizedExecutable) ??
                               AppContext.BaseDirectory
        };
        info.ArgumentList.Add(SilhouetteWorkerLaunchOptions.WorkerArgument);
        info.ArgumentList.Add(SilhouetteWorkerLaunchOptions.LibraryRootArgument);
        info.ArgumentList.Add(normalizedRoot);
        info.ArgumentList.Add(SilhouetteWorkerLaunchOptions.ProjectIdArgument);
        info.ArgumentList.Add(projectId);
        info.ArgumentList.Add(SilhouetteWorkerLaunchOptions.ParentArgument);
        info.ArgumentList.Add(validatedParentProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(SilhouetteWorkerLaunchOptions.ParentCreationArgument);
        info.ArgumentList.Add(
            validatedParentCreationTime.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(SilhouetteWorkerLaunchOptions.LibraryPathFingerprintArgument);
        info.ArgumentList.Add(validatedBinding.CanonicalPathFingerprint);
        info.ArgumentList.Add(SilhouetteWorkerLaunchOptions.LibraryIdentityFingerprintArgument);
        info.ArgumentList.Add(validatedBinding.NativeDirectoryIdentityFingerprint);
        if (forceCpu)
        {
            info.ArgumentList.Add(SilhouetteWorkerLaunchOptions.CpuArgument);
        }
        return info;
    }
}
