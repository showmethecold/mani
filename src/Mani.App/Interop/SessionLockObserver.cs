using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Mani_App.Diagnostics;

namespace Mani_App.Interop;

// Registered only during the M1 diagnostic, on the HWND-owning UI thread.
// Windows adaptation and API references: docs/m1/session-lock-validation.md.
internal sealed class SessionLockObserver : IDisposable
{
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    // Retain the callback even if native removal fails, until WM_NCDESTROY.
    private static readonly Dictionary<nuint, SessionLockObserver> Roots = [];
    private static long _nextId;
    private readonly nint _hwnd;
    private readonly nuint _id;
    private readonly SubclassProc _callback;
    private readonly Func<long> _elapsed;
    private bool _subclass, _registered, _disposed;
    public SessionLockSequence Sequence { get; }
    public bool Released => !_subclass && !_registered;
    public string? CleanupFailure { get; private set; }

    public SessionLockObserver(nint hwnd, Func<long> elapsed)
    {
        _hwnd = hwnd; _elapsed = elapsed; _id = (nuint)Interlocked.Increment(ref _nextId);
        using var process = Process.GetCurrentProcess();
        Sequence = new(process.SessionId);
        _callback = Callback;
        Roots.Add(_id, this);
        if (!SetWindowSubclass(hwnd, _callback, _id, 0))
        { Roots.Remove(_id); throw new InvalidOperationException("Could not observe the player window's session notifications."); }
        _subclass = true;
        if (!WTSRegisterSessionNotification(hwnd, 0)) // NOTIFY_FOR_THIS_SESSION
        {
            int error = Marshal.GetLastWin32Error(); Dispose();
            throw new Win32Exception(error, "Could not register Windows session notifications.");
        }
        _registered = true;
    }
    private nint Callback(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (!_disposed && message == 0x02B1) Sequence.Receive((int)wParam, (int)lParam, _elapsed());
            if (!_disposed && message == 0x0218 && wParam is 4 or 7 or 18)
                Sequence.Fail("Sleep/resume occurred; this check covers lock/unlock only.");
            if (message == 0x0082) // WM_NCDESTROY
            {
                Dispose(); _subclass = _registered = false; Roots.Remove(_id);
            }
        }
        catch (Exception error) { Sequence.Fail("Session observer failed: " + error.Message); }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered)
        {
            if (WTSUnRegisterSessionNotification(_hwnd)) _registered = false;
            else CleanupFailure = "Windows session notification unregistration failed: " + Marshal.GetLastWin32Error();
        }
        if (_subclass)
        {
            if (RemoveWindowSubclass(_hwnd, _callback, _id)) { _subclass = false; Roots.Remove(_id); }
            else CleanupFailure ??= "Window subclass removal failed.";
        }
    }
    [DllImport("comctl32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("wtsapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [DllImport("wtsapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(nint hwnd);
}
