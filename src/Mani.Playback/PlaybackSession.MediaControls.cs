// IINA 1.5.1/build 180 PlayerCore external tracks, audio devices and equalizer port.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;

namespace Mani.Playback;

public enum ExternalTrackKind { Audio, PrimarySubtitle, SecondarySubtitle }
public sealed record AudioDevice(string Name, string Description);
public sealed record AudioOutputState(IReadOnlyList<AudioDevice> Devices, string Selected, string Driver,
    string OutputFormat, string Decoder, string PassthroughFormats, bool Paused, bool FellBackToDefault, bool RecoveryFailed = false);
public sealed record MediaPreferenceResult(IReadOnlyDictionary<string, string> Actual, IReadOnlyList<string> Unsupported);

public sealed partial class PlaybackSession
{
    private readonly SemaphoreSlim _trackEdits = new(1, 1), _mediaPreferenceEdits = new(1, 1);
    private readonly Dictionary<string, string> _appliedMediaPreferences = new(StringComparer.Ordinal);
    private bool _audioDevicesDirty = true, _audioDeviceFallback, _audioRecoveryFailed;
    private long _nextAudioDeviceCheck;

    private AudioDevice[] ReadAudioDevices() => (_client.Node("audio-device-list") as IReadOnlyList<object?> ?? [])
        .OfType<IReadOnlyDictionary<string, object?>>()
        .Where(d => d.GetValueOrDefault("name") is string)
        .Select(d => new AudioDevice((string)d["name"]!, d.GetValueOrDefault("description") as string ?? (string)d["name"]!)).ToArray();

    private void ReconcileAudioDevice()
    {
        if (!_audioDevicesDirty && Environment.TickCount64 < _nextAudioDeviceCheck) return;
        _audioDevicesDirty = false;
        _nextAudioDeviceCheck = Environment.TickCount64 + 2000;
        try
        {
            string selected = Get("audio-device");
            if (selected is "" or "auto") { _audioRecoveryFailed = false; return; }
            var devices = ReadAudioDevices();
            if (devices.Length == 0 || devices.Any(d => d.Name == selected)) return;
            // IINA falls back to automatic selection. Never issue pause=no on recovery;
            // mpv/WASAPI owns default endpoint changes and paused device reconnection.
            SetNative("audio-device", "auto");
            _audioDeviceFallback = true; _audioRecoveryFailed = false;
            Journal("audio-device-fallback", new { paused = Flag("pause") });
        }
        catch (InvalidOperationException error)
        {
            // A temporarily unavailable endpoint must not terminate the player worker.
            if (!_audioRecoveryFailed) Journal("audio-device-retry", new { error = error.GetType().Name });
            _audioRecoveryFailed = true;
        }
    }
    public Task<AudioOutputState> GetAudioOutputAsync() => Invoke(() =>
    {
        ReconcileAudioDevice();
        return new AudioOutputState(ReadAudioDevices(), Get("audio-device"), Get("current-ao"),
            Get("audio-out-params/format"), Get("audio-codec-name"), Get("audio-spdif"), Flag("pause"), _audioDeviceFallback, _audioRecoveryFailed);
    });

    // Serialized per-key updates preserve other filters and avoid reordered EQ/device writes.
    public async Task<MediaPreferenceResult> ApplyMediaPreferencesAsync(MediaPreferences preferences, CancellationToken token = default)
    {
        preferences.Validate();
        await _mediaPreferenceEdits.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var actual = new Dictionary<string, string>();
            var unsupported = new List<string>();
            foreach (var field in MediaPreferences.Fields.Where(f => f.Property.Length > 0))
            {
                token.ThrowIfCancellationRequested();
                if (_appliedMediaPreferences.GetValueOrDefault(field.Key) == preferences.Get(field.Key)) continue;
                try
                {
                    string requested = preferences.Get(field.Key);
                    // Resolve only the generic default, on the session worker. Cache the
                    // requested preference below so unrelated edits do not reset subtitles.
                    string value = field.Key == "Font"
                        ? await Invoke(() => SubtitleFontFallback.Resolve(requested)).ConfigureAwait(false)
                        : requested;
                    await SetAsync(field.Property, value).ConfigureAwait(false);
                    actual[field.Key] = (await GetPropertiesAsync(field.Property).ConfigureAwait(false))[field.Property];
                    if (field.Key == "Letterbox") await SetAsync("sub-ass-force-margins", preferences.Letterbox).ConfigureAwait(false);
                    if (field.Key == "AudioDevice") await RefreshSelectedAudioDeviceAsync().ConfigureAwait(false);
                    _appliedMediaPreferences[field.Key] = preferences.Get(field.Key);
                }
                catch (InvalidOperationException) when (IsAvailable) { _appliedMediaPreferences.Remove(field.Key); unsupported.Add(field.Key); }
            }
            if (_appliedMediaPreferences.GetValueOrDefault("audio-spdif") != preferences.Spdif)
            {
                try
                {
                    await SetAsync("audio-spdif", preferences.Spdif).ConfigureAwait(false);
                    actual["audio-spdif"] = (await GetPropertiesAsync("audio-spdif").ConfigureAwait(false))["audio-spdif"];
                    _appliedMediaPreferences["audio-spdif"] = preferences.Spdif;
                }
                catch (InvalidOperationException) when (IsAvailable)
                {
                    _appliedMediaPreferences.Remove("audio-spdif");
                    unsupported.AddRange(MediaPreferences.Fields.Where(f => f.Key.StartsWith("Spdif", StringComparison.Ordinal)).Select(f => f.Key));
                }
            }
            string equalizer = preferences.EqualizerEnabled == "yes" ? preferences.EqualizerFilter : "";
            if (_appliedMediaPreferences.GetValueOrDefault("audio-equalizer") != equalizer)
            {
                try
                {
                    if (equalizer.Length > 0) await CommandAsync("af", "add", equalizer).ConfigureAwait(false);
                    else if (await Invoke(() => (_client.Node("af") as IReadOnlyList<object?> ?? [])
                        .OfType<IReadOnlyDictionary<string, object?>>().Any(f => f.GetValueOrDefault("label") as string == "mani-audio-eq")).ConfigureAwait(false))
                        await CommandAsync("af", "remove", "@mani-audio-eq").ConfigureAwait(false);
                    _appliedMediaPreferences["audio-equalizer"] = equalizer;
                }
                catch (InvalidOperationException) when (IsAvailable)
                {
                    _appliedMediaPreferences.Remove("audio-equalizer");
                    unsupported.AddRange(MediaPreferences.Fields.Where(f => f.Group == "Equalizer").Select(f => f.Key));
                }
            }
            await Invoke(() =>
            {
                SetNative("sub-auto", "no"); // AutoFileMatcher owns discovery, as in IINA.
                SetNative("audio-file-auto", "no");
                if (Number("volume") > preferences.VolumeLimit) SetNative("volume", preferences.MaximumVolume);
                _audioDevicesDirty = true; ReconcileAudioDevice();
                return true;
            }).ConfigureAwait(false);
            return new(actual, unsupported);
        }
        finally { _mediaPreferenceEdits.Release(); }
    }

    private Task<bool> RefreshSelectedAudioDeviceAsync() => Invoke(() =>
    {
        _audioDeviceFallback = false; _audioRecoveryFailed = false;
        _audioDevicesDirty = true; ReconcileAudioDevice();
        return true;
    });
    // Explicit selection is allowed even when the saved preference has not changed.
    // A missing device remains on auto until the user selects an available endpoint again.
    public async Task SelectAudioDeviceAsync(string name, CancellationToken token = default)
    {
        MediaPreferences.Fields.First(f => f.Key == "AudioDevice").Validate(name);
        await _mediaPreferenceEdits.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if ((await GetPropertiesAsync("audio-device").ConfigureAwait(false))["audio-device"] != name)
                await SetAsync("audio-device", name).ConfigureAwait(false);
            await RefreshSelectedAudioDeviceAsync().ConfigureAwait(false);
        }
        finally { _mediaPreferenceEdits.Release(); }
    }

    public async Task<MediaInfo> SelectAutomaticSubtitleAsync(long request, long entry, CancellationToken token = default)
    {
        await _trackEdits.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var before = await GetMediaInfoAsync(request, entry).ConfigureAwait(false);
            if (before.SubtitleTrack is not ("no" or "0")) return before;
            // With no selected subtitle, the option can still be "auto". Rewriting
            // the same option does not rerun mpv's selection after sub-add. Release
            // it first, then ask the engine to choose again using its language rules.
            await Invoke(() =>
            {
                token.ThrowIfCancellationRequested(); RequireMedia(request, entry);
                return SendCommand(["set", "sid", "no"]);
            }).Unwrap().ConfigureAwait(false);
            await Invoke(() =>
            {
                token.ThrowIfCancellationRequested(); RequireMedia(request, entry);
                return SendCommand(["set", "sid", "auto"]);
            }).Unwrap().ConfigureAwait(false);
            var after = await GetMediaInfoAsync(request, entry).ConfigureAwait(false);
            if (after.SecondarySubtitleTrack != before.SecondarySubtitleTrack)
            {
                // Preserve a manually assigned secondary track if auto selection would take it.
                if (after.SubtitleTrack == before.SecondarySubtitleTrack)
                    await CommandForMediaAsync(request, entry, "set", "sid", "no").ConfigureAwait(false);
                await CommandForMediaAsync(request, entry, "set", "secondary-sid", before.SecondarySubtitleTrack).ConfigureAwait(false);
            }
            var selected = await GetMediaInfoAsync(request, entry).ConfigureAwait(false);
            // PlayerCore.fileLoaded selects the first matched subtitle when no
            // language preference wins. Keep that fallback, excluding the user's
            // secondary selection; mpv owns language equivalence and ranking.
            if (selected.SubtitleTrack is "no" or "0" && selected.Tracks.FirstOrDefault(t =>
                t.Type == "sub" && t.Id.ToString(CultureInfo.InvariantCulture) != selected.SecondarySubtitleTrack) is { } fallback)
            {
                await Invoke(() =>
                {
                    token.ThrowIfCancellationRequested(); RequireMedia(request, entry);
                    return SendCommand(["set", "sid", fallback.Id.ToString(CultureInfo.InvariantCulture)]);
                }).Unwrap().ConfigureAwait(false);
            }
            return await GetMediaInfoAsync(request, entry).ConfigureAwait(false);
        }
        finally { _trackEdits.Release(); }
    }

    public Task CommandForMediaAsync(long request, long entry, params string[] command)
    {
        command = (string[])command.Clone();
        return Invoke(() => { RequireMedia(request, entry); return SendCommand(command); }).Unwrap();
    }

    public Task<IReadOnlyDictionary<string, string>> GetSubtitleChannelValuesAsync(long request, long entry) => Invoke<IReadOnlyDictionary<string, string>>(() =>
    {
        RequireMedia(request, entry);
        var values = MediaPreferences.SubtitleChannelFields.ToDictionary(f => f.Property, f => Get(f.Property));
        RequireMedia(request, entry);
        return values;
    });

    public async Task TrackCommandForMediaAsync(long request, long entry, string[] command, CancellationToken token = default)
    {
        command = (string[])command.Clone();
        await _trackEdits.WaitAsync(token).ConfigureAwait(false);
        try { await Invoke(() => { token.ThrowIfCancellationRequested(); RequireMedia(request, entry); return SendCommand(command); }).Unwrap().ConfigureAwait(false); }
        finally { _trackEdits.Release(); }
    }

    public async Task SelectTrackAsync(MediaInfo before, string property, long id, CancellationToken token = default)
    {
        string type = property switch { "sid" or "secondary-sid" => "sub", "aid" => "audio", "vid" => "video", _ => throw new ArgumentException("Invalid track property.", nameof(property)) };
        var original = before.Tracks.FirstOrDefault(t => t.Type == type && t.Id == id);
        if (id != 0 && original is null) throw new ArgumentException("Track was not present.", nameof(id));
        await _trackEdits.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var actual = await GetMediaInfoAsync(before.RequestId, before.PlaylistEntryId).ConfigureAwait(false);
            var selected = original?.ExternalFilename is { } file ? actual.Tracks.FirstOrDefault(t => t.Type == type && t.ExternalFilename is { } path && SameExternalFile(file, path)) : actual.Tracks.FirstOrDefault(t => t.Type == type && t.Id == id);
            if (id != 0 && selected is null) throw new InvalidOperationException("Track is no longer available.");
            await Invoke(() => { token.ThrowIfCancellationRequested(); RequireMedia(before.RequestId, before.PlaylistEntryId); return SendCommand(["set", property, id == 0 ? "no" : selected!.Id.ToString(CultureInfo.InvariantCulture)]); }).Unwrap().ConfigureAwait(false);
        }
        finally { _trackEdits.Release(); }
    }

    public async Task<MediaInfo> LoadExternalTrackAsync(long request, long entry, string path,
        ExternalTrackKind kind, CancellationToken token = default, bool select = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        if (!await Task.Run(() => File.Exists(path), token).ConfigureAwait(false)) throw new FileNotFoundException("External track not found.", path);
        await _trackEdits.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Invoke(() => { token.ThrowIfCancellationRequested(); RequireMedia(request, entry); return true; }).ConfigureAwait(false);
            var before = await GetMediaInfoAsync(request, entry).ConfigureAwait(false);
            string type = kind == ExternalTrackKind.Audio ? "audio" : "sub";
            var duplicate = before.Tracks.FirstOrDefault(t => t.Type == type && t.ExternalFilename is { } file && SameExternalFile(file, path));
            string prefix = type == "audio" ? "audio" : "sub";
            await CommandForMediaAsync(request, entry, duplicate is null ? [prefix + "-add", path, "auto"] :
                [prefix + "-reload", duplicate.Id.ToString(CultureInfo.InvariantCulture)]).ConfigureAwait(false);
            await Invoke(() => { RequireMedia(request, entry); return true; }).ConfigureAwait(false);
            var loaded = await GetMediaInfoAsync(request, entry).ConfigureAwait(false);
            await RestoreSubtitleSelectionsAsync(request, entry, before, loaded).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var track = loaded.Tracks.LastOrDefault(t => t.Type == type && t.ExternalFilename is { } file && SameExternalFile(file, path));
            if (track is null) throw new InvalidOperationException("External track was not loaded.");
            if (select)
                await CommandForMediaAsync(request, entry, "set", kind switch
                { ExternalTrackKind.Audio => "aid", ExternalTrackKind.PrimarySubtitle => "sid", _ => "secondary-sid" }, track.Id.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await Invoke(() => { RequireMedia(request, entry); return true; }).ConfigureAwait(false);
            return await GetMediaInfoAsync(request, entry).ConfigureAwait(false);
        }
        finally { _trackEdits.Release(); }
    }
    public async Task<MediaInfo> ReloadSubtitlesAsync(long request, long entry, string encoding, CancellationToken token = default)
    {
        MediaPreferences.Fields.First(f => f.Key == "SubtitleEncoding").Validate(encoding);
        await _trackEdits.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await CommandForMediaAsync(request, entry, "set", "sub-codepage", encoding).ConfigureAwait(false);
            var before = await GetMediaInfoAsync(request, entry).ConfigureAwait(false);
            List<Exception> errors = [];
            foreach (var track in before.Tracks.Where(t => t.Type == "sub" && t.External))
            {
                token.ThrowIfCancellationRequested();
                try { await CommandForMediaAsync(request, entry, "sub-reload", track.Id.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false); }
                catch (InvalidOperationException error) { errors.Add(error); }
            }
            await Invoke(() => { RequireMedia(request, entry); return true; }).ConfigureAwait(false);
            var actual = await GetMediaInfoAsync(request, entry).ConfigureAwait(false);
            await RestoreSubtitleSelectionsAsync(request, entry, before, actual).ConfigureAwait(false);
            if (errors.Count > 0) throw new InvalidOperationException("Some external subtitles could not be reloaded. Choose the file again.", errors[0]);
            return await GetMediaInfoAsync(request, entry).ConfigureAwait(false);
        }
        finally { _trackEdits.Release(); }
    }
    private static bool SameExternalFile(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private async Task RestoreSubtitleSelectionsAsync(long request, long entry, MediaInfo before, MediaInfo after)
    {
        string Resolve(string selected)
        {
            if (!long.TryParse(selected, out long id)) return "no";
            var old = before.Tracks.FirstOrDefault(t => t.Type == "sub" && t.Id == id);
            var found = old?.ExternalFilename is { } file ? after.Tracks.FirstOrDefault(t => t.Type == "sub" && t.ExternalFilename is { } other && SameExternalFile(other, file)) :
                after.Tracks.FirstOrDefault(t => t.Type == "sub" && t.Id == id);
            return found?.Id.ToString(CultureInfo.InvariantCulture) ?? "no";
        }
        string primary = Resolve(before.SubtitleTrack), secondary = Resolve(before.SecondarySubtitleTrack);
        if (after.SubtitleTrack == primary && after.SecondarySubtitleTrack == secondary) return;
        // Releasing both selections prevents a temporary ID swap from becoming a conflict.
        await CommandForMediaAsync(request, entry, "set", "sid", "no").ConfigureAwait(false);
        await CommandForMediaAsync(request, entry, "set", "secondary-sid", "no").ConfigureAwait(false);
        await CommandForMediaAsync(request, entry, "set", "sid", primary).ConfigureAwait(false);
        await CommandForMediaAsync(request, entry, "set", "secondary-sid", secondary).ConfigureAwait(false);
    }
}
