using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using WinRT;
using Mani.Playback;

namespace Mani_App.Interop;

internal sealed class SwapChainPanelBridge : IDisposable
{
    // WinUI 3 IID from microsoft.ui.xaml.media.dxinterop.h (NOT the UWP IID).
    private static readonly Guid InterfaceId = new("63aad0b8-7c24-40ff-85a8-640d944cc325");
    private nint _native;
    private readonly SwapChainPanel _panel;
    private nint _attached;
    internal bool HasAttachedSwapChain => _attached != 0;
    internal bool IsAttachedTo(nint swapChain) => swapChain != 0 && _attached == swapChain;
    private (double Width, double Height, float X, float Y) _layout;

    internal SwapChainPanelBridge(SwapChainPanel panel)
    {
        _panel = panel;
        using var inspectable = MarshalInspectable<SwapChainPanel>.CreateMarshaler(panel);
        var iid = InterfaceId;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable.ThisPtr, in iid, out _native));
    }
    internal unsafe void Attach(nint swapChain)
    {
        if (!_panel.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Swapchain attach must run on the UI thread.");
        ObjectDisposedException.ThrowIf(_native == 0, this);
        var layout = (_panel.ActualWidth, _panel.ActualHeight, _panel.CompositionScaleX, _panel.CompositionScaleY);
        if (swapChain != 0 && (_attached != swapChain || _layout != layout))
            CompositionSwapChain.SetPanelScale(swapChain, _panel.CompositionScaleX, _panel.CompositionScaleY);
        _layout = layout;
        if (_attached == swapChain) return;
        var table = *(nint**)_native;
        var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)table[3];
        Marshal.ThrowExceptionForHR(setSwapChain(_native, swapChain));
        // The panel holds its own reference; it does not borrow the caller's lease.
        _attached = swapChain;
    }
    public void Dispose()
    {
        if (_native == 0) return;
        Attach(0);
        Marshal.Release(_native);
        _native = 0;
    }
}
