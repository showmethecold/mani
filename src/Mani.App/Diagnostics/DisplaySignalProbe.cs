using Mani.Playback;
using System.Globalization;
using System.Text.Json;

namespace Mani_App.Diagnostics;

// Reuses mpv's target negotiation and existing DXGI inspection. No HDR conversion is implemented here.
// IINA source/platform assessment: docs/m1/display-validation.md.
internal static class DisplaySignalProbe
{
    internal sealed record Stage(string Id, string Fixture, bool ForceSdr);
    internal sealed record Capture(PlaybackSnapshot State, IReadOnlyDictionary<string, string> Engine, object? Swapchain);
    internal static readonly Stage[] Stages = [
        new("sdr-auto", "sdr", false), new("hdr-auto", "hdr", false),
        new("hdr-to-sdr", "hdr", true), new("sdr-return", "sdr", false)
    ];
    internal static readonly string[] TargetOptions = ["target-prim", "target-trc", "target-peak", "target-colorspace-hint"];
    internal static readonly string[] Properties = [
        "video-params", "video-out-params", "video-target-params", "video-dec-params",
        "target-prim", "target-trc", "target-peak", "target-colorspace-hint",
        "target-colorspace-hint-mode", "target-colorspace-hint-strict", "hdr-reference-white",
        "tone-mapping", "tone-mapping-param", "icc-profile", "icc-profile-auto", "target-lut",
        "hwdec-current", "current-vo", "current-gpu-context", "display-names", "display-fps",
        "vf", "glsl-shaders", "video-output-levels"
    ];

    internal static async Task ApplyTargetAsync(PlaybackSession session, bool forceSdr)
    {
        var values = forceSdr ? new[] { "bt.709", "srgb", "100", "yes" } : new[] { "auto", "auto", "auto", "auto" };
        for (int i = 0; i < TargetOptions.Length; i++) await session.SetAsync(TargetOptions[i], values[i]);
    }

    internal static bool OutputMatchesPolicy(Capture capture)
    {
        var policy = capture.State.ColorOutput;
        if (policy is null || !capture.Engine.TryGetValue("video-target-params", out var json) || json.Length == 0) return false;
        using var data = JsonDocument.Parse(json);
        var target = data.RootElement;
        if (!target.TryGetProperty("primaries", out var prim) || prim.GetString() != policy.Primaries ||
            !target.TryGetProperty("gamma", out var transfer)) return false;
        if (policy.Hdr ? transfer.GetString() != "pq" : transfer.GetString() is not ("srgb" or "gamma2.2" or "bt.1886" or "gamma2.4")) return false;
        if (policy.Peak != "auto" && (!target.TryGetProperty("max-luma", out var peak) ||
            Math.Abs(peak.GetDouble() - double.Parse(policy.Peak, CultureInfo.InvariantCulture)) > 1)) return false;
        return capture.Engine["target-prim"] == policy.Primaries && capture.Engine["target-trc"] == policy.Transfer &&
            capture.Engine["target-peak"] == policy.Peak && capture.Engine["target-colorspace-hint"] == "yes" &&
            capture.Engine["target-colorspace-hint-mode"] == "target" && capture.Engine["target-colorspace-hint-strict"] == "yes";
    }

    internal static async Task<Capture> CaptureAsync(PlaybackSession session)
    {
        var state = await session.ReadAsync();
        var engine = await session.GetPropertiesAsync(Properties);
        using var chain = await session.AcquireSwapChainAsync();
        return new(state, engine, chain is null ? null : CompositionSwapChain.ReadDescription(chain.Pointer));
    }
}
