namespace ClipsToDiscord;

internal sealed record CaptureHostLaunchOptions(
    string PipeName,
    string UrgentPipeName,
    int ParentProcessId)
{
    internal const string HostArgument = "--capture-host";
    internal const string PipeArgument = "--pipe";
    internal const string UrgentPipeArgument = "--urgent-pipe";
    internal const string ParentArgument = "--parent-pid";
    internal const string PipePrefix = "clipcord-capture-";
    internal const string UrgentPipePrefix = "clipcord-capture-urgent-";

    internal static bool TryParse(string[] args, out CaptureHostLaunchOptions? options)
    {
        options = null;
        if (args.Length != 7 || !args[0].Equals(HostArgument, StringComparison.Ordinal)) return false;

        string? pipeName = null;
        string? urgentPipeName = null;
        var parentProcessId = 0;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) return false;
            if (args[index].Equals(PipeArgument, StringComparison.Ordinal))
            {
                pipeName = args[index + 1];
            }
            else if (args[index].Equals(UrgentPipeArgument, StringComparison.Ordinal))
            {
                urgentPipeName = args[index + 1];
            }
            else if (args[index].Equals(ParentArgument, StringComparison.Ordinal) &&
                     int.TryParse(args[index + 1], out var parsedParent))
            {
                parentProcessId = parsedParent;
            }
            else
            {
                return false;
            }
        }

        if (!IsValidPipeName(pipeName, PipePrefix) ||
            !IsValidPipeName(urgentPipeName, UrgentPipePrefix) ||
            pipeName!.Equals(urgentPipeName, StringComparison.Ordinal) ||
            parentProcessId <= 0 ||
            parentProcessId == Environment.ProcessId)
        {
            return false;
        }

        options = new CaptureHostLaunchOptions(pipeName, urgentPipeName!, parentProcessId);
        return true;
    }

    private static bool IsValidPipeName(string? pipeName, string requiredPrefix) =>
        !string.IsNullOrWhiteSpace(pipeName) &&
        pipeName.StartsWith(requiredPrefix, StringComparison.Ordinal) &&
        pipeName.Length <= 120 &&
        pipeName.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
