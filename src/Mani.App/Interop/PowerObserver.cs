using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mani_App.Interop;

// HWND-owned subscription. Adapted from the existing SessionLockObserver lifetime pattern.
// IINA policy and Windows API assessment: docs/m1/sleep-validation.md.
internal sealed class PowerObserver : IDisposable
{
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    private static readonly Dictionary<nuint, PowerObserver> Roots = [];
    private static long _nextId;
    private readonly nint _hwnd;
    private readonly nuint _id;
    private readonly SubclassProc _callback;
    private readonly Action<int> _receive;
    private nint _registration;
    private bool _subclass, _disposed;
    public string? Failure { get; private set; }
    public string? CleanupFailure { get; private set; }
    public bool Active => !_disposed && _subclass && _registration != 0;
    public bool Released => !_subclass && _registration == 0;

    public PowerObserver(nint hwnd, Action<int> receive)
    {
        _hwnd = hwnd; _receive = receive; _id = (nuint)Interlocked.Increment(ref _nextId);
        _callback = Callback; Roots.Add(_id, this);
        if (!SetWindowSubclass(hwnd, _callback, _id, 0))
        { Roots.Remove(_id); throw new InvalidOperationException("Could not observe window power events."); }
        _subclass = true;
        _registration = RegisterSuspendResumeNotification(hwnd, 0);
        if (_registration == 0)
        {
            int error = Marshal.GetLastWin32Error(); Dispose();
            throw new Win32Exception(error, "Could not register sleep/wake notifications.");
        }
    }
    private nint Callback(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        bool handled = false;
        try
        {
            if (!_disposed && message == 0x0218 && wParam is 4 or 7 or 18)
            { _receive((int)wParam); handled = true; }
            if (message == 0x0082)
            {
                Dispose(); _subclass = false; Roots.Remove(_id);
            }
        }
        catch (Exception error) { Failure ??= error.ToString(); }
        nint result = DefSubclassProc(hwnd, message, wParam, lParam);
        return handled ? 1 : result;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registration != 0)
        {
            if (UnregisterSuspendResumeNotification(_registration)) _registration = 0;
            else CleanupFailure = "Power notification unregistration failed: " + Marshal.GetLastWin32Error();
        }
        if (_subclass)
        {
            if (RemoveWindowSubclass(_hwnd, _callback, _id)) { _subclass = false; Roots.Remove(_id); }
            else CleanupFailure ??= "Power window subclass removal failed.";
        }
    }
    [DllImport("comctl32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterSuspendResumeNotification(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterSuspendResumeNotification(nint handle);
}

