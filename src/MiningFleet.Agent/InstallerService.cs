using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Installs or updates xmrig on this node. Picks the right asset off the GitHub release
/// feed for the current OS, unpacks it into the requested directory and points the
/// miner config at the resulting executable.
/// </summary>
public sealed class InstallerService
{
    private const string ReleasesApi = "https://api.github.com/repos/xmrig/xmrig/releases";

    private readonly MinerService _miner;
    private readonly MinerConfigStore _config;
    private readonly ILogger<InstallerService> _log;
    private readonly IHttpClientFactory _httpFactory;

    public InstallerService(MinerService miner, MinerConfigStore config, IHttpClientFactory httpFactory, ILogger<InstallerService> log)
    {
        _miner = miner;
        _config = config;
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<InstallResultDto> InstallAsync(InstallRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TargetPath))
            return new InstallResultDto(false, "TargetPath is required.", null, null);

        var status = await _miner.GetStatusAsync(ct);

        // Only stop mining when the files being replaced are the ones currently executing.
        // Installing a second copy elsewhere has no reason to interrupt a running miner.
        var overwritesRunningMiner = status.Running && IsInside(status.ExecutablePath, request.TargetPath);
        if (overwritesRunningMiner)
        {
            var stop = await _miner.StopAsync(ct);
            if (!stop.Ok)
                return new InstallResultDto(false, $"Could not stop the running miner: {stop.Message}", null, null);
        }

        try
        {
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
                var asset = await ResolveReleaseAssetAsync(http, request.Version, ct);
                if (asset is null)
                {
                    var wanted = string.Join(" or ", AssetPatterns());
                    return new InstallResultDto(
                        false,
                        $"No xmrig release asset matches this node ({RuntimeDescription()}); looked for {wanted}.",
                        null,
                        null);
                }
                (url, version) = asset.Value;
            }

            _log.LogInformation("Downloading xmrig from {Url}", url);
            var archive = Path.Combine(Path.GetTempPath(), $"xmrig-{Guid.NewGuid():N}{GuessExtension(url)}");
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

            var exeName = OperatingSystem.IsWindows() ? "xmrig.exe" : "xmrig";
            var exe = Directory.EnumerateFiles(request.TargetPath, exeName, SearchOption.AllDirectories).FirstOrDefault();
            if (exe is null)
                return new InstallResultDto(false, $"Unpacked the archive but found no {exeName} under {request.TargetPath}.", version, null);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);

            _config.Update(new MinerConfigDto { ExecutablePath = exe });

            var message = $"Installed xmrig {version} to {exe}.";
            if (overwritesRunningMiner && request.RestartAfterInstall)
            {
                var start = await _miner.StartAsync(ct);
                message += start.Ok ? " Miner restarted." : $" Restart failed: {start.Message}";
            }

            return new InstallResultDto(true, message, version, exe);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _log.LogError(ex, "Install failed");
            return new InstallResultDto(false, $"Install failed: {ex.Message}", null, null);
        }
    }

    /// <summary>True when <paramref name="filePath"/> sits under <paramref name="directory"/>.</summary>
    private static bool IsInside(string? filePath, string directory)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        try
        {
            var file = Path.GetFullPath(filePath);
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // An unparseable path cannot be shown to be the running one, so leave the miner alone.
            return false;
        }
    }

    /// <summary>Returns the download URL and tag of the best-matching asset for this machine.</summary>
    private static async Task<(string Url, string Version)?> ResolveReleaseAssetAsync(HttpClient http, string? version, CancellationToken ct)
    {
        var wantLatest = string.IsNullOrWhiteSpace(version) || version.Equals("latest", StringComparison.OrdinalIgnoreCase);
        var endpoint = wantLatest ? $"{ReleasesApi}/latest" : $"{ReleasesApi}/tags/{version}";

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

        // Release assets are named like xmrig-6.26.0-windows-x64.zip / -linux-static-x64.tar.gz.
        // Try the exact platform+architecture first, then loosen to the platform alone.
        foreach (var pattern in AssetPatterns())
        {
            var match = candidates.FirstOrDefault(a => a.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return (match.Url, tag);
        }

        return null;
    }

    /// <summary>
    /// Asset-name fragments to look for, most specific first. Release assets are named like
    /// xmrig-6.26.0-windows-x64.zip / -linux-static-x64.tar.gz / -macos-arm64.tar.gz.
    ///
    /// There is deliberately no fallback to another architecture: handing an x64 archive to
    /// an arm64 node would report a successful install and then fail to execute, which is
    /// worse than saying up front that no asset matches.
    /// </summary>
    private static IEnumerable<string> AssetPatterns(bool isWindows, bool isMacOs, string arch)
    {
        if (isWindows)
        {
            // The plain build is the MSVC one; -windows-gcc- is a separate, slower variant.
            yield return $"-windows-{arch}.zip";
        }
        else if (isMacOs)
        {
            yield return $"-macos-{arch}.tar.gz";
        }
        else
        {
            // The static build carries no glibc version to match against the node.
            yield return $"-linux-static-{arch}.tar.gz";
            yield return $"-linux-{arch}.tar.gz";
        }
    }

    private static IEnumerable<string> AssetPatterns() =>
        AssetPatterns(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), CurrentArchitecture());

    private static string RuntimeDescription()
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        return $"{os}/{CurrentArchitecture()}";
    }

    /// <summary>The architecture fragment xmrig uses in its asset names.</summary>
    private static string CurrentArchitecture() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            // Anything else has no xmrig build; name it so the failure message stays honest.
            var other => other.ToString().ToLowerInvariant(),
        };

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
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
