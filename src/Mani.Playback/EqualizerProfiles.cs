// IINA 1.5.1/build 180 Equalizations.swift / SidebarAudioPane EQ profile port.
// Copyright (c) 2024 lhc. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
using System.Text.Json;
namespace Mani.Playback;

public enum EqualizerProfileError { Name, Count, Size, Data }
public sealed class EqualizerProfileException(EqualizerProfileError reason) : ArgumentException("Invalid equalizer profile: " + reason)
{
    public EqualizerProfileError Reason { get; } = reason;
}

public sealed record EqualizerPreset(string Key, IReadOnlyList<double> Gains);
public static class EqualizerProfiles
{
    public static IReadOnlyList<EqualizerPreset> Presets { get; } = Array.AsReadOnly(new EqualizerPreset[]
    {
        new("flat", Array.AsReadOnly(new double[] { 0,0,0,0,0,0,0,0,0,0 })),
        new("acoustic", Array.AsReadOnly(new double[] { 5.0, 4.9, 3.95,1.05,2.15,1.75,3.5,4.1,3.55,2.15 })),
        new("classical", Array.AsReadOnly(new double[] { 4.75,3.75,3,2.5,-1.5,-1.5,0,2.25,3.25,3.75 })),
        new("dance", Array.AsReadOnly(new double[] { 3.57,6.55,4.99,0,1.92,3.65,5.15,4.54,3.59,0 })),
        new("deep", Array.AsReadOnly(new double[] { 4.95,3.55,1.75,1.00,2.85,2.50,1.45,-2.15,-3.55,-4.60 })),
        new("electronic", Array.AsReadOnly(new double[] { 4.25,3.8,1.2,0,-2.15,2.25,0.85,1.25,3.95,4.8 })),
        new("hip_hop", Array.AsReadOnly(new double[] { 5,4.25,1.5,3,-1,-1,1.5,-0.5,2,3 })),
        new("increase_bass", Array.AsReadOnly(new double[] { 5.5,4.25,3.5,2.5,1.25,0,0,0,0,0 })),
        new("increase_treble", Array.AsReadOnly(new double[] { 0,0,0,0,0,1.25,2.5,3.5,4.25,5.5 })),
        new("increase_vocal", Array.AsReadOnly(new double[] { -1.50,-3.00,-3.00,1.50,3.75,3.75,3.00,1.50,0,-1.50 })),
        new("jazz", Array.AsReadOnly(new double[] { 4,3,1.5,2.25,-1.5,-1.5,0,1.5,3,3.75 })),
        new("latin", Array.AsReadOnly(new double[] { 4.5,3,0,0,-1.5,-1.5,-1.5,0,3,4.5 })),
        new("loundness", Array.AsReadOnly(new double[] { 6,4,0,0,-2,0,-1,-5,5,1 })),
        new("lounge", Array.AsReadOnly(new double[] { -3.00,-1.50,-0.50,1.50,4.00,2.50,0,-1.50,2.00,1.00 })),
        new("piano", Array.AsReadOnly(new double[] { 3,2,0,2.5,3,1.5,3.5,4.5,3,3.5 })),
        new("pop", Array.AsReadOnly(new double[] { -1.5,-1,0,2,4,4,2,0,-1,-1.5 })),
        new("rnb", Array.AsReadOnly(new double[] { 2.62,6.92,5.65,1.33,-2.19,-1.50,2.32,2.65,3.0,3.75 })),
        new("reduce_bass", Array.AsReadOnly(new double[] { -5.5,-4.25,-3.5,-2.5,-1.25,0,0,0,0,0 })),
        new("reduce_treble", Array.AsReadOnly(new double[] { 0,0,0,0,0,-1.25,-2.5,-3.5,-4.25,-5.5 })),
        new("rock", Array.AsReadOnly(new double[] { 5,4,3,1.5,-0.5,-1,0.5,2.5,3.5,4.5 })),
        new("small_speaker", Array.AsReadOnly(new double[] { 5.5,4.25,3.5,2.5,1.25,0,-1.25,-2.5,-3.5,-4.25 })),
        new("spoken_word", Array.AsReadOnly(new double[] { -3.46,-0.47,0,0.69,3.46,4.61,4.84,4.28,2.54,0 })),
    });
    public const int MaximumProfiles = 32, MaximumNameLength = 80, MaximumJsonLength = 32768;
    public static SortedDictionary<string, double[]> Read(string json) => new(
        JsonSerializer.Deserialize<Dictionary<string, double[]>>(json) ?? throw new EqualizerProfileException(EqualizerProfileError.Data), StringComparer.Ordinal);
    public static void Validate(string json)
    {
        try
        {
            if (json is null) throw new EqualizerProfileException(EqualizerProfileError.Data);
            if (json.Length > MaximumJsonLength) throw new EqualizerProfileException(EqualizerProfileError.Size);
            var profiles = Read(json);
            if (profiles.Count > MaximumProfiles) throw new EqualizerProfileException(EqualizerProfileError.Count);
            if (profiles.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > MaximumNameLength || p.Key.Any(char.IsControl)))
                throw new EqualizerProfileException(EqualizerProfileError.Name);
            if (profiles.Any(p => p.Value is null || p.Value.Length != 10 || p.Value.Any(v => !double.IsFinite(v) || v < -12 || v > 12)))
                throw new EqualizerProfileException(EqualizerProfileError.Data);
        }
        catch (JsonException) { throw new EqualizerProfileException(EqualizerProfileError.Data); }
    }
    public static double[] Gains(MediaPreferences preferences) => MediaPreferences.Frequencies.Select(f => double.Parse(preferences.Get("Eq" + f), CultureInfo.InvariantCulture)).ToArray();
    // Compare the values accepted by Mani's numeric controls, not unrounded JSON
    // doubles. WinUI NumberBox must not receive excessive fractional precision.
    public static bool Matches(MediaPreferences preferences, IReadOnlyList<double> gains)
        => Gains(Apply(preferences, gains)).SequenceEqual(Gains(Apply(preferences, Gains(preferences))));
    public static MediaPreferences Apply(MediaPreferences preferences, IReadOnlyList<double> gains)
    {
        if (gains.Count != 10) throw new ArgumentException("Ten bands are required.");
        for (int i = 0; i < 10; i++) preferences = preferences.With("Eq" + MediaPreferences.Frequencies[i], gains[i].ToString(CultureInfo.InvariantCulture));
        return preferences with { EqualizerEnabled = "yes" };
    }
    public static string Save(string json, string name, double[] gains)
    {
        var profiles = Read(json); profiles[name.Trim()] = (double[])gains.Clone();
        string result = JsonSerializer.Serialize(profiles); Validate(result); return result;
    }
    public static string Remove(string json, string name)
    { var profiles = Read(json); profiles.Remove(name); return JsonSerializer.Serialize(profiles); }
    public static string Merge(string current, string before, string edited)
    {
        var result = Read(current); var old = Read(before); var next = Read(edited);
        foreach (string key in old.Keys.Union(next.Keys, StringComparer.Ordinal))
            if (!next.TryGetValue(key, out var value)) result.Remove(key);
            else if (!old.TryGetValue(key, out var previous) || !previous.SequenceEqual(value)) result[key] = value;
        return JsonSerializer.Serialize(result);
    }
}
