using System.Runtime.InteropServices;
using System.Text;

namespace Mani_App.Diagnostics;

// Read-only Windows boundary; IINA's Carbon keyboard-layout APIs are macOS-only.
// This is a thread layout identifier, not a TSF profile or IME conversion-mode identity.
internal static class StartupInput
{
    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardLayoutNameW(StringBuilder name);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public static object Capture()
    {
        uint thread = GetCurrentThreadId();
        nint first = GetKeyboardLayout(0);
        var name = new StringBuilder(9); // KL_NAMELENGTH includes the terminator.
        bool read = GetKeyboardLayoutNameW(name);
        int? error = read ? null : Marshal.GetLastWin32Error();
        nint last = GetKeyboardLayout(0);
        string klid = name.ToString().ToUpperInvariant();
        bool valid = first != 0 && first == last && read && klid.Length == 8 && klid.All(char.IsAsciiHexDigit);
        return new { schema_version = 1, status = valid ? "observed" : "unavailable-or-changing",
            threadId = thread, hkl = ((nuint)first).ToString("X16"), klid,
            languageId = (int)((nuint)first & 0xffff), error,
            limitation = "Calling-thread HKL/KLID only; not a TSF profile, IME conversion mode, or proof that IME modules are absent." };
    }
}
