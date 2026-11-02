using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class AccessibilityTestNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeContrast { public uint Size, Flags; public IntPtr Scheme; }
    public struct Contrast { public uint Size, Flags; public string Scheme; }
    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)]
    static extern bool Parameter(uint action, uint size, ref NativeContrast value, uint flags);
    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)]
    static extern bool Parameter(uint action, uint size, ref int value, uint flags);
    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)]
    static extern bool Parameter(uint action, uint size, IntPtr value, uint flags);
    static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    // SPI_GET returns a borrowed system pointer. A ref LPWStr marshaller would
    // attempt to release it; copy its contents without taking ownership.
    public static Contrast GetContrast() {
        var value = new NativeContrast { Size=(uint)Marshal.SizeOf<NativeContrast>() };
        Check(Parameter(0x42,value.Size,ref value,0));
        return new Contrast { Size=value.Size, Flags=value.Flags, Scheme=Marshal.PtrToStringUni(value.Scheme) };
    }
    public static void SetContrast(Contrast value) {
        var native = new NativeContrast { Size=(uint)Marshal.SizeOf<NativeContrast>(), Flags=value.Flags, Scheme=Marshal.StringToHGlobalUni(value.Scheme) };
        try { Check(Parameter(0x43,native.Size,ref native,2)); }
        finally { Marshal.FreeHGlobal(native.Scheme); }
    }
    public static bool GetAnimations() { int value=0; Check(Parameter(0x1042,0,ref value,0)); return value!=0; }
    public static void SetAnimations(bool value) => Check(Parameter(0x1043,0,new IntPtr(value ? 1 : 0),2));
}
