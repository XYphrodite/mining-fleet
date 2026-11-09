using System.Net;
using System.Text;
using System.Text.Json;
using MiningFleet.Agent;
using MiningFleet.Console;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

public sealed class AgentTariffTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fleet-tariff-tests-{Guid.NewGuid():N}");

    public AgentTariffTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public void MinerConfigStore_persists_tariff_survives_partial_patches_and_clears_explicitly()
    {
        var store = new MinerConfigStore(_dir);
        Assert.Null(store.Current.PricePerKwh);
        Assert.Equal(5.5, store.EffectivePricePerKwh(installedDefault: 5.5));

        var saved = store.Update(new MinerConfigDto { PricePerKwh = 6.8 });
        Assert.Equal(6.8, saved.PricePerKwh);
        Assert.Null(saved.ClearPricePerKwh);
        Assert.Equal(6.8, store.EffectivePricePerKwh(installedDefault: 5.5));

        // Partial updates from MiningSwitch / GPU sync / pool push have PricePerKwh == null
        // and must leave the stored tariff untouched.
        var afterGpuPush = store.Update(new MinerConfigDto
        {
            GpuMiner = new GpuMinerSettingsDto { Enabled = true, Algorithm = "pearlhash" },
        });
        Assert.Equal(6.8, afterGpuPush.PricePerKwh);

        // Reloading from disk keeps the stored tariff.
        var reloaded = new MinerConfigStore(_dir);
        Assert.Equal(6.8, reloaded.Current.PricePerKwh);

        // Zero (free electricity) and tiny values (0.01) are valid tariffs.
        Assert.Equal(0.0, reloaded.Update(new MinerConfigDto { PricePerKwh = 0.0 }).PricePerKwh);
        Assert.Equal(0.01, reloaded.Update(new MinerConfigDto { PricePerKwh = 0.01 }).PricePerKwh);

        // Explicit clear reverts to null without writing ClearPricePerKwh to miner.json.
        var cleared = reloaded.Update(new MinerConfigDto { ClearPricePerKwh = true });
        Assert.Null(cleared.PricePerKwh);
        Assert.Null(cleared.ClearPricePerKwh);
        Assert.DoesNotContain("ClearPricePerKwh\": true", File.ReadAllText(Path.Combine(_dir, "miner.json")));
    }

    [Fact]
    public void Economics_prefers_agent_tariff_from_snapshot_over_local_config_and_default()
    {
        var config = new FleetConfig();
        config.Electricity.PricePerKwh = 5.0;
        config.Electricity.Currency = "RUB";

        // Console fleet.json has NO per-node PricePerKwh (like secondary console desktop-ib88isg),
        // while the agents themselves report 0.01 and 6.80 in /api/v1/status.
        var workstation = OnlineNode("re-7lqd67ahcm0r", watts: 250, agentTariff: 0.01, localTariff: null);
        var xeon = OnlineNode("desktop-ib88isg", watts: 250, agentTariff: 6.80, localTariff: 4.00);
        var freeNode = OnlineNode("solar-rig", watts: 250, agentTariff: 0.0, localTariff: null);
        config.Nodes.AddRange([workstation.Node, xeon.Node, freeNode.Node]);

        var econ = Economics.Calculate([workstation, xeon, freeNode], config, network: null, price: null);

        // 250 W * 24h = 6 kWh/day per machine:
        // workstation: 6 * 0.01 = 0.06
        // xeon:        6 * 6.80 = 40.80 (agent 6.80 wins over stale local 4.00)
        // freeNode:    6 * 0.00 = 0.00  (agent 0.00 wins over fleet default 5.00)
        Assert.Equal(0.06, econ.ForNode("re-7lqd67ahcm0r")!.CostPerDay, precision: 4);
        Assert.Equal(40.80, econ.ForNode("desktop-ib88isg")!.CostPerDay, precision: 4);
        Assert.Equal(0.00, econ.ForNode("solar-rig")!.CostPerDay, precision: 4);
        Assert.Equal(40.86, econ.CostPerDay, precision: 4);
    }

    [Fact]
    public void LifetimeIncome_remembers_agent_tariff_when_node_goes_offline_and_clears_on_reset()
    {
        var ledgerPath = Path.Combine(_dir, "lifetime-income.json");
        var config = new FleetConfig
        {
            Electricity = new ElectricityConfig { Currency = "RUB", PricePerKwh = 5.0 },
        };
        var nodeCfg = new NodeConfig { Name = "mks68i7rtx", Host = "100.64.0.2", PricePerKwh = null };
        config.Nodes.Add(nodeCfg);

        var started = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var online = new NodeState(
            nodeCfg,
            new NodeSnapshotDto(
                new AgentInfoDto("mks68i7rtx", "windows", "1.17.47.0", ApiVersion.Current, 3600, true),
                new MinerStatusDto { Running = true, Installed = true, Hashrate60s = 5000 },
                new HardwareDto { PowerIsMeasured = true, EstimatedPowerWatts = 200 })
            {
                PricePerKwh = 6.8,
                Lifetime = new NodeLifetimeDto
                {
                    StartedAt = started,
                    UpdatedAt = started.AddHours(10),
                    ActiveMiningSeconds = 36_000,
                    PoweredMiningSeconds = 36_000,
                    CpuMiningSeconds = 36_000,
                    TotalCpuHashes = 180_000_000,
                    TotalEnergyKwh = 10.0,
                },
            },
            Error: null,
            PolledAt: started.AddHours(10));

        var ledger = new LifetimeIncomeLedger(ledgerPath);
        var first = ledger.UpdateAndCalculate([online], config, network: null, xmrPrice: null);
        Assert.Equal(68.0, first.ForNode("mks68i7rtx")!.ElectricityCost, precision: 4);

        // New console run while node is offline and fleet.json still has PricePerKwh == null:
        // lifetime-income.json remembers 6.8 from the agent rather than dropping back to 5.0.
        var offline = new NodeState(nodeCfg, Snapshot: null, Error: "timeout", PolledAt: started.AddHours(11));
        var reloadedLedger = new LifetimeIncomeLedger(ledgerPath);
        var whileOffline = reloadedLedger.UpdateAndCalculate([offline], config, network: null, xmrPrice: null);
        Assert.Equal(68.0, whileOffline.ForNode("mks68i7rtx")!.ElectricityCost, precision: 4);

        // Once the upgraded (>= 1.17.47) agent comes back with PricePerKwh == null (cleared to default),
        // the ledger drops the remembered override and uses the 5.0 fleet default.
        var clearedOnline = online with
        {
            Snapshot = online.Snapshot! with { PricePerKwh = null },
        };
        var afterClear = reloadedLedger.UpdateAndCalculate([clearedOnline], config, network: null, xmrPrice: null);
        Assert.Equal(50.0, afterClear.ForNode("mks68i7rtx")!.ElectricityCost, precision: 4);
    }

    [Fact]
    public async Task TariffAction_verifies_readback_and_catches_older_agents()
    {
        var node = new NodeConfig { Name = "test-node", Host = "127.0.0.1", Port = 47800 };
        MinerConfigDto? lastPatch = null;
        using var oldClient = new AgentClient(node, new StubHandler(async req =>
        {
            lastPatch = JsonSerializer.Deserialize<MinerConfigDto>(
                await req.Content!.ReadAsStringAsync(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            // Simulate pre-1.17.47 agent that drops PricePerKwh on the floor.
            return JsonResponse(new MinerConfigDto());
        }));

        var oldResult = await FleetService.TariffAction(6.8, clear: false, "RUB", 5.0)(oldClient, CancellationToken.None);
        Assert.NotNull(oldResult);
        Assert.False(oldResult!.Ok);
        Assert.Contains("upgrade-agents", oldResult.Message);
        Assert.Equal(6.8, lastPatch?.PricePerKwh);

        // Simulate >= 1.17.47 agent that persists and echoes PricePerKwh and honors ClearPricePerKwh.
        var store = new MinerConfigStore(_dir);
        using var newClient = new AgentClient(node, new StubHandler(async req =>
        {
            if (req.Method == HttpMethod.Get)
                return JsonResponse(store.Current);

            var patch = JsonSerializer.Deserialize<MinerConfigDto>(
                await req.Content!.ReadAsStringAsync(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            return JsonResponse(store.Update(patch));
        }));

        var setRes = await FleetService.TariffAction(0.01, clear: false, "RUB", 5.0)(newClient, CancellationToken.None);
        Assert.True(setRes!.Ok);
        Assert.Contains("0.01 RUB/kWh", setRes.Message);

        var getRes = await FleetService.TariffAction(null, clear: false, "RUB", 5.0)(newClient, CancellationToken.None);
        Assert.True(getRes!.Ok);
        Assert.Contains("0.01 RUB/kWh", getRes.Message);

        var clearRes = await FleetService.TariffAction(null, clear: true, "RUB", 5.0)(newClient, CancellationToken.None);
        Assert.True(clearRes!.Ok);
        Assert.Null(store.Current.PricePerKwh);
    }

    private static NodeState OnlineNode(string name, double watts, double? agentTariff, double? localTariff) =>
        new(
            new NodeConfig { Name = name, Host = "127.0.0.1", Enabled = true, PowerFallbackWatts = watts, PricePerKwh = localTariff },
            new NodeSnapshotDto(
                new AgentInfoDto(name, "windows", "1.17.47.0", ApiVersion.Current, 100, true),
                new MinerStatusDto { Running = true, Installed = true, Hashrate60s = 5000 },
                new HardwareDto { PowerIsMeasured = false })
            {
                PricePerKwh = agentTariff,
            },
            Error: null,
            PolledAt: DateTimeOffset.UtcNow);

    private static HttpResponseMessage JsonResponse<T>(T value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request);
    }
}
