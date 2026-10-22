using MiningFleet.Console.Ui;
using MiningFleet.Contracts;
using Spectre.Console.Testing;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Guards how a card's rate and its pool income reach the screen. SRBMiner reports raw hashes per
/// second, so Pearl arrives as 59454100883390 and must be scaled, and the fleet total must not add
/// that to a Cuckaroo29 rate measured in graphs per second.
/// </summary>
public sealed class GpuRateDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly GpuPoolTarget Prl = new("kryptex", "prl", "prl1address");

    private static NodeState Mining(string name, double rate, string unit) =>
        new(new NodeConfig { Name = name, Host = "h" },
            new NodeSnapshotDto(new AgentInfoDto("rig", "Windows", "1", "1", 1, true), new MinerStatusDto(), new HardwareDto())
            {
                GpuMiner = new GpuMinerStatusDto { Running = true, Hashrate = rate, HashrateUnit = unit },
            },
            null, Now);

    [Theory]
    [InlineData(59454100883390.32, "H/s", "TH/s")]
    [InlineData(2.5e15, "H/s", "PH/s")]
    [InlineData(4.2e9, "H/s", "GH/s")]
    [InlineData(950.0, "H/s", "H/s")]
    public void Raw_hashes_are_scaled_to_the_unit_a_person_reads(double rate, string unit, string expected)
    {
        Assert.EndsWith(expected, Economics.FormatGpuRate(rate, unit));
    }

    [Fact]
    public void A_unit_the_miner_named_itself_is_left_exactly_as_it_was()
    {
        Assert.Equal("4.50 g/s", Economics.FormatGpuRate(4.5, "g/s").Replace(',', '.'));
    }

    [Fact]
    public void No_rate_is_a_dash_not_a_zero()
    {
        Assert.Equal("-", Economics.FormatGpuRate(null, "H/s"));
        Assert.Equal("-", Economics.FormatGpuRate(0, "H/s"));
    }

    [Fact]
    public void The_fleet_total_is_summed_per_unit_and_never_across_them()
    {
        var total = Economics.FormatGpuFleetRate([
            Mining("a", 3e13, "H/s"),
            Mining("b", 2e13, "H/s"),
            Mining("c", 4.5, "g/s"),
        ]);

        Assert.Contains("TH/s", total);
        Assert.Contains("g/s", total);
        Assert.Contains(" + ", total);
        Assert.StartsWith("50", total);
    }

    [Fact]
    public void A_fleet_with_no_card_mining_has_no_gpu_total()
    {
        Assert.Equal("-", Economics.FormatGpuFleetRate([]));
    }

    [Fact]
    public void The_gpu_cell_shows_a_scaled_pearl_rate()
    {
        var badge = UiHelpers.GpuBadge(new GpuMinerStatusDto { Running = true, Hashrate = 5.9e13, HashrateUnit = "H/s" });

        Assert.Contains("TH/s", badge);
    }

    [Fact]
    public void The_income_panel_renders_a_wallet_that_has_been_watched_for_a_day()
    {
        var income = new GpuIncome(
            new GpuPoolStats(Prl, 0.0, 0.002, 0.0, 1.0, [], Now),
            [new(Now.AddHours(-24), 0.001), new(Now, 0.002)]);
        var console = new TestConsole();
        console.Profile.Width = 200;

        console.Write(GpuIncomePanel.Render([new("mks68i7rtx", Prl, income, 5.0)], MoneyFormat.Single("RUB"), Now));

        Assert.Contains("PRL — pool wallet income", console.Output);
        Assert.Contains("mks68i7rtx", console.Output);
        Assert.Contains("24h mean", console.Output);
        Assert.Contains("0m ago", console.Output);
        Assert.DoesNotContain("STALE", console.Output);
    }

    [Fact]
    public void The_income_panel_says_so_when_there_is_not_enough_history_for_a_forecast()
    {
        var income = new GpuIncome(new GpuPoolStats(Prl, 0.0, 0.002, 0.0, 1.0, [], Now), [new(Now, 0.002)]);
        var console = new TestConsole();
        console.Profile.Width = 200;

        console.Write(GpuIncomePanel.Render([new("rig", Prl, income, null)], MoneyFormat.Single("RUB"), Now));

        Assert.Contains("need 1h history", console.Output);
        Assert.Contains("unavailable", console.Output);
    }

    [Fact]
    public void The_income_panel_flags_stale_data_and_unverified_config()
    {
        var income = new GpuIncome(
            new GpuPoolStats(Prl, 0.0, 0.002, 0.0, 1.0, [], Now.AddMinutes(-30)),
            [new(Now.AddHours(-24), 0.001), new(Now.AddMinutes(-30), 0.002)]);
        var console = new TestConsole();
        console.Profile.Width = 200;

        console.Write(GpuIncomePanel.Render([new("rig", Prl, income, null, ConfigUnverified: true)], MoneyFormat.Single("RUB"), Now));

        Assert.Contains("STALE", console.Output);
        Assert.Contains("config unverified", console.Output);
    }

    [Fact]
    public void The_income_panel_survives_markup_in_a_node_name_and_a_pool_that_did_not_answer()
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        console.Write(GpuIncomePanel.Render([new("rig[windows]", Prl, null, null)], MoneyFormat.Single("RUB"), Now));

        Assert.Contains("rig[windows]", console.Output);
        Assert.Contains("pool unavailable", console.Output);
    }

    [Fact]
    public void No_wallets_renders_nothing()
    {
        var console = new TestConsole();

        console.Write(GpuIncomePanel.Render([], MoneyFormat.Single("RUB"), Now));

        Assert.True(string.IsNullOrWhiteSpace(console.Output));
    }

    [Fact]
    public void The_xtm_income_panel_renders_forecast_value_in_fleet_currency()
    {
        var account = XtmAccount.From("taric29.luckypool.io:3111", "wallet")!;
        var income = new XtmIncome(Now, Now.AddHours(-30), [new XtmRewardWindow(24, 800, Now)]);
        var money = new MoneyFormat("RUB", "USD", 0.012);
        var console = new TestConsole();
        console.Profile.Width = 200;

        console.Write(XtmIncomePanel.Render([new("re-7lqd67ahcm0r", account, income, Price: 0.1)], Now, money));

        Assert.Contains("Value / next 24h", console.Output);
        Assert.Contains("RUB", console.Output);
        Assert.Contains("USD", console.Output);
    }
}