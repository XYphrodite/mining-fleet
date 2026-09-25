using MiningFleet.Contracts;
using MiningFleet.Console.Ui;
using Spectre.Console;
using Spectre.Console.Testing;

namespace MiningFleet.Console.Tests;

public sealed class NodeActivityTests
{
    [Theory]
    [InlineData(false, false, "idle")]
    [InlineData(false, true, "mining (GPU)")]
    [InlineData(true, false, "mining")]
    [InlineData(true, true, "mining")]
    [InlineData(false, null, "idle")]
    [InlineData(true, null, "mining")]
    public void State_and_fleet_activity_include_either_miner(bool cpu, bool? gpu, string expected)
    {
        var state = State(cpu, gpu);
        Assert.Equal(expected, Render(state).Trim());
        Assert.Equal(cpu || gpu == true, state.AnyMining);
        Assert.Equal(cpu, state.Mining);
    }

    [Fact]
    public void Offline_node_is_not_counted_as_mining()
    {
        var state = State(true, true) with { Snapshot = null, Error = "unreachable" };
        Assert.False(state.AnyMining);
        Assert.Contains("offline", Render(state));
    }

    [Fact]
    public void Gpu_activity_preserves_cpu_failure_and_game_context()
    {
        var state = State(false, true);
        state = state with { Snapshot = state.Snapshot! with
        {
            Miner = state.Snapshot.Miner with { WatchdogNotice = "retry [1]" },
            GameMining = new GameMiningStatusDto
            {
                Active = true, GameName = "DST", Reduced = true, ObservedAt = DateTimeOffset.UtcNow,
            },
        } };
        var text = Render(state);
        Assert.Contains("mining (GPU)", text);
        Assert.Contains("CPU: retry [1]", text);
        Assert.Contains("DST", text);
        Assert.Contains("reduced", text);
        Assert.Contains("FPS unavailable", text);
    }

    [Fact]
    public void Gpu_activity_does_not_hide_missing_cpu_api()
    {
        var state = State(true, true);
        state = state with { Snapshot = state.Snapshot! with
        {
            Miner = state.Snapshot.Miner with { Hashrate60s = 0, ApiError = "unreadable" },
        } };
        Assert.Contains("mining (no api)", Render(state));
    }

    [Fact]
    public void Gpu_only_nodes_count_in_fleet_without_adding_stale_cpu_hashrate()
    {
        var states = new[] { State(true, false), State(false, true), State(true, true), State(false, false) };
        Assert.Equal(3, states.Count(s => s.AnyMining));
        var economics = Economics.Calculate(states, new FleetConfig(), null, null);
        Assert.Equal(8000, economics.TotalHashrate);
    }

    private static NodeState State(bool cpu, bool? gpu) => new(
        new NodeConfig { Name = "rig", Host = "rig" },
        new NodeSnapshotDto(new AgentInfoDto("rig", "Windows", "1", "1", 1, true),
            new MinerStatusDto { Installed = true, Running = cpu, Hashrate60s = 4000 },
            new HardwareDto())
        {
            GpuMiner = gpu is { } running ? new GpuMinerStatusDto { Running = running, Hashrate = 4.16 } : null,
        }, null, DateTimeOffset.UtcNow);

    private static string Render(NodeState state)
    {
        var console = new TestConsole();
        console.Profile.Width = 240;
        console.Write(new Markup(UiHelpers.StatusBadge(state)));
        return console.Output;
    }
}
