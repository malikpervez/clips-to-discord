using System.Diagnostics;
using System.Security.Cryptography;

namespace ClipsToDiscord;

internal sealed record UpdateLaunchRequest(
    StableVersion Version,
    string InstallerPath,
    string InstallerSha256);

internal static class UpdateInstallerLauncher
{
    public static void Launch(UpdateLaunchRequest request)
    {
        Launch(request, UpdateDownloadService.DefaultDownloadRoot, Process.Start);
    }

    internal static void Launch(
        UpdateLaunchRequest request,
        string allowedRoot,
        Func<ProcessStartInfo, Process?> startProcess)
    {
        var installerPath = ValidateInstallerPath(request.InstallerPath, allowedRoot, request.Version);
        using var stream = OpenInstallerForVerification(installerPath);
        VerifyInstallerHash(request, stream);
        if (startProcess(CreateStartInfo(request, allowedRoot)) is null)
        {
            throw new InvalidOperationException("Windows did not start the verified update installer.");
        }
    }

    internal static ProcessStartInfo CreateStartInfo(UpdateLaunchRequest request, string allowedRoot)
    {
        var installerPath = ValidateInstallerPath(request.InstallerPath, allowedRoot, request.Version);
        var startInfo = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!
        };
        startInfo.ArgumentList.Add("/SILENT");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");
        startInfo.ArgumentList.Add("/CLIPCORDRESTART=1");
        return startInfo;
    }

    internal static void VerifyInstaller(UpdateLaunchRequest request, string allowedRoot)
    {
        var installerPath = ValidateInstallerPath(request.InstallerPath, allowedRoot, request.Version);
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("The verified update installer was not found.", installerPath);
        }

        using var stream = OpenInstallerForVerification(installerPath);
        VerifyInstallerHash(request, stream);
    }

    private static FileStream OpenInstallerForVerification(string installerPath) => new(
        installerPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 128 * 1024,
        FileOptions.SequentialScan);

    private static void VerifyInstallerHash(UpdateLaunchRequest request, Stream stream)
    {
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(request.InstallerSha256);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The update installer digest was invalid.", exception);
        }
        if (expectedHash.Length != 32)
        {
            throw new InvalidDataException("The update installer digest was invalid.");
        }

        var actualHash = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidDataException("The staged update installer no longer matches its verified digest.");
        }
    }

    private static string ValidateInstallerPath(string path, string allowedRoot, StableVersion version)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar);
        var expectedPath = Path.Combine(
            root,
            "v" + version,
            GitHubUpdateChecker.InstallerFileName);
        if (!fullPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update installer path was outside ClipCord's update directory.");
        }

        EnsureDirectoryIsNotRedirected(root);
        EnsureDirectoryIsNotRedirected(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath) &&
            (new FileInfo(fullPath).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The update installer cannot be a symbolic link.");
        }
        return fullPath;
    }

    private static void EnsureDirectoryIsNotRedirected(string path)
    {
        if (Directory.Exists(path) &&
            (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The update directory cannot be a symbolic link or junction.");
        }
    }
}
