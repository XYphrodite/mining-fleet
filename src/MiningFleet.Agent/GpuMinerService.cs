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
    private const string ProcessName = "lolMiner";

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
        var configured = _config.Current.GpuMiner?.ExecutablePath;
        if (string.IsNullOrWhiteSpace(configured)) return null;

        if (File.Exists(configured)) return configured;
        if (Directory.Exists(configured))
        {
            var name = OperatingSystem.IsWindows() ? "lolMiner.exe" : "lolMiner";
            var candidate = Path.Combine(configured, name);
            if (File.Exists(candidate)) return candidate;
            return Directory.EnumerateFiles(configured, name, SearchOption.AllDirectories).FirstOrDefault();
        }
        return null;
    }

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
                return CommandResultDto.Failure("The GPU miner is already running.");

            var exe = ResolveExecutable();
            if (exe is null)
                return CommandResultDto.Failure("lolMiner not found. Run a GPU install first.");

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
        await StopAsync(ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        return await StartAsync(ct);
    }

    public async Task<GpuMinerStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var settings = _config.Current.GpuMiner;
        var running = FindRunning();

        var status = new GpuMinerStatusDto
        {
            Running = running is not null,
            Algorithm = settings?.Algorithm,
            Pool = settings?.PoolUrl,
            Notice = running is null ? Notice : null,
        };

        if (running is null || settings?.ApiPort is not { } port) return status;

        try
        {
            using var response = await _http.GetAsync($"http://127.0.0.1:{port}/summary", ct);
            if (!response.IsSuccessStatusCode)
                return status with { Notice = $"the GPU miner's API returned {(int)response.StatusCode}" };

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return Merge(status, doc.RootElement);
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
        var args = new List<string>
        {
            "--algo", s.Algorithm!,
            "--pool", s.PoolUrl!,
            "--user", s.User!,
            "--pass", string.IsNullOrWhiteSpace(s.Password) ? "x" : s.Password,

            // lolMiner's API has no token of any kind, and it binds 0.0.0.0 unless told otherwise -
            // which on a tailnet-joined rig means an unauthenticated miner API answering the whole
            // tailnet. The loopback bind is the only thing protecting it, so it is not optional.
            "--apihost", "127.0.0.1",
            "--apiport", s.ApiPort!.Value.ToString(),
        };
        return args;
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
            return Process.GetProcessesByName(ProcessName).Where(p => !p.HasExited).ToList();
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
