// IINA 1.5.1/build 180 DraggingDetect.getPlayableFiles and Utility extension lists.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
namespace Mani.Playback;

public static class MediaSources
{
    public static IReadOnlyList<string> FromDropText(string text)
    {
        var sources = new List<string>();
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Contains('\0')) return [];
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") sources.Add(line);
            else if (Uri.TryCreate(line, UriKind.Absolute, out uri) && uri.IsFile) sources.Add(uri.LocalPath);
            else if (Path.IsPathFullyQualified(line)) sources.Add(line);
            else return [];
        }
        return sources;
    }
    private static readonly HashSet<string> Playable = new("mkv mp4 avi m4v mov 3gp ts mts m2ts wmv flv f4v asf webm rm rmvb qt dv mpg mpeg mxf vob gif ogv ogm mp3 aac mka dts flac ogg oga mogg m4a ac3 opus wav wv aiff aif ape tta tak".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Blacklist = new("utf utf8 utf-8 idx sub srt smi rt ssa aqt jss js ass mks vtt sup scc lrc m3u m3u8 pls".Split(' '), StringComparer.OrdinalIgnoreCase);
    internal static bool IsPlayableFile(string path) => Playable.Contains(Path.GetExtension(path).TrimStart('.'));
    public static IReadOnlyList<string> Expand(IEnumerable<string> sources)
    {
        var input = sources.ToArray();
        if (input.Length == 1 && new[] { ".m3u", ".m3u8", ".pls", ".cue" }.Contains(Path.GetExtension(input[0]).ToLowerInvariant())) return input;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string value, bool remote = false) => result.TryAdd(remote ? "url:" + value : "file:" + value.ToUpperInvariant(), value);
        foreach (var source in input)
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") { Add(source, remote: true); continue; }
            var path = Path.GetFullPath(source);
            if (!Directory.Exists(path)) { if (!Blacklist.Contains(Path.GetExtension(path).TrimStart('.'))) Add(path); continue; }
            // Follow playable reparse files (including cloud placeholders), but
            // never recurse through a junction/symlink directory into a cycle.
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            var directories = new Stack<string>(); directories.Push(path);
            while (directories.TryPop(out var directory))
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    if (entry.Name.StartsWith('.')) continue;
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    { if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) directories.Push(entry.FullName); }
                    else if (Playable.Contains(entry.Extension.TrimStart('.'))) Add(entry.FullName);
                }
            }
        }
        return result.Values.OrderBy(Path.GetDirectoryName, StringComparer.Ordinal).ThenBy(Path.GetFileName, NaturalNameComparer.Instance).ToArray();
    }
    // Windows replacement for localizedStandardCompare: numeric episode ordering.
    private sealed class NaturalNameComparer : IComparer<string?>
    {
        public static readonly NaturalNameComparer Instance = new();
        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
                {
                    int ai = i, bj = j;
                    while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                    while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                    var x = a[ai..i].TrimStart('0'); var y = b[bj..j].TrimStart('0');
                    int n = x.Length.CompareTo(y.Length); if (n == 0) n = string.CompareOrdinal(x, y);
                    if (n != 0) return n;
                }
                else { int n = char.ToUpperInvariant(a[i++]).CompareTo(char.ToUpperInvariant(b[j++])); if (n != 0) return n; }
            }
            int length = (a.Length - i).CompareTo(b.Length - j);
            return length == 0 ? string.CompareOrdinal(a, b) : length;
        }
    }
}
