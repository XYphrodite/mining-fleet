using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>Installs or updates the GPU miner (lolMiner or SRBMiner-MULTI) on this node.</summary>
public sealed class GpuInstallerService
{
    private const string LolMinerReleasesApi = "https://api.github.com/repos/Lolliedieb/lolMiner-releases/releases";
    private const string SrMinerReleasesApi = "https://api.github.com/repos/doktor83/SRBMiner-Multi/releases";

    private readonly GpuMinerService _gpu;
    private readonly MinerConfigStore _config;
    private readonly ILogger<GpuInstallerService> _log;
    private readonly IHttpClientFactory _httpFactory;

    public GpuInstallerService(GpuMinerService gpu, MinerConfigStore config, IHttpClientFactory httpFactory, ILogger<GpuInstallerService> log)
    {
        _gpu = gpu;
        _config = config;
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<InstallResultDto> InstallAsync(InstallRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TargetPath))
            return new InstallResultDto(false, "TargetPath is required.", null, null);

        var running = _gpu.RunningPid() is not null;
        var exePath = _gpu.ResolveExecutable();
        var overwritesRunning = running && exePath is not null && IsInside(exePath, request.TargetPath);
        if (overwritesRunning)
        {
            var stop = await _gpu.StopAsync(ct);
            if (!stop.Ok)
                return new InstallResultDto(false, $"Could not stop the running GPU miner: {stop.Message}", null, null);
        }

        try
        {
            var software = request.Software ?? GpuMinerSoftware.LolMiner;
            var http = _httpFactory.CreateClient("github");
            string url;
            string? version = request.Version;
            if (!string.IsNullOrWhiteSpace(request.DownloadUrl))
            {
                url = request.DownloadUrl;
                version ??= "custom";
            }
            else
            {
                var asset = await ResolveReleaseAssetAsync(http, software, request.Version, ct);
                if (asset is null)
                {
                    var wanted = string.Join(" or ", AssetPatterns(software));
                    return new InstallResultDto(false, $"No {DisplayName(software)} release asset matches this node ({RuntimeDescription()}); looked for {wanted}.", null, null);
                }
                (url, version) = asset.Value;
            }

            _log.LogInformation("Downloading {Software} from {Url}", DisplayName(software), url);
            var archive = Path.Combine(Path.GetTempPath(), $"{DisplayName(software)}-{Guid.NewGuid():N}{GuessExtension(url)}");
            try
            {
                await using (var source = await http.GetStreamAsync(url, ct))
                await using (var file = File.Create(archive))
                {
                    await source.CopyToAsync(file, ct);
                }

                Directory.CreateDirectory(request.TargetPath);
                Extract(archive, request.TargetPath);
            }
            finally
            {
                TryDelete(archive);
            }

            var exeName = software == GpuMinerSoftware.SrMiner
                ? (OperatingSystem.IsWindows() ? "SRBMiner-MULTI.exe" : "SRBMiner-MULTI")
                : (OperatingSystem.IsWindows() ? "lolMiner.exe" : "lolMiner");
            var exe = Directory.EnumerateFiles(request.TargetPath, exeName, SearchOption.AllDirectories).FirstOrDefault();
            if (exe is null)
                return new InstallResultDto(false, $"Unpacked the archive but found no {exeName} under {request.TargetPath}.", version, null);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);

            var current = _config.Current.GpuMiner;
            var updated = (current ?? new GpuMinerSettingsDto()) with
            {
                ExecutablePath = exe,
                Miner = software,
            };
            _config.Update(new MinerConfigDto { GpuMiner = updated });

            var message = $"Installed {DisplayName(software)} {version} to {exe}.";
            if (overwritesRunning && request.RestartAfterInstall)
            {
                var start = await _gpu.StartAsync(ct);
                message += start.Ok ? " GPU miner restarted." : $" Restart failed: {start.Message}";
            }

            return new InstallResultDto(true, message, version, exe);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _log.LogError(ex, "GPU install failed");
            return new InstallResultDto(false, $"Install failed: {ex.Message}", null, null);
        }
    }

    private static bool IsInside(string filePath, string directory)
    {
        try
        {
            var file = Path.GetFullPath(filePath);
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    private static string DisplayName(GpuMinerSoftware software) =>
        software == GpuMinerSoftware.SrMiner ? "SRBMiner" : "lolMiner";

    private static async Task<(string Url, string Version)?> ResolveReleaseAssetAsync(HttpClient http, GpuMinerSoftware software, string? version, CancellationToken ct)
    {
        var releasesApi = software == GpuMinerSoftware.SrMiner ? SrMinerReleasesApi : LolMinerReleasesApi;
        var wantLatest = string.IsNullOrWhiteSpace(version) || version.Equals("latest", StringComparison.OrdinalIgnoreCase);
        var endpoint = wantLatest ? $"{releasesApi}/latest" : $"{releasesApi}/tags/{version}";

        using var doc = await http.GetFromJsonAsync<JsonDocument>(endpoint, ct)
            ?? throw new HttpRequestException("Empty response from the GitHub releases API.");

        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "unknown" : "unknown";
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        var candidates = assets.EnumerateArray()
            .Select(a => new
            {
                Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "",
            })
            .Where(a => a.Url.Length > 0)
            .ToList();

        foreach (var pattern in AssetPatterns(software))
        {
            var match = candidates.FirstOrDefault(a => a.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return (match.Url, tag);
        }

        return null;
    }

    private static IEnumerable<string> AssetPatterns(GpuMinerSoftware software)
    {
        if (software == GpuMinerSoftware.SrMiner)
        {
            // doktor83/SRBMiner-Multi release names: SRBMiner-Multi-3-7-9-win64.zip
            if (OperatingSystem.IsWindows())
                yield return "win64.zip";
            else
                yield return "Linux.tar.gz";
            yield break;
        }

        if (OperatingSystem.IsWindows())
            yield return "_Win64.zip";
        else if (OperatingSystem.IsMacOS())
            yield return "_Mac64.tar.gz";
        else
            yield return "_Lin64.tar.gz";
    }

    private static string RuntimeDescription()
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return $"{os}/{arch}";
    }

    private static void Extract(string archive, string targetPath)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, targetPath, overwriteFiles: true);
            return;
        }
        if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || archive.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, targetPath, overwriteFiles: true);
            return;
        }
        throw new InvalidDataException($"Unsupported archive type: {Path.GetFileName(archive)}");
    }

    private static string GuessExtension(string url) =>
        url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz"
        : url.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? ".tgz"
        : ".zip";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
