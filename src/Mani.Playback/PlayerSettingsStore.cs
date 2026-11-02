// Preference semantics and missing-key migration adapted from IINA 1.5.1/build 180
// Preference/LegacyMigration. Copyright (c) 2016-2023 lhc. GPL-3.0;
// see licenses/IINA-GPL-3.0.txt. Storage is a Windows/.NET platform adaptation.
using System.Text.Json;
using System.Security.Cryptography;

namespace Mani.Playback;

// IINA Preference.OSCPosition raw values; unrecognized stored values use Floating.
public enum OscPosition { Floating = 0, Top = 1, Bottom = 2 }

public enum ResizeWindowTiming { Always = 0, OnlyWhenOpen = 1, Never = 2 }
public enum ResizeWindowOption { FitScreen = 0, Half = 1, Normal = 2, OneAndHalf = 3, Double = 4 }

public sealed record PlayerSettings(double VolumePercent = 50, bool Muted = false, bool PauseWhenOpen = false,
    double OscHorizontal = .5, double OscBottom = .1, OscPosition OscPosition = OscPosition.Floating,
    bool EdgeToEdgeVideo = true, bool DockedControls = false,
    bool UnlockWindowAspectRatio = false, bool UsePhysicalResolution = true,
    ResizeWindowTiming ResizeWindowTiming = ResizeWindowTiming.OnlyWhenOpen,
    ResizeWindowOption ResizeWindowOption = ResizeWindowOption.Normal,
    bool SidebarSettingsAtLeading = false, int LeadingSidebarWidth = 360, int TrailingSidebarWidth = 360,
    bool AlwaysFloatOnTop = false, bool AutoSwitchToMusicMode = true, bool MusicModeShowAlbumArt = true,
    bool EnableControlBarAutoHide = true, double ControlBarAutoHideTimeout = 2.5,
    bool EnableOsd = true, bool DisableOsdFileStart = false, bool DisableOsdPauseResume = false,
    bool DisableOsdSeek = false, bool DisableOsdSpeed = false, double OsdAutoHideTimeout = 1, double OsdTextSize = 20, string Language = InterfaceLanguage.English, bool RecordHistory = true, bool ResumePosition = true, bool ShowChapterPositions = false, bool EnableThumbnailPreview = true)
{
    public MediaPreferences Media { get; init; } = new();
}

// IINA's number fields have minima 0 seconds / 5 points. Upper bounds protect
// the Windows millisecond timer and bound glyph/layout work for corrupt settings.
public static class InterfacePreferenceLimits
{
    public const double MaximumSeconds = int.MaxValue / 1000d, MaximumTextSize = 512;
    public static bool ValidSeconds(double value) => double.IsFinite(value) && value >= 0 && value <= MaximumSeconds;
    public static bool ValidTextSize(double value) => double.IsFinite(value) && value >= 5 && value <= MaximumTextSize;
}

public sealed class PlayerSettingsStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool Recovered { get; private set; }
    public bool RecoveryAdjustedAutoHide { get; private set; }
    public async Task<PlayerSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var settings = document is null ? new() : Parse(document.RootElement);
            if (Recovered && settings.ControlBarAutoHideTimeout <= 0)
            {
                RecoveryAdjustedAutoHide = true;
                settings = settings with { ControlBarAutoHideTimeout = 2.5 };
            }
            return settings;
        }
        finally { _gate.Release(); }
    }
    private async Task<JsonDocument?> ReadAsync(CancellationToken token)
    {
        async Task<JsonDocument> ReadFile(string file)
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            try { _ = Parse(document.RootElement); return document; }
            catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
            { document.Dispose(); throw new JsonException("Invalid settings value in the current schema.", error); }
            catch { document.Dispose(); throw; }
        }
        if (!File.Exists(path))
        {
            if (!File.Exists(path + ".backup")) return null;
            var backup = await ReadFile(path + ".backup").ConfigureAwait(false);
            Recovered = true; return backup;
        }
        try { return await ReadFile(path).ConfigureAwait(false); }
        catch (JsonException) when (File.Exists(path + ".backup"))
        {
            // Only validated backups recover damaged JSON. Future schema and
            // inaccessible files still fail closed without replacing preferences.
            var backup = await ReadFile(path + ".backup").ConfigureAwait(false);
            try
            {
                string evidence = path + ".corrupt-" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)));
                if (!File.Exists(evidence)) File.Copy(path, evidence);
            }
            catch { backup.Dispose(); throw; }
            Recovered = true; return backup;
        }
    }
    private static PlayerSettings Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Settings must be a JSON object.");
        int version = root.TryGetProperty("schemaVersion", out var schema) ? schema.GetInt32() : 0;
        if (version is < 0 or > 1) throw new InvalidDataException($"Unsupported settings schema: {version}.");
        // Schema 0 is the unversioned import format. A modern key always wins.
        double volume = root.TryGetProperty("volumePercent", out var current) ? current.GetDouble() :
            root.TryGetProperty("volume", out var legacy) ? legacy.GetDouble() : 50;
        bool Flag(string name, bool fallback = false) => root.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;
        if (!double.IsFinite(volume)) throw new JsonException("Invalid stored volume.");
        double Position(string key, double fallback)
        {
            double value = root.TryGetProperty(key, out var stored) ? stored.GetDouble() : fallback;
            if (!double.IsFinite(value)) throw new JsonException("Invalid stored OSC position.");
            return Math.Clamp(value, 0, 1);
        }
        T Option<T>(string key, T fallback) where T : struct, Enum =>
            root.TryGetProperty(key, out var stored) && stored.TryGetInt32(out int raw) && Enum.IsDefined(typeof(T), raw)
                ? (T)Enum.ToObject(typeof(T), raw) : fallback;
        int Width(string key) => Math.Clamp(root.TryGetProperty(key, out var value) ? value.GetInt32() : 360, 300, 500);
        double Number(string key, double fallback, double minimum, double maximum)
        {
            double value = root.TryGetProperty(key, out var stored) ? stored.GetDouble() : fallback;
            if (!double.IsFinite(value)) throw new JsonException("Invalid stored interface setting.");
            return Math.Clamp(value, minimum, maximum);
        }
        int position = root.TryGetProperty("oscPosition", out var mode) ? mode.GetInt32() : 0;
        var media = root.TryGetProperty("media", out var mediaValue) ? mediaValue.Deserialize<MediaPreferences>() ?? throw new JsonException("Missing media preferences.") : new();
        try { media.Validate(); }
        catch (ArgumentException error) { throw new JsonException("Invalid media preferences.", error); }
        return new(Math.Clamp(volume, 0, media.VolumeLimit), Flag("muted"), Flag("pauseWhenOpen"),
            Position("oscHorizontal", .5), Position("oscBottom", .1),
            Enum.IsDefined((OscPosition)position) ? (OscPosition)position : OscPosition.Floating,
            Flag("edgeToEdgeVideo", true), Flag("dockedControlBarAndTitlebar"),
            Flag("unlockWindowAspectRatio"), Flag("usePhysicalResolution", true),
            Option("resizeWindowTiming", ResizeWindowTiming.OnlyWhenOpen), Option("resizeWindowOption", ResizeWindowOption.Normal),
            Flag("sidebarSettingsDisplayAtLeading"), Width("leadingSidebarWidth"), Width("trailingSidebarWidth"), Flag("alwaysFloatOnTop"), Flag("autoSwitchToMusicMode", true), Flag("musicModeShowAlbumArt", true),
            Flag("enableControlBarAutoHide", true), Number("controlBarAutoHideTimeout", 2.5, 0, InterfacePreferenceLimits.MaximumSeconds),
            Flag("enableOSD", true), Flag("disableOSDFileStartMsg"), Flag("disableOSDPauseResumeMsgs"), Flag("disableOSDSeekMsg"), Flag("disableOSDSpeedMsg"),
            Number("osdAutoHideTimeout", 1, 0, InterfacePreferenceLimits.MaximumSeconds), Number("osdTextSize", 20, 5, InterfacePreferenceLimits.MaximumTextSize),
            InterfaceLanguage.Normalize(root.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String ? language.GetString() : null), Flag("recordHistory", true), Flag("resumePosition", true), Flag("showChapterPos"), Flag("enableThumbnailPreview", true)) { Media = media };
    }
    // One key map drives persisted values and unknown-key exclusion.
    private static readonly IReadOnlyDictionary<string, Func<PlayerSettings, object?>> Fields = new Dictionary<string, Func<PlayerSettings, object?>>
    {
        ["media"] = settings => settings.Media,
        ["language"] = settings => InterfaceLanguage.Normalize(settings.Language),
        ["recordHistory"] = settings => settings.RecordHistory,
        ["resumePosition"] = settings => settings.ResumePosition,
        ["showChapterPos"] = settings => settings.ShowChapterPositions,
        ["enableThumbnailPreview"] = settings => settings.EnableThumbnailPreview,
        ["volumePercent"] = settings => settings.VolumePercent,
        ["muted"] = settings => settings.Muted,
        ["pauseWhenOpen"] = settings => settings.PauseWhenOpen,
        ["oscHorizontal"] = settings => settings.OscHorizontal,
        ["oscBottom"] = settings => settings.OscBottom,
        ["oscPosition"] = settings => (int)settings.OscPosition,
        ["edgeToEdgeVideo"] = settings => settings.EdgeToEdgeVideo,
        ["dockedControlBarAndTitlebar"] = settings => settings.DockedControls,
        ["unlockWindowAspectRatio"] = settings => settings.UnlockWindowAspectRatio,
        ["usePhysicalResolution"] = settings => settings.UsePhysicalResolution,
        ["resizeWindowTiming"] = settings => (int)settings.ResizeWindowTiming,
        ["resizeWindowOption"] = settings => (int)settings.ResizeWindowOption,
        ["sidebarSettingsDisplayAtLeading"] = settings => settings.SidebarSettingsAtLeading,
        ["leadingSidebarWidth"] = settings => settings.LeadingSidebarWidth,
        ["trailingSidebarWidth"] = settings => settings.TrailingSidebarWidth,
        ["alwaysFloatOnTop"] = settings => settings.AlwaysFloatOnTop,
        ["autoSwitchToMusicMode"] = settings => settings.AutoSwitchToMusicMode,
        ["musicModeShowAlbumArt"] = settings => settings.MusicModeShowAlbumArt,
        ["enableControlBarAutoHide"] = settings => settings.EnableControlBarAutoHide,
        ["controlBarAutoHideTimeout"] = settings => settings.ControlBarAutoHideTimeout,
        ["enableOSD"] = settings => settings.EnableOsd,
        ["disableOSDFileStartMsg"] = settings => settings.DisableOsdFileStart,
        ["disableOSDPauseResumeMsgs"] = settings => settings.DisableOsdPauseResume,
        ["disableOSDSeekMsg"] = settings => settings.DisableOsdSeek,
        ["disableOSDSpeedMsg"] = settings => settings.DisableOsdSpeed,
        ["osdAutoHideTimeout"] = settings => settings.OsdAutoHideTimeout,
        ["osdTextSize"] = settings => settings.OsdTextSize,
    };
    public async Task SaveAsync(PlayerSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Media.Validate();
        if (!double.IsFinite(settings.VolumePercent) || settings.VolumePercent < 0 || settings.VolumePercent > settings.Media.VolumeLimit ||
            !double.IsFinite(settings.OscHorizontal) || settings.OscHorizontal is < 0 or > 1 ||
            !double.IsFinite(settings.OscBottom) || settings.OscBottom is < 0 or > 1 ||
            !Enum.IsDefined(settings.OscPosition) || !Enum.IsDefined(settings.ResizeWindowTiming) || !Enum.IsDefined(settings.ResizeWindowOption) ||
            settings.LeadingSidebarWidth is < 300 or > 500 || settings.TrailingSidebarWidth is < 300 or > 500 ||
            !InterfacePreferenceLimits.ValidSeconds(settings.ControlBarAutoHideTimeout) || !InterfacePreferenceLimits.ValidSeconds(settings.OsdAutoHideTimeout) ||
            !InterfacePreferenceLimits.ValidTextSize(settings.OsdTextSize))
            throw new ArgumentOutOfRangeException(nameof(settings));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var previous = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (previous is not null) _ = Parse(previous.RootElement); // Never overwrite corrupt/future data.
            if (Recovered && settings.ControlBarAutoHideTimeout <= 0)
            { settings = settings with { ControlBarAutoHideTimeout = 2.5 }; RecoveryAdjustedAutoHide = true; }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                foreach (var (key, read) in Fields)
                {
                    writer.WritePropertyName(key);
                    if (key == "media") WriteMedia(writer, settings.Media, previous?.RootElement);
                    else JsonSerializer.Serialize(writer, read(settings));
                }
                if (previous is not null)
                    foreach (var property in previous.RootElement.EnumerateObject())
                        if (property.Name != "schemaVersion" && !Fields.ContainsKey(property.Name)) property.WriteTo(writer);
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path) && !Recovered) File.Copy(path, path + ".backup", overwrite: true);
            File.Move(temporary, path, overwrite: true);
            Recovered = false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }
    private static void WriteMedia(Utf8JsonWriter writer, MediaPreferences media, JsonElement? previous)
    {
        var current = JsonSerializer.SerializeToElement(media);
        writer.WriteStartObject();
        foreach (var property in current.EnumerateObject()) property.WriteTo(writer);
        if (previous is { } root && root.TryGetProperty("media", out var old) && old.ValueKind == JsonValueKind.Object)
            foreach (var property in old.EnumerateObject())
                if (!current.TryGetProperty(property.Name, out _)) property.WriteTo(writer);
        writer.WriteEndObject();
    }

}
