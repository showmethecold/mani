using System.Runtime.InteropServices;

namespace Mani.Playback;

// Windows adaptation of IINA 1.5.1's sans-serif default (AppData.swift) and
// system-font availability boundary (FontPickerWindowController.swift).
// Keep the saved preference and explicit family names intact; libass still
// owns glyph fallback, embedded fonts, and each channel's ASS override policy.
internal static class SubtitleFontFallback
{
    private static readonly Lazy<string> DefaultFamily = new(FindSystemDefault);

    internal static string Resolve(string requested) => IsDefault(requested) ? DefaultFamily.Value : requested;
    internal static string Resolve(string requested, string defaultFamily) => IsDefault(requested) ? defaultFamily : requested;
    private static bool IsDefault(string requested) => requested.Trim().Equals("sans-serif", StringComparison.OrdinalIgnoreCase);

    internal static string SelectDefault(Func<string, bool> installed)
    {
        foreach (string family in new[] { "Microsoft YaHei", "Microsoft JhengHei" })
            if (installed(family)) return family;
        return "sans-serif";
    }

    private static unsafe string FindSystemDefault()
    {
        if (!OperatingSystem.IsWindows()) return "sans-serif";
        nint factory = 0, collection = 0;
        try
        {
            var iid = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
            Marshal.ThrowExceptionForHR(DWriteCreateFactory(0, in iid, out factory));
            // IDWriteFactory::GetSystemFontCollection, immediately after IUnknown.
            var getCollection = (delegate* unmanaged[Stdcall]<nint, nint*, int, int>)(*(nint**)factory)[3];
            Marshal.ThrowExceptionForHR(getCollection(factory, &collection, 0));
            nint fonts = collection;
            // IDWriteFontCollection::FindFamilyName, after Count and GetFontFamily.
            var findFamily = (delegate* unmanaged[Stdcall]<nint, char*, uint*, int*, int>)(*(nint**)collection)[5];
            return SelectDefault(family =>
            {
                uint index; int exists;
                fixed (char* name = family)
                    Marshal.ThrowExceptionForHR(findFamily(fonts, name, &index, &exists));
                return exists != 0;
            });
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Missing fonts/services must never prevent playback.
            return "sans-serif";
        }
        finally
        {
            if (collection != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)collection)[2])(collection);
            if (factory != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)factory)[2])(factory);
        }
    }

    [DllImport("dwrite.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DWriteCreateFactory(uint factoryType, in Guid iid, out nint factory);
}
