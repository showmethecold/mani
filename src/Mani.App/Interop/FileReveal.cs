using System.Runtime.InteropServices;

namespace Mani_App.Interop;

// Windows boundary for IINA's NSWorkspace.activateFileViewerSelecting.
internal static class FileReveal
{
    public static Task ShowAsync(string path) => Task.Run(() =>
    {
        // Shell parsing can access slow storage; never run it on the UI thread.
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
        nint item = 0;
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The current file is unavailable.", path);
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, 0, out item, 0, out _));
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, 0, 0));
        }
        finally
        {
            if (item != 0) Marshal.FreeCoTaskMem(item);
            CoUninitialize();
        }
    });

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint item, uint attributes, out uint actualAttributes);
    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(nint item, uint count, nint children, uint flags);
}
