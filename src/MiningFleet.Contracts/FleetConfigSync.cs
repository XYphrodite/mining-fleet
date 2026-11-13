using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiningFleet.Contracts;

/// <summary>Peer endpoint extracted from a synced <c>fleet.json</c>.</summary>
public sealed record FleetPeerEndpoint(string Name, string Host, int Port, string Token)
{
    public string Endpoint => $"http://{Host}:{Port}";
}

/// <summary>
/// Shared validation, hashing, ordering, and peer extraction rules for <c>fleet.json</c>
/// peer-to-peer synchronization across consoles and agents.
/// </summary>
public static class FleetConfigSync
{
    public const string FleetConfigFileName = "fleet.json";
    public const string SyncMetadataFileName = "fleet.sync.json";
    public const string NodeIdHeader = "X-Fleet-Node-Id";
    public const int DefaultAgentPort = 47800;

    public static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public static string SanitizeToken(string? token) =>
        string.IsNullOrEmpty(token) ? "" : token.Trim().Trim('\uFEFF', '\u200B');

    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var trimmed = host.Trim();
        return trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("127.0.0.1", StringComparison.Ordinal)
            || trimmed.Equals("::1", StringComparison.Ordinal)
            || trimmed.Equals("[::1]", StringComparison.Ordinal)
            || trimmed.Equals("0.0.0.0", StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures <paramref name="json"/> is a JSON object with a <c>nodes</c> array so broken
    /// edits or truncated payloads can never overwrite working <c>fleet.json</c> files across the fleet.
    /// </summary>
    public static bool TryValidateJson(string? json, out string? error)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "fleet.json is empty.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "fleet.json root must be a JSON object.";
                return false;
            }

            if (!TryGetPropertyIgnoreCase(doc.RootElement, "nodes", out var nodes)
                || nodes.ValueKind != JsonValueKind.Array)
            {
                error = "fleet.json must contain a nodes array.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"fleet.json is not valid JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Last-write-wins comparison with deterministic SHA-256 tie-breaking so two machines with
    /// identical timestamps converge to the same copy instead of oscillating.
    /// </summary>
    public static bool IsNewer(FleetConfigMetaDto? candidate, FleetConfigMetaDto? current)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.Sha256)) return false;
        if (current is null) return true;
        if (string.Equals(candidate.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase))
            return false;

        if (candidate.UpdatedAtUtc > current.UpdatedAtUtc) return true;
        if (candidate.UpdatedAtUtc < current.UpdatedAtUtc) return false;

        return string.Compare(candidate.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase) > 0;
    }

    /// <summary>
    /// Extracts enabled remote peer endpoints from raw <c>fleet.json</c> text without needing
    /// console-only types in the shared contracts assembly.
    /// </summary>
    public static IReadOnlyList<FleetPeerEndpoint> ParsePeers(string? json, string? fallbackToken = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];

            var root = doc.RootElement;
            var defaultToken = TryGetString(root, "token");
            if (string.IsNullOrWhiteSpace(defaultToken))
                defaultToken = fallbackToken ?? "";
            defaultToken = SanitizeToken(defaultToken);

            var defaultPort = TryGetInt(root, "agentPort") is { } p && p > 0 ? p : DefaultAgentPort;

            if (!TryGetPropertyIgnoreCase(root, "nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return [];

            var peers = new List<FleetPeerEndpoint>();
            var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in nodes.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (TryGetBool(item, "enabled") == false) continue;

                var host = TryGetString(item, "host")?.Trim();
                if (string.IsNullOrWhiteSpace(host)) continue;

                var port = TryGetInt(item, "port") is { } nodePort && nodePort > 0 ? nodePort : defaultPort;
                var name = TryGetString(item, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(name)) name = host;

                var nodeToken = SanitizeToken(TryGetString(item, "token"));
                var effectiveToken = string.IsNullOrWhiteSpace(nodeToken) ? defaultToken : nodeToken;

                var endpointKey = $"{host}:{port}";
                if (!seenEndpoints.Add(endpointKey)) continue;

                peers.Add(new FleetPeerEndpoint(name, host, port, effectiveToken));
            }

            return peers;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Writes <paramref name="content"/> via a staging temp file and optionally sets its UTC mtime.</summary>
    public static void WriteFileAtomic(string path, string content, DateTimeOffset? lastWriteUtc = null)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        var temp = $"{fullPath}.tmp.{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (lastWriteUtc is { } ts)
                File.SetLastWriteTimeUtc(temp, ts.UtcDateTime);

            File.Move(temp, fullPath, overwrite: true);
            if (lastWriteUtc is { } tsAfter)
                File.SetLastWriteTimeUtc(fullPath, tsAfter.UtcDateTime);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value)) return true;
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int? TryGetInt(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out var prop)
        && prop.ValueKind == JsonValueKind.Number
        && prop.TryGetInt32(out var val)
            ? val
            : null;

    private static bool? TryGetBool(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out var prop)
        && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : null;
}
