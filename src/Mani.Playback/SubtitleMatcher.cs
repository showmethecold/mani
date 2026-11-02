// Port of IINA 1.5.1/build 180 AutoFileMatcher and FileGroup/FileInfo.
// Copyright (c) 2017 lhc. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
using System.Text;

namespace Mani.Playback;

public static class SubtitleMatcher
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(
        ".utf .utf8 .utf-8 .idx .sub .srt .smi .rt .ssa .aqt .jss .js .ass .mks .vtt .sup .scc .lrc".Split(' '), StringComparer.OrdinalIgnoreCase);
    public static Task<IReadOnlyList<string>> FindAsync(string media, MediaPreferences preferences, CancellationToken token) => Task.Run<IReadOnlyList<string>>(() =>
    {
        if (preferences.SubtitleMatching == "no" || !Path.IsPathFullyQualified(media) || !File.Exists(media)) return [];
        var folder = Path.GetDirectoryName(media)!;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        IEnumerable<string> Files(string path)
        {
            try { return Directory.GetFiles(path, "*", options).Where(f => !Path.GetFileName(f).StartsWith('.')); }
            catch (IOException) { return []; }
            catch (UnauthorizedAccessException) { return []; }
        }
        var siblings = Files(folder).ToArray();
        var subtitles = siblings.Where(f => Extensions.Contains(Path.GetExtension(f))).ToList();
        foreach (string raw in preferences.SubtitleSearchPaths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            token.ThrowIfCancellationRequested();
            string path = raw.Replace('/', Path.DirectorySeparatorChar);
            bool wildcard = path.EndsWith("\\*", StringComparison.Ordinal);
            if (wildcard) path = path[..^2];
            if (path.StartsWith('~')) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
            try
            {
                path = Path.GetFullPath(path, folder);
                var directories = wildcard ? Directory.GetDirectories(path, "*", new EnumerationOptions
                    { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint }) : [path];
                foreach (var directory in directories)
                {
                    token.ThrowIfCancellationRequested();
                    subtitles.AddRange(Files(directory).Where(f => Extensions.Contains(Path.GetExtension(f))));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
        // An idx/sub pair is one VobSub track, loaded through its index.
        var unique = subtitles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        unique = unique.Where(f => !f.EndsWith(".sub", StringComparison.OrdinalIgnoreCase) ||
            !unique.Contains(Path.ChangeExtension(f, ".idx"), StringComparer.OrdinalIgnoreCase)).ToArray();
        return Match(media, siblings.Where(MediaSources.IsPlayableFile), unique, preferences, token);
    }, token);

    public static IReadOnlyList<string> Match(string media, IEnumerable<string> mediaFiles, IEnumerable<string> subtitles,
        MediaPreferences preferences, CancellationToken token = default)
    {
        if (preferences.SubtitleMatching == "no") return [];
        var videos = mediaFiles.Append(media).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Select(p => new MatchFile(p)).ToArray();
        var subs = subtitles.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Select(p => new MatchFile(p)).ToArray();
        var videoGroups = Group(videos, "", token);
        var subGroups = Group(subs, "", token);
        var prefixes = new Dictionary<string, string>();
        if (preferences.SubtitleMatching == "iina")
        {
            foreach (var (vp, members) in videoGroups.Where(g => g.Value.Length > 2))
            {
                token.ThrowIfCancellationRequested();
                string? closest = subGroups.Keys.OrderBy(sp => Distance(vp, sp)).FirstOrDefault();
                if (closest is null) continue;
                string? reverse = videoGroups.Where(g => g.Value.Length > 2).OrderBy(g => Distance(g.Key, closest)).Select(g => g.Key).FirstOrDefault();
                if (reverse == vp && Distance(vp, closest) < (int)((TextElements(vp).Length + TextElements(closest).Length) * .6)) prefixes[vp] = closest;
            }
        }
        var matched = new Dictionary<string, List<MatchFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos)
        {
            token.ThrowIfCancellationRequested();
            var list = new List<MatchFile>();
            if (video.Prefix.Length > 0 && prefixes.TryGetValue(video.Prefix, out string? prefix))
                foreach (var sub in subs.Where(s => s.Prefix == prefix && s.SeriesName is not null))
                    if (video.SeriesName is { } name && (long.TryParse(name, out long vn) && long.TryParse(sub.SeriesName, out long sn) ? vn == sn : name == sub.SeriesName))
                    { list.Add(sub); sub.Matched = true; }
            foreach (var sub in subs.Where(s => !s.Matched && s.Name.Contains(video.Name, StringComparison.Ordinal)))
            { list.Add(sub); sub.Matched = true; }
            matched[video.Path] = list;
        }
        string[] priorities = preferences.SubtitlePriority.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        int Occurrences(MatchFile file) => priorities.Sum(p => (file.Name.Length - file.Name.Replace(p, "", StringComparison.Ordinal).Length) / p.Length);
        // IINA promotes entries above the common minimum, not a ranked relevance score.
        // Keep deterministic file order where its Swift Set had no defined order.
        foreach (var list in matched.Values.Where(list => list.Count > 0))
        {
            int minimum = list.Min(Occurrences);
            var ordered = list.OrderByDescending(s => Occurrences(s) > minimum).ToArray();
            list.Clear(); list.AddRange(ordered);
        }
        // AutoFileMatcher.startMatching stage 2. Directory discovery already supplies
        // the full candidate set; matching does not need to mutate Mani's playlist.
        ForceMatch(videos, subs, matched, token);
        return matched.GetValueOrDefault(media, []).Select(s => s.Path).ToArray();
    }
    private static void ForceMatch(MatchFile[] videos, MatchFile[] subs,
        Dictionary<string, List<MatchFile>> matched, CancellationToken token)
    {
        var remainingVideos = videos.Where(v => matched[v.Path].Count == 0).ToArray();
        var remainingSubs = subs.Where(s => !s.Matched).ToArray();
        // Upstream's strict limit, before any pairwise allocation or distance work.
        if ((long)remainingVideos.Length * remainingSubs.Length >= 100 * 100) return;
        var distances = new int[remainingVideos.Length, remainingSubs.Length];
        var videoMin = Enumerable.Repeat(int.MaxValue, remainingVideos.Length).ToArray();
        var subMin = Enumerable.Repeat(int.MaxValue, remainingSubs.Length).ToArray();
        for (int s = 0; s < remainingSubs.Length; s++)
            for (int v = 0; v < remainingVideos.Length; v++)
            {
                token.ThrowIfCancellationRequested();
                var video = remainingVideos[v]; var sub = remainingSubs[s];
                int threshold = (int)((video.Characters.Length + sub.Characters.Length) * .6);
                int raw = Distance(video.Prefix, sub.Prefix) + Distance(video.Suffix, sub.Suffix);
                int distance = raw < threshold ? raw : int.MaxValue;
                distances[v, s] = distance;
                videoMin[v] = Math.Min(videoMin[v], distance); subMin[s] = Math.Min(subMin[s], distance);
            }
        for (int v = 0; v < remainingVideos.Length; v++)
        {
            token.ThrowIfCancellationRequested();
            if (videoMin[v] == int.MaxValue) continue;
            for (int s = 0; s < remainingSubs.Length; s++)
                if (distances[v, s] == videoMin[v] && distances[v, s] == subMin[s])
                    matched[remainingVideos[v].Path].Add(remainingSubs[s]);
        }
    }
    private static string[] TextElements(string value)
    {
        int[] positions = StringInfo.ParseCombiningCharacters(value);
        return positions.Select((start, i) => value[start..(i + 1 < positions.Length ? positions[i + 1] : value.Length)]).ToArray();
    }
    private sealed class MatchFile(string path)
    {
        public string Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path), Prefix = "";
        public string[] Characters = TextElements(System.IO.Path.GetFileNameWithoutExtension(path));
        public string Suffix => Name[Prefix.Length..];
        public string? SeriesName;
        public bool Matched;
        public void SetPrefix(string prefix)
        {
            Prefix = prefix.Length < Name.Length ? prefix : "";
            if (Prefix.Length == 0) return;
            string suffix = Name[Prefix.Length..];
            bool digit = false;
            SeriesName = string.Concat(suffix.EnumerateRunes().TakeWhile(c => { if (Rune.IsDigit(c)) digit = true; else if (digit) return false; return true; }));
        }
    }
    private static Dictionary<string, MatchFile[]> Group(MatchFile[] files, string prefix, CancellationToken token)
    {
        if (files.Length < 3) return new() { [prefix] = files };
        Dictionary<string, MatchFile[]> groups;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int length = TextElements(prefix).Length;
            groups = files.GroupBy(f => f.Characters.Length > length ? string.Concat(f.Characters.Take(length + 1)) : prefix)
                .ToDictionary(g => g.Key, g => g.ToArray());
            if (groups.Count != 1 || files.All(f => f.Name.Length <= prefix.Length)) break;
            prefix = groups.Keys.Single();
        }
        string[] chars = groups.Keys.Where(k => k.Length > prefix.Length).Select(k => TextElements(k)[^1]).ToArray();
        if (groups.Count < 2 || chars.Any(c => c.Length == 1 && char.IsAsciiDigit(c[0])) || chars.Count(c => "零一二三四五六七八九十".Contains(c, StringComparison.Ordinal)) >= 3 || groups.Values.Max(v => v.Length) < 3)
        {
            foreach (var file in files) file.SetPrefix(prefix);
            return new() { [prefix] = files };
        }
        return groups.SelectMany(g => Group(g.Value, g.Key, token)).ToDictionary(g => g.Key, g => g.Value);
    }
    // IINA ObjcUtils.levDistance dynamic programming, two rows instead of its full matrix.
    private static int Distance(string a, string b)
    {
        // ObjcUtils uses UTF-32, insertion/deletion 1 and substitution 4.
        var left = a.EnumerateRunes().ToArray();
        var right = b.EnumerateRunes().ToArray();
        var row = Enumerable.Range(0, right.Length + 1).ToArray();
        for (int i = 0; i < left.Length; i++)
        {
            int previous = row[0]; row[0] = i + 1;
            for (int j = 0; j < right.Length; j++)
            { int saved = row[j + 1]; row[j + 1] = Math.Min(Math.Min(row[j] + 1, saved + 1), previous + (left[i] == right[j] ? 0 : 4)); previous = saved; }
        }
        return row[^1];
    }
}
