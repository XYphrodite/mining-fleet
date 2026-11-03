using MiningFleet.Agent;
using MiningFleet.Console;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

/// <summary>
/// The kit does the updating, but each side still owns its wiring: which repositories,
/// which asset twins, which executables identify a payload, and which files survive.
/// A drift here installs the wrong payload or wipes a node's identity.
/// </summary>
public sealed class SelfUpdateWiringTests
{
    [Fact]
    public void Agent_options_cover_both_repos_and_agent_zips()
    {
        var options = FleetAgentUpdate.Options();

        Assert.Equal("XYphrodite/mining-fleet", options.Repository);
        Assert.Contains("XYphrodite/xmrig-fleet", options.FallbackRepositories);
        Assert.Equal(ReleaseAssets.AgentZipNamesFor(FleetAgentUpdate.PreferredVariant()), options.ExecutableAssetNames);
        Assert.Equal(AgentIdentity.ExeFileNames, options.ProbeExecutableNames);
        Assert.True(options.ApplyPayloadImmediately);
        Assert.Equal([(byte)'P', (byte)'K'], options.ExpectedMagic);
    }

    [Fact]
    public void Agent_options_protect_every_identity_file()
    {
        var options = FleetAgentUpdate.Options();

        foreach (var name in AgentUpdateService.ProtectedFileNames)
            Assert.Contains(name, options.ExcludedFileNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Console_options_try_the_configured_repo_first()
    {
        var options = FleetConsoleUpdate.Options(new UpdateConfig { Repository = "XYphrodite/xmrig-fleet" });

        Assert.Equal(["XYphrodite/xmrig-fleet", "XYphrodite/mining-fleet"], options.FallbackRepositories.Prepend(options.Repository));
        Assert.Equal(ReleaseAssets.ConsoleZipNamesFor(FleetConsoleUpdate.PreferredVariant()), options.ExecutableAssetNames);
        Assert.True(options.ApplyPayloadImmediately);
    }

    [Fact]
    public void Console_options_leave_a_custom_fork_alone_and_pass_the_auth_through()
    {
        var options = FleetConsoleUpdate.Options(new UpdateConfig { Repository = "someone/else", Token = "secret" });

        Assert.Equal("someone/else", options.Repository);
        Assert.Empty(options.FallbackRepositories);
        Assert.Equal("secret", options.AuthorizationToken);
    }

    [Fact]
    public void Console_options_send_no_credentials_by_default()
    {
        var options = FleetConsoleUpdate.Options(new UpdateConfig());

        Assert.Null(options.AuthorizationToken);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("latest", null)]
    [InlineData("LATEST", null)]
    [InlineData("v1.4.0", "v1.4.0")]
    [InlineData("1.4.0", "1.4.0")]
    public void Agent_tag_mapping_keeps_latest_off_the_wire(string? version, string? expected)
    {
        Assert.Equal(expected, FleetAgentUpdate.NormalizeTag(version));
    }

    [Fact]
    public void Executable_probe_reads_magic_not_names()
    {
        using var dir = new TempDirectory();
        var exe = Path.Combine(dir.Path, "tool.exe");
        var text = Path.Combine(dir.Path, "notes.txt");
        File.WriteAllBytes(exe, [(byte)'M', (byte)'Z', 0]);
        File.WriteAllText(text, "plain text, no magic");

        Assert.True(UpdateFiles.IsPortableExecutable(exe));
        Assert.False(UpdateFiles.IsPortableExecutable(text));
        Assert.False(UpdateFiles.IsPortableExecutable(Path.Combine(dir.Path, "missing.exe")));
    }

    [Fact]
    public void Console_options_protect_every_operator_state_file()
    {
        var options = FleetConsoleUpdate.Options(new UpdateConfig());

        Assert.Contains("fleet.json", FleetConsoleUpdate.ProtectedFileNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("gpu-income.json", FleetConsoleUpdate.ProtectedFileNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(LifetimeIncomeLedger.FileName, FleetConsoleUpdate.ProtectedFileNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in FleetConsoleUpdate.ProtectedFileNames)
            Assert.Contains(name, options.ExcludedFileNames, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("help")]
    [InlineData("--help")]
    public void Config_free_cli_commands_succeed_without_touching_config(string arg)
    {
        Assert.True(Cli.TryRunConfigFree([arg], out var exitCode));
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Staged_probe_strips_stray_protected_files_created_as_side_effect()
    {
        using var installDir = new TempDirectory();
        using var stageDir = new TempDirectory();
        var stagedExe = Path.Combine(stageDir.Path, "mining-fleet.exe");
        File.WriteAllBytes(stagedExe, [(byte)'M', (byte)'Z', 0]);

        var session = new VariantSwapSession(
            installDir.Path,
            FleetVariant.Light,
            probe: (exe, _) =>
            {
                // Simulate an older console whose `version` command wrote a blank fleet.json in staging.
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, "fleet.json"), "{}");
                return Task.FromResult(true);
            },
            FleetConsoleUpdate.ProtectedFileNames);

        var ok = await session.ProbeAsync(stagedExe, CancellationToken.None);

        Assert.True(ok);
        Assert.True(File.Exists(stagedExe));
        Assert.False(File.Exists(Path.Combine(stageDir.Path, "fleet.json")));
    }

    [Fact]
    public void Installed_exe_prefers_what_is_on_disk()
    {
        using var dir = new TempDirectory();
        Assert.Equal(Path.Combine(dir.Path, "a.exe"), UpdateFiles.FirstExisting(dir.Path, ["a.exe", "b.exe"], "a.exe"));

        File.WriteAllText(Path.Combine(dir.Path, "b.exe"), "x");
        Assert.Equal(Path.Combine(dir.Path, "b.exe"), UpdateFiles.FirstExisting(dir.Path, ["a.exe", "b.exe"], "a.exe"));

        File.WriteAllText(Path.Combine(dir.Path, "a.exe"), "x");
        Assert.Equal(Path.Combine(dir.Path, "a.exe"), UpdateFiles.FirstExisting(dir.Path, ["a.exe", "b.exe"], "a.exe"));
    }
}
