// IINA MainMenuActions.menuShowCurrentFileInFinder local-file guard.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

internal static class LocalMediaFile
{
    public static string? PathFrom(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            // Check native paths first: a literal %20 or # is part of a filename.
            if (Path.IsPathFullyQualified(source)) return Path.GetFullPath(source);
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile &&
                Path.IsPathFullyQualified(uri.LocalPath)) return Path.GetFullPath(uri.LocalPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
        return null;
    }
}
