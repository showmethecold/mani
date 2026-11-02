using System;
using System.Drawing;
using System.Runtime.InteropServices;

// Test-only native input and independent visible-cursor oracle. No UIA calls.
public static class CursorTestNative
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct CursorInfo { public int Size; public uint Flags; public IntPtr Cursor; public Point Position; }
    [StructLayout(LayoutKind.Sequential)] public struct Mouse { public int x, y; public uint data, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct Key { public ushort code, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] public struct Union { [FieldOffset(0)] public Mouse mouse; [FieldOffset(0)] public Key key; }
    [StructLayout(LayoutKind.Sequential)] public struct Input { public uint type; public Union value; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint process);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    public static void RequireForeground(int process)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var actual);
        if (actual != process) throw new InvalidOperationException("Target process is not foreground; no input sent.");
    }
    static void Send(Input[] input)
    {
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length)
            throw new InvalidOperationException("SendInput failed.");
    }
    public static void Text(string text, int process)
    {
        RequireForeground(process);
        var input = new Input[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            input[i * 2] = new Input { type = 1, value = new Union { key = new Key { scan = text[i], flags = 4 } } };
            input[i * 2 + 1] = new Input { type = 1, value = new Union { key = new Key { scan = text[i], flags = 6 } } };
        }
        Send(input);
    }
    public static void KeyPress(ushort code, ushort[] modifiers, int process)
    {
        RequireForeground(process);
        var input = new Input[modifiers.Length * 2 + 2]; int n = 0;
        foreach (var mod in modifiers) input[n++] = new Input { type = 1, value = new Union { key = new Key { code = mod } } };
        input[n++] = new Input { type = 1, value = new Union { key = new Key { code = code } } };
        input[n++] = new Input { type = 1, value = new Union { key = new Key { code = code, flags = 2 } } };
        for (int i = modifiers.Length - 1; i >= 0; i--) input[n++] = new Input { type = 1, value = new Union { key = new Key { code = modifiers[i], flags = 2 } } };
        Send(input);
    }
    public static void MouseEvent(uint flags, int process)
    {
        RequireForeground(process);
        Send(new[] { new Input { value = new Union { mouse = new Mouse { flags = flags } } } });
    }
    public static void MouseMove(int x, int y, int process)
    {
        RequireForeground(process);
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        int dx = (int)Math.Round((x - left) * 65535.0 / (width - 1));
        int dy = (int)Math.Round((y - top) * 65535.0 / (height - 1));
        Send(new[] { new Input { value = new Union { mouse = new Mouse { x = dx, y = dy, flags = 0xE001 } } } });
    }
    public static bool HasPixels(CursorInfo info)
    {
        if ((info.Flags & 1) == 0) return false;
        foreach (var color in new[] { Color.Black, Color.White })
        {
            using var bitmap = new Bitmap(80, 80); using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(color); var dc = graphics.GetHdc();
            try { if (!DrawIconEx(dc, 8, 8, info.Cursor, 64, 64, 0, IntPtr.Zero, 3)) throw new InvalidOperationException("Cannot inspect cursor."); }
            finally { graphics.ReleaseHdc(dc); }
            for (int y = 0; y < 80; y++) for (int x = 0; x < 80; x++)
                if ((bitmap.GetPixel(x, y).ToArgb() & 0xffffff) != (color.ToArgb() & 0xffffff)) return true;
        }
        return false;
    }
}
