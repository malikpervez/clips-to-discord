using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace ClipsToDiscord;

internal static class GraphicsCaptureItemFactory
{
    private const int EInvalidArgument = unchecked((int)0x80070057);
    private const int ErrorInvalidWindowHandle = unchecked((int)0x80070578);
    private const string RuntimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    private static readonly Guid InteropInterfaceId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid CaptureItemInterfaceId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindowDelegate(
        nint instance,
        nint window,
        in Guid interfaceId,
        out nint result);

    internal static GraphicsCaptureItem CreateForWindow(nint window)
    {
        if (window == 0) throw new ArgumentException("A valid game window is required.", nameof(window));
        using var factory = WinRT.ActivationFactory.Get(RuntimeClass, InteropInterfaceId);
        var vtable = Marshal.ReadIntPtr(factory.ThisPtr);
        var method = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        var createForWindow = Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(method);
        var resultCode = createForWindow(factory.ThisPtr, window, CaptureItemInterfaceId, out var itemPointer);
        Marshal.ThrowExceptionForHR(resultCode);
        if (itemPointer == 0)
        {
            throw new InvalidOperationException("Windows did not return a capture item for that game window.");
        }
        try
        {
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            WinRT.MarshalInterface<GraphicsCaptureItem>.DisposeAbi(itemPointer);
        }
    }

    internal static bool IsUnavailableWindowFailure(Exception exception) =>
        exception.HResult is EInvalidArgument or ErrorInvalidWindowHandle ||
        exception is InvalidOperationException &&
        exception.Message.Contains(
            "capture item for that game window",
            StringComparison.OrdinalIgnoreCase);
}
