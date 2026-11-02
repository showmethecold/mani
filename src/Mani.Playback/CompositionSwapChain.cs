using System.Numerics;
using System.Runtime.InteropServices;

namespace Mani.Playback;

// DXGI 1.3 IDXGISwapChain2; the caller must hold a live swapchain reference.
public static class CompositionSwapChain
{
    private static readonly Guid InterfaceId = new("a8be2ac4-199f-4946-b331-79599fb98de7");

    public static unsafe void SetPanelScale(nint swapChain, float scaleX, float scaleY)
    {
        if (!float.IsFinite(scaleX) || scaleX <= 0 || !float.IsFinite(scaleY) || scaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleX), "Composition scales must be finite and positive.");
        var native = Query(swapChain);
        try
        {
            // The buffer uses physical pixels; XAML subsequently applies its own DIP scale.
            var matrix = Matrix3x2.CreateScale(1 / scaleX, 1 / scaleY);
            var table = *(nint**)native;
            var setMatrix = (delegate* unmanaged[Stdcall]<nint, Matrix3x2*, int>)table[34];
            Marshal.ThrowExceptionForHR(setMatrix(native, &matrix));
        }
        finally { Marshal.Release(native); }
    }

    public static unsafe Matrix3x2 ReadTransform(nint swapChain)
    {
        var native = Query(swapChain);
        try
        {
            Matrix3x2 matrix;
            var table = *(nint**)native;
            var getMatrix = (delegate* unmanaged[Stdcall]<nint, Matrix3x2*, int>)table[35];
            Marshal.ThrowExceptionForHR(getMatrix(native, &matrix));
            return matrix;
        }
        finally { Marshal.Release(native); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Description
    {
        public uint Width, Height, Format;
        public int Stereo;
        public uint SampleCount, SampleQuality, BufferUsage, BufferCount;
        public int Scaling, SwapEffect, AlphaMode;
        public uint Flags;
    }
    public static unsafe object ReadDescription(nint swapChain)
    {
        var native = Query(swapChain);
        try
        {
            Description desc;
            var get = (delegate* unmanaged[Stdcall]<nint, Description*, int>)(*(nint**)native)[18];
            Marshal.ThrowExceptionForHR(get(native, &desc));
            var formatName = desc.Format switch { 28 => "R8G8B8A8_UNORM", 24 => "R10G10B10A2_UNORM", 10 => "R16G16B16A16_FLOAT", 87 => "B8G8R8A8_UNORM", _ => "DXGI_FORMAT_" + desc.Format };
            return new { desc.Width, desc.Height, desc.Format, formatName, desc.BufferCount, desc.AlphaMode };
        }
        finally { Marshal.Release(native); }
    }

    private static nint Query(nint swapChain)
    {
        if (swapChain == 0) throw new ArgumentException("A live swapchain is required.", nameof(swapChain));
        var iid = InterfaceId;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(swapChain, in iid, out var native));
        return native;
    }
}
