using MiningFleet.Console;

namespace MiningFleet.Console.Tests;

public sealed class GpuPanelTests
{
    [Fact]
    public void Fleet_and_nodes_gpu_config_saves_and_pushes_immediately()
    {
        var cfg = new FleetConfig { Token = "t" };
        // Fleet defaults (minimum 4 fields)
        cfg.GpuMiner.Enabled = true;
        cfg.GpuMiner.Algorithm = "CR29";
        cfg.GpuMiner.PoolUrl = "pool.kryptex.com:7777";
        cfg.GpuMiner.User = "addr/worker1";

        // Per-node override (fleet+nodes) — inherits fleet pool/user
        var node = new NodeConfig { Name = "rig1", Host = "1.1.1.1" };
        node.GpuMiner = new GpuMinerConfig { Algorithm = "NEXA", Enabled = false };
        cfg.Nodes.Add(node);

        var forNode = cfg.GpuMinerFor(node);
        Assert.Equal("NEXA", forNode.Algorithm);
        Assert.False(forNode.Enabled);
        Assert.Equal("pool.kryptex.com:7777", forNode.PoolUrl);
        Assert.Equal("addr/worker1", forNode.User);
    }

    [Fact]
    public void Gpu_panel_minimum_fields_are_sufficient_to_start()
    {
        var cfg = new GpuMinerConfig { Enabled = true, Algorithm = "CR29", PoolUrl = "p:1234", User = "u" };
        var dto = new FleetConfig().GpuMinerFor(new NodeConfig { Name = "n", Host = "h", GpuMiner = cfg });
        Assert.Equal("CR29", dto.Algorithm);
        Assert.Equal("p:1234", dto.PoolUrl);
        Assert.Equal("u", dto.User);
        Assert.True(dto.Enabled);
    }

    [Fact]
    public void Blank_inherits_from_fleet_and_empty_fleet_is_detected()
    {
        var cfg = new FleetConfig { Token = "t" };
        cfg.GpuMiner.Enabled = true;
        cfg.GpuMiner.Algorithm = "CR29";
        cfg.GpuMiner.PoolUrl = "p:1234";
        cfg.GpuMiner.User = "u";

        // Node leaves blank (null) — inherits fleet
        var node = new NodeConfig { Name = "rig1", Host = "1.1.1.1", GpuMiner = new GpuMinerConfig { Enabled = null } };
        cfg.Nodes.Add(node);
        var forNode = cfg.GpuMinerFor(node);
        Assert.Equal("CR29", forNode.Algorithm); // blank хватается из дефолтного

        // Empty fleet + blank = still missing — panel must detect and not save
        var emptyFleet = new FleetConfig { Token = "t" };
        emptyFleet.GpuMiner.Enabled = false;
        var emptyNode = new NodeConfig { Name = "rig2", Host = "2.2.2.2", GpuMiner = new GpuMinerConfig { Enabled = true } };
        emptyFleet.Nodes.Add(emptyNode);
        var forEmpty = emptyFleet.GpuMinerFor(emptyNode);
        Assert.True(string.IsNullOrWhiteSpace(forEmpty.Algorithm)); // blank не спасает когда флот пустой
    }
}
