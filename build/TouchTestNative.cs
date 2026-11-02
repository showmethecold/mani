using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

public static class TouchTestNative
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct PointerInfo
    {
        public uint Type, Id, Frame, Flags;
        public IntPtr Device, Target;
        public Point Pixel, Himetric, RawPixel, RawHimetric;
        public uint Time, History; public int Input; public uint Keys;
        public ulong Performance; public uint ButtonChange;
    }
    [StructLayout(LayoutKind.Sequential)] public struct TouchInfo
    {
        public PointerInfo Pointer; public uint Flags, Mask; public Rect Contact, RawContact;
        public uint Orientation, Pressure;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InitializeTouchInjection(uint maxCount, uint feedback);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InjectTouchInput(uint count, [In] TouchInfo[] contacts);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private static bool initialized;
    private static void Send(TouchInfo[] contacts, string stage)
    {
        if (InjectTouchInput((uint)contacts.Length, contacts)) return;
        int error = Marshal.GetLastWin32Error();
        if (error == 21) { Thread.Sleep(2); if (InjectTouchInput((uint)contacts.Length, contacts)) return; error = Marshal.GetLastWin32Error(); }
        throw new Win32Exception(error, "Touch injection " + stage + " failed (" + error + ").");
    }
    public static void Gesture(IntPtr window, int x, int y, int dx, int dy, int spread, int fingers = 1, bool cancel = false)
    {
        if (fingers < 1 || fingers > 2) throw new ArgumentOutOfRangeException("fingers");
        if (GetForegroundWindow() != window) throw new InvalidOperationException("Target window is not foreground.");
        if (!initialized) { if (!InitializeTouchInjection(2, 3)) throw new Win32Exception(); initialized = true; }
        var points = new TouchInfo[fingers];
        for (int i = 0; i < fingers; i++) points[i] = new TouchInfo { Pointer = new PointerInfo { Type = 2, Id = (uint)i }, Mask = 7, Orientation = 90, Pressure = 512 };
        bool down = false; TouchInfo[] last = null;
        try
        {
            for (int frame = 0; frame <= 12; frame++)
            {
                if (GetForegroundWindow() != window) throw new InvalidOperationException("Foreground changed during injection.");
                for (int i = 0; i < fingers; i++)
                {
                    int px = x + dx * frame / 12 + (fingers == 1 ? 0 : (i == 0 ? -1 : 1) * (80 + spread * frame / 12));
                    int py = y + dy * frame / 12;
                    points[i].Pointer.Pixel = new Point { X = px, Y = py };
                    points[i].Contact = new Rect { Left = px - 2, Top = py - 2, Right = px + 2, Bottom = py + 2 };
                    points[i].Pointer.Flags = (frame == 0 ? 0x10000u : 0x20000u) | 6;
                }
                Send(points, "frame " + frame); down = true; last = (TouchInfo[])points.Clone(); Thread.Sleep(40);
            }
        }
        finally
        {
            if (down)
            {
                for (int i = 0; i < fingers; i++) last[i].Pointer.Flags = 0x40000u | (cancel ? 0x8000u : 0);
                Send(last, "release");
            }
        }
    }
}
