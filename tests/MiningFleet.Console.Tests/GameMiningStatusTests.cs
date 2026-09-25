using System.Text.Json;
using MiningFleet.Agent;
using MiningFleet.Console;
using MiningFleet.Console.Ui;
using MiningFleet.Contracts;
using Spectre.Console;
using Spectre.Console.Testing;

namespace MiningFleet.Console.Tests;

public sealed class GameMiningStatusTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T02:00:00Z");
    private static GameMiningStatusDto Active => new()
    {
        ObservedAt = Now, Active = true, GameName = "DST", Reduced = true,
    };

    [Fact]
    public void Fresh_governor_status_round_trips_through_snapshot_and_renders_without_pausing()
    {
        using var dir = new TempDirectory();
        Write(dir, Active);
        var game = GameMiningStatusReader.Read(dir.Path, Now);
        var snapshot = State(game).Snapshot!;
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var received = JsonSerializer.Deserialize<NodeSnapshotDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var output = Render(State(received!.GameMining));
        Assert.True(received.Miner.Running);
        Assert.Contains("mining · DST · reduced", output);
        Assert.Contains("FPS unavailable", output);
    }

    [Theory]
    [InlineData(-91)]
    [InlineData(6)]
    public void Stale_or_future_heartbeat_does_not_claim_a_running_game(int seconds)
    {
        using var dir = new TempDirectory();
        Write(dir, Active with { ObservedAt = Now.AddSeconds(seconds) });
        Assert.Null(GameMiningStatusReader.Read(dir.Path, Now));
    }

    [Fact]
    public void Closed_game_and_missing_helper_leave_the_old_badge_unchanged()
    {
        using var dir = new TempDirectory();
        Assert.Null(GameMiningStatusReader.Read(dir.Path, Now));
        Write(dir, Active with { Active = false });
        Assert.Null(GameMiningStatusReader.Read(dir.Path, Now));
        Assert.Equal("[green]mining[/]", UiHelpers.StatusBadge(State(null)));
        Assert.Equal("[green]mining[/]", UiHelpers.StatusBadge(State(Active with { Active = false })));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"active\":true,\"gameName\":\"DST\"}")]
    public void Invalid_or_incomplete_telemetry_does_not_break_status(string json)
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, GameMiningStatusReader.FileName), json);
        Assert.Null(GameMiningStatusReader.Read(dir.Path, Now));
    }

    [Fact]
    public void Negative_fps_is_unknown_but_zero_is_a_real_reading()
    {
        using var dir = new TempDirectory();
        Write(dir, Active with { Fps = -1 });
        Assert.Null(GameMiningStatusReader.Read(dir.Path, Now)!.Fps);
        Write(dir, Active with { Fps = 0 });
        var read = GameMiningStatusReader.Read(dir.Path, Now);
        Assert.Equal(0d, read!.Fps);
        Assert.Contains("FPS 0", Render(State(read)));
    }

    [Fact]
    public void Game_name_is_escaped_and_a_measured_fps_is_shown()
    {
        var output = Render(State(Active with { GameName = "game[mod]", Fps = 31.25, Reduced = false }));
        Assert.Contains("game[mod]", output);
        Assert.Contains("FPS 31", output);
        Assert.DoesNotContain("reduced", output);
        Assert.DoesNotContain("unavailable", output);
    }

    [Fact]
    public void Game_context_preserves_no_api_and_watchdog_messages()
    {
        var state = State(Active);
        var snapshot = state.Snapshot!;
        var noApi = state with { Snapshot = snapshot with
        {
            Miner = snapshot.Miner with { Hashrate60s = 0, ApiError = "unreadable" },
        } };
        Assert.Contains("mining (no api)", Render(noApi));
        var failed = state with { Snapshot = snapshot with
        {
            Miner = snapshot.Miner with { Running = false, WatchdogNotice = "retry [1]" },
        } };
        var output = Render(failed);
        Assert.Contains("retry [1]", output);
        Assert.Contains("DST", output);
    }

    private static void Write(TempDirectory dir, GameMiningStatusDto value) =>
        File.WriteAllText(Path.Combine(dir.Path, GameMiningStatusReader.FileName),
            JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static NodeState State(GameMiningStatusDto? game) => new(
        new NodeConfig { Name = "rig", Host = "rig" },
        new NodeSnapshotDto(new AgentInfoDto("rig", "Windows", "1", "1", 1, true),
            new MinerStatusDto { Installed = true, Running = true, Hashrate60s = 4000 },
            new HardwareDto()) { GameMining = game }, null, Now);

    private static string Render(NodeState state)
    {
        var console = new TestConsole();
        console.Profile.Width = 180;
        console.Write(new Markup(UiHelpers.StatusBadge(state)));
        return console.Output;
    }
}
