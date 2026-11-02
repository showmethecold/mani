// Windows element-local cursor boundary; retain Button keyboard/UIA semantics.
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Mani_App;

public sealed class VideoSurfaceButton : Button
{
    private InputCursor? _cursor;
    private bool _hidden;
    internal void HidePointer(bool hidden)
    {
        if (_hidden == hidden) return;
        if (hidden) _cursor ??= Interop.TransparentCursor.Create();
        ProtectedCursor = hidden ? _cursor : null;
        _hidden = hidden;
    }
    internal void RestorePointer() => HidePointer(false);
    internal void ReleaseCursors()
    {
        ProtectedCursor = null;
        _cursor?.Dispose(); _cursor = null; _hidden = false;
    }
}
