// IINA 1.5.1/build 180 HistoryWindowController searchFieldAction/prepareData/getKey.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;

namespace Mani.Playback;

public enum HistorySearchField { FullPath, Filename }
public enum HistoryGrouping { Date, Location }
public sealed record HistoryGroup(string Label, IReadOnlyList<PlaybackHistoryEntry> Entries);

public static class HistoryBrowser
{
    private static Uri? Remote(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    public static string Filename(string source) => Remote(source) is { } uri ? Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "") : Path.GetFileName(source);
    public static string Location(string source) => Remote(source) is { } uri ? uri.Host : Path.GetDirectoryName(source) ?? "";
    public static IReadOnlyList<HistoryGroup> Query(IEnumerable<PlaybackHistoryEntry> entries, string query,
        HistorySearchField field, HistoryGrouping grouping, CultureInfo culture, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var compare = culture.CompareInfo;
        var filtered = entries.Where(entry => query.Length == 0 || compare.IndexOf(
            field == HistorySearchField.Filename ? Filename(entry.Source) : Remote(entry.Source) is { } uri ? Uri.UnescapeDataString(uri.AbsolutePath) : entry.Source,
            query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
        if (grouping == HistoryGrouping.Date)
            return filtered.GroupBy(entry => TimeZoneInfo.ConvertTime(entry.PlayedAt, zone).Date)
                .OrderByDescending(group => group.Key)
                .Select(group => new HistoryGroup(group.Key.ToString("D", culture), group.ToArray())).ToArray();
        var ordering = Comparer<string>.Create((a, b) => compare.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.NumericOrdering));
        return filtered.GroupBy(entry => Location(entry.Source)).OrderBy(group => group.Key, ordering)
            .Select(group => new HistoryGroup(group.Key, group.ToArray())).ToArray();
    }
}
