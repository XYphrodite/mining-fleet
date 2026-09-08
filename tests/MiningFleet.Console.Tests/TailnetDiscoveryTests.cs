using MiningFleet.Console;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Node discovery writes a host into fleet.json and every later poll depends on it resolving.
/// A MagicDNS name that does not resolve on the operator's machine turns the whole fleet into
/// connection errors, so the choice between name and address is guarded here.
/// </summary>
public sealed class TailnetDiscoveryTests
{
    private const string Status = """
    {
      "MagicDNSSuffix": "tail08a9a5.ts.net",
      "CurrentTailnet": { "Name": "example.github", "MagicDNSSuffix": "tail08a9a5.ts.net", "MagicDNSEnabled": true },
      "Self": {
        "HostName": "operator-pc", "OS": "windows", "Online": true,
        "TailscaleIPs": ["100.89.154.125", "fd7a:115c::1"],
        "DNSName": "operator-pc.tail08a9a5.ts.net."
      },
      "Peer": {
        "key-1": {
          "HostName": "rig-1", "OS": "windows", "Online": true,
          "TailscaleIPs": ["100.105.87.52"], "DNSName": "rig-1.tail08a9a5.ts.net."
        },
        "key-2": {
          "HostName": "phone", "OS": "android", "Online": false, "LastSeen": "2026-08-27T10:00:00Z",
          "TailscaleIPs": [], "DNSName": "phone.tail08a9a5.ts.net."
        }
      }
    }
    """;

    private static Task<bool> Resolves(string name, CancellationToken ct) => Task.FromResult(true);

    private static Task<bool> DoesNotResolve(string name, CancellationToken ct) => Task.FromResult(false);

    [Fact]
    public void ParseCarriesTheFullyQualifiedNameWithoutItsTrailingDot()
    {
        var rig = Parse().Single(m => m.Name == "rig-1");

        Assert.Equal("rig-1.tail08a9a5.ts.net", rig.DnsName);
        Assert.Equal("100.105.87.52", rig.Address);
    }

    [Fact]
    public void AMachineWithNoTailnetAddressIsSkipped()
    {
        Assert.DoesNotContain(Parse(), m => m.Name == "phone");
    }

    [Fact]
    public async Task AResolvingNameIsWhatGetsStored()
    {
        var machines = await TailscaleService.ResolvableNamesAsync(Parse(), Resolves);

        Assert.Equal("rig-1.tail08a9a5.ts.net", machines.Single(m => m.Name == "rig-1").Host);
    }

    [Fact]
    public async Task ANameThisMachineCannotResolveFallsBackToTheAddress()
    {
        var machines = await TailscaleService.ResolvableNamesAsync(Parse(), DoesNotResolve);

        var rig = machines.Single(m => m.Name == "rig-1");
        Assert.Null(rig.DnsName);
        Assert.Equal("100.105.87.52", rig.Host);
    }

    [Fact]
    public async Task MagicDnsOffForTheTailnetMeansAddressesAndNoLookupAtAll()
    {
        var machines = TailscaleService.Parse(Status.Replace("\"MagicDNSEnabled\": true", "\"MagicDNSEnabled\": false"));

        var resolved = await TailscaleService.ResolvableNamesAsync(
            machines,
            (_, _) => throw new InvalidOperationException("nothing to resolve, so nothing should be looked up"));

        Assert.All(resolved, m => Assert.Null(m.DnsName));
        Assert.Equal("100.105.87.52", resolved.Single(m => m.Name == "rig-1").Host);
    }

    [Fact]
    public void AnOlderClientThatOnlyReportsTheSuffixStillOffersNames()
    {
        var withoutTailnetBlock = TailscaleService.Parse("""
        {
          "MagicDNSSuffix": "tail08a9a5.ts.net",
          "Self": {
            "HostName": "operator-pc", "OS": "windows", "Online": true,
            "TailscaleIPs": ["100.89.154.125"], "DNSName": "operator-pc.tail08a9a5.ts.net."
          }
        }
        """);

        Assert.Equal("operator-pc.tail08a9a5.ts.net", withoutTailnetBlock.Single().DnsName);
    }

    private static IReadOnlyList<TailnetMachine> Parse() => TailscaleService.Parse(Status);
}
