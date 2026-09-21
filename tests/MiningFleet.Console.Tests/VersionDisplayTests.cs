using System.Reflection;

namespace MiningFleet.Console.Tests;

public sealed class VersionDisplayTests
{
    [Fact]
    public void Console_version_is_present_and_escaped_for_main_screen()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        // Main screen does Markup.Escape(vX.Y.Z) — version must survive Spectre markup.
        var escaped = MiningFleet.Console.Ui.UiHelpers.Escape($"v{ver}");
        Assert.Contains(ver, escaped);
        Assert.Matches(@"^\d+\.\d+\.\d+(\.\d+)?$", ver);
    }

    [Fact]
    public void Main_screen_version_line_is_markup_safe()
    {
        var fakeVersion = "1.17.10";
        var line = $"v{Spectre.Console.Markup.Escape(fakeVersion)}";
        Assert.Equal("v1.17.10", line);
    }
}
