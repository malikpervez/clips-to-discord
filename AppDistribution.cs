using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipsToDiscord;

internal static class AppDistribution
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    public static bool IsPackaged { get; } = DetectPackageIdentity();

    public static bool UsesStoreUpdates => IsPackaged;

    internal static ProcessStartInfo CreateStoreUpdatesStartInfo() => new(
        "ms-windows-store://downloadsandupdates")
    {
        UseShellExecute = true
    };

    internal static bool HasPackageIdentityForResult(int result) => result switch
    {
        0 => true,
        ErrorInsufficientBuffer => true,
        AppModelErrorNoPackage => false,
        _ => throw new Win32Exception(result)
    };

    private static bool DetectPackageIdentity()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        if (result == AppModelErrorNoPackage) return false;
        if (result != ErrorInsufficientBuffer && result != 0)
        {
            throw new Win32Exception(result);
        }

        if (result == 0) return true;
        var packageFullName = new StringBuilder((int)length);
        result = GetCurrentPackageFullName(ref length, packageFullName);
        return HasPackageIdentityForResult(result);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        StringBuilder? packageFullName);
}
