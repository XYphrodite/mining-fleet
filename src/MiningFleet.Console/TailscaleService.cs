using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MiningFleet.Console;

/// <summary>
/// One machine in the tailnet. <see cref="Address"/> is always the 100.x address;
/// <see cref="DnsName"/> is its MagicDNS name, and is null unless the name can actually be
/// resolved from this machine.
/// </summary>
public sealed record TailnetMachine(string Name, string Address, string? DnsName, string Os, bool Online, string? LastSeen, bool IsSelf = false)
{
    /// <summary>What to store as a node's host: loopback for the local agent, MagicDNS or IPv4 for peers.</summary>
    public string Host => IsSelf ? "127.0.0.1" : DnsName ?? Address;
}

/// <summary>
/// Reads the local tailnet through the tailscale CLI so nodes can be added by picking
/// them from a list instead of typing 100.x addresses by hand.
/// </summary>
public static class TailscaleService
{
    public static async Task<IReadOnlyList<TailnetMachine>> ListAsync(CancellationToken ct)
    {
        var json = await RunAsync(ct);
        if (json is null) return [];

        // Use MagicDNS for peers, but never route the local agent through its own Tailscale name.
        return await ResolvableNamesAsync(Parse(json), ct: ct);
    }

    /// <summary>
    /// Parses `tailscale status --json`. MagicDNS names are carried only when the tailnet has
    /// MagicDNS switched on; whether *this* machine resolves them is a separate question, and
    /// the one <see cref="ResolvableNamesAsync"/> answers.
    /// </summary>
    public static IReadOnlyList<TailnetMachine> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var magicDns = MagicDnsEnabled(root);
        var machines = new List<TailnetMachine>();

        if (root.TryGetProperty("Self", out var self))
            Add(self, magicDns, machines, isSelf: true);

        if (root.TryGetProperty("Peer", out var peers) && peers.ValueKind == JsonValueKind.Object)
        {
            foreach (var peer in peers.EnumerateObject())
                Add(peer.Value, magicDns, machines);
        }

        return machines.OrderByDescending(m => m.Online).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Keeps MagicDNS for peers only when it resolves from this machine. The local node remains
    /// on IPv4 loopback through <see cref="Host"/> regardless of this result.
    /// </summary>
    public static async Task<IReadOnlyList<TailnetMachine>> ResolvableNamesAsync(
        IReadOnlyList<TailnetMachine> machines,
        Func<string, CancellationToken, Task<bool>>? resolves = null,
        CancellationToken ct = default)
    {
        var probe = machines.FirstOrDefault(m => m.DnsName is not null)?.DnsName;
        if (probe is null) return machines;

        resolves ??= ResolvesAsync;
        if (await resolves(probe, ct)) return machines;

        return machines.Select(m => m with { DnsName = null }).ToList();
    }

    private static bool MagicDnsEnabled(JsonElement root)
    {
        // Newer clients report it per tailnet; older ones only ever set the suffix.
        if (root.TryGetProperty("CurrentTailnet", out var tailnet) && tailnet.ValueKind == JsonValueKind.Object
            && tailnet.TryGetProperty("MagicDNSEnabled", out var enabled)
            && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return enabled.ValueKind == JsonValueKind.True;

        return root.TryGetProperty("MagicDNSSuffix", out var suffix)
            && !string.IsNullOrEmpty(suffix.GetString());
    }

    private static void Add(JsonElement element, bool magicDns, List<TailnetMachine> into, bool isSelf = false)
    {
        var name = element.TryGetProperty("HostName", out var h) ? h.GetString() ?? "" : "";
        if (name.Length == 0) return;

        var address = element.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
            ? ips.EnumerateArray().Select(i => i.GetString()).FirstOrDefault(i => i is not null && i.Contains('.')) ?? ""
            : "";
        if (address.Length == 0) return;

        // The fully qualified name, trailing dot trimmed: it resolves whether or not the search
        // domain is configured, which the bare hostname does not.
        var dnsName = magicDns && element.TryGetProperty("DNSName", out var d)
            ? (d.GetString() ?? "").TrimEnd('.')
            : "";

        into.Add(new TailnetMachine(
            name,
            address,
            dnsName.Length > 0 ? dnsName : null,
            element.TryGetProperty("OS", out var os) ? os.GetString() ?? "" : "",
            element.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.True,
            element.TryGetProperty("LastSeen", out var seen) ? seen.GetString() : null,
            isSelf));
    }

    private static async Task<bool> ResolvesAsync(string name, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var addresses = await Dns.GetHostAddressesAsync(name, timeout.Token);
            return addresses.Length > 0;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<string?> RunAsync(CancellationToken ct)
    {
        foreach (var exe in CandidateExecutables())
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(exe, "status --json")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process is null) continue;

                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                if (process.ExitCode == 0 && output.TrimStart().StartsWith('{')) return output;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or JsonException)
            {
                // Try the next candidate path.
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateExecutables()
    {
        yield return "tailscale";
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files\Tailscale\tailscale.exe";
            yield return @"C:\Program Files (x86)\Tailscale\tailscale.exe";
        }
        else
        {
            yield return "/usr/bin/tailscale";
            yield return "/usr/local/bin/tailscale";
            yield return "/Applications/Tailscale.app/Contents/MacOS/Tailscale";
        }
    }
}
