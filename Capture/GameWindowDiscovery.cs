using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClipsToDiscord;

internal sealed record GameWindowSnapshot(
    nint WindowHandle,
    int ProcessId,
    string WindowTitle,
    string ProcessName,
    string DisplayName,
    int Width,
    int Height,
    bool IsVisible = true,
    bool IsMinimized = false,
    bool IsCloaked = false,
    bool IsRootWindow = true,
    bool IsToolWindow = false,
    bool IsKnownGame = false);

internal sealed record GameWindowCandidate(
    nint WindowHandle,
    int ProcessId,
    string DisplayName,
    int Width,
    int Height,
    DateTimeOffset ObservedAt);

internal interface IForegroundWindowSource
{
    GameWindowSnapshot? InspectForegroundWindow();
}

internal static class GameWindowCandidatePolicy
{
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost", "chatgpt", "chrome", "claude", "clipstodiscord", "clipcord",
        "cmd", "code", "codex", "devenv", "discord", "dwm", "explorer", "firefox",
        "lockapp", "msedge", "obs64", "powershell", "pwsh", "spotify", "steamwebhelper",
        "teams", "windowsterminal",
        "searchapp", "searchhost", "shellexperiencehost", "startmenuexperiencehost",
        "systemsettings", "taskmgr", "textinputhost", "widgets", "widgetservice"
    };
    private static readonly string[] ExcludedHelperMarkers =
    {
        "anticheat", "anti-cheat", "javelin anticheat", "javelin anti-cheat"
    };

    internal static GameWindowCandidate? Create(
        GameWindowSnapshot? snapshot,
        DateTimeOffset observedAt,
        params int[] excludedProcessIds)
    {
        if (snapshot is null || snapshot.WindowHandle == 0 || snapshot.ProcessId <= 0 ||
            !snapshot.IsVisible || snapshot.IsMinimized || snapshot.IsCloaked ||
            !snapshot.IsRootWindow || snapshot.IsToolWindow || !snapshot.IsKnownGame ||
            snapshot.Width < 640 || snapshot.Height < 360 ||
            string.IsNullOrWhiteSpace(snapshot.WindowTitle) ||
            excludedProcessIds.Contains(snapshot.ProcessId) ||
            ExcludedProcesses.Contains(snapshot.ProcessName) ||
            IsCaptureHelper(snapshot))
        {
            return null;
        }

        var displayName = NormalizeDisplayName(
            snapshot.DisplayName,
            snapshot.WindowTitle,
            snapshot.ProcessName);
        return string.IsNullOrWhiteSpace(displayName)
            ? null
            : new GameWindowCandidate(
                snapshot.WindowHandle,
                snapshot.ProcessId,
                displayName,
                snapshot.Width,
                snapshot.Height,
                observedAt);
    }

    private static bool IsCaptureHelper(GameWindowSnapshot snapshot) =>
        ExcludedHelperMarkers.Any(marker =>
            snapshot.ProcessName.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            snapshot.DisplayName.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            snapshot.WindowTitle.Contains(marker, StringComparison.OrdinalIgnoreCase));

    internal static string NormalizeDisplayName(
        string? displayName,
        string? windowTitle,
        string? processName)
    {
        var processStem = Path.GetFileNameWithoutExtension(processName ?? string.Empty).Trim();
        var value = displayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals(processStem, StringComparison.OrdinalIgnoreCase))
        {
            value = windowTitle?.Trim() ?? string.Empty;
        }
        foreach (var suffix in new[] { "-Win64-Shipping", "-Win32-Shipping", "-Shipping", ".exe" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^suffix.Length].Trim();
                break;
            }
        }

        var canonical = new StringBuilder(value.Length);
        var separatorPending = false;
        foreach (var character in value.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && canonical.Length > 0) canonical.Append(' ');
                canonical.Append(character);
                separatorPending = false;
            }
            else if (character is not ('™' or '®' or '©'))
            {
                separatorPending = canonical.Length > 0;
            }
        }
        var result = canonical.ToString();
        return result.Length > 80 ? result[..80].TrimEnd(' ') : result;
    }
}

internal sealed class ForegroundGameWindowMonitor
{
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(45);
    private const int RequiredConsecutiveObservations = 3;
    private readonly object _gate = new();
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<nint, int, bool> _isWindowOwnedByProcess;
    private nint _pendingHandle;
    private int _pendingCount;
    private GameWindowCandidate? _stable;

    internal ForegroundGameWindowMonitor(
        Func<int, bool>? isProcessAlive = null,
        Func<nint, int, bool>? isWindowOwnedByProcess = null)
    {
        _isProcessAlive = isProcessAlive ?? (_ => true);
        _isWindowOwnedByProcess = isWindowOwnedByProcess ?? ((_, _) => true);
    }

    internal GameWindowCandidate? Observe(GameWindowCandidate? candidate, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (candidate is not null)
            {
                if (_pendingHandle == candidate.WindowHandle)
                {
                    _pendingCount++;
                }
                else
                {
                    _pendingHandle = candidate.WindowHandle;
                    _pendingCount = 1;
                }

                if (_pendingCount >= RequiredConsecutiveObservations)
                {
                    _stable = candidate with { ObservedAt = now };
                }
            }
            else
            {
                _pendingHandle = 0;
                _pendingCount = 0;
            }

            if (_stable is not null &&
                (now - _stable.ObservedAt > Retention ||
                 !_isProcessAlive(_stable.ProcessId) ||
                 !_isWindowOwnedByProcess(_stable.WindowHandle, _stable.ProcessId)))
            {
                _stable = null;
            }
            return _stable;
        }
    }

    internal GameWindowCandidate? Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_stable is not null &&
                (now - _stable.ObservedAt > Retention ||
                 !_isProcessAlive(_stable.ProcessId) ||
                 !_isWindowOwnedByProcess(_stable.WindowHandle, _stable.ProcessId)))
            {
                _stable = null;
            }
            return _stable;
        }
    }

    internal void Invalidate(nint windowHandle, int processId)
    {
        lock (_gate)
        {
            if (_stable is not null &&
                (_stable.WindowHandle == windowHandle || _stable.ProcessId == processId))
            {
                _stable = null;
            }
            if (_pendingHandle == windowHandle)
            {
                _pendingHandle = 0;
                _pendingCount = 0;
            }
        }
    }
}

internal static class WindowsGameProcessLifetime
{
    internal static bool IsAlive(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

internal static class WindowsGameWindowLifetime
{
    internal static bool IsOwnedByProcess(nint windowHandle, int processId)
    {
        if (windowHandle == 0 || processId <= 0 || !IsWindow(windowHandle)) return false;
        _ = GetWindowThreadProcessId(windowHandle, out var ownerProcessId);
        return ownerProcessId == (uint)processId;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}

internal sealed class WindowsForegroundWindowSource : IForegroundWindowSource
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const uint GaRoot = 2;
    private const uint DwmwaCloaked = 14;

    public GameWindowSnapshot? InspectForegroundWindow()
    {
        var window = GetForegroundWindow();
        if (window == 0) return null;
        _ = GetWindowThreadProcessId(window, out var processIdValue);
        if (processIdValue == 0 || processIdValue > int.MaxValue) return null;
        var processId = (int)processIdValue;
        var title = ReadWindowTitle(window);
        var processName = string.Empty;
        var displayName = string.Empty;
        string? executablePath = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            processName = process.ProcessName;
            try
            {
                executablePath = process.MainModule?.FileName;
                var version = process.MainModule?.FileVersionInfo;
                displayName = version?.FileDescription ?? version?.ProductName ?? string.Empty;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Anti-cheat-protected games can deny MainModule access. Their registered
                // executable name remains available through GameConfigStore.
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }

        var boundsValid = GetWindowRect(window, out var bounds);
        var cloaked = 0;
        _ = DwmGetWindowAttribute(window, DwmwaCloaked, out cloaked, sizeof(int));
        var extendedStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        return new GameWindowSnapshot(
            window,
            processId,
            title,
            processName,
            displayName,
            boundsValid ? Math.Max(0, bounds.Right - bounds.Left) : 0,
            boundsValid ? Math.Max(0, bounds.Bottom - bounds.Top) : 0,
            IsWindowVisible(window),
            IsIconic(window),
            cloaked != 0,
            GetAncestor(window, GaRoot) == window,
            (extendedStyle & WsExToolWindow) != 0,
            WindowsGameRegistration.IsKnownGameProcess(processName, executablePath));
    }

    private static string ReadWindowTitle(nint window)
    {
        var length = Math.Clamp(GetWindowTextLength(window), 0, 4096);
        if (length == 0) return string.Empty;
        var text = new StringBuilder(length + 1);
        return GetWindowText(window, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint window,
        uint attribute,
        out int value,
        int valueSize);
}

internal static class WindowsGameRegistration
{
    private static readonly string[] GamePathMarkers =
    {
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\",
        @"\riot games\", @"\xboxgames\",
        @"\ea games\", @"\ubisoft game launcher\games\"
    };
    private static readonly RefreshingPathSet RegisteredPaths = new(
        LoadRegisteredPaths,
        () => DateTimeOffset.UtcNow,
        TimeSpan.FromSeconds(30));

    internal static bool IsKnownGameExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        string fullPath;
        try { fullPath = Path.GetFullPath(executablePath); }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (GamePathMarkers.Any(marker => fullPath.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return RegisteredPaths.Contains(fullPath);
    }

    internal static bool IsKnownGameProcess(string? processName, string? executablePath) =>
        IsKnownGameExecutable(executablePath) ||
        !string.IsNullOrWhiteSpace(processName) &&
        RegisteredPaths.ContainsExecutableName(processName);

    private static HashSet<string> LoadRegisteredPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (children is null) return paths;
            foreach (var childName in children.GetSubKeyNames())
            {
                using var child = children.OpenSubKey(childName);
                if (child?.GetValue("MatchedExeFullPath") is not string path ||
                    string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
                try { paths.Add(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))); }
                catch (Exception exception) when (
                    exception is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        return paths;
    }
}

internal sealed class RefreshingPathSet
{
    private readonly object _gate = new();
    private readonly Func<IEnumerable<string>> _loader;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _refreshInterval;
    private HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _refreshAfter = DateTimeOffset.MinValue;

    internal RefreshingPathSet(
        Func<IEnumerable<string>> loader,
        Func<DateTimeOffset> clock,
        TimeSpan refreshInterval)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(clock);
        if (refreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        _loader = loader;
        _clock = clock;
        _refreshInterval = refreshInterval;
    }

    internal bool Contains(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            RefreshCore();
            return _paths.Contains(path);
        }
    }

    internal bool ContainsExecutableName(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        var normalizedName = Path.GetFileNameWithoutExtension(processName.Trim());
        lock (_gate)
        {
            RefreshCore();
            return _paths.Any(path => string.Equals(
                Path.GetFileNameWithoutExtension(path),
                normalizedName,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RefreshCore()
    {
        var now = _clock();
        if (now < _refreshAfter) return;
        _paths = new HashSet<string>(
            _loader().Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);
        _refreshAfter = now + _refreshInterval;
    }
}
