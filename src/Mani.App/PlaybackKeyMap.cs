// IINA 1.5.1/build 180 config/iina-default-input.conf and responder scope port.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

[Flags]
internal enum PlaybackModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Windows = 8 }
internal enum PlaybackKeyAction { Seek, Volume, Frame, SpeedMultiply, SpeedReset, Pause, Mute, Fullscreen, EnterFullscreen, Stop, Panel, AbLoop, FileLoop, Screenshot, WindowSize, WindowStep, Property, SubtitleSeek, ShowProgress, PictureInPicture, OnTop, Quit, RevealFile, Playlist, Chapter, Edition }
internal readonly record struct PlaybackKeyBinding(PlaybackKeyAction Action, string Value = "");

internal static class PlaybackKeyMap
{
    // Windows virtual-key values; OEM punctuation denotes the bracket, slash,
    // comma and period keys. Meta becomes Ctrl only for these audited bindings.
    public static PlaybackKeyBinding? Resolve(int key, PlaybackModifiers modifiers, bool media,
        bool ownsInput = false, bool navigationControl = false, bool compact = false, bool pageNavigationControl = false)
    {
        if (ownsInput) return null;
        // IINA routes quit through application shutdown, including idle windows.
        if (key == 81 && modifiers == PlaybackModifiers.None) return new(PlaybackKeyAction.Quit);
        if (!media || (navigationControl && key is 13 or >= 37 and <= 40) ||
            (pageNavigationControl && key is 33 or 34)) return null;
        PlaybackKeyBinding? binding = (key, modifiers) switch
        {
            (37, PlaybackModifiers.Control) => new(PlaybackKeyAction.Playlist, "-1"),
            (39, PlaybackModifiers.Control) => new(PlaybackKeyAction.Playlist, "1"),
            (80, PlaybackModifiers.Control | PlaybackModifiers.Shift) => new(PlaybackKeyAction.Panel, "Playlist"),
            (67, PlaybackModifiers.Control | PlaybackModifiers.Shift) => new(PlaybackKeyAction.Panel, "Chapters"),
            (188, PlaybackModifiers.Control | PlaybackModifiers.Shift) => new(PlaybackKeyAction.Chapter, "-1"),
            (190, PlaybackModifiers.Control | PlaybackModifiers.Shift) => new(PlaybackKeyAction.Chapter, "1"),
            (69, PlaybackModifiers.Shift) => new(PlaybackKeyAction.Edition),
            (71, PlaybackModifiers.Shift) => new(PlaybackKeyAction.Property, "add sub-scale 0.1"),
            (70, PlaybackModifiers.Shift) => new(PlaybackKeyAction.Property, "add sub-scale -0.1"),
            (82, PlaybackModifiers.None) => new(PlaybackKeyAction.Property, "add sub-pos -1"),
            (82, PlaybackModifiers.Shift) or (84, PlaybackModifiers.None) => new(PlaybackKeyAction.Property, "add sub-pos 1"),
            (37, PlaybackModifiers.None) => new(PlaybackKeyAction.Seek, "-5"),
            (39, PlaybackModifiers.None) => new(PlaybackKeyAction.Seek, "5"),
            (37, PlaybackModifiers.Shift) => new(PlaybackKeyAction.SubtitleSeek, "-1"),
            (39, PlaybackModifiers.Shift) => new(PlaybackKeyAction.SubtitleSeek, "1"),
            (33, PlaybackModifiers.Shift) => new(PlaybackKeyAction.Seek, "600"),
            (34, PlaybackModifiers.Shift) => new(PlaybackKeyAction.Seek, "-600"),
            (79, PlaybackModifiers.None) or (80, PlaybackModifiers.Shift) => new(PlaybackKeyAction.ShowProgress),
            (38, PlaybackModifiers.None) => new(PlaybackKeyAction.Volume, "5"),
            (40, PlaybackModifiers.None) => new(PlaybackKeyAction.Volume, "-5"),
            (38, PlaybackModifiers.Alt) => new(PlaybackKeyAction.Volume, "1"),
            (40, PlaybackModifiers.Alt) => new(PlaybackKeyAction.Volume, "-1"),
            (37, PlaybackModifiers.Alt) or (188, PlaybackModifiers.None) => new(PlaybackKeyAction.Frame, "back"),
            (39, PlaybackModifiers.Alt) or (190, PlaybackModifiers.None) => new(PlaybackKeyAction.Frame),
            (219, PlaybackModifiers.Control) => new(PlaybackKeyAction.SpeedMultiply, "0.5"),
            (221, PlaybackModifiers.Control) => new(PlaybackKeyAction.SpeedMultiply, "2"),
            (219, PlaybackModifiers.Control | PlaybackModifiers.Alt) => new(PlaybackKeyAction.SpeedMultiply, "1/1.1"),
            (221, PlaybackModifiers.Control | PlaybackModifiers.Alt) => new(PlaybackKeyAction.SpeedMultiply, "1.1"),
            (220, PlaybackModifiers.Control) => new(PlaybackKeyAction.SpeedReset),
            (80, PlaybackModifiers.None) => new(PlaybackKeyAction.Pause),
            (77, PlaybackModifiers.None) or (191, PlaybackModifiers.Control) => new(PlaybackKeyAction.Mute),
            (70, PlaybackModifiers.None) => new(PlaybackKeyAction.Fullscreen),
            // Audited Windows mapping for Ctrl+Meta; do not collapse both to Ctrl.
            (70, PlaybackModifiers.Control | PlaybackModifiers.Alt) => new(PlaybackKeyAction.Fullscreen),
            (80, PlaybackModifiers.Control | PlaybackModifiers.Alt) => new(PlaybackKeyAction.PictureInPicture),
            (84, PlaybackModifiers.Control | PlaybackModifiers.Alt) => new(PlaybackKeyAction.OnTop),
            (86, PlaybackModifiers.Control | PlaybackModifiers.Alt) => new(PlaybackKeyAction.Property, "cycle video"),
            (65, PlaybackModifiers.Control | PlaybackModifiers.Alt) => new(PlaybackKeyAction.Property, "cycle audio"),
            (83, PlaybackModifiers.Control | PlaybackModifiers.Alt) => new(PlaybackKeyAction.Property, "cycle sub"),
            (82, PlaybackModifiers.Control | PlaybackModifiers.Shift) => new(PlaybackKeyAction.RevealFile),
            (13, PlaybackModifiers.None) => new(PlaybackKeyAction.EnterFullscreen),
            (190, PlaybackModifiers.Control) => new(PlaybackKeyAction.Stop),
            (76, PlaybackModifiers.Control) => new(PlaybackKeyAction.AbLoop),
            (76, PlaybackModifiers.Control | PlaybackModifiers.Shift) => new(PlaybackKeyAction.FileLoop),
            (83, PlaybackModifiers.Control) => new(PlaybackKeyAction.Screenshot),
            (48, PlaybackModifiers.Control) => new(PlaybackKeyAction.WindowSize, "Half"),
            (49, PlaybackModifiers.Control) => new(PlaybackKeyAction.WindowSize, "Normal"),
            (50, PlaybackModifiers.Control) => new(PlaybackKeyAction.WindowSize, "Double"),
            (51, PlaybackModifiers.Control) => new(PlaybackKeyAction.WindowSize, "FitScreen"),
            (189, PlaybackModifiers.Control) => new(PlaybackKeyAction.WindowStep, "-1"),
            (187, PlaybackModifiers.Control) => new(PlaybackKeyAction.WindowStep, "1"),
            // IINA normalizes uppercase Z/X/C and shifted punctuation to the
            // corresponding Shift chords. Keep native mpv add/set/cycle semantics.
            (57, PlaybackModifiers.Shift) => new(PlaybackKeyAction.Property, "add audio-delay 0.5"),
            (48, PlaybackModifiers.Shift) => new(PlaybackKeyAction.Property, "add audio-delay -0.5"),
            (57, PlaybackModifiers.Alt | PlaybackModifiers.Shift) => new(PlaybackKeyAction.Property, "add audio-delay 0.1"),
            (48, PlaybackModifiers.Alt | PlaybackModifiers.Shift) => new(PlaybackKeyAction.Property, "add audio-delay -0.1"),
            (189, PlaybackModifiers.Shift) => new(PlaybackKeyAction.Property, "set audio-delay 0"),
            (90, PlaybackModifiers.Shift) => new(PlaybackKeyAction.Property, "add sub-delay -0.5"),
            (88, PlaybackModifiers.Shift) => new(PlaybackKeyAction.Property, "add sub-delay 0.5"),
            (90, PlaybackModifiers.Alt | PlaybackModifiers.Shift) => new(PlaybackKeyAction.Property, "add sub-delay -0.1"),
            (88, PlaybackModifiers.Alt | PlaybackModifiers.Shift) => new(PlaybackKeyAction.Property, "add sub-delay 0.1"),
            (67, PlaybackModifiers.Shift) => new(PlaybackKeyAction.Property, "set sub-delay 0"),
            (86, PlaybackModifiers.None) => new(PlaybackKeyAction.Property, "cycle sub-visibility"),
            (86, PlaybackModifiers.Alt) => new(PlaybackKeyAction.Property, "cycle secondary-sub-visibility"),
            (86, PlaybackModifiers.Control | PlaybackModifiers.Shift) => new(PlaybackKeyAction.Panel, "Video"),
            (65, PlaybackModifiers.Control | PlaybackModifiers.Shift) => new(PlaybackKeyAction.Panel, "Audio"),
            (83, PlaybackModifiers.Control | PlaybackModifiers.Shift) => new(PlaybackKeyAction.Panel, "Subtitles"),
            _ => null
        };
        return compact && binding?.Action is PlaybackKeyAction.Panel or PlaybackKeyAction.WindowSize or PlaybackKeyAction.WindowStep ? null : binding;
    }
}
