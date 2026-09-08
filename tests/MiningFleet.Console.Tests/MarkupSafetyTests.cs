using Spectre.Console;
using Spectre.Console.Testing;
using MiningFleet.Console;
using MiningFleet.Console.Ui;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Spectre renders prompts and widgets as markup, so a square bracket in data it did not
/// author is read as a style tag. This has crashed the console twice — once on the OS name
/// tailscale reports (`[windows]`), once on a default install path — and neither showed up
/// until someone opened the screen. These tests exercise the render path with hostile text.
/// </summary>
public sealed class MarkupSafetyTests
{
    private const string Hostile = "rig[windows]";

    private static TestConsole UseTestConsole()
    {
        var console = new TestConsole().Interactive();
        AnsiConsole.Console = console;
        return console;
    }

    private static FleetConfig ConfigWithHostileNames()
    {
        var config = new FleetConfig();
        config.Nodes.Add(new NodeConfig { Name = Hostile, Host = "100.64.0.1", Enabled = true });
        config.Nodes.Add(new NodeConfig { Name = "plain-rig", Host = "100.64.0.2", Enabled = true });
        return config;
    }

    /// <summary>
    /// Spectre 0.57.2 writes single-selection choices literally, so this path does not fail
    /// today even without escaping — verified by removing the escape and watching only the
    /// multi-selection test go red. It is kept as a canary: if a Spectre upgrade starts
    /// treating these choices as markup, this turns red instead of an operator's screen.
    /// </summary>
    [Fact]
    public void SelectNode_renders_a_name_containing_markup()
    {
        var console = UseTestConsole();
        console.Input.PushKey(ConsoleKey.Enter);

        var picked = UiHelpers.SelectNode(ConfigWithHostileNames(), "Node");

        Assert.NotNull(picked);
        Assert.Equal(Hostile, picked!.Name);
    }

    /// <summary>The path that actually crashed: multi-selection choices are parsed as markup.</summary>
    [Fact]
    public void SelectNodes_renders_a_name_containing_markup()
    {
        var console = UseTestConsole();
        // First entry is the "all enabled nodes" choice.
        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.Enter);

        var picked = UiHelpers.SelectNodes(ConfigWithHostileNames(), "Nodes");

        Assert.Equal(2, picked.Count);
    }

    [Fact]
    public void Text_renders_a_default_value_containing_markup()
    {
        var console = UseTestConsole();
        console.Input.PushTextWithEnter("");

        var path = AnsiConsole.Prompt(
            UiHelpers.Text("Path:").AllowEmpty().DefaultValue(@"C:\mining\[rig]\xmrig"));

        // Escaping is for display only: the accepted default must come back untouched.
        Assert.Equal(@"C:\mining\[rig]\xmrig", path);
    }

    [Fact]
    public void Text_returns_typed_markup_characters_unchanged()
    {
        var console = UseTestConsole();
        console.Input.PushTextWithEnter(@"D:\[x]\y");

        var typed = AnsiConsole.Prompt(UiHelpers.Text("Path:"));

        Assert.Equal(@"D:\[x]\y", typed);
    }

    [Fact]
    public void StatusBadge_renders_an_error_containing_markup()
    {
        UseTestConsole();
        var state = new NodeState(
            new NodeConfig { Name = "n", Host = "h" },
            Snapshot: null,
            Error: "connect failed [10061]",
            PolledAt: DateTimeOffset.Now);

        // Would throw if the error text reached the parser unescaped.
        AnsiConsole.MarkupLine(UiHelpers.StatusBadge(state));
    }

    [Theory]
    [InlineData("[windows]")]
    [InlineData("100.64.0.1 [offline]")]
    [InlineData("[[already escaped]]")]
    public void Escape_output_survives_the_markup_parser(string text)
    {
        UseTestConsole();
        AnsiConsole.MarkupLine(UiHelpers.Escape(text));
    }

    [Fact]
    public void GpuBadge_renders_a_notice_containing_markup()
    {
        UseTestConsole();

        // Everything in this DTO is written by the node, not by the console: an algorithm name and
        // a hashrate unit come from the miner's API, and the notice can carry an error message.
        AnsiConsole.MarkupLine(UiHelpers.GpuBadge(new MiningFleet.Contracts.GpuMinerStatusDto
        {
            Running = false,
            Notice = "paused - port [11434] busy",
        }));

        AnsiConsole.MarkupLine(UiHelpers.GpuBadge(new MiningFleet.Contracts.GpuMinerStatusDto
        {
            Running = true,
            Hashrate = 4.48,
            HashrateUnit = "g/s [tari]",
        }));
    }

    [Fact]
    public void GpuShares_renders_every_state_it_can_be_in()
    {
        UseTestConsole();

        // Null and zero are different answers - an agent that cannot say against a card that has
        // nothing to report - and both reach the markup parser.
        AnsiConsole.MarkupLine(UiHelpers.GpuShares(null));
        AnsiConsole.MarkupLine(UiHelpers.GpuShares(new MiningFleet.Contracts.GpuMinerStatusDto { Running = true }));
        AnsiConsole.MarkupLine(UiHelpers.GpuShares(new MiningFleet.Contracts.GpuMinerStatusDto
        {
            Running = true,
            AcceptedShares = 137,
            StaleShares = 4,
        }));
    }
}
