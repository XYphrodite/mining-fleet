using System.IO.Compression;
using System.Security.Cryptography;
using MiningFleet.Agent;
using MiningFleet.Contracts;
using SelfUpdateKit;

namespace MiningFleet.Console.Tests;

/// <summary>
/// The light (framework-dependent) twin of every payload: the installer picks it when
/// the .NET runtime is present, the marker next to the binary keeps it across updates,
/// and the node's identity files survive in every variant.
/// </summary>
public sealed class VariantTests
{
    [Theory]
    [InlineData("Microsoft.NETCore.App 10.0.1 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]", true)]
    [InlineData("Microsoft.WindowsDesktop.App 10.0.5 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]", true)]
    [InlineData("Microsoft.NETCore.App 9.0.1 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]", false)]
    [InlineData("Microsoft.WindowsDesktop.App 9.0.1 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]", false)]
    [InlineData("Microsoft.NETCore.App 11.0.0 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]", false)]
    [InlineData("Microsoft.AspNetCore.App 10.0.1 [C:\\Program Files\\dotnet\\shared\\Microsoft.AspNetCore.App]", false)]
    [InlineData("Microsoft.NETCore.App", false)]
    [InlineData("", false)]
    public void A_runtime_line_qualifies_only_the_target_major(string? line, bool expected)
    {
        Assert.Equal(expected, ReleaseAssets.IsCompatibleRuntimeLine(line));
    }

    [Fact]
    public void Light_names_mirror_the_full_names_with_a_suffix()
    {
        var full = ReleaseAssets.ConsoleZipNamesFor(FleetVariant.Full);
        var light = ReleaseAssets.ConsoleZipNamesFor(FleetVariant.Light);

        Assert.Equal(full.Count, light.Count);
        foreach (var (f, l) in full.Zip(light))
            Assert.Equal(f.Replace(".zip", "-light.zip"), l);
    }

    [Fact]
    public void The_light_agent_asset_is_not_an_acceptable_full_match()
    {
        var light = ReleaseAssets.AgentZipNamesFor(FleetVariant.Light);

        Assert.All(light, name => Assert.EndsWith("-light.zip", name));
        Assert.Empty(ReleaseAssets.AgentZipNames.Intersect(light, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(light[0], ReleaseAssets.Pick(light, light));
    }

    [Theory]
    [InlineData("light", FleetVariant.Light)]
    [InlineData("Light", FleetVariant.Light)]
    [InlineData("full", FleetVariant.Full)]
    [InlineData("anything-else", FleetVariant.Full)]
    public void The_marker_decides_the_variant_and_defaults_to_full(string marker, FleetVariant expected)
    {
        using var dir = new TempDirectory();
        var exe = Path.Combine(dir.Path, "mining-fleet");
        File.WriteAllText(Path.Combine(dir.Path, ReleaseAssets.VariantMarkerFileName), marker);

        Assert.Equal(expected, ReleaseAssets.InstalledVariant(exe));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_marker_means_a_pre_variant_full_install(string? processPath)
    {
        using var dir = new TempDirectory();
        var exe = processPath is null ? null : Path.Combine(dir.Path, "mining-fleet");

        Assert.Equal(FleetVariant.Full, ReleaseAssets.InstalledVariant(exe));
    }

    [Fact]
    public void Each_side_updates_within_its_installed_variant()
    {
        var config = new UpdateConfig();

        Assert.Equal(
            ReleaseAssets.ConsoleZipNamesFor(FleetVariant.Light),
            FleetConsoleUpdate.Options(config, FleetVariant.Light).ExecutableAssetNames);
        Assert.Equal(
            ReleaseAssets.AgentZipNamesFor(FleetVariant.Light),
            FleetAgentUpdate.Options(FleetVariant.Light).ExecutableAssetNames);
        Assert.Equal(
            ReleaseAssets.AgentZipNames,
            FleetAgentUpdate.Options(FleetVariant.Full).ExecutableAssetNames);
    }

    [Fact]
    public void The_light_agent_update_still_protects_node_identity()
    {
        var options = FleetAgentUpdate.Options(FleetVariant.Light);

        foreach (var name in AgentUpdateService.ProtectedFileNames)
            Assert.Contains(name, options.ExcludedFileNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Update_prefers_light_whenever_compatible_runtime_exists_and_requires_aspnet_for_agent()
    {
        string[] baseOnly = ["Microsoft.NETCore.App 10.0.2 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]"];
        string[] desktopOnly = ["Microsoft.WindowsDesktop.App 10.0.5 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]"];
        string[] aspOnly = ["Microsoft.AspNetCore.App 10.0.2 [C:\\Program Files\\dotnet\\shared\\Microsoft.AspNetCore.App]"];
        string[] fullRuntime =
        [
            "Microsoft.NETCore.App 10.0.2 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]",
            "Microsoft.AspNetCore.App 10.0.2 [C:\\Program Files\\dotnet\\shared\\Microsoft.AspNetCore.App]",
        ];

        // Console needs only NETCore.App or WindowsDesktop.App.
        Assert.Equal(FleetVariant.Light, ReleaseAssets.PreferredVariant(agent: false, baseOnly));
        Assert.Equal(FleetVariant.Light, ReleaseAssets.PreferredVariant(agent: false, desktopOnly));
        Assert.Equal(FleetVariant.Full, ReleaseAssets.PreferredVariant(agent: false, aspOnly));

        // Agent uses Microsoft.NET.Sdk.Web and needs BOTH base runtime and AspNetCore.App.
        Assert.Equal(FleetVariant.Full, ReleaseAssets.PreferredVariant(agent: true, baseOnly));
        Assert.Equal(FleetVariant.Full, ReleaseAssets.PreferredVariant(agent: true, desktopOnly));
        Assert.Equal(FleetVariant.Full, ReleaseAssets.PreferredVariant(agent: true, aspOnly));
        Assert.Equal(FleetVariant.Light, ReleaseAssets.PreferredVariant(agent: true, fullRuntime));

        // Explicit MINING_FLEET_VARIANT=full override forces Full even when runtimes exist.
        Assert.Equal(FleetVariant.Full, ReleaseAssets.PreferredVariant(agent: true, fullRuntime, variantOverride: "full"));

        // 32-bit x86 runtimes cannot run a 64-bit light binary.
        if (Environment.Is64BitProcess)
        {
            string[] x86Only =
            [
                "Microsoft.NETCore.App 10.0.2 [C:\\Program Files (x86)\\dotnet\\shared\\Microsoft.NETCore.App]",
                "Microsoft.AspNetCore.App 10.0.2 [C:\\Program Files (x86)\\dotnet\\shared\\Microsoft.AspNetCore.App]",
            ];
            Assert.Equal(FleetVariant.Full, ReleaseAssets.PreferredVariant(agent: false, x86Only));
            Assert.Equal(FleetVariant.Full, ReleaseAssets.PreferredVariant(agent: true, x86Only));
        }
    }

    [Fact]
    public async Task Full_to_light_transition_retires_hostfxr_preserves_state_and_rolls_back_on_failed_probe()
    {
        using var root = new TempDirectory();
        var installDir = Path.Combine(root.Path, "install");
        var stagingDir = Path.Combine(root.Path, "staging");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(stagingDir);

        // Existing Full installation with self-contained runtime DLLs + protected node files + logs.
        File.WriteAllBytes(Path.Combine(installDir, "mining-fleet-agent.exe"), [(byte)'M', (byte)'Z', 1]);
        File.WriteAllText(Path.Combine(installDir, "hostfxr.dll"), "full-hostfxr");
        File.WriteAllText(Path.Combine(installDir, "coreclr.dll"), "full-coreclr");
        File.WriteAllText(Path.Combine(installDir, "mining-fleet-agent.dll"), "app-dll");
        File.WriteAllText(Path.Combine(installDir, "appsettings.json"), "{\"Agent\":{\"Token\":\"secret\"}}");
        File.WriteAllText(Path.Combine(installDir, "miner.json"), "{\"wallet\":\"keep\"}");
        File.WriteAllText(Path.Combine(installDir, "xmrig-api.token"), "keep-token");
        File.WriteAllText(Path.Combine(installDir, "lifetime.json"), "{\"totalCpuHashes\":42}");
        File.WriteAllText(Path.Combine(installDir, "agent.log"), "keep-log");

        // Staged Light payload contains only the app binary and its direct dependencies.
        File.WriteAllBytes(Path.Combine(stagingDir, "mining-fleet-agent.exe"), [(byte)'M', (byte)'Z', 2]);
        File.WriteAllText(Path.Combine(stagingDir, "mining-fleet-agent.dll"), "light-dll");

        // 1. Failed post-install probe rolls retired runtime DLLs right back.
        var failingSession = new VariantSwapSession(
            installDir,
            FleetVariant.Light,
            (p, _) => Task.FromResult(p.StartsWith(stagingDir, StringComparison.OrdinalIgnoreCase)),
            AgentUpdateService.ProtectedFileNames);

        Assert.True(await failingSession.ProbeAsync(Path.Combine(stagingDir, "mining-fleet-agent.exe"), CancellationToken.None));
        Assert.False(await failingSession.ProbeAsync(Path.Combine(installDir, "mining-fleet-agent.exe"), CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(installDir, "hostfxr.dll")));
        Assert.True(File.Exists(Path.Combine(installDir, "coreclr.dll")));

        // 2. Successful probe retires hostfxr.dll & coreclr.dll, writes light marker, keeps node state, and cleans up .old-* on next start.
        var okSession = new VariantSwapSession(
            installDir,
            FleetVariant.Light,
            (_, _) => Task.FromResult(true),
            AgentUpdateService.ProtectedFileNames);

        Assert.True(await okSession.ProbeAsync(Path.Combine(stagingDir, "mining-fleet-agent.exe"), CancellationToken.None));
        Assert.True(await okSession.ProbeAsync(Path.Combine(installDir, "mining-fleet-agent.exe"), CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(installDir, "hostfxr.dll")));
        Assert.False(File.Exists(Path.Combine(installDir, "coreclr.dll")));
        Assert.True(File.Exists(Path.Combine(installDir, "mining-fleet-agent.dll")));
        Assert.Equal(FleetVariant.Light, ReleaseAssets.InstalledVariant(Path.Combine(installDir, "mining-fleet-agent.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "appsettings.json")));
        Assert.True(File.Exists(Path.Combine(installDir, "miner.json")));
        Assert.True(File.Exists(Path.Combine(installDir, "xmrig-api.token")));
        Assert.True(File.Exists(Path.Combine(installDir, "lifetime.json")));
        Assert.True(File.Exists(Path.Combine(installDir, "agent.log")));

        var cleaned = UpdateFiles.CleanUpRetiredFiles(installDir);
        Assert.True(cleaned >= 2);
        Assert.Empty(Directory.EnumerateFiles(installDir, "*.old*"));
    }

    [Fact]
    public async Task SelfUpdateService_converts_same_version_full_install_to_light_and_restores_on_failure()
    {
        using var root = new TempDirectory();
        var installDir = Path.Combine(root.Path, "agent");
        var buildDir = Path.Combine(root.Path, "light-payload");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(buildDir);

        var installedExe = Path.Combine(installDir, "mining-fleet-agent.exe");
        File.WriteAllBytes(installedExe, [(byte)'M', (byte)'Z', 10]);
        File.WriteAllText(Path.Combine(installDir, "hostfxr.dll"), "self-contained-hostfxr");
        File.WriteAllText(Path.Combine(installDir, "coreclr.dll"), "self-contained-coreclr");
        File.WriteAllText(Path.Combine(installDir, "appsettings.json"), "{\"Agent\":{\"Token\":\"keep\"}}");
        File.WriteAllText(Path.Combine(installDir, "lifetime.json"), "{\"totalCpuHashes\":99}");

        File.WriteAllBytes(Path.Combine(buildDir, "mining-fleet-agent.exe"), [(byte)'M', (byte)'Z', 20]);
        File.WriteAllText(Path.Combine(buildDir, "mining-fleet-agent.dll"), "light-app");
        File.WriteAllText(Path.Combine(buildDir, "appsettings.json"), "{\"Agent\":{\"Token\":\"CHANGE-ME\"}}");

        var zipPath = Path.Combine(root.Path, "mining-fleet-agent-win-x64-light.zip");
        ZipFile.CreateFromDirectory(buildDir, zipPath);
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant();
        var source = new FakeZipReleaseSource("v1.17.40", new ReleaseVersion(1, 17, 40), zipPath, sha);
        var options = FleetAgentUpdate.Options(FleetVariant.Light);

        // Simulated failure after swap restores both the exe and retired runtime DLLs.
        var brokenSession = new VariantSwapSession(
            installDir,
            FleetVariant.Light,
            (exe, _) => Task.FromResult(!Path.GetFullPath(exe).StartsWith(installDir, StringComparison.OrdinalIgnoreCase)),
            AgentUpdateService.ProtectedFileNames);
        var brokenUpdater = new SelfUpdateService(
            installedExe,
            new ReleaseVersion(1, 17, 40),
            source,
            options,
            probe: brokenSession.ProbeAsync);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            brokenUpdater.UpdateAsync(new SelfUpdateRequest(Tag: "v1.17.40"), CancellationToken.None));
        Assert.Equal([(byte)'M', (byte)'Z', 10], File.ReadAllBytes(installedExe));
        Assert.True(File.Exists(Path.Combine(installDir, "hostfxr.dll")));
        Assert.True(File.Exists(Path.Combine(installDir, "coreclr.dll")));

        // Simulated apphost rule: a Light exe refuses to run if hostfxr.dll is still beside it.
        var realSession = new VariantSwapSession(
            installDir,
            FleetVariant.Light,
            (exe, _) =>
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
                return Task.FromResult(!File.Exists(Path.Combine(dir, "hostfxr.dll")));
            },
            AgentUpdateService.ProtectedFileNames);
        var updater = new SelfUpdateService(
            installedExe,
            new ReleaseVersion(1, 17, 40),
            source,
            options,
            probe: realSession.ProbeAsync);

        var report = await updater.UpdateAsync(new SelfUpdateRequest(Tag: "v1.17.40"), CancellationToken.None);

        Assert.Equal(SelfUpdateStatus.Updated, report.Status);
        Assert.Equal([(byte)'M', (byte)'Z', 20], File.ReadAllBytes(installedExe));
        Assert.False(File.Exists(Path.Combine(installDir, "hostfxr.dll")));
        Assert.False(File.Exists(Path.Combine(installDir, "coreclr.dll")));
        Assert.Equal("{\"Agent\":{\"Token\":\"keep\"}}", File.ReadAllText(Path.Combine(installDir, "appsettings.json")));
        Assert.Equal("{\"totalCpuHashes\":99}", File.ReadAllText(Path.Combine(installDir, "lifetime.json")));
        Assert.Equal(FleetVariant.Light, ReleaseAssets.InstalledVariant(installedExe));

        UpdateFiles.CleanUpRetiredFiles(installDir);
        new ExecutableReplacer().RemoveRetiredCopies(installedExe);
        Assert.Empty(Directory.EnumerateFiles(installDir, "*.old*"));
    }

    private sealed class FakeZipReleaseSource(string tag, ReleaseVersion version, string zipPath, string sha256) : IReleaseSource
    {
        private static readonly Uri AssetUri = new("https://example.invalid/mining-fleet-agent-win-x64-light.zip");
        private static readonly Uri ShaUri = new("https://example.invalid/mining-fleet-agent-win-x64-light.zip.sha256");

        public Task<ReleaseDescriptor> ResolveAsync(string? requestedTag, CancellationToken cancellationToken) =>
            Task.FromResult(new ReleaseDescriptor(tag, version, AssetUri, ShaUri, new FileInfo(zipPath).Length, null, null));

        public Task DownloadAsync(Uri address, string destinationPath, CancellationToken cancellationToken, Action<long, long?>? progress = null)
        {
            File.Copy(zipPath, destinationPath, overwrite: true);
            var len = new FileInfo(destinationPath).Length;
            progress?.Invoke(len, len);
            return Task.CompletedTask;
        }

        public Task<string> ReadTextAsync(Uri address, CancellationToken cancellationToken) =>
            Task.FromResult($"{sha256}  mining-fleet-agent-win-x64-light.zip");
    }
}
