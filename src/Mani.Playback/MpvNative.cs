using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Mani.Playback;

// ABI follows the headers in config/libmpv.lock.json. Windows unsigned long is uint32.
internal static class MpvNative
{
    private const string Library = "mani-libmpv";
    private static readonly object Gate = new();
    private static nint _library;
    private static string? _hash;
    private static string? _path;

    internal static void Load(string path, string expectedHash)
    {
        lock (Gate)
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("libmpv requires Windows x64.");
            path = Path.GetFullPath(path);
            if (_library != 0)
            {
                if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Restart before changing the libmpv path.");
                if (!string.Equals(_hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Resident libmpv does not match the pinned manifest.");
                return;
            }
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("libmpv DLL SHA-256 does not match the pinned manifest.");
            _library = NativeLibrary.Load(Path.GetFullPath(path), typeof(MpvNative).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            _hash = actual;
            _path = path;
            NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly,
                (name, _, _) => name == Library ? _library : 0);
            // Intentionally resident until process exit; never unload underneath native worker threads.
        }
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Event
    {
        internal int Id, Error;
        internal ulong Reply;
        internal nint Data;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyEvent
    {
        internal nint Name;
        internal int Format;
        internal nint Data;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct EndFile
    {
        internal int Reason, Error;
        internal long PlaylistEntryId, InsertId;
        internal int InsertCount;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct LogMessage
    {
        internal nint Prefix, Level, Text;
        internal int LogLevel;
    }
    [StructLayout(LayoutKind.Explicit, Size = 16)] internal struct Node
    {
        [FieldOffset(0)] internal nint Pointer;
        [FieldOffset(0)] internal long Integer;
        [FieldOffset(0)] internal int Flag;
        [FieldOffset(0)] internal double Number;
        [FieldOffset(8)] internal int Format;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct NodeList
    {
        internal int Count;
        internal nint Values, Keys;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ByteArray
    {
        internal nint Data;
        internal nuint Size;
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern uint mpv_client_api_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_initialize(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_terminate_destroy(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_set_option_string(nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_set_property_string(nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_get_property_string(nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_get_property(nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out long value);
    [DllImport(Library, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetFlag(nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out int value);
    [DllImport(Library, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetDouble(nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_command(nint handle, nint args);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_command_async(nint handle, ulong reply, nint args);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_abort_async_command(nint handle, ulong reply);
    [DllImport(Library, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetNode(nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out Node value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_free_node_contents(ref Node value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_observe_property(nint handle,
        ulong replyUserData, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_wait_event(nint handle, double timeout);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_request_log_messages(nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string level);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_error_string(int error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_free(nint value);

    internal static string Utf8(nint pointer) => Marshal.PtrToStringUTF8(pointer) ?? "";
    internal static string Error(int code) => Utf8(mpv_error_string(code));
    internal static void Check(int code, string action)
    {
        if (code < 0) throw new InvalidOperationException($"{action}: {Error(code)} ({code})");
    }
    internal static unsafe void Command(nint handle, string[] values, ulong? reply = null)
    {
        if (values.Length == 0) throw new ArgumentException("An mpv command is required.", nameof(values));
        var strings = new nint[values.Length + 1];
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i].Contains('\0')) throw new ArgumentException("NUL is not valid in an mpv argument.");
                strings[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
            }
            fixed (nint* args = strings) Check(reply is ulong id
                ? mpv_command_async(handle, id, (nint)args) : mpv_command(handle, (nint)args), values[0]);
        }
        finally { foreach (var value in strings) if (value != 0) Marshal.FreeCoTaskMem(value); }
    }
}
