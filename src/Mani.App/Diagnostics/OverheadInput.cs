using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Mani_App.Diagnostics;

internal sealed record OverheadInputModule(string name, string path);
internal sealed record OverheadInputSnapshot(int schema_version, string label, double startedQpcSeconds,
    double endedQpcSeconds, bool uiThread, JsonElement inputBefore, JsonElement inputAfter,
    bool complete, IReadOnlyList<OverheadInputModule> modules, IReadOnlyList<string> errors);

// Reuses the startup layout reader and module-presence strategy. No file versions,
// thread/virtual-address enumeration, global input changes or forced GC.
internal static class OverheadInput
{
    public const string Policy = "english-us-no-sogou-v1";
    private static double Clock => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public static OverheadInputSnapshot Capture(string label, bool uiThread, OverheadPollGate gate)
    {
        if (gate.Measuring) throw new InvalidOperationException("Input enumeration cannot run inside a measurement window.");
        double start = Clock;
        var before = JsonSerializer.SerializeToElement(StartupInput.Capture());
        var modules = new List<OverheadInputModule>();
        var errors = new List<string>();
        try
        {
            using var process = Process.GetCurrentProcess();
            var all = process.Modules;
            if (all.Count > 2048) errors.Add("Module limit reached.");
            foreach (ProcessModule module in all)
            {
                using (module)
                {
                    if (modules.Count >= 2048) continue;
                    try { modules.Add(new(module.ModuleName, module.FileName)); }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
                    { errors.Add("Module: " + error.Message); }
                }
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        { errors.Add("Modules: " + error.Message); }
        var after = JsonSerializer.SerializeToElement(StartupInput.Capture());
        return new(1, label, start, Clock, uiThread, before, after,
            errors.Count == 0 && modules.Count > 0, modules, errors);
    }

    internal static bool IsSogou(OverheadInputModule module) =>
        module.path.Replace('\\', '/').Split('/').Contains("SogouInput", StringComparer.OrdinalIgnoreCase) ||
        module.name.Equals("SogouTSF.ime", StringComparison.OrdinalIgnoreCase) ||
        module.name.Equals("SogouPY.ime", StringComparison.OrdinalIgnoreCase);

    private static (uint Thread, string Hkl, string Klid) Layout(JsonElement value)
    {
        if (value.GetProperty("schema_version").GetInt32() != 1 || value.GetProperty("status").GetString() != "observed" ||
            value.GetProperty("error").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Input layout is unavailable or changing.");
        uint thread = value.GetProperty("threadId").GetUInt32();
        string hkl = value.GetProperty("hkl").GetString() ?? "";
        string klid = value.GetProperty("klid").GetString() ?? "";
        if (thread == 0 || hkl.Length != 16 || hkl != hkl.ToUpperInvariant() || !hkl.All(char.IsAsciiHexDigit) ||
            !ulong.TryParse(hkl, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong native) ||
            (native & 0xffff) != 0x0409 || value.GetProperty("languageId").GetInt32() != 0x0409 || klid != "00000409")
            throw new InvalidDataException("Select English (United States), US keyboard with Win+Space before launching Mani.");
        return (thread, hkl, klid);
    }

    public static void Validate(OverheadInputSnapshot snapshot, OverheadInputSnapshot initial)
    {
        try
        {
            if (snapshot.schema_version != 1 || !snapshot.uiThread || !snapshot.complete || snapshot.errors.Count != 0 ||
                snapshot.modules.Count is < 1 or > 2048 || snapshot.modules.Any(m => string.IsNullOrEmpty(m.name) || string.IsNullOrEmpty(m.path)) ||
                !double.IsFinite(snapshot.startedQpcSeconds) || !double.IsFinite(snapshot.endedQpcSeconds) ||
                snapshot.startedQpcSeconds < 0 || snapshot.endedQpcSeconds < snapshot.startedQpcSeconds)
                throw new InvalidDataException("Incomplete UI-thread input/module observation.");
            var layout = Layout(snapshot.inputBefore);
            if (layout != Layout(snapshot.inputAfter) || layout != Layout(initial.inputBefore))
                throw new InvalidDataException("Input layout or UI thread changed between boundaries.");
            if (snapshot.modules.Any(IsSogou))
                throw new InvalidDataException("Sogou modules are loaded. Close Mani and select the Windows US keyboard before launching again.");
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid input condition metadata.", error); }
    }
}
