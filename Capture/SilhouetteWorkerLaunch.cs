using System.Diagnostics;

namespace ClipsToDiscord;

internal sealed record SilhouetteWorkerLaunchOptions(
    string LibraryRoot,
    string ProjectId,
    bool ForceCpu)
{
    internal const string WorkerArgument = "--silhouette-worker";
    internal const string LibraryRootArgument = "--library-root";
    internal const string ProjectIdArgument = "--project-id";
    internal const string CpuArgument = "--cpu";

    internal static bool TryParse(
        string[] args,
        out SilhouetteWorkerLaunchOptions? options)
    {
        options = null;
        if (args.Length is not (5 or 6) ||
            !args[0].Equals(WorkerArgument, StringComparison.Ordinal))
        {
            return false;
        }

        string? libraryRoot = null;
        string? projectId = null;
        var forceCpu = false;
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index].Equals(CpuArgument, StringComparison.Ordinal))
            {
                if (forceCpu)
                {
                    return false;
                }
                forceCpu = true;
                continue;
            }

            if (index + 1 >= args.Length)
            {
                return false;
            }
            var value = args[++index];
            if (args[index - 1].Equals(LibraryRootArgument, StringComparison.Ordinal))
            {
                if (libraryRoot is not null)
                {
                    return false;
                }
                libraryRoot = value;
            }
            else if (args[index - 1].Equals(ProjectIdArgument, StringComparison.Ordinal))
            {
                if (projectId is not null)
                {
                    return false;
                }
                projectId = value;
            }
            else
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(libraryRoot) ||
            !Path.IsPathFullyQualified(libraryRoot) ||
            !IsProjectId(projectId))
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
            projectId!,
            forceCpu);
        return true;
    }

    internal static bool IsProjectId(string? projectId) =>
        projectId is { Length: 32 } && projectId.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal static class SilhouetteWorkerLaunch
{
    internal static ProcessStartInfo CreateStartInfo(
        string libraryRoot,
        string projectId,
        bool forceCpu = false,
        string? executablePath = null)
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
        info.ArgumentList.Add(Path.GetFullPath(libraryRoot.Trim()));
        info.ArgumentList.Add(SilhouetteWorkerLaunchOptions.ProjectIdArgument);
        info.ArgumentList.Add(projectId);
        if (forceCpu)
        {
            info.ArgumentList.Add(SilhouetteWorkerLaunchOptions.CpuArgument);
        }
        return info;
    }
}
