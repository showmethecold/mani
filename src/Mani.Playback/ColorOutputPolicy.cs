// Portions ported from IINA 1.5.1/build 180, iina/VideoView.swift:
// isHDRVideo, refreshEdrMode, requestEdrMode and setToneMappingForHDR/SDR.
// Copyright © 2016 lhc. All rights reserved.
// IINA is distributed under the GNU General Public License, version 3.
// See licenses/IINA-GPL-3.0.txt and docs/m1/display-validation.md.
// Windows adaptation: active HDR state, DXGI BT.2020/PQ container and mpv hints.
using System.Globalization;

namespace Mani.Playback;

public sealed record DisplayColorState(bool HdrEnabled, double PeakNits);
public sealed record ColorOutputTarget(bool Hdr, string Primaries, string Transfer, string Peak);

public static class ColorOutputPolicy
{
    // Preserve IINA's accepted transfer functions and primaries, including missing/unknown -> SDR.
    public static bool IsHdrVideo(string primaries, string transfer) =>
        transfer is "hlg" or "pq" && primaries is "bt.2020" or "display-p3";

    public static ColorOutputTarget Select(string primaries, string transfer, DisplayColorState display, bool forceSdr = false)
    {
        if (forceSdr) return new(false, "bt.709", "srgb", "100");
        if (!display.HdrEnabled || !IsHdrVideo(primaries, transfer))
            return new(false, "bt.709", "srgb", "auto");
        // mpv target-peak accepts integral 10..10000 nits. Missing/invalid display data uses its auto fallback.
        var peak = double.IsFinite(display.PeakNits) && display.PeakNits is >= 10 and <= 10000
            ? Math.Round(display.PeakNits).ToString(CultureInfo.InvariantCulture) : "auto";
        // IINA converts HLG to PQ. DXGI's HDR10 container is BT.2020, also for P3 source content.
        return new(true, "bt.2020", "pq", peak);
    }
}
