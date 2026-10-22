using System.Text.Json;
using MiningFleet.Agent;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Guards the reading of SRBMiner-MULTI's stats. The first version of this parser guessed the
/// field names from nothing, and on a live node the miner ran at 59 TH/s while the fleet showed
/// no hashrate and no devices. The sample below is what SRBMiner-MULTI 3.6.1 really answered on an
/// RTX 4060 mining pearlhash on Kryptex, with the wallet replaced.
/// </summary>
public sealed class SrMinerStatsTests
{
    private const string LiveSample = """
        {"rig_name":"SRBMiner-Multi-Rig","miner_version":"3.6.1","mining_time":22,"total_cpu_workers":0,
         "total_gpu_workers":1,"total_workers":1,
         "cpu_devices":[{"id":0,"device":"cpu0","model":"12th Gen Intel(R) Core(TM) i7-12700KF","package_power":0.0}],
         "gpu_devices":[{"id":0,"topology_id":"0000:01:00.0","bus_id":1,"device":"gpu0","vendor":"nvidia",
           "model":"nvidia_geforce_rtx_4060","fan_speed_percent":58,"core_clock":2550,"memory_clock":8251,
           "temperature":79,"asic_power":0,"off_temperature":100}],
         "algorithms":[{"id":0,"name":"pearlhash",
           "pool":{"pool":"prl.kryptex.network:7048","wallet":"prl1example/mks68i7rtx",
                   "time_connected":"2026-10-22 16:29:29","uptime":19,"difficulty":0.987,"last_job_received":19,"latency":0},
           "shares":{"total":3,"accepted":2,"rejected":1,"avg_find_time":19},
           "hashrate":{"1min":56013842775641.0,"1hr":0.0,"6hr":0.0,"12hr":0.0,"cpu":{"total":0.0},
                       "gpu":{"gpu0":59454100883390.32,"total":59454100883390.32}},
           "gpu_accepted_shares":{"gpu0":2},"gpu_rejected_shares":{"gpu0":1},"gpu_compute_errors":{"gpu0":0}}]}
        """;

    private static GpuMinerStatusDto Merge(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var empty = new GpuMinerStatusDto { Running = true, Software = GpuMinerSoftware.SrMiner, Algorithm = "pearlhash" };
        return (GpuMinerStatusDto)typeof(GpuMinerService)
            .GetMethod("MergeSrMiner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [empty, doc.RootElement])!;
    }

    [Fact]
    public void The_live_sample_gives_the_cards_total_rate_in_raw_hashes()
    {
        var status = Merge(LiveSample);

        Assert.Equal(59454100883390.32, status.Hashrate);
        Assert.Equal("H/s", status.HashrateUnit);
    }

    [Fact]
    public void The_live_sample_gives_the_card_with_its_own_rate_and_sensors()
    {
        var device = Assert.Single(Merge(LiveSample).Devices!);

        Assert.Equal("nvidia_geforce_rtx_4060", device.Name);
        Assert.Equal(59454100883390.32, device.Hashrate);
        Assert.Equal(79, device.TemperatureC);
        Assert.Equal(58, device.FanPercent);
        Assert.Equal(2550, device.CoreClockMhz);
        Assert.Equal(8251, device.MemoryClockMhz);
    }

    [Fact]
    public void Shares_are_read_from_the_nested_object_and_there_is_no_stale_counter()
    {
        var status = Merge(LiveSample);

        Assert.Equal(2, status.AcceptedShares);
        Assert.Equal(1, status.RejectedShares);
        Assert.Null(status.StaleShares);
    }

    [Fact]
    public void The_pool_is_the_host_and_never_the_operators_login()
    {
        var status = Merge(LiveSample);

        Assert.Equal("prl.kryptex.network:7048", status.Pool);
        Assert.Equal("pearlhash", status.Algorithm);
        Assert.DoesNotContain("prl1", status.Pool);
    }

    [Fact]
    public void A_card_still_building_its_first_average_falls_back_to_the_one_minute_rate()
    {
        var status = Merge("""
            {"gpu_devices":[{"id":0,"device":"gpu0","model":"x","temperature":50}],
             "algorithms":[{"name":"pearlhash","hashrate":{"1min":1000.0,"gpu":{}}}]}
            """);

        Assert.Equal(1000.0, status.Hashrate);
        Assert.Null(Assert.Single(status.Devices!).Hashrate);
    }

    [Fact]
    public void A_cpu_rate_is_never_mistaken_for_the_cards()
    {
        var status = Merge("""
            {"algorithms":[{"name":"pearlhash","hashrate":{"cpu":{"total":777.0},"gpu":{"total":5.0}}}]}
            """);

        Assert.Equal(5.0, status.Hashrate);
    }

    [Fact]
    public void An_unrecognised_document_blanks_the_cells_instead_of_throwing()
    {
        var status = Merge("""{"something":"else"}""");

        Assert.Null(status.Hashrate);
        Assert.Empty(status.Devices!);
        Assert.Null(status.AcceptedShares);
    }

    [Fact]
    public void An_older_flat_shape_still_reads()
    {
        var status = Merge("""
            {"algorithms":[{"name":"pearlhash","hashrate":123.0,"accepted":4,"rejected":0,"pool":"p:1"}]}
            """);

        Assert.Equal(123.0, status.Hashrate);
        Assert.Equal(4, status.AcceptedShares);
        Assert.Equal("p:1", status.Pool);
    }
}
