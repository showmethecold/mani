using Mani_App;

namespace Mani.UnitTests;

public sealed class PlaybackKeyMapTests
{
    [Fact]
    public void RevealFileUsesExactChordAndPreservesInputOwnership()
    {
        var chord = PlaybackModifiers.Control | PlaybackModifiers.Shift;
        var expected = new PlaybackKeyBinding(PlaybackKeyAction.RevealFile);
        Assert.Equal(expected, PlaybackKeyMap.Resolve(82, chord, true));
        Assert.Equal(expected, PlaybackKeyMap.Resolve(82, chord, true, compact: true, navigationControl: true));
        Assert.Null(PlaybackKeyMap.Resolve(82, chord, false));
        Assert.Null(PlaybackKeyMap.Resolve(82, chord, true, ownsInput: true));
        foreach (var modifiers in Enumerable.Range(0, 16).Select(value => (PlaybackModifiers)value).Where(value => value != chord))
            Assert.NotEqual(expected, PlaybackKeyMap.Resolve(82, modifiers, true));
    }
    [Theory]
    [InlineData(86, "cycle video")]
    [InlineData(65, "cycle audio")]
    [InlineData(83, "cycle sub")]
    public void TrackCyclesKeepOriginalCommandsAndExactInputScope(int key, string command)
    {
        var expected = new PlaybackKeyBinding(PlaybackKeyAction.Property, command);
        var chord = PlaybackModifiers.Control | PlaybackModifiers.Alt;
        Assert.Equal(expected, PlaybackKeyMap.Resolve(key, chord, true));
        Assert.Equal(expected, PlaybackKeyMap.Resolve(key, chord, true, compact: true, navigationControl: true));
        Assert.Null(PlaybackKeyMap.Resolve(key, chord, false));
        Assert.Null(PlaybackKeyMap.Resolve(key, chord, true, ownsInput: true));
        foreach (var modifiers in Enumerable.Range(0, 16).Select(value => (PlaybackModifiers)value).Where(value => value != chord))
            Assert.NotEqual(expected, PlaybackKeyMap.Resolve(key, modifiers, true));
    }

    [Fact]
    public void TrackCyclesKeepExistingSidebarAndSubtitleVisibilityBindings()
    {
        foreach (int key in new[] { 86, 65, 83 })
            Assert.Equal(PlaybackKeyAction.Panel, PlaybackKeyMap.Resolve(key, PlaybackModifiers.Control | PlaybackModifiers.Shift, true)!.Value.Action);
        Assert.Equal("cycle sub-visibility", PlaybackKeyMap.Resolve(86, PlaybackModifiers.None, true)!.Value.Value);
        Assert.Equal("cycle secondary-sub-visibility", PlaybackKeyMap.Resolve(86, PlaybackModifiers.Alt, true)!.Value.Value);
    }
    [Theory]
    [InlineData(80, PlaybackKeyAction.PictureInPicture)]
    [InlineData(84, PlaybackKeyAction.OnTop)]
    [InlineData(70, PlaybackKeyAction.Fullscreen)]
    internal void WindowActionsUseAuditedChordsAndKeepEditingScope(int key, PlaybackKeyAction action)
    {
        const PlaybackModifiers chord = PlaybackModifiers.Control | PlaybackModifiers.Alt;
        var expected = new PlaybackKeyBinding(action);
        Assert.Equal(expected, PlaybackKeyMap.Resolve(key, chord, true));
        Assert.Equal(expected, PlaybackKeyMap.Resolve(key, chord, true, navigationControl: true, pageNavigationControl: true));
        Assert.Equal(expected, PlaybackKeyMap.Resolve(key, chord, true, compact: true));
        Assert.Null(PlaybackKeyMap.Resolve(key, chord, false));
        Assert.Null(PlaybackKeyMap.Resolve(key, chord, true, ownsInput: true));
        foreach (var modifier in new[] { PlaybackModifiers.Control, PlaybackModifiers.Alt,
            chord | PlaybackModifiers.Shift, chord | PlaybackModifiers.Windows })
            Assert.Null(PlaybackKeyMap.Resolve(key, modifier, true));
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, false, true)]
    [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)]
    [InlineData(true, true, false)] [InlineData(true, true, true)]
    public void QuitUsesApplicationScopeButNeverCapturesTextOrModifiedQ(bool media, bool navigation, bool compact)
    {
        Assert.Equal(new PlaybackKeyBinding(PlaybackKeyAction.Quit),
            PlaybackKeyMap.Resolve(81, PlaybackModifiers.None, media, navigationControl: navigation, compact: compact));
        Assert.Null(PlaybackKeyMap.Resolve(81, PlaybackModifiers.None, media, ownsInput: true, navigationControl: navigation, compact: compact));
        for (int mask = 1; mask < 16; mask++)
            Assert.Null(PlaybackKeyMap.Resolve(81, (PlaybackModifiers)mask, media, navigationControl: navigation, compact: compact));
    }

    [Fact]
    public void WindowAliasesPreserveExistingPauseMuteFullscreenAndAccelerators()
    {
        Assert.Equal(PlaybackKeyAction.Pause, PlaybackKeyMap.Resolve(80, PlaybackModifiers.None, true)!.Value.Action);
        Assert.Equal(PlaybackKeyAction.Mute, PlaybackKeyMap.Resolve(77, PlaybackModifiers.None, true)!.Value.Action);
        Assert.Equal(PlaybackKeyAction.Fullscreen, PlaybackKeyMap.Resolve(70, PlaybackModifiers.None, true)!.Value.Action);
        Assert.Null(PlaybackKeyMap.Resolve(77, PlaybackModifiers.Control | PlaybackModifiers.Shift, true));
        Assert.Null(PlaybackKeyMap.Resolve(87, PlaybackModifiers.Control, true));
        Assert.Null(PlaybackKeyMap.Resolve(122, PlaybackModifiers.None, true));
    }

    [Theory]
    [InlineData(37, PlaybackModifiers.Shift, PlaybackKeyAction.SubtitleSeek, "-1")]
    [InlineData(39, PlaybackModifiers.Shift, PlaybackKeyAction.SubtitleSeek, "1")]
    [InlineData(33, PlaybackModifiers.Shift, PlaybackKeyAction.Seek, "600")]
    [InlineData(34, PlaybackModifiers.Shift, PlaybackKeyAction.Seek, "-600")]
    [InlineData(79, PlaybackModifiers.None, PlaybackKeyAction.ShowProgress, "")]
    [InlineData(80, PlaybackModifiers.Shift, PlaybackKeyAction.ShowProgress, "")]
    internal void NavigationCommandsKeepExactModifiersAndMediaScope(int key, PlaybackModifiers modifiers, PlaybackKeyAction action, string value)
    {
        var expected = new PlaybackKeyBinding(action, value);
        Assert.Equal(expected, PlaybackKeyMap.Resolve(key, modifiers, true));
        Assert.Equal(expected, PlaybackKeyMap.Resolve(key, modifiers, true, compact: true));
        Assert.Null(PlaybackKeyMap.Resolve(key, modifiers, false));
        Assert.Null(PlaybackKeyMap.Resolve(key, modifiers, true, ownsInput: true));
        foreach (var extra in new[] { PlaybackModifiers.Control, PlaybackModifiers.Alt, PlaybackModifiers.Windows })
            if (key == 80 && modifiers == PlaybackModifiers.Shift && extra == PlaybackModifiers.Control)
                Assert.Equal(new PlaybackKeyBinding(PlaybackKeyAction.Panel, "Playlist"), PlaybackKeyMap.Resolve(key, modifiers | extra, true));
            else Assert.Null(PlaybackKeyMap.Resolve(key, modifiers | extra, true));
        if (key is >= 33 and <= 40)
            Assert.Null(PlaybackKeyMap.Resolve(key, modifiers, true, navigationControl: true, pageNavigationControl: true));
    }

    [Theory]
    [InlineData(33, "600")]
    [InlineData(34, "-600")]
    public void PageSeekWorksOnButtonsButLeavesPagedControlsTheirKeys(int key, string amount)
    {
        var expected = new PlaybackKeyBinding(PlaybackKeyAction.Seek, amount);
        Assert.Equal(expected, PlaybackKeyMap.Resolve(key, PlaybackModifiers.Shift, true, navigationControl: true));
        Assert.Equal(expected, PlaybackKeyMap.Resolve(key, PlaybackModifiers.Shift, true, navigationControl: true, compact: true));
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.Shift, true, pageNavigationControl: true));
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.Shift, true, navigationControl: true, pageNavigationControl: true));
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.Shift, true, ownsInput: true));
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.None, true, navigationControl: true));
    }

    [Fact]
    public void ShowProgressUppercasePDoesNotReplacePauseOrUppercaseO()
    {
        Assert.Equal(PlaybackKeyAction.Pause, PlaybackKeyMap.Resolve(80, PlaybackModifiers.None, true)!.Value.Action);
        Assert.Null(PlaybackKeyMap.Resolve(79, PlaybackModifiers.Shift, true));
        Assert.Null(PlaybackKeyMap.Resolve(33, PlaybackModifiers.None, true));
        Assert.Null(PlaybackKeyMap.Resolve(34, PlaybackModifiers.None, true));
    }

    [Theory]
    [InlineData(57, PlaybackModifiers.Shift, "add audio-delay 0.5")]
    [InlineData(48, PlaybackModifiers.Shift, "add audio-delay -0.5")]
    [InlineData(57, PlaybackModifiers.Alt | PlaybackModifiers.Shift, "add audio-delay 0.1")]
    [InlineData(48, PlaybackModifiers.Alt | PlaybackModifiers.Shift, "add audio-delay -0.1")]
    [InlineData(189, PlaybackModifiers.Shift, "set audio-delay 0")]
    [InlineData(90, PlaybackModifiers.Shift, "add sub-delay -0.5")]
    [InlineData(88, PlaybackModifiers.Shift, "add sub-delay 0.5")]
    [InlineData(90, PlaybackModifiers.Alt | PlaybackModifiers.Shift, "add sub-delay -0.1")]
    [InlineData(88, PlaybackModifiers.Alt | PlaybackModifiers.Shift, "add sub-delay 0.1")]
    [InlineData(67, PlaybackModifiers.Shift, "set sub-delay 0")]
    [InlineData(86, PlaybackModifiers.None, "cycle sub-visibility")]
    [InlineData(86, PlaybackModifiers.Alt, "cycle secondary-sub-visibility")]
    internal void PropertyBindingsKeepSourceCommandsAndTextScope(int key, PlaybackModifiers modifiers, string command)
    {
        Assert.Equal(new PlaybackKeyBinding(PlaybackKeyAction.Property, command), PlaybackKeyMap.Resolve(key, modifiers, true));
        Assert.Equal(command, PlaybackKeyMap.Resolve(key, modifiers, true, compact: true)!.Value.Value);
        Assert.Null(PlaybackKeyMap.Resolve(key, modifiers, false));
        Assert.Null(PlaybackKeyMap.Resolve(key, modifiers, true, ownsInput: true));
        Assert.Null(PlaybackKeyMap.Resolve(key, modifiers | PlaybackModifiers.Windows, true));
        Assert.NotEqual(new PlaybackKeyBinding(PlaybackKeyAction.Property, command), PlaybackKeyMap.Resolve(key, modifiers | PlaybackModifiers.Control, true));
    }
    [Theory]
    [InlineData(90)] [InlineData(88)] [InlineData(67)]
    public void UppercaseSubtitleBindingsDoNotAliasBareOrAltLetters(int key)
    {
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.None, true));
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.Alt, true));
    }

    [Theory]
    [InlineData(37)] [InlineData(39)] [InlineData(38)] [InlineData(40)]
    [InlineData(13)] [InlineData(80)] [InlineData(77)] [InlineData(190)]
    public void UnavailableMediaOrEditingNeverRunsPlayerKeys(int key)
    {
        Assert.NotNull(PlaybackKeyMap.Resolve(key, PlaybackModifiers.None, true));
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.None, false));
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.None, true, ownsInput: true));
    }

    [Theory]
    [InlineData(37)] [InlineData(39)] [InlineData(38)] [InlineData(40)] [InlineData(13)]
    public void ControlsKeepNavigationAndActivation(int key)
    {
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.None, true, navigationControl: true));
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.Alt, true, navigationControl: true));
    }

    [Theory]
    [InlineData(86, "Video")] [InlineData(65, "Audio")] [InlineData(83, "Subtitles")]
    public void PanelChordsRespectEditingAndCompactMode(int key, string page)
    {
        const PlaybackModifiers chord = PlaybackModifiers.Control | PlaybackModifiers.Shift;
        Assert.Equal(new PlaybackKeyBinding(PlaybackKeyAction.Panel, page), PlaybackKeyMap.Resolve(key, chord, true, navigationControl: true));
        Assert.Null(PlaybackKeyMap.Resolve(key, chord, true, ownsInput: true));
        Assert.Null(PlaybackKeyMap.Resolve(key, chord, true, compact: true));
        if (key == 83) Assert.Equal(PlaybackKeyAction.Screenshot, PlaybackKeyMap.Resolve(key, PlaybackModifiers.Control, true)!.Value.Action);
        else Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.Control, true));
    }

    [Fact]
    public void ModifiersDoNotFallThroughToBareBindings()
    {
        foreach (int key in new[] { 37, 38, 39, 40, 13, 80, 77, 70, 188, 190, 219, 221, 220, 191, 86, 65, 83 })
        {
            Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.Windows, true));
            Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.Control | PlaybackModifiers.Windows, true));
            if (key is not (37 or 39 or 70 or 80)) Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.Shift, true));
        }
        // Ctrl+period stops; plain period advances one frame.
        Assert.Equal(PlaybackKeyAction.Stop, PlaybackKeyMap.Resolve(190, PlaybackModifiers.Control, true)!.Value.Action);
        Assert.Equal(PlaybackKeyAction.Frame, PlaybackKeyMap.Resolve(190, PlaybackModifiers.None, true)!.Value.Action);
        // Existing Space/F11/open/close remain in their existing handlers.
        foreach (int key in new[] { 32, 122, 87, 78 })
            Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.None, true));
    }

    [Fact]
    public void FineVolumeAndSpeedFollowIinaRatherThanMpvDefaults()
    {
        Assert.Equal(new PlaybackKeyBinding(PlaybackKeyAction.Volume, "5"), PlaybackKeyMap.Resolve(38, PlaybackModifiers.None, true));
        Assert.Equal(new PlaybackKeyBinding(PlaybackKeyAction.Volume, "1"), PlaybackKeyMap.Resolve(38, PlaybackModifiers.Alt, true));
        Assert.Equal(new PlaybackKeyBinding(PlaybackKeyAction.SpeedMultiply, "1/1.1"),
            PlaybackKeyMap.Resolve(219, PlaybackModifiers.Control | PlaybackModifiers.Alt, true));
        Assert.Equal(new PlaybackKeyBinding(PlaybackKeyAction.SpeedMultiply, "0.5"),
            PlaybackKeyMap.Resolve(219, PlaybackModifiers.Control, true));
        Assert.Equal(PlaybackKeyAction.EnterFullscreen, PlaybackKeyMap.Resolve(13, PlaybackModifiers.None, true)!.Value.Action);
        Assert.Equal(PlaybackKeyAction.Fullscreen, PlaybackKeyMap.Resolve(70, PlaybackModifiers.None, true)!.Value.Action);
    }

    [Fact]
    public void RepeatedRelativeKeysAccumulateAndCannotCrossMedia()
    {
        var pending = new PendingWheelInput();
        for (int n = 0; n < 5; n++)
        {
            var seek = PlaybackKeyMap.Resolve(39, PlaybackModifiers.None, true)!.Value;
            var volume = PlaybackKeyMap.Resolve(40, PlaybackModifiers.Alt, true)!.Value;
            pending.Add(1, 2, new(double.Parse(seek.Value), double.Parse(volume.Value)));
        }
        Assert.Equal(new WheelChange(25, -5), pending.Take(1, 2));
        Assert.Equal(default, pending.Take(1, 2));
        pending.Add(1, 2, new(5, 1));
        Assert.Equal(default, pending.Take(2, 2));
        pending.Add(1, 2, new(5, 1));
        Assert.Equal(default, pending.Take(1, 3));
    }

    [Fact]
    public void NonNavigationCommandsRemainUsableFromPlayerButtons()
    {
        Assert.Equal(PlaybackKeyAction.Mute, PlaybackKeyMap.Resolve(77, PlaybackModifiers.None, true, navigationControl: true, compact: true)!.Value.Action);
        Assert.Equal(PlaybackKeyAction.Pause, PlaybackKeyMap.Resolve(80, PlaybackModifiers.None, true, navigationControl: true, compact: true)!.Value.Action);
    }
    [Theory]
    [InlineData(76, PlaybackModifiers.Control, PlaybackKeyAction.AbLoop)]
    [InlineData(76, PlaybackModifiers.Control | PlaybackModifiers.Shift, PlaybackKeyAction.FileLoop)]
    [InlineData(83, PlaybackModifiers.Control, PlaybackKeyAction.Screenshot)]
    internal void LoopAndCaptureChordsKeepMediaAndEditingScope(int key, PlaybackModifiers modifiers, PlaybackKeyAction action)
    {
        Assert.Equal(action, PlaybackKeyMap.Resolve(key, modifiers, true)!.Value.Action);
        Assert.Equal(action, PlaybackKeyMap.Resolve(key, modifiers, true, compact: true)!.Value.Action);
        Assert.Equal(action, PlaybackKeyMap.Resolve(key, modifiers, true, navigationControl: true)!.Value.Action);
        Assert.Null(PlaybackKeyMap.Resolve(key, modifiers, false));
        Assert.Null(PlaybackKeyMap.Resolve(key, modifiers, true, ownsInput: true));
        // Ctrl+Alt+S now cycles subtitles; it must still never take a screenshot.
        Assert.NotEqual(action, PlaybackKeyMap.Resolve(key, modifiers | PlaybackModifiers.Alt, true)?.Action);
        Assert.Null(PlaybackKeyMap.Resolve(key, modifiers | PlaybackModifiers.Windows, true));
    }
    [Theory]
    [InlineData(48, PlaybackKeyAction.WindowSize, "Half")]
    [InlineData(49, PlaybackKeyAction.WindowSize, "Normal")]
    [InlineData(50, PlaybackKeyAction.WindowSize, "Double")]
    [InlineData(51, PlaybackKeyAction.WindowSize, "FitScreen")]
    [InlineData(189, PlaybackKeyAction.WindowStep, "-1")]
    [InlineData(187, PlaybackKeyAction.WindowStep, "1")]
    internal void WindowChordsKeepEditingCompactAndModifierScope(int key, PlaybackKeyAction action, string value)
    {
        Assert.Equal(new PlaybackKeyBinding(action, value), PlaybackKeyMap.Resolve(key, PlaybackModifiers.Control, true));
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.Control, false));
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.Control, true, ownsInput: true));
        Assert.Null(PlaybackKeyMap.Resolve(key, PlaybackModifiers.Control, true, compact: true));
        foreach (var modifier in new[] { PlaybackModifiers.None, PlaybackModifiers.Control | PlaybackModifiers.Shift,
            PlaybackModifiers.Control | PlaybackModifiers.Alt, PlaybackModifiers.Control | PlaybackModifiers.Windows })
            Assert.Null(PlaybackKeyMap.Resolve(key, modifier, true));
    }
}
