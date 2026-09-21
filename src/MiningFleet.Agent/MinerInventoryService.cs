using MiningFleet.Contracts;

namespace MiningFleet.Agent;

public sealed class MinerInventoryService
{
    private readonly MinerService _miner;
    private readonly GpuMinerService _gpu;
    private readonly MinerConfigStore _config;

    public MinerInventoryService(MinerService miner, GpuMinerService gpu, MinerConfigStore config)
    {
        _miner = miner;
        _gpu = gpu;
        _config = config;
    }

    public IReadOnlyList<MinerInventoryItemDto> List()
    {
        var items = new List<MinerInventoryItemDto>();

        var xmrigExe = _miner.ResolveExecutable();
        var xmrigConfigured = _config.Current.ExecutablePath;
        items.Add(new MinerInventoryItemDto
        {
            Kind = MinerKind.Xmrig,
            Name = "xmrig",
            Installed = xmrigExe is not null,
            ExecutablePath = xmrigExe,
            ConfiguredPath = xmrigConfigured,
            Version = null,
            SizeBytes = xmrigExe is not null ? TrySize(xmrigExe) : null,
        });

        var lolExe = _gpu.ResolveExecutable();
        var lolConfigured = _config.Current.GpuMiner?.ExecutablePath;
        items.Add(new MinerInventoryItemDto
        {
            Kind = MinerKind.LolMiner,
            Name = "lolMiner",
            Installed = lolExe is not null,
            ExecutablePath = lolExe,
            ConfiguredPath = lolConfigured,
            Version = null,
            SizeBytes = lolExe is not null ? TrySize(lolExe) : null,
        });

        return items;
    }

    public DirListingDto ListDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new DirListingDto { Path = path ?? "", Exists = false, Error = "Path is required." };

        try
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
            {
                var info = new FileInfo(full);
                return new DirListingDto
                {
                    Path = full,
                    Exists = true,
                    IsDirectory = false,
                    Entries = [new DirEntryDto(info.Name, false, info.Length, info.LastWriteTimeUtc)],
                    TotalSize = info.Length,
                };
            }

            if (!Directory.Exists(full))
                return new DirListingDto { Path = full, Exists = false, Error = "Path does not exist." };

            var entries = new List<DirEntryDto>();
            long total = 0;
            foreach (var dir in Directory.EnumerateDirectories(full))
            {
                var di = new DirectoryInfo(dir);
                entries.Add(new DirEntryDto(di.Name, true, 0, di.LastWriteTimeUtc));
            }
            foreach (var file in Directory.EnumerateFiles(full))
            {
                var fi = new FileInfo(file);
                entries.Add(new DirEntryDto(fi.Name, false, fi.Length, fi.LastWriteTimeUtc));
                total += fi.Length;
            }
            return new DirListingDto
            {
                Path = full,
                Exists = true,
                IsDirectory = true,
                Entries = entries.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                TotalSize = total,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new DirListingDto { Path = path, Exists = false, Error = ex.Message };
        }
    }

    public async Task<UninstallResultDto> UninstallAsync(UninstallRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TargetPath))
            return new UninstallResultDto(false, "TargetPath is required.");

        var full = Path.GetFullPath(request.TargetPath);

        if (request.Kind == MinerKind.Xmrig)
        {
            var exe = _miner.ResolveExecutable();
            if (exe is not null && IsInside(exe, full))
            {
                var stop = await _miner.StopAsync(ct);
                if (!stop.Ok && !stop.Message.Contains("not running", StringComparison.OrdinalIgnoreCase))
                    return new UninstallResultDto(false, $"Could not stop xmrig: {stop.Message}");
            }
        }
        else
        {
            var exe = _gpu.ResolveExecutable();
            if (exe is not null && IsInside(exe, full))
            {
                var stop = await _gpu.StopAsync(ct);
                if (!stop.Ok && !stop.Message.Contains("not running", StringComparison.OrdinalIgnoreCase))
                    return new UninstallResultDto(false, $"Could not stop lolMiner: {stop.Message}");
            }
        }

        try
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
            }
            else
            {
                return new UninstallResultDto(false, $"Path does not exist: {full}");
            }

            if (request.Kind == MinerKind.Xmrig)
            {
                var cfg = _config.Current.ExecutablePath;
                if (cfg is not null && IsInside(cfg, full))
                    _config.ClearXmrigPath();
            }
            else
            {
                var cfg = _config.Current.GpuMiner?.ExecutablePath;
                if (cfg is not null && IsInside(cfg, full))
                    _config.ClearGpuMinerPath();
            }

            return new UninstallResultDto(true, $"Removed {full}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UninstallResultDto(false, $"Failed to remove {full}: {ex.Message}");
        }
    }

    private static bool IsInside(string filePath, string directory)
    {
        try
        {
            var file = Path.GetFullPath(filePath);
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return file.Equals(root, StringComparison.OrdinalIgnoreCase)
                || file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static long? TrySize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return null; }
    }
}
