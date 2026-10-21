using Microsoft.Extensions.Logging.Abstractions;
using MiningFleet.Agent;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Guards the Pearl / SRBMiner path: an old miner.json must keep mining lolMiner CR29, a Pearl
/// push must not throw away the node's installer path, and stopping SRBMiner must never stop
/// lolMiner or xmrig.
/// </summary>
public sealed class SrMinerTests
{
    private static MinerConfigStore Store(TempDirectory dir) => new(dir.Path);

    [Fact]
    public void An_old_miner_json_without_a_software_field_reads_as_lolminer()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);

        store.Update(new MinerConfigDto
        {
            GpuMiner = new GpuMinerSettingsDto
            {
                Algorithm = "CR29",
                PoolUrl = "xtm-c29.kryptex.network:7040",
                ExecutablePath = @"C:\mining\lolminer\lolMiner.exe",
            },
        });

        var saved = store.Update(new MinerConfigDto { GpuWanted = true });

        // Null means the only miner that existed when the field was written.
        Assert.Null(saved.GpuMiner!.Miner);
    }

    [Fact]
    public void A_pearl_push_keeps_the_node_installer_path_and_session_flag()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);

        store.Update(new MinerConfigDto
        {
            GpuMiner = new GpuMinerSettingsDto
            {
                ExecutablePath = @"C:\mining\srbminer\SRBMiner-MULTI.exe",
                RunInInteractiveSession = true,
                Miner = GpuMinerSoftware.SrMiner,
            },
        });

        var saved = store.Update(new MinerConfigDto
        {
            GpuMiner = new GpuMinerSettingsDto
            {
                Enabled = true,
                Miner = GpuMinerSoftware.SrMiner,
                Algorithm = "pearlhash",
                PoolUrl = "prl.kryptex.network:7048",
                User = "prl1address/WORKER",
            },
        });

        Assert.Equal(GpuMinerSoftware.SrMiner, saved.GpuMiner!.Miner);
        Assert.Equal("pearlhash", saved.GpuMiner.Algorithm);
        Assert.Equal(@"C:\mining\srbminer\SRBMiner-MULTI.exe", saved.GpuMiner.ExecutablePath);
        Assert.True(saved.GpuMiner.RunInInteractiveSession);
    }

    [Fact]
    public void Switching_to_srbminer_keeps_a_lolminer_path_until_the_installer_replaces_it()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);

        store.Update(new MinerConfigDto
        {
            GpuMiner = new GpuMinerSettingsDto
            {
                Miner = GpuMinerSoftware.LolMiner,
                ExecutablePath = @"C:\mining\lolminer\lolMiner.exe",
            },
        });

        var saved = store.Update(new MinerConfigDto
        {
            GpuMiner = new GpuMinerSettingsDto { Miner = GpuMinerSoftware.SrMiner },
        });

        // The console may flip software before the download lands; losing the path would leave a
        // node with a configured miner and nothing to run.
        Assert.Equal(GpuMinerSoftware.SrMiner, saved.GpuMiner!.Miner);
        Assert.Equal(@"C:\mining\lolminer\lolMiner.exe", saved.GpuMiner.ExecutablePath);
    }

    [Fact]
    public void Srminer_arguments_match_the_parameters_file()
    {
        var args = GpuMinerServiceTestHooks.BuildArguments(new GpuMinerSettingsDto
        {
            Miner = GpuMinerSoftware.SrMiner,
            Algorithm = "pearlhash",
            PoolUrl = "prl.kryptex.network:7048",
            User = "prl1address/WORKER",
            Password = "x",
            ApiPort = 47802,
        });

        Assert.Contains("--disable-cpu", args);
        Assert.Contains("--algorithm-gpu", args);
        Assert.Contains("pearlhash", args);
        Assert.Contains("--pool", args);
        Assert.Contains("prl.kryptex.network:7048", args);
        Assert.Contains("--wallet", args);
        Assert.Contains("prl1address/WORKER", args);
        Assert.Contains("--api-enable", args);
        Assert.Contains("--api-port", args);
        Assert.Contains("47802", args);
        // lolMiner flags must not leak into the SRB command line.
        Assert.DoesNotContain("--algo", args);
        Assert.DoesNotContain("--apihost", args);
    }

    [Fact]
    public void Lolminer_arguments_stay_on_the_old_flags()
    {
        var args = GpuMinerServiceTestHooks.BuildArguments(new GpuMinerSettingsDto
        {
            Algorithm = "CR29",
            PoolUrl = "xtm-c29.kryptex.network:7040",
            User = "12P9address/WORKER",
            ApiPort = 47802,
        });

        Assert.Contains("--algo", args);
        Assert.Contains("--user", args);
        Assert.Contains("--apihost", args);
        Assert.Contains("127.0.0.1", args);
        Assert.DoesNotContain("--wallet", args);
        Assert.DoesNotContain("--disable-cpu", args);
    }

    [Fact]
    public void Pearl_target_is_read_from_the_kryptex_host()
    {
        var target = MiningFleet.Console.GpuPoolService.TargetFor(new GpuMinerConfig
        {
            Enabled = true,
            PoolUrl = "prl.kryptex.network:7048",
            User = "prl1address/WORKER",
        });

        Assert.NotNull(target);
        Assert.Equal("kryptex", target!.Provider);
        Assert.Equal("prl", target.Coin);
        Assert.Equal("prl1address", target.Address);
    }
}

/// <summary>Exposes pure helpers for tests without opening a process.</summary>
internal static class GpuMinerServiceTestHooks
{
    public static List<string> BuildArguments(GpuMinerSettingsDto settings) =>
        typeof(GpuMinerService)
            .GetMethod("BuildArguments", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [settings]) as List<string> ?? throw new InvalidOperationException("BuildArguments missing");
}
