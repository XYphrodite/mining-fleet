using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Updates the agent itself from a published mining-fleet release, so a fleet-wide roll-out does
/// not need an RDP session per node.
///
/// Two rules make this safe to drive remotely:
///
/// 1. <see cref="ProtectedFiles"/> is never overwritten. The node's identity lives in those files
///    - the fleet token, the xmrig API token and the pushed miner config. Replacing the token
///    locks the console out of the very node it was updating, and only a visit to the machine
///    gets it back.
/// 2. The payload is verified to contain the agent executable before anything is moved. A wrong
///    or truncated archive must fail loudly while the node still works, never half-installed.
///
/// The running executable cannot be deleted, but it can be renamed, so each file is moved aside
/// to <c>.old</c> before the new one is copied over. A detached helper then starts the service
/// again and this process leaves cleanly - see <see cref="ScheduleRestart"/> for why not simply
/// exiting non-zero. The miner is a separate process and keeps hashing throughout.
/// </summary>
public sealed class AgentUpdateService
{
    private const string BackupSuffix = ".old";

    /// <summary>Node-specific state that survives every update. See the class remarks.</summary>
    private static readonly string[] ProtectedFiles =
    [
        "appsettings.json",
        "appsettings.Production.json",
        "xmrig-api.token",
        "miner.json",
    ];

    /// <summary>Exposed so a test can assert the list still covers everything that identifies a node.</summary>
    public static IReadOnlyList<string> ProtectedFileNames => ProtectedFiles;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AgentUpdateService> _log;

    public AgentUpdateService(IHttpClientFactory httpFactory, ILogger<AgentUpdateService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public static string CurrentVersion =>
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)).ToString();

    private static string InstallDirectory => AppContext.BaseDirectory;

    public async Task<AgentUpdateResultDto> UpdateAsync(AgentUpdateRequestDto request, CancellationToken ct)
    {
        var from = CurrentVersion;
        var staging = Path.Combine(Path.GetTempPath(), $"{AgentIdentity.Name}-update-{Guid.NewGuid():N}");
        var archive = staging + ".zip";

        try
        {
            var http = _httpFactory.CreateClient("github");
            http.DefaultRequestHeaders.UserAgent.ParseAdd(AgentIdentity.Name);

            string url;
            string? tag = null;

            if (!string.IsNullOrWhiteSpace(request.DownloadUrl))
            {
                url = request.DownloadUrl!;
            }
            else
            {
                var asset = await ResolveAssetAsync(http, request.Version, ct);
                if (asset is null)
                    return new AgentUpdateResultDto(false, $"No release asset named {string.Join(" or ", AssetNames)} was found.", from, null, false);

                (url, tag) = asset.Value;

                if (!request.Force && IsSameVersion(from, tag))
                    return new AgentUpdateResultDto(true, $"Already running {from}; nothing to do.", from, tag, false);
            }

            _log.LogInformation("Agent update: downloading {Url}", url);
            await DownloadAsync(http, url, archive, ct);

            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);

            // Refuse to touch the installation unless the payload is really an agent build.
            var exeName = AgentIdentity.FindPayloadExe(staging);
            if (exeName is null)
                return new AgentUpdateResultDto(false, $"Downloaded payload does not contain {string.Join(" or ", AgentIdentity.ExeFileNames)}; installation left untouched.", from, tag, false);

            var written = SwapIntoPlace(staging, InstallDirectory);
            _log.LogWarning("Agent update: {Count} files replaced, restarting into {Version}", written, tag ?? "the new build");

            ScheduleRestart();

            return new AgentUpdateResultDto(
                true,
                $"Updated from {from} to {tag ?? "the downloaded build"} ({written} files). Restarting; the miner keeps running.",
                from,
                tag,
                true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _log.LogError(ex, "Agent update failed");
            return new AgentUpdateResultDto(false, ex.Message, from, null, false);
        }
        finally
        {
            TryDelete(archive);
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>Leaves long enough for the HTTP response to reach the console, then hands over.</summary>
    private void ScheduleRestart() => _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(2));

        // A detached helper starts the service again, and this process then leaves cleanly.
        //
        // Exiting non-zero also restarts it - SCM's failure actions see a crash and act - but
        // that budget is only three deep and resets once a day, and every update spends one.
        // The fourth update within 24 hours therefore left a node down with no way back in
        // except a visit to the machine, which is exactly what this feature exists to avoid.
        // The delay is `ping`, not `timeout`. A service's child process has no console on stdin,
        // and `timeout` refuses to run without one - "ERROR: Input redirection is not supported,
        // exiting the process immediately" - so the cushion this needs never existed. `sc start`
        // ran about a second later instead of five, while this process was still alive and the
        // service still RUNNING, and lost with error 1056. That made every self-update a race:
        // mks68i7rtx came back from 1.12.0 and did not come back from 1.13.1, and sat there
        // mining with no agent until somebody started the service by hand.
        //
        // Three attempts rather than one, because the right moment cannot be calculated from
        // here: this process has to be gone and the service fully stopped, and how long that
        // takes depends on what the miner and the sensors are doing as they shut down. A
        // `sc start` against a service that is already running is a harmless 1056.
        try
        {
            var helperPath = WriteRestartHelper(InstallDirectory);
            using var helper = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{helperPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Could not schedule the restart; the node may need starting by hand");
        }

        _log.LogWarning("Agent update: replaced, leaving so the new binary can take over");
        Environment.Exit(0);
    });

    /// <summary>
    /// Hands the node from the old service name to the new one after this process is gone.
    /// The miner is a different process and is not touched. If the new service will not
    /// start, the helper falls back to the name the node was already running under.
    /// </summary>
    public static string WriteRestartHelper(string installDirectory)
    {
        var newExe = Path.Combine(installDirectory, AgentIdentity.ExeFileName);
        var legacyExe = Path.Combine(installDirectory, AgentIdentity.LegacyExeFileName);
        var exe = File.Exists(newExe) ? newExe : legacyExe;
        var path = Path.Combine(Path.GetTempPath(), $"{AgentIdentity.Name}-restart-{Guid.NewGuid():N}.cmd");
        var n = AgentIdentity.ServiceName;
        var o = AgentIdentity.LegacyServiceName;
        var script =
            $"""
            @echo off
            setlocal
            set NEW={n}
            set OLD={o}
            set "EXE={exe}"
            ping -n 6 127.0.0.1 > nul
            call :migrate
            ping -n 11 127.0.0.1 > nul
            call :migrate
            ping -n 21 127.0.0.1 > nul
            call :migrate
            del "%~f0"
            exit /b 0

            :migrate
            sc query %NEW% > nul 2>&1
            if errorlevel 1 sc create %NEW% binPath= "%EXE%" start= auto DisplayName= "mining-fleet agent" > nul
            sc config %NEW% binPath= "%EXE%" start= auto DisplayName= "mining-fleet agent" > nul
            sc description %NEW% "Controls xmrig and reports hardware telemetry to the mining-fleet console." > nul
            sc failure %NEW% reset= 86400 actions= restart/5000/restart/15000/restart/60000 > nul
            sc query %NEW% | findstr /C:"RUNNING" > nul
            if not errorlevel 1 goto started
            sc start %NEW% > nul
            sc query %NEW% | findstr /C:"RUNNING" > nul
            if not errorlevel 1 goto started
            sc config %OLD% binPath= "%EXE%" > nul
            sc start %OLD% > nul
            exit /b 0
            :started
            sc delete %OLD% > nul
            exit /b 0
            """;
        File.WriteAllText(path, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>
    /// Copies the payload over the installation, renaming any file in use out of the way first.
    /// Files in <see cref="ProtectedFiles"/> are skipped: they carry this node's identity.
    /// </summary>
    private static int SwapIntoPlace(string source, string target)
    {
        var written = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);

            if (ProtectedFiles.Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase))
                continue;

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (File.Exists(destination))
            {
                var backup = destination + BackupSuffix;
                TryDelete(backup);
                // The running executable cannot be deleted, but it can be renamed.
                File.Move(destination, backup);
            }

            File.Copy(file, destination, overwrite: true);
            written++;
        }

        return written;
    }

    /// <summary>Removes files displaced by an earlier update. Safe to call on every start.</summary>
    public static void CleanUpPreviousUpdate()
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(InstallDirectory, "*" + BackupSuffix, SearchOption.AllDirectories))
                TryDelete(stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers waste a few megabytes; never let cleanup stop the agent from starting.
        }
    }

    private static async Task<(string Url, string Tag)?> ResolveAssetAsync(HttpClient http, string? version, CancellationToken ct)
    {
        foreach (var repo in ReleaseAssets.GitHubRepositories)
        {
            var found = await ResolveAssetFromRepoAsync(http, repo, version, ct);
            if (found is not null) return found;
        }

        return null;
    }

    private static async Task<(string Url, string Tag)?> ResolveAssetFromRepoAsync(
        HttpClient http, string repo, string? version, CancellationToken ct)
    {
        using var response = await http.GetAsync($"https://api.github.com/repos/{repo}/releases", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var wanted = string.IsNullOrWhiteSpace(version) || version.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? null
            : version;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag is null) continue;
            if (wanted is not null && !tag.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (wanted is null && release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;

            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            var available = new List<(string Name, string Url)>();
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name is null || url is null) continue;
                available.Add((name, url));
            }

            var picked = PickAsset(available.Select(a => a.Name));
            if (picked is not null)
            {
                var match = available.First(a => a.Name.Equals(picked, StringComparison.OrdinalIgnoreCase));
                return (match.Url, tag);
            }

            if (wanted is not null) return null;
        }

        return null;
    }

    /// <summary>Preferred agent zip, e.g. mining-fleet-agent-win-x64.zip. See <see cref="AssetNames"/>.</summary>
    public static string AssetName => AssetNames[0];

    /// <summary>
    /// Agent zip names, new then legacy. Matched in full: a release also carries the console zip,
    /// and a fragment match would unpack it over this agent.
    /// </summary>
    public static IReadOnlyList<string> AssetNames => ReleaseAssets.AgentZipNames;

    public static string? PickAsset(IEnumerable<string?> available) =>
        ReleaseAssets.Pick(AssetNames, available);

    /// <summary>Assembly versions carry four parts, release tags three: compare what both have.</summary>
    public static bool IsSameVersion(string assemblyVersion, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        return Version.TryParse(assemblyVersion, out var mine)
            && Version.TryParse(tag.TrimStart('v', 'V'), out var theirs)
            && mine.Major == theirs.Major && mine.Minor == theirs.Minor && mine.Build == theirs.Build;
    }

    private static async Task DownloadAsync(HttpClient http, string url, string destination, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);
        await source.CopyToAsync(file, ct);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
