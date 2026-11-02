using System.Text.Json;

namespace Mani_App.Diagnostics;

internal readonly record struct PauseRequest(int RequestId, bool Paused);

internal static class WebProbeProtocol
{
    internal const string HostName = "mani-probe.invalid";
    internal const string DocumentUri = "https://mani-probe.invalid/m1-web-probe.html";
    internal static string? DispatchRejection(int messageGeneration, int currentGeneration,
        bool closing, bool ready, bool busy, bool probeCommandsAllowed, bool sessionAvailable)
    {
        if (closing) return "closing";
        if (messageGeneration != currentGeneration) return "stale-surface";
        if (!sessionAvailable) return "no-session";
        if (!ready) return "not-ready";
        if (busy && !probeCommandsAllowed) return "busy";
        return null;
    }

    internal static bool TryParse(string source, string json, string token, out PauseRequest request)
    {
        request = default;
        if (source != DocumentUri || string.IsNullOrEmpty(token) || json.Length > 2048) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            // Reject duplicate keys rather than letting parser-specific first/last-key rules disagree.
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!keys.Add(property.Name)) return false;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var v) || v != 1 ||
                !root.TryGetProperty("token", out var secret) || secret.ValueKind != JsonValueKind.String || secret.GetString() != token ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "set-pause" ||
                !root.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number < 1 ||
                !root.TryGetProperty("paused", out var paused) || paused.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            request = new PauseRequest(number, paused.GetBoolean());
            return true;
        }
        catch (JsonException) { return false; }
    }
}

// Correlate completions to our document; cancelled unrelated navigation is not a load result.
internal sealed class WebProbeNavigation
{
    private ulong? _documentId;
    internal bool Starting(string uri, ulong id)
    {
        if (uri != WebProbeProtocol.DocumentUri) return false;
        _documentId = id;
        return true;
    }
    internal bool IsCurrent(ulong id) => _documentId == id;
}
