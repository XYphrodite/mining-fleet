using Spectre.Console;
using Spectre.Console.Testing;
using MiningFleet.Console;
using MiningFleet.Console.Ui;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Guards the two ways out of a menu, both of which are Spectre settings that default to off and
/// neither of which fails loudly when it is missing.
///
/// Escape is the sharper of the two. Without a cancel result Spectre swallows the key and the
/// prompt simply waits; with the wrong one it hands back a value the caller cannot tell from a
/// deliberate choice. Two menus in Miner control read their answer with <c>== "on"</c> and a third
/// falls through a <c>default:</c>, so a careless cancel value there pushes a setting to every
/// selected node - autostart off across the fleet, or the session monitor off, which was measured
/// at 7,092 -> 4,380 H/s. These tests drive the real prompts rather than asserting on the builder,
/// because what is being checked is Spectre's behaviour and not this project's intent.
/// </summary>
public sealed class MenuNavigationTests
{
    private static TestConsole UseTestConsole()
    {
        var console = new TestConsole().Interactive();
        AnsiConsole.Console = console;
        return console;
    }

    private static FleetConfig ConfigWithTwoNodes()
    {
        var config = new FleetConfig();
        config.Nodes.Add(new NodeConfig { Name = "rig-1", Host = "100.64.0.1", Enabled = true });
        config.Nodes.Add(new NodeConfig { Name = "rig-2", Host = "100.64.0.2", Enabled = true });
        return config;
    }

    [Fact]
    public void Down_from_the_last_entry_wraps_to_the_first()
    {
        var console = UseTestConsole();
        // Three downs from the top of a three-entry menu: onto the last, then round to the first.
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        var choice = AnsiConsole.Prompt(UiHelpers.Menu("Action", "< back", "start", "stop", "< back"));

        Assert.Equal("start", choice);
    }

    [Fact]
    public void Up_from_the_first_entry_wraps_to_the_last()
    {
        var console = UseTestConsole();
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        var choice = AnsiConsole.Prompt(UiHelpers.Menu("Action", "< back", "start", "stop", "< back"));

        // Wrapping that only worked downwards would be a worse trap than none at all.
        Assert.Equal("< back", choice);
    }

    [Fact]
    public void Escape_answers_with_the_menus_own_way_out()
    {
        var console = UseTestConsole();
        console.Input.PushKey(ConsoleKey.Escape);

        var choice = AnsiConsole.Prompt(UiHelpers.Menu("Action", "< back", "start", "stop", "< back"));

        Assert.Equal("< back", choice);
    }

    [Fact]
    public void Escape_on_a_two_answer_menu_is_neither_answer()
    {
        var console = UseTestConsole();
        console.Input.PushKey(ConsoleKey.Escape);

        // The exact shape of Miner control's autostart and session-monitor prompts, whose callers
        // read the result with `== "on"`. Anything but the third answer here is a setting pushed
        // to every node the operator selected a moment earlier.
        var answer = AnsiConsole.Prompt(UiHelpers.Menu("Autostart should be", "< back", "on", "off", "< back"));

        Assert.Equal("< back", answer);
        Assert.NotEqual("off", answer);
    }

    /// <summary>
    /// The cancel value has to be one of the choices, or Escape returns something no branch of the
    /// caller's switch handles - which is how a menu ends up silently doing nothing, or doing the
    /// default thing.
    /// </summary>
    [Fact]
    public void A_menu_cannot_escape_to_an_answer_it_does_not_offer()
    {
        Assert.Throws<ArgumentException>(() => UiHelpers.Menu("Action", "< back", "start", "stop"));
    }

    [Fact]
    public void Escape_backs_out_of_the_node_picker()
    {
        var console = UseTestConsole();
        console.Input.PushKey(ConsoleKey.Escape);

        var picked = UiHelpers.SelectNode(ConfigWithTwoNodes(), "Node");

        // Null is what every caller already checks for; it is the "< back" entry's own answer.
        Assert.Null(picked);
    }

    [Fact]
    public void Escape_selects_no_nodes_rather_than_all_of_them()
    {
        var console = UseTestConsole();
        console.Input.PushKey(ConsoleKey.Escape);

        var picked = UiHelpers.SelectNodes(ConfigWithTwoNodes(), "Nodes");

        // "* all enabled nodes" is the entry the cursor starts on, so a cancel result built from
        // the highlighted item would turn a mis-key into a fleet-wide command.
        Assert.Empty(picked);
    }

    [Fact]
    public void Escape_discards_a_selection_made_before_it()
    {
        var console = UseTestConsole();
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.Escape);

        var picked = UiHelpers.SelectNodes(ConfigWithTwoNodes(), "Nodes");

        // Escape means "never mind", not "confirm what I have so far".
        Assert.Empty(picked);
    }
}
