using System.Diagnostics;
using System.Text.Json;
using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Owns the GPU miner process on this node: start, stop, and reading live stats off its loopback
/// HTTP API. A deliberate twin of <see cref="MinerService"/> rather than a shared base class,
/// because the two miners agree on almost nothing — a different executable, a different pool,
/// usually a different coin, and an algorithm that belongs to the card rather than to the fleet.
///
/// Three differences from the CPU path are load-bearing and none of them is symmetry:
///
/// It sets process priority explicitly. Launched at BelowNormal on a node whose CPU miner holds
/// every thread, this miner could not submit shares before they went stale — 18% of them, by the
/// pool's own count, and nothing else showed it.
///
/// It never touches an xmrig process. The fleet's <c>stop</c> is already infamous for killing
/// every miner on a node; that must not happen by accident in the other direction.
///
/// It waits seconds, not milliseconds, before declaring a start good. A bad pool crashes the CPU
/// miner instantly, but the failure seen on a real node here is slower and quieter: both backends
/// initialise and the miner then stops without ever reaching worker-thread init.
/// </summary>
public sealed class GpuMinerService : IDisposable
{
    /// <summary>Process name without extension, which is how <see cref="Process.GetProcessesByName"/> matches.</summary>
    private const string LolMinerProcessName = "lolMiner";

    /// <summary>SRBMiner-MULTI's image name. Never stops lolMiner or xmrig.</summary>
    private const string SrMinerProcessName = "SRBMiner-MULTI";

    private readonly MinerConfigStore _config;
    private readonly ILogger<GpuMinerService> _log;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<string> _recentOutput = new();

    private Process? _process;

    public GpuMinerService(MinerConfigStore config, ILogger<GpuMinerService> log)
    {
        _config = config;
        _log = log;
        // The miner API is on loopback; a system proxy must never intercept it.
        _http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>
    /// Why the miner is not running, when something other than an operator stopped it. Set by
    /// <see cref="GpuPauseService"/> and reported straight through to the console, because
    /// "paused for the model" and "stopped" must never be shown as the same thing.
    /// </summary>
    public string? Notice { get; set; }

    public IReadOnlyList<string> RecentOutput
    {
        get { lock (_recentOutput) return _recentOutput.ToList(); }
    }

    /// <summary>Accepts either the exe path itself or the directory it was installed into.</summary>
    public string? ResolveExecutable()
    {
        var settings = _config.Current.GpuMiner;
        var configured = settings?.ExecutablePath;
        var software = SoftwareOf(settings);
        var wantedExe = ExeName(software);
        var otherExe = ExeName(software == GpuMinerSoftware.SrMiner ? GpuMinerSoftware.LolMiner : GpuMinerSoftware.SrMiner);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured)
                && !string.Equals(Path.GetFileName(configured), otherExe, StringComparison.OrdinalIgnoreCase))
            {
                return configured;
            }

            if (Directory.Exists(configured) && FindInDirectory(configured, wantedExe) is { } inConfiguredDir)
                return inConfiguredDir;

            var sibling = SiblingMinerDirectory(configured, software);
            if (sibling is not null && Directory.Exists(sibling) && FindInDirectory(sibling, wantedExe) is { } inSibling)
                return inSibling;

            var defaultDir = DefaultInstallDirectory(software);
            if (Directory.Exists(defaultDir) && FindInDirectory(defaultDir, wantedExe) is { } inDefault)
                return inDefault;
        }

        return null;
    }

    private static string? FindInDirectory(string directory, string exeName)
    {
        var candidate = Path.Combine(directory, exeName);
        if (File.Exists(candidate)) return candidate;
        return Directory.EnumerateFiles(directory, exeName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string? SiblingMinerDirectory(string configuredPath, GpuMinerSoftware software)
    {
        try
        {
            var dir = File.Exists(configuredPath) ? Path.GetDirectoryName(configuredPath) : configuredPath;
            for (var depth = 0; depth < 4 && !string.IsNullOrWhiteSpace(dir); depth++)
            {
                var leaf = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (leaf.Equals("lolminer", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals("srbminer", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrWhiteSpace(parent))
                        return Path.Combine(parent, software == GpuMinerSoftware.SrMiner ? "srbminer" : "lolMiner");
                }
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
        }
        return null;
    }

    private static string DefaultInstallDirectory(GpuMinerSoftware software) =>
        software == GpuMinerSoftware.SrMiner
            ? (OperatingSystem.IsWindows() ? @"C:\mining\srbminer" : "/opt/srbminer")
            : (OperatingSystem.IsWindows() ? @"C:\mining\lolMiner" : "/opt/lolMiner");

    private static GpuMinerSoftware SoftwareOf(GpuMinerSettingsDto? settings) =>
        settings?.Miner ?? GpuMinerSoftware.LolMiner;

    private static string ExeName(GpuMinerSoftware software) => software switch
    {
        GpuMinerSoftware.SrMiner => OperatingSystem.IsWindows() ? "SRBMiner-MULTI.exe" : "SRBMiner-MULTI",
        _ => OperatingSystem.IsWindows() ? "lolMiner.exe" : "lolMiner",
    };

    private static string ProcessNameOf(GpuMinerSoftware software) => software switch
    {
        GpuMinerSoftware.SrMiner => SrMinerProcessName,
        _ => LolMinerProcessName,
    };

    /// <summary>The running miner's pid, or null. Cheap, because the pause service asks every second.</summary>
    public int? RunningPid()
    {
        try { return FindRunning()?.Id; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public async Task<CommandResultDto> StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var settings = _config.Current.GpuMiner;
            if (settings is null)
                return CommandResultDto.Failure("This node has no GPU miner settings. Push them from the console first.");

            if (settings.RunInInteractiveSession == true)
                return CommandResultDto.Failure(
                    "This node is set to run the GPU miner in its logged-on session, which this agent cannot do yet. " +
                    "Clear that setting to start in the agent's own session, or keep using the scheduled task on the node.");

            if (FindRunning() is not null)
            {
                _config.Update(new MinerConfigDto { GpuWanted = true });
                return CommandResultDto.Failure("The GPU miner is already running.");
            }

            var exe = ResolveExecutable();
            if (exe is null)
                return CommandResultDto.Failure($"{ExeName(SoftwareOf(settings))} not found. Run a GPU install first.");

            var missing = Missing(settings);
            if (missing is not null)
                return CommandResultDto.Failure($"Cannot start the GPU miner: no {missing} configured.");

            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in BuildArguments(settings)) psi.ArgumentList.Add(a);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Capture(e.Data);
            process.ErrorDataReceived += (_, e) => Capture(e.Data);

            if (!process.Start())
                return CommandResultDto.Failure("Failed to start the GPU miner process.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            Notice = null;
            SetNormalPriority(process);
            _log.LogInformation("Started the GPU miner (pid {Pid}) from {Exe}", process.Id, exe);

            // Seconds rather than the CPU path's 700 ms: the failure worth catching here is a miner
            // that initialises both backends and then stops, which takes longer than a bad-pool exit.
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (process.HasExited)
            {
                var tail = string.Join(" | ", RecentOutput.TakeLast(3));
                return CommandResultDto.Failure($"The GPU miner exited immediately (code {process.ExitCode}). {tail}");
            }

            // Same shape as the CPU miner: a start that worked records the wish, a failed
            // one changes nothing, and the watchdog reads it back.
            _config.Update(new MinerConfigDto { GpuWanted = true });
            return CommandResultDto.Success($"GPU miner started on {settings.Algorithm}, pid {process.Id}.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GPU start failed");
            return CommandResultDto.Failure($"GPU start failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommandResultDto> StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var processes = AllGpuMinerProcesses();
            if (processes.Count == 0)
            {
                _process = null;
                _config.Update(new MinerConfigDto { GpuWanted = false });
                return CommandResultDto.Success("The GPU miner was not running.");
            }

            var stopped = 0;
            foreach (var p in processes)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
                    stopped++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    _log.LogWarning(ex, "Could not stop GPU miner pid {Pid}", p.Id);
                }
                finally
                {
                    p.Dispose();
                }
            }

            _process = null;
            if (stopped > 0)
                _config.Update(new MinerConfigDto { GpuWanted = false });
            return stopped > 0
                ? CommandResultDto.Success($"Stopped {stopped} GPU miner process(es).")
                : CommandResultDto.Failure("Found the GPU miner but could not stop it. Try running the agent elevated.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommandResultDto> RestartAsync(CancellationToken ct)
    {
        var wanted = _config.Current.GpuWanted;
        await StopAsync(ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        var result = await StartAsync(ct);
        if (!result.Ok && wanted == true)
            _config.Update(new MinerConfigDto { GpuWanted = true });
        return result;
    }

    public async Task<GpuMinerStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var settings = _config.Current.GpuMiner;
        var running = FindRunning();

        var status = new GpuMinerStatusDto
        {
            Running = running is not null,
            Software = SoftwareOf(settings),
            Algorithm = settings?.Algorithm,
            Pool = settings?.PoolUrl,
            Notice = running is null ? Notice : null,
        };

        if (running is null || settings?.ApiPort is not { } port) return status;

        var software = SoftwareOf(settings);
        // lolMiner serves /summary; SRBMiner-MULTI serves its JSON at the API root.
        var apiPath = software == GpuMinerSoftware.SrMiner ? "/" : "/summary";
        try
        {
            using var response = await _http.GetAsync($"http://127.0.0.1:{port}{apiPath}", ct);
            if (!response.IsSuccessStatusCode)
                return status with { Notice = $"the GPU miner's API returned {(int)response.StatusCode}" };

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return software == GpuMinerSoftware.SrMiner
                ? MergeSrMiner(status, doc.RootElement)
                : Merge(status, doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Process is up but its API is not answering yet - lolMiner takes tens of seconds to
            // build a dataset before it serves anything.
            return status with { Notice = ex.Message };
        }
    }

    /// <summary>
    /// Folds lolMiner's <c>/summary</c> into the status, field by field through ValueKind checks so
    /// a renamed field blanks one cell instead of breaking the screen.
    /// </summary>
    private static GpuMinerStatusDto Merge(GpuMinerStatusDto status, JsonElement root)
    {
        var algo = default(JsonElement);
        if (root.TryGetProperty("Algorithms", out var algos)
            && algos.ValueKind == JsonValueKind.Array
            && algos.GetArrayLength() > 0)
        {
            algo = algos[0];
        }

        var devices = new List<GpuDeviceStatusDto>();
        if (root.TryGetProperty("Workers", out var workers) && workers.ValueKind == JsonValueKind.Array)
        {
            // Per-card hashrate lives on the algorithm, not the worker, so the two arrays are read
            // side by side and a mismatch in length simply leaves the rate blank.
            algo.TryGetProperty("Worker_Performance", out var perWorker);
            for (var i = 0; i < workers.GetArrayLength(); i++)
            {
                var w = workers[i];
                devices.Add(new GpuDeviceStatusDto(
                    ReadString(w, "Name") ?? $"GPU {i}",
                    ReadDouble(perWorker, i),
                    ReadNumber(w, "Core_Temp"),
                    ReadNumber(w, "Fan_Speed"),
                    ReadNumber(w, "CCLK"),
                    ReadNumber(w, "MCLK")));
            }
        }

        return status with
        {
            Algorithm = ReadString(algo, "Algorithm") ?? status.Algorithm,
            Pool = ReadString(algo, "Pool") ?? status.Pool,
            Hashrate = ReadNumber(algo, "Total_Performance"),
            HashrateUnit = ReadString(algo, "Performance_Unit"),
            AcceptedShares = (int?)ReadNumber(algo, "Total_Accepted"),
            StaleShares = (int?)ReadNumber(algo, "Total_Stales"),
            RejectedShares = (int?)ReadNumber(algo, "Total_Rejected"),
            Devices = devices,
        };
    }

    /// <summary>
    /// Folds SRBMiner-MULTI's JSON stats into the status. The Parameters file says only that the
    /// API sits at the root of the port; the shape below was read off a live 3.6.1 on an RTX 4060
    /// mining pearlhash.
    ///
    /// <c>gpu_devices[]</c> carries the card (<c>model</c>, <c>temperature</c>, <c>fan_speed_percent</c>,
    /// <c>core_clock</c>, <c>memory_clock</c>) but no hashrate. The rate lives on the algorithm, as
    /// <c>algorithms[0].hashrate.gpu.{gpuN, total}</c> in raw H/s, and the shares are a nested
    /// <c>shares{accepted, rejected}</c> object. There is no stale-share counter at all.
    ///
    /// The older flat field names stay as fallbacks, and every read goes through a ValueKind check,
    /// so a renamed field blanks one cell instead of breaking the screen.
    /// </summary>
    private static GpuMinerStatusDto MergeSrMiner(GpuMinerStatusDto status, JsonElement root)
    {
        var algo = root;
        if (root.TryGetProperty("algorithms", out var algos) && algos.ValueKind == JsonValueKind.Array && algos.GetArrayLength() > 0)
            algo = algos[0];
        else if (root.TryGetProperty("Algorithms", out algos) && algos.ValueKind == JsonValueKind.Array && algos.GetArrayLength() > 0)
            algo = algos[0];

        var rates = default(JsonElement);
        if (algo.ValueKind == JsonValueKind.Object) algo.TryGetProperty("hashrate", out rates);
        var gpuRates = default(JsonElement);
        if (rates.ValueKind == JsonValueKind.Object) rates.TryGetProperty("gpu", out gpuRates);

        var devices = new List<GpuDeviceStatusDto>();
        var cards = root.TryGetProperty("gpu_devices", out var gpuDevs) && gpuDevs.ValueKind == JsonValueKind.Array
            ? gpuDevs
            : root.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array ? devs : default;
        if (cards.ValueKind == JsonValueKind.Array)
        {
            for (var i = 0; i < cards.GetArrayLength(); i++)
            {
                var d = cards[i];
                // The per-card rate is keyed by the card's own handle ("gpu0"), not by position.
                var handle = ReadString(d, "device") ?? $"gpu{ReadNumber(d, "id") ?? i}";
                devices.Add(new GpuDeviceStatusDto(
                    ReadString(d, "model") ?? ReadString(d, "name") ?? ReadString(d, "Name") ?? $"GPU {i}",
                    ReadNumber(gpuRates, handle) ?? ReadNumber(d, "hashrate") ?? ReadNumber(d, "Hashrate"),
                    ReadNumber(d, "temperature") ?? ReadNumber(d, "temp") ?? ReadNumber(d, "Temperature"),
                    ReadNumber(d, "fan_speed_percent") ?? ReadNumber(d, "fan") ?? ReadNumber(d, "fan_percent") ?? ReadNumber(d, "Fan"),
                    ReadNumber(d, "core_clock") ?? ReadNumber(d, "core_clock_mhz"),
                    ReadNumber(d, "memory_clock") ?? ReadNumber(d, "memory_clock_mhz")));
            }
        }

        // The GPU total is the current rate; "1min" is the same thing smoothed, and a flat
        // number is what older builds sent. The CPU rate is deliberately never read: this
        // status describes the card.
        var hashrate = ReadNumber(gpuRates, "total")
            ?? ReadNumber(rates, "1min")
            ?? ReadNumber(algo, "hashrate") ?? ReadNumber(algo, "Hashrate")
            ?? ReadNumber(root, "hashrate") ?? ReadNumber(root, "Hashrate");
        // SRBMiner reports raw H/s; the unit travels with the number so Th/s is never compared to g/s.
        var unit = ReadString(algo, "hashrate_unit") ?? ReadString(algo, "unit")
            ?? ReadString(root, "hashrate_unit") ?? "H/s";

        var shares = default(JsonElement);
        if (algo.ValueKind == JsonValueKind.Object) algo.TryGetProperty("shares", out shares);

        // The pool is an object here ({"pool": "host:port", "wallet": ...}) and a bare string in
        // older builds. Only the host is read: the wallet inside that object is the operator's login.
        var pool = default(JsonElement);
        if (algo.ValueKind == JsonValueKind.Object) algo.TryGetProperty("pool", out pool);
        var poolName = pool.ValueKind == JsonValueKind.Object ? ReadString(pool, "pool") : ReadString(algo, "pool");

        return status with
        {
            Algorithm = ReadString(algo, "algorithm") ?? ReadString(algo, "name") ?? ReadString(algo, "Algorithm") ?? status.Algorithm,
            Pool = poolName ?? ReadString(algo, "Pool") ?? status.Pool,
            Hashrate = hashrate,
            HashrateUnit = unit,
            AcceptedShares = (int?)(ReadNumber(shares, "accepted") ?? ReadNumber(algo, "accepted") ?? ReadNumber(algo, "Accepted") ?? ReadNumber(algo, "accepted_shares")),
            StaleShares = (int?)(ReadNumber(algo, "stale") ?? ReadNumber(algo, "Stale") ?? ReadNumber(algo, "stale_shares")),
            RejectedShares = (int?)(ReadNumber(shares, "rejected") ?? ReadNumber(algo, "rejected") ?? ReadNumber(algo, "Rejected") ?? ReadNumber(algo, "rejected_shares")),
            Devices = devices,
        };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? ReadNumber(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    private static double? ReadDouble(JsonElement array, int index) =>
        array.ValueKind == JsonValueKind.Array
        && array.GetArrayLength() > index
        && array[index].ValueKind == JsonValueKind.Number
            ? array[index].GetDouble()
            : null;

    /// <summary>The first required setting that is missing, or null when the miner can start.</summary>
    private static string? Missing(GpuMinerSettingsDto s) =>
        string.IsNullOrWhiteSpace(s.Algorithm) ? "algorithm"
        : string.IsNullOrWhiteSpace(s.PoolUrl) ? "pool"
        : string.IsNullOrWhiteSpace(s.User) ? "pool login"
        : s.ApiPort is null ? "API port"
        : null;

    private static List<string> BuildArguments(GpuMinerSettingsDto s)
    {
        if (SoftwareOf(s) == GpuMinerSoftware.SrMiner)
        {
            // Flags from SRBMiner-MULTI's Parameters file (doktor83/SRBMiner-Multi). --wallet is the
            // pool login as the pool wants it (Kryptex: address/worker). --api-enable is required
            // for the JSON stats endpoint; the miner already binds 127.0.0.1.
            return
            [
                "--disable-cpu",
                "--algorithm-gpu", s.Algorithm!,
                "--pool", s.PoolUrl!,
                "--wallet", s.User!,
                "--password", string.IsNullOrWhiteSpace(s.Password) ? "x" : s.Password,
                "--api-enable",
                "--api-port", s.ApiPort!.Value.ToString(),
            ];
        }

        return
        [
            "--algo", s.Algorithm!,
            "--pool", s.PoolUrl!,
            "--user", s.User!,
            "--pass", string.IsNullOrWhiteSpace(s.Password) ? "x" : s.Password,

            // lolMiner's API has no token of any kind, and it binds 0.0.0.0 unless told otherwise -
            // which on a tailnet-joined rig means an unauthenticated miner API answering the whole
            // tailnet. The loopback bind is the only thing protecting it, so it is not optional.
            "--apihost", "127.0.0.1",
            "--apiport", s.ApiPort!.Value.ToString(),
        ];
    }

    /// <summary>
    /// Puts the miner at normal priority. Whatever starts a process passes its own priority on, and
    /// a GPU miner inheriting a background priority behind a CPU miner that owns every thread loses
    /// shares it cannot submit in time.
    /// </summary>
    private void SetNormalPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.Normal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // A process that exited between Start and here, or a platform that will not say.
            _log.LogWarning(ex, "Could not set the GPU miner's priority");
        }
    }

    public string? RunningExecutablePath()
    {
        if (FindRunning() is not { } p) return null;
        try
        {
            return p.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private Process? FindRunning()
    {
        if (_process is { HasExited: false }) return _process;
        _process = null;
        return AllGpuMinerProcesses().FirstOrDefault();
    }

    /// <summary>
    /// Every GPU miner process on the node — and deliberately no xmrig process. Stopping GPU mining
    /// must never stop the machine's main earner.
    /// </summary>
    private static List<Process> AllGpuMinerProcesses()
    {
        try
        {
            // Both software names are collected so a switch from lolMiner to SRBMiner still stops
            // the process that is actually running. xmrig is never in this list.
            var list = Process.GetProcessesByName(LolMinerProcessName).Where(p => !p.HasExited).ToList();
            list.AddRange(Process.GetProcessesByName(SrMinerProcessName).Where(p => !p.HasExited));
            return list;
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private void Capture(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_recentOutput)
        {
            _recentOutput.AddLast(line);
            while (_recentOutput.Count > 200) _recentOutput.RemoveFirst();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
        _process?.Dispose();
    }
}
