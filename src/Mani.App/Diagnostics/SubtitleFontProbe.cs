#if MANI_DIAGNOSTICS
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;

namespace Mani_App.Diagnostics;

// Opt-in evidence collection; uses product preference and track operations.
// Pixel comparisons are performed on the saved, fixed-frame captures.
internal static class SubtitleFontProbe
{
    internal static async Task RunAsync(PlaybackSession session, string source, string directory,
        Func<Task<object?>> captureHost, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var evidence = new List<object>();
        string zh = Path.Combine(directory, "中文.srt"), en = Path.Combine(directory, "English.srt");
        await File.WriteAllTextAsync(zh, "1\n00:00:00,000 --> 00:00:08,000\n请随意遮住你们的眼睛\n简体：发展语言；繁體：發展語言。\n", token);
        await File.WriteAllTextAsync(en, "1\n00:00:00,000 --> 00:00:08,000\nEnglish subtitles: Aa Bb 0123456789\nMusic, punctuation & readability!\n", token);
        string Ass(string text, int align, string color, string font = "Consolas") => $"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 1280
            PlayResY: 720
            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,{font},36,{color},&H000000FF,&H00000000,&H80000000,-1,-1,0,0,100,100,2,0,1,2,1,{align},20,20,24,1
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:08.00,Default,,0,0,0,,{text}
            """;
        string a = Path.Combine(directory, "primary.ass"), b = Path.Combine(directory, "secondary.ass");
        await File.WriteAllTextAsync(a, Ass("请随意遮住你们的眼睛 ASS", 2, "&H0000FFFF", "Microsoft JhengHei"), token);
        await File.WriteAllTextAsync(b, Ass("Styled SECONDARY Bb 4567", 8, "&H00FFFF00"), token);
        var preferences = new MediaPreferences();
        await session.SetAsync("ao", "null");
        await session.SetAsync("msg-level", "all=warn,sub=debug");
        await session.SetAsync("screenshot-format", "png");
        await session.SetAsync("screenshot-high-bit-depth", "no");
        async Task Apply(string font)
        {
            preferences = preferences with { Font = font };
            var result = await session.ApplyMediaPreferencesAsync(preferences, token);
            if (result.Unsupported.Count > 0) throw new InvalidOperationException(string.Join(",", result.Unsupported));
        }
        await Apply("sans-serif");
        string resolved = (await session.GetPropertiesAsync("sub-font"))["sub-font"];
        var ready = await session.LoadAsync(source, true, token);
        async Task Set(string key, string value) => await session.CommandForMediaAsync(ready.RequestId, ready.PlaylistEntryId, "set", key, value);
        async Task Load(string file, ExternalTrackKind kind) => await session.LoadExternalTrackAsync(ready.RequestId, ready.PlaylistEntryId, file, kind, token);
        async Task Capture(string name)
        {
            token.ThrowIfCancellationRequested();
            long restarts = await session.GetPlaybackRestartCountAsync();
            await session.CommandForMediaAsync(ready.RequestId, ready.PlaylistEntryId, "seek", "2", "absolute+exact");
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                var state = await session.ReadAsync();
                if (!state.Seeking && Math.Abs(state.Position - 2) < .08 && await session.GetPlaybackRestartCountAsync() > restarts) break;
                if (timeout.Elapsed.TotalSeconds > 10) throw new TimeoutException("Seek: " + name);
                await Task.Delay(30, token);
            }
            await Task.Delay(350, token);
            object? host = await captureHost();
            await session.CommandAsync("screenshot-to-file", Path.Combine(directory, name + ".png"), "window");
            var properties = await session.GetPropertiesAsync("sub-font", "sid", "secondary-sid", "sub-text", "secondary-sub-text",
                "sub-delay", "secondary-sub-delay", "sub-pos", "secondary-sub-pos", "sub-ass-override", "secondary-sub-ass-override");
            evidence.Add(new { name, properties, host });
            await File.WriteAllTextAsync(Path.Combine(directory, "frames.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), token);
        }
        await Set("sid", "no"); await Set("secondary-sid", "no");
        await Capture("none");
        await Load(zh, ExternalTrackKind.PrimarySubtitle); await Load(en, ExternalTrackKind.SecondarySubtitle);
        await Capture("default-dual");
        await Apply(resolved); await Capture("explicit-resolved-dual");
        await session.SetAsync("sub-font", "sans-serif"); await Capture("old-generic-dual");
        await Apply("sans-serif"); await Capture("restored-default-dual");
        await Set("sub-visibility", "no"); await Capture("english-only");
        await Set("sub-visibility", "yes"); await Set("secondary-sub-visibility", "no"); await Capture("chinese-only");
        await Set("secondary-sub-visibility", "yes");
        await Set("sub-delay", "3"); await Capture("primary-delayed"); await Set("sub-delay", "0");
        await Set("secondary-sub-delay", "3"); await Capture("secondary-delayed"); await Set("secondary-sub-delay", "0");
        await Set("secondary-sub-pos", "30"); await Capture("secondary-position"); await Set("secondary-sub-pos", "0");
        await Set("sub-pos", "65"); await Capture("primary-position"); await Set("sub-pos", "100");
        await session.ReloadSubtitlesAsync(ready.RequestId, ready.PlaylistEntryId, "auto", token); await Capture("reloaded-dual");
        await Set("sid", "no"); await Set("secondary-sid", "no");
        await Load(en, ExternalTrackKind.PrimarySubtitle); await Load(zh, ExternalTrackKind.SecondarySubtitle);
        await Capture("swapped-dual");
        await Set("secondary-sub-visibility", "no");
        await Apply("Arial"); await Capture("explicit-arial");
        await Apply("Consolas"); await session.SetAsync("sub-font", "Arial"); await Capture("raw-arial");
        await Apply("Arial"); await Capture("reselected-arial");
        await Set("secondary-sub-visibility", "yes");
        await Load(a, ExternalTrackKind.PrimarySubtitle); await Load(b, ExternalTrackKind.SecondarySubtitle);
        foreach (string mode in new[] { "no", "scale" })
        {
            preferences = preferences with { PrimaryOverride = mode, SecondaryOverride = mode };
            await Apply("Arial"); await session.SetAsync("sub-font", "sans-serif"); await Capture("ass-" + mode + "-original");
            await Apply("sans-serif"); await Capture("ass-" + mode + "-resolved");
        }
        // Confirm user-requested overrides still apply independently.
        preferences = preferences with { PrimaryOverride = "force", SecondaryOverride = "no" };
        await Apply("sans-serif"); await Capture("ass-primary-force");
        preferences = preferences with { PrimaryOverride = "no", SecondaryOverride = "force" };
        await Apply("sans-serif"); await Capture("ass-secondary-force");
        // Existing local fixture contains an attached Arial font (never distribute it).
        await Set("secondary-sid", "no"); await Set("sid", "3");
        await session.SetAsync("sub-font-provider", "none");
        await Apply("Arial"); await Capture("attached-original");
        await Apply("sans-serif"); await Capture("attached-resolved");
        await session.SetAsync("sub-font-provider", "auto");
        ready = await session.LoadAsync(source, true, token);
        preferences = new(); await Apply("sans-serif");
        await Set("sid", "no"); await Set("secondary-sid", "no");
        await Load(zh, ExternalTrackKind.PrimarySubtitle); await Load(en, ExternalTrackKind.SecondarySubtitle);
        await Capture("reopened-dual");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            completed = true, resolved, frames = evidence.Count,
            source, sourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source, token))),
            scope = "Native properties, track operations and fixed-frame captures; host attachment recorded per frame. Pixel checks separate; user acceptance pending."
        }, new JsonSerializerOptions { WriteIndented = true }), token);
    }
}
#endif
