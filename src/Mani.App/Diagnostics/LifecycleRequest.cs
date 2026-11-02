using System.Text.Json;

namespace Mani_App.Diagnostics;

// Windows diagnostic activation only. IINA assessment: docs/m1/lifecycle-validation.md.
internal sealed record LifecycleRequest(int schema_version, string automationId, string testCase, DateTimeOffset requestedAtUtc)
{
    public static readonly string[] Cases = ["recovery-close", "load-burst-close", "probe-close"];
    public static LifecycleRequest Claim(string directory)
    {
        var pending = Path.Combine(directory, "pending-lifecycle.json");
        var claimed = Path.Combine(directory, "claimed-lifecycle-" + Guid.NewGuid().ToString("N") + ".json");
        File.Move(pending, claimed);
        var request = JsonSerializer.Deserialize<LifecycleRequest>(File.ReadAllText(claimed))
            ?? throw new InvalidDataException("Empty lifecycle request.");
        if (request.schema_version != 1 || !Cases.Contains(request.testCase) ||
            !Guid.TryParseExact(request.automationId, "N", out var id) || id.ToString("N") != request.automationId)
            throw new InvalidDataException("Invalid lifecycle request.");
        var age = (DateTimeOffset.UtcNow - request.requestedAtUtc).TotalSeconds;
        if (age < -5 || age > 600) throw new InvalidDataException("Lifecycle request expired.");
        return request;
    }
}
