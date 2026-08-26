using System.Runtime.InteropServices;

namespace ClipsToDiscord;

[Flags]
internal enum GlobalHotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004
}

internal readonly record struct GlobalHotkeyBinding(GlobalHotkeyModifiers Modifiers, Keys Key)
{
    public const string DefaultDisplayText = "Ctrl + Alt + L";

    public static GlobalHotkeyBinding Default { get; } = new(
        GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Alt,
        Keys.L);

    public string DisplayText
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(GlobalHotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(GlobalHotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(GlobalHotkeyModifiers.Shift)) parts.Add("Shift");
            parts.Add(FormatKey(Key));
            return string.Join(" + ", parts);
        }
    }

    public static bool TryParse(string? value, out GlobalHotkeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split(
            '+',
            StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(string.IsNullOrWhiteSpace)) return false;

        var modifiers = GlobalHotkeyModifiers.None;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var modifier = parts[index] switch
            {
                var part when part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                              part.Equals("Control", StringComparison.OrdinalIgnoreCase) =>
                    GlobalHotkeyModifiers.Control,
                var part when part.Equals("Alt", StringComparison.OrdinalIgnoreCase) =>
                    GlobalHotkeyModifiers.Alt,
                var part when part.Equals("Shift", StringComparison.OrdinalIgnoreCase) =>
                    GlobalHotkeyModifiers.Shift,
                _ => GlobalHotkeyModifiers.None
            };
            if (modifier == GlobalHotkeyModifiers.None || modifiers.HasFlag(modifier)) return false;
            modifiers |= modifier;
        }

        return TryParseKey(parts[^1], out var key) && TryCreate(modifiers, key, out binding);
    }

    public static bool TryFromKeyData(Keys keyData, out GlobalHotkeyBinding binding)
    {
        var modifiers = GlobalHotkeyModifiers.None;
        if (keyData.HasFlag(Keys.Control)) modifiers |= GlobalHotkeyModifiers.Control;
        if (keyData.HasFlag(Keys.Alt)) modifiers |= GlobalHotkeyModifiers.Alt;
        if (keyData.HasFlag(Keys.Shift)) modifiers |= GlobalHotkeyModifiers.Shift;
        return TryCreate(modifiers, keyData & Keys.KeyCode, out binding);
    }

    private static bool TryCreate(
        GlobalHotkeyModifiers modifiers,
        Keys key,
        out GlobalHotkeyBinding binding)
    {
        binding = default;
        const GlobalHotkeyModifiers supported = GlobalHotkeyModifiers.Control |
                                                 GlobalHotkeyModifiers.Alt |
                                                 GlobalHotkeyModifiers.Shift;
        if ((modifiers & ~supported) != 0 ||
            (modifiers & (GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Alt)) == 0 ||
            !IsSupportedKey(key) ||
            (modifiers == GlobalHotkeyModifiers.Alt && key == Keys.F4))
        {
            return false;
        }

        binding = new GlobalHotkeyBinding(modifiers, key);
        return true;
    }

    private static bool TryParseKey(string value, out Keys key)
    {
        key = Keys.None;
        var normalized = value.Trim();
        if (normalized.Length == 1)
        {
            var character = char.ToUpperInvariant(normalized[0]);
            if (character is >= 'A' and <= 'Z')
            {
                key = (Keys)character;
                return true;
            }
            if (character is >= '0' and <= '9')
            {
                key = (Keys)((int)Keys.D0 + (character - '0'));
                return true;
            }
        }

        if (normalized.Length is >= 2 and <= 3 &&
            normalized[0] is 'F' or 'f' &&
            int.TryParse(normalized[1..], out var functionNumber) &&
            functionNumber is >= 1 and <= 24)
        {
            key = (Keys)((int)Keys.F1 + (functionNumber - 1));
            return true;
        }

        return false;
    }

    private static bool IsSupportedKey(Keys key) =>
        key is >= Keys.A and <= Keys.Z ||
        key is >= Keys.D0 and <= Keys.D9 ||
        key is >= Keys.F1 and <= Keys.F24;

    private static string FormatKey(Keys key) =>
        key is >= Keys.D0 and <= Keys.D9
            ? ((int)key - (int)Keys.D0).ToString()
            : key.ToString();
}

internal interface IGlobalHotkeyRegistrar
{
    bool Register(IntPtr windowHandle, int identifier, GlobalHotkeyBinding binding);
    bool Unregister(IntPtr windowHandle, int identifier);
    int GetLastError();
}

internal sealed class GlobalHotkeyManager : NativeWindow, IDisposable
{
    internal const int ModeHotkeyIdentifier = 0x4343;
    internal const int CaptureHotkeyIdentifier = 0x4344;
    internal const int HotkeyIdentifier = ModeHotkeyIdentifier;
    internal const int WmHotkey = 0x0312;
    internal const uint ModNoRepeat = 0x4000;
    internal const int HotkeyConflictError = 1409;
    private readonly IGlobalHotkeyRegistrar _registrar;
    private readonly Dictionary<int, GlobalHotkeyBinding> _registeredBindings = [];
    private bool _disposed;

    public GlobalHotkeyManager()
        : this(new Win32GlobalHotkeyRegistrar())
    {
    }

    internal GlobalHotkeyManager(IGlobalHotkeyRegistrar registrar)
    {
        _registrar = registrar;
        CreateHandle(new CreateParams
        {
            Caption = "ClipCord global shortcut",
            Parent = new IntPtr(-3)
        });
    }

    internal GlobalHotkeyBinding? RegisteredBinding => GetBinding(ModeHotkeyIdentifier);
    internal event EventHandler? Pressed;
    internal event EventHandler<GlobalHotkeyPressedEventArgs>? HotkeyPressed;

    internal bool TrySetBinding(GlobalHotkeyBinding? binding, out int errorCode)
        => TrySetBinding(ModeHotkeyIdentifier, binding, out errorCode);

    internal GlobalHotkeyBinding? GetBinding(int identifier) =>
        _registeredBindings.TryGetValue(identifier, out var binding) ? binding : null;

    internal bool TrySetBinding(
        int identifier,
        GlobalHotkeyBinding? binding,
        out int errorCode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsSupportedIdentifier(identifier))
        {
            throw new ArgumentOutOfRangeException(nameof(identifier));
        }
        errorCode = 0;
        var previous = GetBinding(identifier);
        if (previous == binding) return true;
        if (binding is not null && _registeredBindings.Any(pair =>
                pair.Key != identifier && pair.Value == binding.Value))
        {
            errorCode = HotkeyConflictError;
            return false;
        }

        if (previous is not null)
        {
            if (!_registrar.Unregister(Handle, identifier))
            {
                errorCode = _registrar.GetLastError();
                return false;
            }
            _registeredBindings.Remove(identifier);
        }

        if (binding is null) return true;
        if (_registrar.Register(Handle, identifier, binding.Value))
        {
            _registeredBindings[identifier] = binding.Value;
            return true;
        }

        errorCode = _registrar.GetLastError();
        if (previous is not null && _registrar.Register(Handle, identifier, previous.Value))
        {
            _registeredBindings[identifier] = previous.Value;
        }
        else if (previous is not null)
        {
            Log.Error("ClipCord could not restore the previous global shortcut after a registration failure.");
        }
        return false;
    }

    internal bool HandleHotkeyMessage(int identifier)
    {
        if (_disposed || !_registeredBindings.TryGetValue(identifier, out var binding)) return false;
        try
        {
            if (identifier == ModeHotkeyIdentifier)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
            HotkeyPressed?.Invoke(this, new GlobalHotkeyPressedEventArgs(identifier, binding));
        }
        catch (Exception exception)
        {
            Log.Error("The global shortcut handler failed.", exception);
        }
        return true;
    }

    internal static uint GetNativeModifiers(GlobalHotkeyBinding binding) =>
        (uint)binding.Modifiers | ModNoRepeat;

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && HandleHotkeyMessage(message.WParam.ToInt32())) return;
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != IntPtr.Zero)
        {
            foreach (var identifier in _registeredBindings.Keys.ToArray())
            {
                _registrar.Unregister(Handle, identifier);
            }
            _registeredBindings.Clear();
        }
        if (Handle != IntPtr.Zero) DestroyHandle();
        Pressed = null;
        HotkeyPressed = null;
        GC.SuppressFinalize(this);
    }

    private static bool IsSupportedIdentifier(int identifier) =>
        identifier is ModeHotkeyIdentifier or CaptureHotkeyIdentifier;

    private sealed class Win32GlobalHotkeyRegistrar : IGlobalHotkeyRegistrar
    {
        public bool Register(IntPtr windowHandle, int identifier, GlobalHotkeyBinding binding) =>
            RegisterHotKey(
                windowHandle,
                identifier,
                GetNativeModifiers(binding),
                (uint)binding.Key);

        public bool Unregister(IntPtr windowHandle, int identifier) =>
            UnregisterHotKey(windowHandle, identifier);

        public int GetLastError() => Marshal.GetLastWin32Error();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(
            IntPtr windowHandle,
            int identifier,
            uint modifiers,
            uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr windowHandle, int identifier);
    }
}

internal sealed class GlobalHotkeyPressedEventArgs(
    int identifier,
    GlobalHotkeyBinding binding) : EventArgs
{
    internal int Identifier { get; } = identifier;
    internal GlobalHotkeyBinding Binding { get; } = binding;
}
