// Windows Shell APIs used only by the isolated package integration test.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class FileActivationProbe
{
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr id);
        void SetIDList(IntPtr id);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder text, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder text, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int count, out int index);
    }
    public static string[] Shortcut(string path)
    {
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
        try
        {
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0);
            var target = new System.Text.StringBuilder(32768);
            var icon = new System.Text.StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
            int index; link.GetIconLocation(icon, icon.Capacity, out index);
            return new[] {target.ToString(), icon.ToString(), index.ToString()};
        }
        finally { Marshal.ReleaseComObject(link); }
    }
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivation
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string app,
            [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint process);
    }
    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint count, out IAssocHandler handler, out uint fetched);
    }
    [ComImport, Guid("f04061ac-1659-4a3f-a954-775aa57fc083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssocHandler
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
    }
    [ComImport, Guid("71e806fb-8dee-46fc-bf8c-7748a8a1ae13"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithProgID
    {
        void SetProgID([MarshalAs(UnmanagedType.LPWStr)] string id);
        void GetProgID([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ExecuteInfo
    {
        public uint size, mask;
        public IntPtr window;
        public string verb, file, parameters, directory;
        public int show;
        public IntPtr instance, itemList;
        public string className;
        public IntPtr classKey;
        public uint hotKey;
        public IntPtr icon, process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ExecuteInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHAssocEnumHandlers(string extension, uint filter, out IEnumAssocHandlers handlers);
    public static string[][] Handlers(string extension)
    {
        IEnumAssocHandlers handlers;
        Marshal.ThrowExceptionForHR(SHAssocEnumHandlers(extension, 0, out handlers));
        var result = new List<string[]>();
        try
        {
            IAssocHandler handler; uint fetched;
            while (handlers.Next(1, out handler, out fetched) == 0 && fetched == 1)
            {
                try
                {
                    string name, label, icon; int index;
                    Marshal.ThrowExceptionForHR(handler.GetName(out name));
                    Marshal.ThrowExceptionForHR(handler.GetUIName(out label));
                    handler.GetIconLocation(out icon, out index);
                    result.Add(new[] { name, label, icon });
                }
                finally { Marshal.ReleaseComObject(handler); }
            }
        }
        finally { Marshal.ReleaseComObject(handlers); }
        return result.ToArray();
    }

    public static uint Activate(string app, string commandLine)
    {
        var activation = (IActivation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
        try
        {
            uint process;
            Marshal.ThrowExceptionForHR(activation.ActivateApplication(app, commandLine, 0, out process));
            return process;
        }
        finally { Marshal.ReleaseComObject(activation); }
    }

    public static void OpenWith(string packageName, string[] paths)
    {
        IEnumAssocHandlers handlers;
        Marshal.ThrowExceptionForHR(SHAssocEnumHandlers(System.IO.Path.GetExtension(paths[0]), 0, out handlers));
        try
        {
            IAssocHandler handler; uint fetched;
            while (handlers.Next(1, out handler, out fetched) == 0 && fetched == 1)
            {
                try
                {
                    string icon; int index;
                    handler.GetIconLocation(out icon, out index);
                    if (icon == null || !icon.Contains(packageName)) continue;
                    string progId;
                    ((IObjectWithProgID)handler).GetProgID(out progId);
                    foreach (var path in paths)
                    {
                        var info = new ExecuteInfo { size = (uint)Marshal.SizeOf(typeof(ExecuteInfo)),
                            mask = 0x501, verb = "open", file = path, className = progId, show = 1 };
                        if (!ShellExecuteExW(ref info)) throw new System.ComponentModel.Win32Exception();
                    }
                    return;
                }
                finally { Marshal.ReleaseComObject(handler); }
            }
            throw new InvalidOperationException("Isolated handler was not found.");
        }
        finally { Marshal.ReleaseComObject(handlers); }
    }
}
