using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using WinRT;

namespace Mani_App.Interop;

// Element-local counterpart of NSCursor.setHiddenUntilMouseMoves. WinUI null
// means inherit, so use a transparent cursor rather than a process/global hide count.
internal static class TransparentCursor
{
    internal static unsafe InputCursor Create()
    {
        var andMask = new byte[128];
        Array.Fill(andMask, (byte)255);
        nint cursor = CreateCursor(0, 0, 0, 32, 32, andMask, new byte[128]);
        if (cursor == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint name = 0, factory = 0, result = 0;
        try
        {
            const string className = "Microsoft.UI.Input.InputCursor";
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out name));
            // Public Windows App SDK Microsoft.UI.Input.InputCursor.Interop.h:
            // IInputCursorStaticsInterop derives from IInspectable (six slots).
            var iid = new Guid("ac6f5065-90c4-46ce-beb7-05e138e54117");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, in iid, out factory));
            var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)factory)[6];
            Marshal.ThrowExceptionForHR(create(factory, cursor, &result));
            return MarshalInterface<InputCursor>.FromAbi(result);
        }
        finally
        {
            if (result != 0) Marshal.Release(result);
            if (factory != 0) Marshal.Release(factory);
            if (name != 0) WindowsDeleteString(name);
            // CreateFromHCursor copies a custom cursor; this handle remains ours.
            DestroyCursor(cursor);
        }
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateCursor(nint instance, int xHotspot, int yHotspot, int width, int height, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyCursor(nint cursor);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
}
