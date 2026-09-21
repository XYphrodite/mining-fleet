using MiningFleet.Agent;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

public sealed class MinerInventoryTests
{
    [Fact]
    public async Task List_contains_both_miners()
    {
        using var dir = new TempDirectory();
        var store = new MinerConfigStore(dir.Path);
        var miner = new MinerService(store, Microsoft.Extensions.Options.Options.Create(new AgentOptions()), new TestLogger<MinerService>());
        var gpu = new GpuMinerService(store, new TestLogger<GpuMinerService>());
        var inv = new MinerInventoryService(miner, gpu, store);

        var list = inv.List();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, i => i.Kind == MinerKind.Xmrig && i.Name == "xmrig");
        Assert.Contains(list, i => i.Kind == MinerKind.LolMiner && i.Name == "lolMiner");
    }

    [Fact]
    public void ListDir_returns_entries_and_total()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        File.WriteAllText(Path.Combine(dir.Path, "sub", "b.txt"), "world");

        var store = new MinerConfigStore(dir.Path);
        var miner = new MinerService(store, Microsoft.Extensions.Options.Options.Create(new AgentOptions()), new TestLogger<MinerService>());
        var gpu = new GpuMinerService(store, new TestLogger<GpuMinerService>());
        var inv = new MinerInventoryService(miner, gpu, store);

        var listing = inv.ListDir(dir.Path);

        Assert.True(listing.Exists);
        Assert.True(listing.IsDirectory);
        Assert.Contains(listing.Entries, e => e.Name == "a.txt" && !e.IsDirectory);
        Assert.Contains(listing.Entries, e => e.Name == "sub" && e.IsDirectory);
    }

    [Fact]
    public void ListDir_missing_path_reports_not_exists()
    {
        using var dir = new TempDirectory();
        var store = new MinerConfigStore(dir.Path);
        var miner = new MinerService(store, Microsoft.Extensions.Options.Options.Create(new AgentOptions()), new TestLogger<MinerService>());
        var gpu = new GpuMinerService(store, new TestLogger<GpuMinerService>());
        var inv = new MinerInventoryService(miner, gpu, store);

        var listing = inv.ListDir(Path.Combine(dir.Path, "nope"));

        Assert.False(listing.Exists);
        Assert.NotNull(listing.Error);
    }

    [Fact]
    public void ListDir_handles_file_path()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.Path, "single.txt");
        File.WriteAllText(file, "x");

        var store = new MinerConfigStore(dir.Path);
        var miner = new MinerService(store, Microsoft.Extensions.Options.Options.Create(new AgentOptions()), new TestLogger<MinerService>());
        var gpu = new GpuMinerService(store, new TestLogger<GpuMinerService>());
        var inv = new MinerInventoryService(miner, gpu, store);

        var listing = inv.ListDir(file);

        Assert.True(listing.Exists);
        Assert.False(listing.IsDirectory);
        Assert.Single(listing.Entries);
    }

    [Fact]
    public async Task Uninstall_deletes_directory_and_clears_config()
    {
        using var dir = new TempDirectory();
        var minerDir = Path.Combine(dir.Path, "xmrig");
        Directory.CreateDirectory(minerDir);
        var exe = Path.Combine(minerDir, OperatingSystem.IsWindows() ? "xmrig.exe" : "xmrig");
        File.WriteAllText(exe, "fake");

        var store = new MinerConfigStore(dir.Path);
        store.Update(new MinerConfigDto { ExecutablePath = exe });

        var miner = new MinerService(store, Microsoft.Extensions.Options.Options.Create(new AgentOptions()), new TestLogger<MinerService>());
        var gpu = new GpuMinerService(store, new TestLogger<GpuMinerService>());
        var inv = new MinerInventoryService(miner, gpu, store);

        var result = await inv.UninstallAsync(new UninstallRequestDto { Kind = MinerKind.Xmrig, TargetPath = minerDir }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(Directory.Exists(minerDir));
        Assert.Null(store.Current.ExecutablePath);
    }

    [Fact]
    public async Task Uninstall_gpu_clears_gpu_path()
    {
        using var dir = new TempDirectory();
        var gpuDir = Path.Combine(dir.Path, "lolMiner");
        Directory.CreateDirectory(gpuDir);
        var exe = Path.Combine(gpuDir, OperatingSystem.IsWindows() ? "lolMiner.exe" : "lolMiner");
        File.WriteAllText(exe, "fake");

        var store = new MinerConfigStore(dir.Path);
        store.Update(new MinerConfigDto { GpuMiner = new GpuMinerSettingsDto { ExecutablePath = exe, Algorithm = "CR29" } });

        var miner = new MinerService(store, Microsoft.Extensions.Options.Options.Create(new AgentOptions()), new TestLogger<MinerService>());
        var gpu = new GpuMinerService(store, new TestLogger<GpuMinerService>());
        var inv = new MinerInventoryService(miner, gpu, store);

        var result = await inv.UninstallAsync(new UninstallRequestDto { Kind = MinerKind.LolMiner, TargetPath = gpuDir }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(Directory.Exists(gpuDir));
        Assert.Null(store.Current.GpuMiner?.ExecutablePath);
    }

    [Fact]
    public async Task Uninstall_missing_path_fails()
    {
        using var dir = new TempDirectory();
        var store = new MinerConfigStore(dir.Path);
        var miner = new MinerService(store, Microsoft.Extensions.Options.Options.Create(new AgentOptions()), new TestLogger<MinerService>());
        var gpu = new GpuMinerService(store, new TestLogger<GpuMinerService>());
        var inv = new MinerInventoryService(miner, gpu, store);

        var result = await inv.UninstallAsync(new UninstallRequestDto { Kind = MinerKind.Xmrig, TargetPath = Path.Combine(dir.Path, "nope") }, CancellationToken.None);

        Assert.False(result.Ok);
    }

    private sealed class TestLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
