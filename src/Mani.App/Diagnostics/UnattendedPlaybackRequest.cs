using System.Text.Json;

namespace Mani_App.Diagnostics;

// Windows-only test activation bridge. IINA source assessment: docs/m1/long-playback.md.
internal sealed record UnattendedPlaybackRequest(int schema_version, string automationId,
    string mediaPath, int durationSeconds, DateTimeOffset requestedAtUtc)
{
    public static UnattendedPlaybackRequest Claim(string directory)
    {
        var pending = Path.Combine(directory, "pending-long-playback.json");
        // Claim before parsing: malformed/expired requests cannot replay on the next launch.
        var claimed = Path.Combine(directory, "claimed-" + Guid.NewGuid().ToString("N") + ".json");
        File.Move(pending, claimed);
        var request = JsonSerializer.Deserialize<UnattendedPlaybackRequest>(File.ReadAllText(claimed))
            ?? throw new InvalidDataException("Empty unattended playback request.");
        if (request.schema_version != 1 || request.durationSeconds != 1800 ||
            !Guid.TryParseExact(request.automationId, "N", out var id) || id.ToString("N") != request.automationId)
            throw new InvalidDataException("Invalid unattended playback request identity or duration.");
        var age = (DateTimeOffset.UtcNow - request.requestedAtUtc).TotalSeconds;
        if (age < -5 || age > 600) throw new InvalidDataException("Unattended playback request expired.");
        if (string.IsNullOrWhiteSpace(request.mediaPath) || !Path.IsPathFullyQualified(request.mediaPath) || !File.Exists(request.mediaPath))
            throw new FileNotFoundException("Unattended movie must be an existing absolute local path.");
        return request;
    }
}
