using MiningFleet.Agent;
using MiningFleet.Console;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Guards the arithmetic behind handing whole cores back to the person at the machine.
///
/// The masks matter more than they look. A reservation that frees two *logical* CPUs on a
/// hyperthreaded core frees no core at all - the sibling of a saturated RandomX thread shares its
/// execution units - so it would read as a fix and change nothing. And a reservation larger than
/// the machine would leave the miner an empty mask, which is not a slow miner but a stopped one.
/// </summary>
public sealed class CpuReservationTests
{
    /// <summary>
    /// An i7-12700KF as Windows actually reports it: eight hyperthreaded P-cores taking logical
    /// 0-15, then four E-cores with one thread each on 16-19. Twelve cores, twenty logical
    /// processors - the shape that makes "two logical per core" wrong.
    /// </summary>
    private static IReadOnlyList<ulong> Hybrid()
    {
        var cores = new List<ulong>();
        for (var p = 0; p < 8; p++) cores.Add((3UL << (p * 2)));      // pairs: 0|1, 2|3, ...
        for (var e = 16; e < 20; e++) cores.Add(1UL << e);            // singles: 16, 17, 18, 19
        return cores;
    }

    [Fact]
    public void Reserving_a_core_frees_both_of_its_threads()
    {
        var mask = CpuReservation.MaskFor(Hybrid(), reserved: 1);

        // Logical 0 and 1 are one physical core. Freeing only one of them would leave the miner
        // running flat out on the other half of the very core the person is trying to use.
        Assert.NotNull(mask);
        Assert.Equal(0UL, mask!.Value & 0b11);
        Assert.Equal(0b100UL, mask.Value & 0b100);
    }

    [Fact]
    public void Two_reserved_cores_are_the_four_logical_cpus_measured_on_the_node()
    {
        var mask = CpuReservation.MaskFor(Hybrid(), reserved: 2);

        // 1048560 is the mask that was set on mks68i7rtx by hand: every logical CPU except 0-3.
        // It cost 7% of the hashrate and stopped the stutter, so it is the number to reproduce.
        Assert.Equal(1048560UL, mask);
    }

    [Fact]
    public void Reserving_nothing_restricts_nothing()
    {
        // Null rather than a full mask, so the caller can tell "no reservation" from "a
        // reservation that happens to allow everything" and lift rather than re-apply.
        Assert.Null(CpuReservation.MaskFor(Hybrid(), reserved: 0));
        Assert.Null(CpuReservation.MaskFor(Hybrid(), reserved: -1));
    }

    [Fact]
    public void A_machine_that_did_not_report_its_topology_reserves_nothing()
    {
        // PhysicalCores returns empty on a platform or machine that will not answer. Guessing the
        // layout is exactly the mistake the OS call exists to avoid.
        Assert.Null(CpuReservation.MaskFor([], reserved: 2));
    }

    [Fact]
    public void A_reservation_bigger_than_the_machine_still_leaves_the_miner_a_core()
    {
        var mask = CpuReservation.MaskFor(Hybrid(), reserved: 99);

        // A typo, or a config copied from a bigger rig. An empty affinity mask does not slow a
        // miner down, it stops it - and a stopped miner looks exactly like one that crashed.
        Assert.NotNull(mask);
        Assert.Equal(1UL << 19, mask);
    }

    [Fact]
    public void The_cores_handed_back_are_the_first_ones()
    {
        var mask = CpuReservation.MaskFor(Hybrid(), reserved: 2)!.Value;

        // On Intel's hybrid parts the performance cores are reported first, and a person wants a
        // fast core rather than an efficient one. The E-cores stay with the miner.
        for (var e = 16; e < 20; e++) Assert.NotEqual(0UL, mask & (1UL << e));
    }

    [Fact]
    public void A_plain_hyperthreaded_cpu_reserves_whole_cores_too()
    {
        // Six cores, twelve threads - the shape of the other two nodes in this fleet.
        var cores = Enumerable.Range(0, 6).Select(c => 3UL << (c * 2)).ToArray();

        Assert.Equal(0b111111111100UL, CpuReservation.MaskFor(cores, reserved: 1));
    }

    /// <summary>
    /// Reads this machine's real topology, which is the only way to check the struct offsets the
    /// P/Invoke walks by hand. Wrong offsets would not throw; they would return plausible-looking
    /// masks, and the miner would be pinned to cores nobody chose.
    /// </summary>
    [Fact]
    public void The_topology_read_from_this_machine_accounts_for_every_logical_cpu()
    {
        var cores = CpuReservation.PhysicalCores();

        // Not Windows, a machine with processor groups, or one that would not answer. Reserving
        // nothing is the documented behaviour there, and there is nothing to check.
        if (cores.Count == 0 || Environment.ProcessorCount > 64) return;

        ulong union = 0;
        var bits = 0;
        foreach (var core in cores)
        {
            union |= core;
            bits += System.Numerics.BitOperations.PopCount(core);
        }

        // Every logical CPU belongs to exactly one core: the union covers the machine, and the
        // per-core counts add up to it rather than overlapping.
        Assert.Equal(Environment.ProcessorCount, System.Numerics.BitOperations.PopCount(union));
        Assert.Equal(Environment.ProcessorCount, bits);
        Assert.True(cores.Count <= Environment.ProcessorCount);
    }

    [Fact]
    public void The_node_answers_with_what_it_stored_not_what_it_was_asked()
    {
        using var dir = new TempDirectory();
        var store = new MinerConfigStore(dir.Path);

        store.Update(new MinerConfigDto { ReservedCores = 2 });

        // MinerConfigStore.Update enumerates every field by hand, so one forgotten there is
        // accepted over HTTP, echoed back as though saved, and dropped on the next write.
        Assert.Equal(2, new MinerConfigStore(dir.Path).Current.ReservedCores);

        // A push that does not mention it leaves it alone, like every other stored setting.
        Assert.Equal(2, store.Update(new MinerConfigDto { AutoStartMiner = true }).ReservedCores);
    }

    [Fact]
    public void A_node_reserves_what_it_names_and_otherwise_follows_the_fleet()
    {
        var config = new FleetConfig { ReservedCores = 0 };

        var gaming = new NodeConfig { Name = "mks68i7rtx", ReservedCores = 2 };
        var rig = new NodeConfig { Name = "rig-in-a-cupboard" };

        // The question is about the room the machine is in, so the node's answer has to win.
        Assert.Equal(2, config.ReservedCoresFor(gaming));
        Assert.Equal(0, config.ReservedCoresFor(rig));

        // And a node that names 0 means 0, rather than falling through to a fleet default.
        Assert.Equal(0, new FleetConfig { ReservedCores = 2 }
            .ReservedCoresFor(new NodeConfig { Name = "rig", ReservedCores = 0 }));
    }
}
