using System.ComponentModel;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Mani_App.Diagnostics;

// Lifecycle observation follows IINA MemoryUsage; Windows-only metadata adaptation.
// Scope and metric definitions: docs/m1/startup-resource-diagnostics.md.
internal static class StartupInventory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRegion
    {
        public nint Base, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint Size;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(nint address, out MemoryRegion region, nuint length);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint error);

    private static bool Expected(Exception error) => error is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException;
    private static double Clock => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static object VirtualRegions()
    {
        var totals = new Dictionary<string, ulong> { ["private"] = 0, ["image"] = 0, ["mapped"] = 0, ["other"] = 0 };
        ulong address = 0;
        int regions = 0;
        var start = Clock;
        string? error = null;
        bool complete = false;
        for (; regions < 131072 && Clock - start < 2; regions++)
        {
            if (VirtualQuery((nint)address, out var region, (nuint)Marshal.SizeOf<MemoryRegion>()) == 0)
            {
                int code = Marshal.GetLastWin32Error();
                complete = code == 87 && regions > 0; // Beyond the process address space.
                if (!complete) error = "VirtualQuery: " + code;
                break;
            }
            if (region.State == 0x1000)
            {
                string kind = region.Type switch { 0x20000 => "private", 0x1000000 => "image", 0x40000 => "mapped", _ => "other" };
                totals[kind] = checked(totals[kind] + (ulong)region.Size);
            }
            ulong next = checked((ulong)region.Base + (ulong)region.Size);
            if (next <= address || next > long.MaxValue) { error = "Address traversal stopped."; break; }
            address = next;
        }
        if (!complete && error is null) error = "Region/time limit reached.";
        return new { status = complete ? "observed" : "partial", error, regions, readSeconds = Clock - start,
            committedVirtualBytesByType = totals,
            limitation = "Virtual address categories, not resident/private-byte owners; copy-on-write image pages can remain MEM_IMAGE." };
    }

    public static object Capture(string label, object context)
    {
        double started = Clock;
        var before = OverheadRuntime.MemoryCheckpoint(label);
        var input = StartupInput.Capture();
        var errors = new List<string>();
        var modules = new List<object>();
        var threads = new List<object>();
        using var process = Process.GetCurrentProcess();
        uint? Gui(uint flag)
        {
            SetLastError(0);
            uint value = GetGuiResources(process.Handle, flag);
            int error = Marshal.GetLastWin32Error();
            if (value == 0 && error != 0) { errors.Add("GetGuiResources " + flag + ": " + error); return null; }
            return value;
        }
        uint? gdi = Gui(0), user = Gui(1);
        try
        {
            var all = process.Modules;
            if (all.Count > 2048) errors.Add("Module limit reached.");
            foreach (ProcessModule module in all)
            {
                using (module)
                {
                    if (modules.Count >= 2048) continue;
                    try { modules.Add(new { name = module.ModuleName, path = module.FileName,
                        imageBytes = module.ModuleMemorySize, version = module.FileVersionInfo.FileVersion }); }
                    catch (Exception error) when (Expected(error)) { errors.Add("Module: " + error.Message); }
                }
            }
        }
        catch (Exception error) when (Expected(error)) { errors.Add("Modules: " + error.Message); }
        try
        {
            var all = process.Threads;
            if (all.Count > 2048) errors.Add("Thread limit reached.");
            foreach (ProcessThread thread in all)
            {
                using (thread)
                {
                    if (threads.Count >= 2048) continue;
                    try { threads.Add(new { id = thread.Id, state = thread.ThreadState.ToString(),
                        cpuSeconds = thread.TotalProcessorTime.TotalSeconds }); }
                    catch (Exception error) when (Expected(error)) { errors.Add("Thread: " + error.Message); }
                }
            }
        }
        catch (Exception error) when (Expected(error)) { errors.Add("Threads: " + error.Message); }
        var environment = new Dictionary<string, string?>();
        foreach (string name in new[] { "DOTNET_gcServer", "COMPlus_gcServer", "DOTNET_GCHeapCount", "COMPlus_GCHeapCount",
            "DOTNET_GCConserveMemory", "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_ReadyToRun", "CORECLR_ENABLE_PROFILING" })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            environment[name] = value is null || (value.Length <= 32 && value.All(c => char.IsAsciiHexDigit(c) || c is 'x' or 'X'))
                ? value : "(non-numeric value omitted)";
        }
        var runtime = new { framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            debuggerAttached = Debugger.IsAttached, GCSettings.IsServerGC, latencyMode = GCSettings.LatencyMode.ToString(),
            environment, startupHooksConfigured = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS")),
            profilerPathConfigured = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CORECLR_PROFILER_PATH")) };
        var virtualMemory = VirtualRegions();
        var after = OverheadRuntime.MemoryCheckpoint(label);
        return new { schema_version = 1, diagnosticOnly = true, label, startedQpcSeconds = started,
            endedQpcSeconds = Clock, memoryBefore = before, memoryAfter = after, context, runtime, input,
            modules, threads, gdiObjects = gdi, userObjects = user, errors, virtualMemory,
            limitation = "Sequential self-observation can allocate and load modules; inventories are not atomic or performance baselines. No heap/stack/content dump or arbitrary handle enumeration." };
    }
}
