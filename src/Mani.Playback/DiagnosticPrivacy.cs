using System.Text.RegularExpressions;

namespace Mani.Playback;

// Diagnostic output does not need signed URLs; history and playback keep the original.
public static partial class DiagnosticPrivacy
{
    [GeneratedRegex("[a-z][a-z0-9+.-]*://[^\\s\\\"<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetworkUrl();
    public static bool IsNetworkSource(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile;
    public static string Redact(string value) => NetworkUrl().Replace(value, "<network-media>");
}
