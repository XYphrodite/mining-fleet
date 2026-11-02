using MiningFleet.Agent;
using MiningFleet.Console.Ui;
using MiningFleet.Contracts;
using Spectre.Console;
using Spectre.Console.Testing;

namespace MiningFleet.Console.Tests;

public sealed class LifetimeIncomeTests
{
    [Fact]
    public void Agent_tracker_integrates_cpu_gpu_and_energy_sanitizes_pool_and_persists_across_restarts()
    {
        using var dir = new TempDirectory();
        var t0 = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

        var tracker = new LifetimeTracker(dir.Path, t0);
        tracker.Sample(
            cpuRunning: true,
            cpuHashrate: 5_000,
            gpuRunning: true,
            gpuPool: "stratum+tcp://secretWallet123@prl-eu.kryptex.network:7048/worker1",
            gpuAlgorithm: "pearlhash",
            gpuUnit: "H/s",
            gpuHashrate: 60e12,
            powerWatts: 200,
            elapsedSeconds: 30,
            now: t0.AddSeconds(30));
        tracker.Flush();

        var filePath = Path.Combine(dir.Path, LifetimeTracker.FileName);
        Assert.True(File.Exists(filePath));
        var rawJson = File.ReadAllText(filePath);
        Assert.DoesNotContain("secretWallet123", rawJson);
        Assert.DoesNotContain("worker1", rawJson);
        Assert.Contains("prl-eu.kryptex.network:7048", rawJson);

        var reloaded = new LifetimeTracker(dir.Path, t0.AddHours(1));
        var snap = reloaded.Snapshot();

        Assert.Equal(t0, snap.StartedAt);
        Assert.Equal(30, snap.ActiveMiningSeconds, 6);
        Assert.Equal(30, snap.CpuMiningSeconds, 6);
        Assert.Equal(30, snap.GpuMiningSeconds, 6);
        Assert.Equal(150_000, snap.TotalCpuHashes, 6);
        Assert.Equal(0.2 * (30.0 / 3600.0), snap.TotalEnergyKwh, 9);
        Assert.Single(snap.GpuBuckets);
        Assert.Equal(60e12 * (30.0 / 86400.0), snap.GpuBuckets[0].TotalHashrateDays, 2);
    }

    [Fact]
    public void Agent_tracker_clamps_long_sleep_gaps_and_ignores_idle_ticks()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var tracker = new LifetimeTracker(basePath: null, now: t0);

        // Idle machine accumulates nothing.
        tracker.Sample(false, 5_000, false, null, null, null, null, 120, 15, t0.AddSeconds(15));
        Assert.Equal(0, tracker.Snapshot().ActiveMiningSeconds);
        Assert.Equal(0, tracker.Snapshot().TotalCpuHashes);
        Assert.Equal(0, tracker.Snapshot().TotalEnergyKwh);

        // 8-hour sleep gap is clamped to MaxSampleSeconds (60s).
        tracker.Sample(true, 1_000, false, null, null, null, null, 100, 8 * 3600, t0.AddHours(8));
        var snap = tracker.Snapshot();
        Assert.Equal(LifetimeTracker.MaxSampleSeconds, snap.ActiveMiningSeconds, 6);
        Assert.Equal(1_000 * LifetimeTracker.MaxSampleSeconds, snap.TotalCpuHashes, 6);
    }

    [Fact]
    public void Console_ledger_starts_from_zero_locks_historical_difficulty_and_persists_without_secrets()
    {
        using var dir = new TempDirectory();
        var configPath = Path.Combine(dir.Path, "fleet.json");
        var ledgerPath = Path.Combine(dir.Path, LifetimeIncomeLedger.FileName);

        var config = FleetConfig.Load(configPath);
        config.Electricity = new ElectricityConfig { Currency = "RUB", PricePerKwh = 5.0 };
        config.Pool = new PoolConfig { Wallet = "48secretMoneroWalletAddress999" };
        config.GpuMiner = new GpuMinerConfig { Enabled = true, PoolUrl = "prl.kryptex.network:7048", User = "0xSecretGpuWallet/rig1" };
        config.Nodes = [new NodeConfig { Name = "R5-3600", Host = "100.64.0.1" }];
        config.Save();

        var t0 = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var day1Net = new PoolNetworkStats(1e8, 1000, NetworkHashrate: 3_000_000_000, 3.6e11, 1, BlockRewardXmr: 0.6, BlockTimeSeconds: 120, Price: 15_000, "RUB");
        var coinQuotes = new Dictionary<string, NodeGpuQuote>(StringComparer.OrdinalIgnoreCase)
        {
            ["prl"] = new("PRL", CoinsPerHashrateDay: 2.0e-14, Price: 50.0),
        };

        var stateDay1 = MakeState(
            config.Nodes[0],
            new NodeLifetimeDto
            {
                StartedAt = t0,
                UpdatedAt = t0.AddDays(1),
                ActiveMiningSeconds = 86_400,
                PoweredMiningSeconds = 86_400,
                CpuMiningSeconds = 86_400,
                GpuMiningSeconds = 86_400,
                TotalCpuHashes = 5_000.0 * 86_400.0,
                TotalEnergyKwh = 0.2 * 24.0, // 4.8 kWh -> 24 RUB
                GpuBuckets = [new GpuLifetimeBucketDto("prl.kryptex.network:7048", "pearlhash", "H/s", 86_400, 50e12)],
            });

        var ledger = new LifetimeIncomeLedger(ledgerPath);
        var summary1 = ledger.UpdateAndCalculate([stateDay1], config, day1Net, xmrPrice: 15_000, coinQuotes: coinQuotes);

        var expectedDay1Xmr = 5_000.0 * Economics.XmrPerHashDay(day1Net)!.Value;
        var expectedDay1Prl = 50e12 * 2.0e-14; // 1.0 PRL
        var node1 = Assert.Single(summary1.Nodes);
        Assert.Equal(expectedDay1Xmr, node1.CpuXmr, 9);
        Assert.Equal(expectedDay1Prl, Assert.Single(node1.GpuCoins).Amount, 9);
        Assert.Equal(4.8, node1.EnergyKwh, 6);
        Assert.Equal(24.0, node1.ElectricityCost, 6);
        Assert.Equal(expectedDay1Xmr * 15_000 + 50.0, node1.TotalRevenue!.Value, 6);

        // Day 2: network hash rate doubles (yield halves). Day 1's banked XMR must stay intact!
        var day2Net = day1Net with { NetworkHashrate = 6_000_000_000 };
        var stateDay2 = MakeState(
            config.Nodes[0],
            new NodeLifetimeDto
            {
                StartedAt = t0,
                UpdatedAt = t0.AddDays(2),
                ActiveMiningSeconds = 172_800,
                PoweredMiningSeconds = 172_800,
                CpuMiningSeconds = 172_800,
                GpuMiningSeconds = 86_400,
                TotalCpuHashes = 10_000.0 * 86_400.0, // +5000 H/s * 86400s on day 2
                TotalEnergyKwh = 9.6,
                GpuBuckets = [new GpuLifetimeBucketDto("prl.kryptex.network:7048", "pearlhash", "H/s", 86_400, 50e12)],
            });

        var summary2 = ledger.UpdateAndCalculate([stateDay2], config, day2Net, xmrPrice: 15_000, coinQuotes: coinQuotes);
        var expectedDay2DeltaXmr = 5_000.0 * Economics.XmrPerHashDay(day2Net)!.Value;
        Assert.Equal(expectedDay1Xmr + expectedDay2DeltaXmr, summary2.TotalCpuXmr, 9);

        // Reload from disk and verify no secrets were persisted.
        var ledgerText = File.ReadAllText(ledgerPath);
        Assert.DoesNotContain("48secretMoneroWalletAddress999", ledgerText);
        Assert.DoesNotContain("0xSecretGpuWallet", ledgerText);

        var reloaded = new LifetimeIncomeLedger(ledgerPath)
            .UpdateAndCalculate([stateDay2], config, day2Net, xmrPrice: 15_000, coinQuotes: coinQuotes);
        Assert.Equal(summary2.TotalCpuXmr, reloaded.TotalCpuXmr, 9);
        Assert.Equal(9.6, reloaded.TotalEnergyKwh, 6);
    }

    [Fact]
    public void Console_ledger_holds_pending_work_during_pool_outage_and_settles_later_with_zero_loss()
    {
        var config = new FleetConfig
        {
            Electricity = new ElectricityConfig { Currency = "RUB", PricePerKwh = 5.0 },
            Nodes = [new NodeConfig { Name = "Xeon", Host = "100.64.0.2" }],
        };
        var t0 = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var ledger = new LifetimeIncomeLedger();

        var snap1 = MakeState(config.Nodes[0], new NodeLifetimeDto
        {
            StartedAt = t0,
            UpdatedAt = t0.AddHours(6),
            ActiveMiningSeconds = 21_600,
            PoweredMiningSeconds = 21_600,
            CpuMiningSeconds = 21_600,
            GpuMiningSeconds = 21_600,
            TotalCpuHashes = 100_000_000,
            TotalEnergyKwh = 1.2,
            GpuBuckets = [new GpuLifetimeBucketDto("xtm-c29.kryptex.network:7040", "CR29", "g/s", 21_600, 1.5)],
        });

        // Outage: network and GPU quotes are unavailable.
        var duringOutage = ledger.UpdateAndCalculate([snap1], config, network: null, xmrPrice: null, coinQuotes: null);
        Assert.True(duringOutage.HasMissingRevenue);
        Assert.Null(duringOutage.TotalRevenue);

        // Recovery: pools come back and node has hashed another 50M hashes.
        var snap2 = MakeState(config.Nodes[0], snap1.Snapshot!.Lifetime! with
        {
            UpdatedAt = t0.AddHours(9),
            TotalCpuHashes = 150_000_000,
            GpuBuckets = [new GpuLifetimeBucketDto("xtm-c29.kryptex.network:7040", "CR29", "g/s", 32_400, 2.25)],
        });
        var net = new PoolNetworkStats(1e8, 1000, 3_000_000_000, 3.6e11, 1, 0.6, 120, 15_000, "RUB");
        var quotes = new Dictionary<string, NodeGpuQuote>(StringComparer.OrdinalIgnoreCase)
        {
            ["xtm-c29"] = new("XTM", CoinsPerHashrateDay: 200.0, Price: 0.25),
        };

        var recovered = ledger.UpdateAndCalculate([snap2], config, net, xmrPrice: 15_000, coinQuotes: quotes);
        Assert.False(recovered.HasMissingRevenue);
        Assert.Equal(150_000_000 * (Economics.XmrPerHashDay(net)!.Value / 86400.0), recovered.TotalCpuXmr, 9);
        Assert.Equal(2.25 * 200.0, Assert.Single(recovered.TotalGpuCoins).Amount, 6);
    }

    [Fact]
    public void Console_ledger_survives_agent_os_reinstall_preserves_offline_nodes_and_covers_unpowered_seconds()
    {
        var rig1 = new NodeConfig { Name = "R5-1600", Host = "100.64.0.3", PowerFallbackWatts = 120 };
        var rig2 = new NodeConfig { Name = "Offline-Rig", Host = "100.64.0.4" };
        var config = new FleetConfig
        {
            Electricity = new ElectricityConfig { Currency = "RUB", PricePerKwh = 6.0 },
            Nodes = [rig1, rig2],
        };
        var net = new PoolNetworkStats(1e8, 1000, 3_000_000_000, 3.6e11, 1, 0.6, 120, 15_000, "RUB");
        var t0 = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var ledger = new LifetimeIncomeLedger();

        // Initial run: rig1 has unmeasured power (PoweredMiningSeconds = 0), rig2 is online.
        var s1 = MakeState(rig1, new NodeLifetimeDto
        {
            StartedAt = t0,
            UpdatedAt = t0.AddHours(1),
            ActiveMiningSeconds = 3600,
            PoweredMiningSeconds = 0,
            CpuMiningSeconds = 3600,
            TotalCpuHashes = 18_000_000,
            TotalEnergyKwh = 0,
        });
        var s2 = MakeState(rig2, new NodeLifetimeDto
        {
            StartedAt = t0,
            UpdatedAt = t0.AddHours(1),
            ActiveMiningSeconds = 3600,
            PoweredMiningSeconds = 3600,
            CpuMiningSeconds = 3600,
            TotalCpuHashes = 36_000_000,
            TotalEnergyKwh = 0.25,
        });

        var first = ledger.UpdateAndCalculate([s1, s2], config, net, xmrPrice: 15_000);
        Assert.Equal(0.12, first.ForNode("R5-1600")!.EnergyKwh, 6); // 120W * 1h = 0.12 kWh

        // Later: rig1 had OS reinstalled (new StartedAt, counter restarted from 0 -> 9M), and rig2 is offline.
        var s1Reinstalled = MakeState(rig1, new NodeLifetimeDto
        {
            StartedAt = t0.AddDays(5),
            UpdatedAt = t0.AddDays(5).AddMinutes(30),
            ActiveMiningSeconds = 1800,
            PoweredMiningSeconds = 1800,
            CpuMiningSeconds = 1800,
            TotalCpuHashes = 9_000_000,
            TotalEnergyKwh = 0.06,
        });
        var s2Offline = new NodeState(rig2, null, "unreachable", t0.AddDays(5));

        var second = ledger.UpdateAndCalculate([s1Reinstalled, s2Offline], config, net, xmrPrice: 15_000);
        var r1After = second.ForNode("R5-1600")!;
        var r2After = second.ForNode("Offline-Rig")!;

        Assert.Equal(27_000_000, r1After.TotalCpuHashes, 6);
        Assert.Equal(0.18, r1After.EnergyKwh, 6);
        Assert.False(r2After.Online);
        Assert.True(r2After.HasData);
        Assert.Equal(36_000_000, r2After.TotalCpuHashes, 6);
    }

    [Fact]
    public void Lifetime_table_and_dashboard_render_hostile_names_offline_tags_and_upgrade_badges_safely()
    {
        var hostile = new NodeConfig { Name = "rig[4060]", Host = "100.64.0.10" };
        var legacy = new NodeConfig { Name = "old-agent", Host = "100.64.0.11" };
        var offline = new NodeConfig { Name = "sleeping-rig", Host = "100.64.0.12" };
        var config = new FleetConfig
        {
            Electricity = new ElectricityConfig { Currency = "RUB", PricePerKwh = 5.0 },
            Nodes = [hostile, legacy, offline],
        };

        var t0 = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
        var net = new PoolNetworkStats(1e8, 1000, 3_000_000_000, 3.6e11, 1, 0.6, 120, 15_000, "RUB");
        var coinQuotes = new Dictionary<string, NodeGpuQuote>(StringComparer.OrdinalIgnoreCase)
        {
            ["prl"] = new("PRL", 2.0e-14, 50.0),
        };
        var ledger = new LifetimeIncomeLedger();

        // Seed sleeping-rig before it goes offline.
        ledger.UpdateAndCalculate(
            [MakeState(offline, new NodeLifetimeDto
            {
                StartedAt = t0,
                UpdatedAt = t0.AddHours(2),
                ActiveMiningSeconds = 7200,
                PoweredMiningSeconds = 7200,
                CpuMiningSeconds = 7200,
                TotalCpuHashes = 36_000_000,
                TotalEnergyKwh = 0.3,
            })],
            config, net, 15_000, coinQuotes);

        var states = new[]
        {
            MakeState(hostile, new NodeLifetimeDto
            {
                StartedAt = t0,
                UpdatedAt = t0.AddHours(4),
                ActiveMiningSeconds = 14_400,
                PoweredMiningSeconds = 14_400,
                CpuMiningSeconds = 14_400,
                GpuMiningSeconds = 14_400,
                TotalCpuHashes = 72_000_000,
                TotalEnergyKwh = 0.8,
                GpuBuckets = [new GpuLifetimeBucketDto("prl.kryptex.network:7048", "pearlhash", "H/s", 14_400, 25e12)],
            }),
            MakeState(legacy, lifetime: null),
            new NodeState(offline, null, "timeout", t0.AddHours(4)),
        };

        var lifetime = ledger.UpdateAndCalculate(states, config, net, 15_000, coinQuotes);
        var economics = Economics.Calculate(states, config, net, 15_000);
        var money = MoneyFormat.Single("RUB");

        var console = new TestConsole();
        console.Profile.Width = 200;
        console.Write(EconomicsScreen.BuildLifetimeTable(lifetime, money));
        console.Write(Dashboard.RenderView(states, economics, money, net, 15_000, [], [], t0.AddHours(4), lifetime: lifetime));

        var output = console.Output;
        Assert.Contains("All-time per node", output);
        Assert.Contains("Fleet all-time", output);
        Assert.Contains("rig[4060]", output);
        Assert.Contains("sleeping-rig (offline)", output);
        Assert.Contains("upgrade agent", output);
        Assert.Contains("All-time income", output);
        Assert.Contains("PRL", output);
        Assert.Contains("XMR", output);
    }

    private static NodeState MakeState(NodeConfig node, NodeLifetimeDto? lifetime)
    {
        var snap = new NodeSnapshotDto(
            new AgentInfoDto(node.Name, "Windows", "1.17.39", "1", 3600, true),
            new MinerStatusDto { Installed = true, Running = true, Hashrate60s = 5_000, UptimeSeconds = 3600 },
            new HardwareDto { CpuName = "Ryzen", LogicalCores = 12, EstimatedPowerWatts = 150, PowerIsMeasured = true })
        {
            Lifetime = lifetime,
        };
        return new NodeState(node, snap, null, DateTimeOffset.UtcNow);
    }
}
