// IINA 1.5.1/build 180 AppDelegate.handleOpenFile: collect local paths, then
// use the ordinary media-open pipeline after initialization. Windows adaptation.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Runtime.InteropServices;

namespace Mani_App;

internal static class FileActivationRequest
{
    // The OS/.NET owns command-line quoting. Never split on spaces or interpret
    // filenames as player options; malformed paths and switches are not media.
    public static IReadOnlyList<string> FromPaths(IEnumerable<string> paths) => paths
        .Select(LocalMediaFile.PathFrom).OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<string> FromCommandLine(string commandLine, string executable)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        nint arguments = CommandLineToArgvW(commandLine, out int count);
        if (arguments == 0) throw new System.ComponentModel.Win32Exception();
        try
        {
            var paths = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, i * nint.Size))!;
                if (i == 0 && string.Equals(LocalMediaFile.PathFrom(value), executable, StringComparison.OrdinalIgnoreCase)) continue;
                paths.Add(value);
            }
            return FromPaths(paths);
        }
        finally { _ = LocalFree(arguments); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
