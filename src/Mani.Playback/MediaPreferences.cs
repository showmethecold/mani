// Port of IINA 1.5.1/build 180 Preference, MPVController, CharEncoding and PlayerCore.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mani.Playback;

public sealed record MediaPreference(string Key, string Property, string Default, string Group,
    double? Minimum = null, double? Maximum = null, string[]? Choices = null)
{
    public string Validate(string value)
    {
        if (Key == "EqualizerProfiles") { EqualizerProfiles.Validate(value); return value; }
        if (value is null || value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException("Invalid media preference.", Key);
        if (Choices is not null && !Choices.Contains(value)) throw new ArgumentException("Unknown media preference choice.", Key);
        if (Minimum is double min)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number < min || number > Maximum)
                throw new ArgumentOutOfRangeException(Key);
            if (Key is "MarginX" or "MarginY" && number != Math.Truncate(number)) throw new ArgumentOutOfRangeException(Key);
            return number.ToString("0.######", CultureInfo.InvariantCulture);
        }
        if (Key.EndsWith("Color", StringComparison.Ordinal) && !Regex.IsMatch(value, "^#[0-9a-fA-F]{8}$")) throw new ArgumentException("Use #AARRGGBB.", Key);
        if (Key is "AudioLanguage" or "SubtitleLanguage" && !Regex.IsMatch(value, "^[a-zA-Z0-9, _-]*$")) throw new ArgumentException("Invalid language list.", Key);
        if (Key == "SubtitleEncoding" && !MediaPreferences.Encodings.Contains(value)) throw new ArgumentException("Unknown encoding.", Key);
        if (Key is "Font" or "AudioDevice" && string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", Key);
        return value;
    }
}

public sealed record MediaPreferences
{
    public string MaximumVolume { get; init; } = "100";
    public string AudioLanguage { get; init; } = "";
    public string AudioDevice { get; init; } = "auto";
    public string SpdifAc3 { get; init; } = "no";
    public string SpdifDts { get; init; } = "no";
    public string SpdifDtsHd { get; init; } = "no";
    public string SpdifEac3 { get; init; } = "no";
    public string SpdifTrueHd { get; init; } = "no";
    public string EqualizerProfiles { get; init; } = "{}";
    public string EqualizerEnabled { get; init; } = "no";
    public string Eq32 { get; init; } = "0";
    public string Eq64 { get; init; } = "0";
    public string Eq125 { get; init; } = "0";
    public string Eq250 { get; init; } = "0";
    public string Eq500 { get; init; } = "0";
    public string Eq1000 { get; init; } = "0";
    public string Eq2000 { get; init; } = "0";
    public string Eq4000 { get; init; } = "0";
    public string Eq8000 { get; init; } = "0";
    public string Eq16000 { get; init; } = "0";
    public string SubtitleMatching { get; init; } = "iina";
    public string SubtitleSearchPaths { get; init; } = "./*";
    public string SubtitlePriority { get; init; } = "";
    public string SubtitleLanguage { get; init; } = "";
    public string SubtitleEncoding { get; init; } = "auto";
    public string PrimaryOverride { get; init; } = "scale";
    public string SecondaryOverride { get; init; } = "scale";
    public string PrimaryPosition { get; init; } = "100";
    public string Font { get; init; } = "sans-serif";
    public string FontSize { get; init; } = "55";
    public string Bold { get; init; } = "no";
    public string Italic { get; init; } = "no";
    public string TextColor { get; init; } = "#FFFFFFFF";
    public string BorderStyle { get; init; } = "outline-and-shadow";
    public string BorderSize { get; init; } = "3";
    public string BorderColor { get; init; } = "#FF000000";
    public string ShadowOffset { get; init; } = "0";
    public string ShadowColor { get; init; } = "#00000000";
    public string Blur { get; init; } = "0";
    public string Spacing { get; init; } = "0";
    public string MarginX { get; init; } = "25";
    public string MarginY { get; init; } = "22";
    public string AlignX { get; init; } = "center";
    public string AlignY { get; init; } = "bottom";
    public string ScaleWithWindow { get; init; } = "yes";
    public string Letterbox { get; init; } = "yes";
    public static IReadOnlyList<MediaPreference> Fields { get; } = Array.AsReadOnly(new MediaPreference[]
    {
        new("MaximumVolume", "volume-max", "100", "Audio", 100, 1000),
        new("AudioLanguage", "alang", "", "Audio"),
        new("AudioDevice", "audio-device", "auto", "Audio"),
        new("SpdifAc3", "", "no", "Audio", Choices: "no,yes".Split(',')),
        new("SpdifDts", "", "no", "Audio", Choices: "no,yes".Split(',')),
        new("SpdifDtsHd", "", "no", "Audio", Choices: "no,yes".Split(',')),
        new("SpdifEac3", "", "no", "Audio", Choices: "no,yes".Split(',')),
        new("SpdifTrueHd", "", "no", "Audio", Choices: "no,yes".Split(',')),
        new("EqualizerProfiles", "", "{}", "Library"),
        new("EqualizerEnabled", "", "no", "Equalizer", Choices: "no,yes".Split(',')),
        new("Eq32", "", "0", "Equalizer", -12, 12),
        new("Eq64", "", "0", "Equalizer", -12, 12),
        new("Eq125", "", "0", "Equalizer", -12, 12),
        new("Eq250", "", "0", "Equalizer", -12, 12),
        new("Eq500", "", "0", "Equalizer", -12, 12),
        new("Eq1000", "", "0", "Equalizer", -12, 12),
        new("Eq2000", "", "0", "Equalizer", -12, 12),
        new("Eq4000", "", "0", "Equalizer", -12, 12),
        new("Eq8000", "", "0", "Equalizer", -12, 12),
        new("Eq16000", "", "0", "Equalizer", -12, 12),
        new("SubtitleMatching", "", "iina", "SubtitleLoad", Choices: "no,name,iina".Split(',')),
        new("SubtitleSearchPaths", "", "./*", "SubtitleLoad"),
        new("SubtitlePriority", "", "", "SubtitleLoad"),
        new("SubtitleLanguage", "slang", "", "SubtitleLoad"),
        new("SubtitleEncoding", "sub-codepage", "auto", "SubtitleLoad"),
        new("PrimaryOverride", "sub-ass-override", "scale", "SubtitleChannels", Choices: "no,scale,yes,force,strip".Split(',')),
        new("SecondaryOverride", "secondary-sub-ass-override", "scale", "SubtitleChannels", Choices: "no,scale,yes,force,strip".Split(',')),
        new("PrimaryPosition", "sub-pos", "100", "SubtitleChannels", 0, 150),
        new("Font", "sub-font", "sans-serif", "SubtitleStyle"),
        new("FontSize", "sub-font-size", "55", "SubtitleStyle", 1, 9000),
        new("Bold", "sub-bold", "no", "SubtitleStyle", Choices: "no,yes".Split(',')),
        new("Italic", "sub-italic", "no", "SubtitleStyle", Choices: "no,yes".Split(',')),
        new("TextColor", "sub-color", "#FFFFFFFF", "SubtitleStyle"),
        new("BorderStyle", "sub-border-style", "outline-and-shadow", "SubtitleStyle", Choices: "outline-and-shadow,opaque-box,background-box".Split(',')),
        new("BorderSize", "sub-outline-size", "3", "SubtitleStyle", 0, 100),
        new("BorderColor", "sub-outline-color", "#FF000000", "SubtitleStyle"),
        new("ShadowOffset", "sub-shadow-offset", "0", "SubtitleStyle", 0, 100),
        new("ShadowColor", "sub-back-color", "#00000000", "SubtitleStyle"),
        new("Blur", "sub-blur", "0", "SubtitleStyle", 0, 20),
        new("Spacing", "sub-spacing", "0", "SubtitleStyle", -10, 10),
        new("MarginX", "sub-margin-x", "25", "SubtitleStyle", 0, 10000),
        new("MarginY", "sub-margin-y", "22", "SubtitleStyle", 0, 10000),
        new("AlignX", "sub-align-x", "center", "SubtitleStyle", Choices: "left,center,right".Split(',')),
        new("AlignY", "sub-align-y", "bottom", "SubtitleStyle", Choices: "top,center,bottom".Split(',')),
        new("ScaleWithWindow", "sub-scale-by-window", "yes", "SubtitleStyle", Choices: "no,yes".Split(',')),
        new("Letterbox", "sub-use-margins", "yes", "SubtitleStyle", Choices: "no,yes".Split(',')),
    });
    // IINA sidebar values belong to one mpv instance, not the shared preferences file.
    public static IReadOnlyList<MediaPreference> SubtitleChannelFields { get; } = Array.AsReadOnly(
        Fields.Where(f => f.Group == "SubtitleChannels").Concat(new MediaPreference[]
        {
            new("SecondaryPosition", "secondary-sub-pos", "0", "SubtitleChannels", 0, 150),
            new("PrimaryScale", "sub-scale", "1", "SubtitleChannels", 0.1, 100),
            new("SecondaryScale", "secondary-sub-scale", "1", "SubtitleChannels", 0.1, 100),
        }).ToArray());
    public string Get(string key) => key switch
    {
        "MaximumVolume" => MaximumVolume,
        "AudioLanguage" => AudioLanguage,
        "AudioDevice" => AudioDevice,
        "SpdifAc3" => SpdifAc3,
        "SpdifDts" => SpdifDts,
        "SpdifDtsHd" => SpdifDtsHd,
        "SpdifEac3" => SpdifEac3,
        "SpdifTrueHd" => SpdifTrueHd,
        "EqualizerProfiles" => EqualizerProfiles,
        "EqualizerEnabled" => EqualizerEnabled,
        "Eq32" => Eq32,
        "Eq64" => Eq64,
        "Eq125" => Eq125,
        "Eq250" => Eq250,
        "Eq500" => Eq500,
        "Eq1000" => Eq1000,
        "Eq2000" => Eq2000,
        "Eq4000" => Eq4000,
        "Eq8000" => Eq8000,
        "Eq16000" => Eq16000,
        "SubtitleMatching" => SubtitleMatching,
        "SubtitleSearchPaths" => SubtitleSearchPaths,
        "SubtitlePriority" => SubtitlePriority,
        "SubtitleLanguage" => SubtitleLanguage,
        "SubtitleEncoding" => SubtitleEncoding,
        "PrimaryOverride" => PrimaryOverride,
        "SecondaryOverride" => SecondaryOverride,
        "PrimaryPosition" => PrimaryPosition,
        "Font" => Font,
        "FontSize" => FontSize,
        "Bold" => Bold,
        "Italic" => Italic,
        "TextColor" => TextColor,
        "BorderStyle" => BorderStyle,
        "BorderSize" => BorderSize,
        "BorderColor" => BorderColor,
        "ShadowOffset" => ShadowOffset,
        "ShadowColor" => ShadowColor,
        "Blur" => Blur,
        "Spacing" => Spacing,
        "MarginX" => MarginX,
        "MarginY" => MarginY,
        "AlignX" => AlignX,
        "AlignY" => AlignY,
        "ScaleWithWindow" => ScaleWithWindow,
        "Letterbox" => Letterbox,
        _ => throw new ArgumentException("Unknown media preference.", nameof(key))
    };
    public MediaPreferences With(string key, string value)
    {
        value = Fields.First(f => f.Key == key).Validate(value);
        return key switch
        {
            "MaximumVolume" => this with { MaximumVolume = value },
            "AudioLanguage" => this with { AudioLanguage = value },
            "AudioDevice" => this with { AudioDevice = value },
            "SpdifAc3" => this with { SpdifAc3 = value },
            "SpdifDts" => this with { SpdifDts = value },
            "SpdifDtsHd" => this with { SpdifDtsHd = value },
            "SpdifEac3" => this with { SpdifEac3 = value },
            "SpdifTrueHd" => this with { SpdifTrueHd = value },
            "EqualizerProfiles" => this with { EqualizerProfiles = value },
            "EqualizerEnabled" => this with { EqualizerEnabled = value },
            "Eq32" => this with { Eq32 = value },
            "Eq64" => this with { Eq64 = value },
            "Eq125" => this with { Eq125 = value },
            "Eq250" => this with { Eq250 = value },
            "Eq500" => this with { Eq500 = value },
            "Eq1000" => this with { Eq1000 = value },
            "Eq2000" => this with { Eq2000 = value },
            "Eq4000" => this with { Eq4000 = value },
            "Eq8000" => this with { Eq8000 = value },
            "Eq16000" => this with { Eq16000 = value },
            "SubtitleMatching" => this with { SubtitleMatching = value },
            "SubtitleSearchPaths" => this with { SubtitleSearchPaths = value },
            "SubtitlePriority" => this with { SubtitlePriority = value },
            "SubtitleLanguage" => this with { SubtitleLanguage = value },
            "SubtitleEncoding" => this with { SubtitleEncoding = value },
            "PrimaryOverride" => this with { PrimaryOverride = value },
            "SecondaryOverride" => this with { SecondaryOverride = value },
            "PrimaryPosition" => this with { PrimaryPosition = value },
            "Font" => this with { Font = value },
            "FontSize" => this with { FontSize = value },
            "Bold" => this with { Bold = value },
            "Italic" => this with { Italic = value },
            "TextColor" => this with { TextColor = value },
            "BorderStyle" => this with { BorderStyle = value },
            "BorderSize" => this with { BorderSize = value },
            "BorderColor" => this with { BorderColor = value },
            "ShadowOffset" => this with { ShadowOffset = value },
            "ShadowColor" => this with { ShadowColor = value },
            "Blur" => this with { Blur = value },
            "Spacing" => this with { Spacing = value },
            "MarginX" => this with { MarginX = value },
            "MarginY" => this with { MarginY = value },
            "AlignX" => this with { AlignX = value },
            "AlignY" => this with { AlignY = value },
            "ScaleWithWindow" => this with { ScaleWithWindow = value },
            "Letterbox" => this with { Letterbox = value },
            _ => throw new ArgumentException("Unknown media preference.", nameof(key))
        };
    }
    public void Validate() { foreach (var field in Fields) field.Validate(Get(field.Key)); }
    public static MediaPreferences Merge(MediaPreferences current, MediaPreferences before, MediaPreferences edited)
    {
        foreach (var field in Fields)
            if (before.Get(field.Key) != edited.Get(field.Key)) current = current.With(field.Key, field.Key == "EqualizerProfiles" ? Mani.Playback.EqualizerProfiles.Merge(current.EqualizerProfiles, before.EqualizerProfiles, edited.EqualizerProfiles) : edited.Get(field.Key));
        return current;
    }
    [JsonIgnore] public double VolumeLimit => double.Parse(MaximumVolume, CultureInfo.InvariantCulture);
    [JsonIgnore] public string Spdif => string.Join(",", new[] { (SpdifAc3, "ac3"), (SpdifDts, "dts"), (SpdifDtsHd, "dts-hd"), (SpdifEac3, "eac3"), (SpdifTrueHd, "truehd") }.Where(p => p.Item1 == "yes").Select(p => p.Item2));
    public static IReadOnlyList<int> Frequencies { get; } = Array.AsReadOnly(new[] { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 });
    [JsonIgnore] public string EqualizerFilter => "@mani-audio-eq:lavfi=[" + string.Join(",", Frequencies.Select(f =>
        $"equalizer=f={f}:t=h:width={(f / 1.224744871).ToString("R", CultureInfo.InvariantCulture)}:g={Get("Eq" + f)}")) + "]";
    public static IReadOnlyList<string> Encodings { get; } = Array.AsReadOnly(new[] {
        "auto", "UTF-8", "UTF-16", "UTF-16BE", "UTF-16LE", "ISO-8859-6", "WINDOWS-1256", "LATIN7", "WINDOWS-1257", "LATIN8", "WINDOWS-1250", "ISO-8859-5", "WINDOWS-1251", "ISO-8859-2", "WINDOWS-1252", "ISO-8859-7", "WINDOWS-1253", "ISO-8859-8", "WINDOWS-1255", "SHIFT-JIS", "ISO-2022-JP-2", "EUC-KR", "CP949", "ISO-2022-KR", "LATIN6", "LATIN4", "KOI8-R", "GBK", "GB18030", "ISO-2022-CN-EXT", "LATIN3", "LATIN10", "TIS-620", "WINDOWS-874", "EUC-TW", "BIG5", "BIG5-HKSCS", "LATIN5", "WINDOWS-1254", "KOI8-U", "WINDOWS-1258", "VISCII", "LATIN1", "LATIN-9"
    });
}
