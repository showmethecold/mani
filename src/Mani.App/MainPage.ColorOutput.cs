using Mani.Playback;
using Microsoft.Graphics.Display;

namespace Mani_App;

public sealed partial class MainPage
{
    private DisplayColorState? _lastDisplayColor;
    private ColorOutputTarget? _lastColorOutput;

    private async Task SyncDisplayColorAsync()
    {
        if (_session is null) return;
        var display = new DisplayColorState(false, 0);
        try
        {
            var color = _display?.GetAdvancedColorInfo();
            if (color is not null)
                display = new(color.CurrentAdvancedColorKind == DisplayAdvancedColorKind.HighDynamicRange,
                    color.MaxLuminanceInNits);
        }
        catch (Exception error)
        {
            if (_lastDisplayColor != display) Trace("display-color-unavailable", new { error = error.ToString() });
        }
        if (_lastDisplayColor == display) return;
        await _session.ConfigureDisplayAsync(display);
        _lastDisplayColor = display;
        Trace("display-color-policy", display);
    }
}
