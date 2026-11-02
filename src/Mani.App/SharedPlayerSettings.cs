// IINA Preference per-key writes/notifications and initial softVolume semantics.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
// Windows adaptation: one ordered JSON writer, preserving edits from each window.
using Mani.Playback;

namespace Mani_App;

internal sealed class SharedPlayerSettings(PlayerSettingsStore store, TimeSpan? writeDelay = null)
{
    private Task<PlayerSettings>? _load;
    private Task _write = Task.CompletedTask;
    private PlayerSettings? _pending;
    private CancellationTokenSource? _delay;
    private bool _flushRequested;
    public PlayerSettings Current { get; private set; } = new();
    public bool Recovered => store.Recovered;
    public bool RecoveryAdjustedAutoHide => store.RecoveryAdjustedAutoHide;
    public event Action<PlayerSettings>? Changed;
    public async Task<PlayerSettings> LoadAsync()
    {
        // Share in-flight initialization. A later window can retry after repair;
        // unreadable/future preferences never become writable default settings.
        var load = _load ??= InitializeAsync();
        try { await load; }
        catch { if (ReferenceEquals(_load, load)) _load = null; throw; }
        return Current;
    }
    private async Task<PlayerSettings> InitializeAsync() => Current = await store.LoadAsync();
    public Task UpdateAsync(PlayerSettings baseline, PlayerSettings edited)
    {
        if (_load?.IsCompletedSuccessfully != true) throw new InvalidOperationException("Settings have not loaded.");
        var next = Merge(Current, baseline, edited);
        if (next == Current && _pending is null) return _write;
        Current = next;
        _pending = next;
        // Publish immediately; coalesce disk writes across every window.
        if (_write.IsCompleted) _write = WritePendingAsync();
        var write = _write;
        Changed?.Invoke(next);
        return write;
    }
    public Task FlushAsync()
    {
        _flushRequested = true;
        _delay?.Cancel();
        if (_write.IsCompleted && _pending is not null) _write = WritePendingAsync();
        return _write;
    }
    private async Task WritePendingAsync()
    {
        while (_pending is not null)
        {
            using (var delay = _delay = new CancellationTokenSource())
            {
                try { if (!_flushRequested) await Task.Delay(writeDelay ?? TimeSpan.FromMilliseconds(750), delay.Token); }
                catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
                finally { _delay = null; }
            }
            var settings = _pending; _pending = null; _flushRequested = false;
            try { await store.SaveAsync(settings!); }
            catch { _pending ??= settings; throw; }
        }
    }
    internal static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        System.Text.Json.JsonException or InvalidOperationException or FormatException;
    public static PlayerSettings Merge(PlayerSettings current, PlayerSettings baseline, PlayerSettings edited)
    {
        T Pick<T>(T now, T before, T after) => EqualityComparer<T>.Default.Equals(before, after) ? now : after;
        return new(
            Pick(current.VolumePercent, baseline.VolumePercent, edited.VolumePercent),
            Pick(current.Muted, baseline.Muted, edited.Muted),
            Pick(current.PauseWhenOpen, baseline.PauseWhenOpen, edited.PauseWhenOpen),
            Pick(current.OscHorizontal, baseline.OscHorizontal, edited.OscHorizontal),
            Pick(current.OscBottom, baseline.OscBottom, edited.OscBottom),
            Pick(current.OscPosition, baseline.OscPosition, edited.OscPosition),
            Pick(current.EdgeToEdgeVideo, baseline.EdgeToEdgeVideo, edited.EdgeToEdgeVideo),
            Pick(current.DockedControls, baseline.DockedControls, edited.DockedControls),
            Pick(current.UnlockWindowAspectRatio, baseline.UnlockWindowAspectRatio, edited.UnlockWindowAspectRatio),
            Pick(current.UsePhysicalResolution, baseline.UsePhysicalResolution, edited.UsePhysicalResolution),
            Pick(current.ResizeWindowTiming, baseline.ResizeWindowTiming, edited.ResizeWindowTiming),
            Pick(current.ResizeWindowOption, baseline.ResizeWindowOption, edited.ResizeWindowOption),
            Pick(current.SidebarSettingsAtLeading, baseline.SidebarSettingsAtLeading, edited.SidebarSettingsAtLeading),
            Pick(current.LeadingSidebarWidth, baseline.LeadingSidebarWidth, edited.LeadingSidebarWidth),
            Pick(current.TrailingSidebarWidth, baseline.TrailingSidebarWidth, edited.TrailingSidebarWidth),
            Pick(current.AlwaysFloatOnTop, baseline.AlwaysFloatOnTop, edited.AlwaysFloatOnTop),
            Pick(current.AutoSwitchToMusicMode, baseline.AutoSwitchToMusicMode, edited.AutoSwitchToMusicMode),
            Pick(current.MusicModeShowAlbumArt, baseline.MusicModeShowAlbumArt, edited.MusicModeShowAlbumArt),
            Pick(current.EnableControlBarAutoHide, baseline.EnableControlBarAutoHide, edited.EnableControlBarAutoHide),
            Pick(current.ControlBarAutoHideTimeout, baseline.ControlBarAutoHideTimeout, edited.ControlBarAutoHideTimeout),
            Pick(current.EnableOsd, baseline.EnableOsd, edited.EnableOsd),
            Pick(current.DisableOsdFileStart, baseline.DisableOsdFileStart, edited.DisableOsdFileStart),
            Pick(current.DisableOsdPauseResume, baseline.DisableOsdPauseResume, edited.DisableOsdPauseResume),
            Pick(current.DisableOsdSeek, baseline.DisableOsdSeek, edited.DisableOsdSeek),
            Pick(current.DisableOsdSpeed, baseline.DisableOsdSpeed, edited.DisableOsdSpeed),
            Pick(current.OsdAutoHideTimeout, baseline.OsdAutoHideTimeout, edited.OsdAutoHideTimeout),
            Pick(current.OsdTextSize, baseline.OsdTextSize, edited.OsdTextSize),
            Pick(current.Language, baseline.Language, edited.Language),
            Pick(current.RecordHistory, baseline.RecordHistory, edited.RecordHistory),
            Pick(current.ResumePosition, baseline.ResumePosition, edited.ResumePosition),
            Pick(current.ShowChapterPositions, baseline.ShowChapterPositions, edited.ShowChapterPositions),
            Pick(current.EnableThumbnailPreview, baseline.EnableThumbnailPreview, edited.EnableThumbnailPreview))
        { Media = MediaPreferences.Merge(current.Media, baseline.Media, edited.Media) };
    }
    // Running sessions retain their volume/mute, while new ones read Current.
    public static PlayerSettings ForExistingWindow(PlayerSettings shared, PlayerSettings baseline, PlayerSettings local)
        => Merge(shared, baseline, local) with { VolumePercent = local.VolumePercent, Muted = local.Muted };
}
