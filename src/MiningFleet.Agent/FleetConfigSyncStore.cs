using System.Text;
using System.Text.Json;
using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Persists the newest synced <c>fleet.json</c> alongside the agent and keeps local console
/// <c>fleet.json</c> files on the same machine synchronized with it.
/// </summary>
public sealed class FleetConfigSyncStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _agentFleetPath;
    private readonly string _statePath;
    private readonly Func<IReadOnlyList<string>> _localPathsProvider;
    private readonly ILogger<FleetConfigSyncStore>? _log;
    private readonly object _gate = new();
    private SyncState _state;

    public FleetConfigSyncStore(
        string basePath,
        Func<IReadOnlyList<string>>? localPathsProvider = null,
        ILogger<FleetConfigSyncStore>? log = null)
    {
        _agentFleetPath = Path.Combine(basePath, FleetConfigSync.FleetConfigFileName);
        _statePath = Path.Combine(basePath, FleetConfigSync.SyncMetadataFileName);
        _localPathsProvider = localPathsProvider ?? DiscoverLocalConsoleFleetPaths;
        _log = log;
        _state = LoadState();

        lock (_gate)
        {
            RefreshFromLocalFilesLocked(out _);
        }
    }

    public FleetConfigMetaDto? CurrentMeta
    {
        get { lock (_gate) return _state.Current; }
    }

    /// <summary>
    /// Reads the newest local <c>fleet.json</c> (checking for manual or console edits on disk)
    /// and returns the sync envelope, or null if this node has not received one yet.
    /// </summary>
    public FleetConfigSyncDto? GetCurrentSync()
    {
        lock (_gate)
        {
            RefreshFromLocalFilesLocked(out _);
            if (_state.Current is not { } meta || !File.Exists(_agentFleetPath))
                return null;

            try
            {
                var json = File.ReadAllText(_agentFleetPath, Encoding.UTF8);
                return new FleetConfigSyncDto
                {
                    UpdatedAtUtc = meta.UpdatedAtUtc,
                    Sha256 = meta.Sha256,
                    Json = json,
                    OriginHost = meta.OriginHost,
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.LogWarning(ex, "Could not read {Path} for fleet config sync.", _agentFleetPath);
                return null;
            }
        }
    }

    /// <summary>
    /// Scans the agent's copy and local console copies on this PC for edits. Returns true when a
    /// newer valid <c>fleet.json</c> was picked up locally and should be propagated to peers.
    /// </summary>
    public bool RefreshFromLocalFiles(out FleetConfigSyncDto? updated)
    {
        lock (_gate)
        {
            return RefreshFromLocalFilesLocked(out updated);
        }
    }

    /// <summary>
    /// Applies a <c>fleet.json</c> pushed by a console or peer agent when it is strictly newer
    /// than what this machine currently holds.
    /// </summary>
    public FleetConfigPushResultDto ApplyRemote(FleetConfigSyncDto incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (!FleetConfigSync.TryValidateJson(incoming.Json, out _))
            return new FleetConfigPushResultDto(false, "invalid", CurrentMeta);

        var sha = FleetConfigSync.ComputeSha256(incoming.Json);
        var origin = string.IsNullOrWhiteSpace(incoming.OriginHost) ? null : incoming.OriginHost.Trim();
        var candidate = new FleetConfigMetaDto(incoming.UpdatedAtUtc, sha, origin);

        lock (_gate)
        {
            RefreshFromLocalFilesLocked(out _);

            if (_state.Current is { } current)
            {
                if (string.Equals(current.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                    return new FleetConfigPushResultDto(true, "unchanged", current);

                if (!FleetConfigSync.IsNewer(candidate, current))
                    return new FleetConfigPushResultDto(false, "stale", current);
            }

            WriteToAllTargetsLocked(incoming.Json, candidate);
            _log?.LogInformation(
                "Applied newer fleet.json from {Origin} (updatedAt={UpdatedAt}, sha256={ShaPrefix})",
                origin ?? "peer",
                candidate.UpdatedAtUtc.ToString("O"),
                sha[..Math.Min(12, sha.Length)]);

            return new FleetConfigPushResultDto(true, "updated", candidate);
        }
    }

    private bool RefreshFromLocalFilesLocked(out FleetConfigSyncDto? updated)
    {
        updated = null;
        var targets = GetTargetPaths();

        // Keep agent state consistent if its own fleet.json was deleted externally.
        if (_state.Current is not null && !File.Exists(_agentFleetPath))
        {
            _state.Current = null;
            _state.LocalFiles.Remove(_agentFleetPath);
        }

        Candidate? best = null;
        foreach (var path in targets)
        {
            if (!File.Exists(path)) continue;

            string json;
            DateTimeOffset mtimeUtc;
            try
            {
                json = File.ReadAllText(path, Encoding.UTF8);
                mtimeUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!FleetConfigSync.TryValidateJson(json, out _)) continue;

            var sha = FleetConfigSync.ComputeSha256(json);
            _state.LocalFiles.TryGetValue(path, out var previousMark);

            if (previousMark is not null && string.Equals(previousMark.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                continue;

            if (_state.Current is not null && string.Equals(_state.Current.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                _state.LocalFiles[path] = new LocalFileMark(sha, mtimeUtc);
                continue;
            }

            // A file that already has a prior sync mark was genuinely edited locally afterwards, so
            // ensure its effective timestamp advances past the previous synced copy even under small
            // clock skew between PCs. An unseen file keeps its filesystem mtime so an old machine
            // booting up does not clobber a newer fleet config.
            var effectiveTime = mtimeUtc;
            if (previousMark is not null && _state.Current is { } current && effectiveTime <= current.UpdatedAtUtc)
                effectiveTime = current.UpdatedAtUtc.AddMilliseconds(1);

            var meta = new FleetConfigMetaDto(effectiveTime, sha, Environment.MachineName);
            if (best is null || FleetConfigSync.IsNewer(meta, best.Meta))
                best = new Candidate(meta, json, path);
        }

        if (best is not null && FleetConfigSync.IsNewer(best.Meta, _state.Current))
        {
            WriteToAllTargetsLocked(best.Json, best.Meta);
            updated = new FleetConfigSyncDto
            {
                UpdatedAtUtc = best.Meta.UpdatedAtUtc,
                Sha256 = best.Meta.Sha256,
                Json = best.Json,
                OriginHost = best.Meta.OriginHost,
            };
            _log?.LogInformation(
                "Picked up newer local fleet.json from {Path} (updatedAt={UpdatedAt}, sha256={ShaPrefix})",
                best.SourcePath,
                best.Meta.UpdatedAtUtc.ToString("O"),
                best.Meta.Sha256[..Math.Min(12, best.Meta.Sha256.Length)]);
            return true;
        }

        // If we already hold a synced fleet.json and a local console install does not yet match it
        // (for instance after a fresh console install or missing fleet.json), mirror it there.
        if (_state.Current is { } held && File.Exists(_agentFleetPath))
        {
            string? heldJson = null;
            foreach (var path in targets)
            {
                if (string.Equals(path, _agentFleetPath, StringComparison.OrdinalIgnoreCase)) continue;
                var needsWrite = !File.Exists(path);
                if (!needsWrite)
                {
                    try
                    {
                        var existingSha = FleetConfigSync.ComputeSha256(File.ReadAllText(path, Encoding.UTF8));
                        needsWrite = !string.Equals(existingSha, held.Sha256, StringComparison.OrdinalIgnoreCase)
                                     && !_state.LocalFiles.ContainsKey(path);
                        // Wait: if it was unseen and strictly older than held, mirror held onto it.
                        if (needsWrite)
                        {
                            var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                            needsWrite = mtime < held.UpdatedAtUtc;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        needsWrite = false;
                    }
                }

                if (!needsWrite) continue;
                heldJson ??= File.ReadAllText(_agentFleetPath, Encoding.UTF8);
                TryWriteTargetLocked(path, heldJson, held);
            }
        }

        return false;
    }

    private void WriteToAllTargetsLocked(string json, FleetConfigMetaDto meta)
    {
        foreach (var path in GetTargetPaths())
            TryWriteTargetLocked(path, json, meta);

        _state.Current = meta;
        SaveStateLocked();
    }

    private void TryWriteTargetLocked(string path, string json, FleetConfigMetaDto meta)
    {
        try
        {
            FleetConfigSync.WriteFileAtomic(path, json, meta.UpdatedAtUtc);
            _state.LocalFiles[path] = new LocalFileMark(meta.Sha256, meta.UpdatedAtUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not write synced fleet.json to {Path}.", path);
        }
    }

    private IReadOnlyList<string> GetTargetPaths()
    {
        var paths = new List<string> { Path.GetFullPath(_agentFleetPath) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { paths[0] };

        foreach (var candidate in _localPathsProvider())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var full = Path.GetFullPath(candidate);
            if (seen.Add(full)) paths.Add(full);
        }

        return paths;
    }

    private SyncState LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var loaded = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(_statePath), JsonOptions);
                if (loaded is not null)
                {
                    loaded.LocalFiles = new Dictionary<string, LocalFileMark>(
                        loaded.LocalFiles ?? new Dictionary<string, LocalFileMark>(),
                        StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new SyncState();
    }

    private void SaveStateLocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_state, JsonOptions);
            FleetConfigSync.WriteFileAtomic(_statePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not persist fleet.sync.json.");
        }
    }

    /// <summary>
    /// Discovers operator console installations on this machine so the 24/7 agent service can
    /// pick up manual edits and update local console <c>fleet.json</c> files even when the console
    /// UI is closed.
    /// </summary>
    public static IReadOnlyList<string> DiscoverLocalConsoleFleetPaths()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfInstalled(string fleetPath, string exeName, string legacyExeName)
        {
            var dir = Path.GetDirectoryName(fleetPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

            var hasFleet = File.Exists(fleetPath);
            var hasExe = File.Exists(Path.Combine(dir, exeName)) || File.Exists(Path.Combine(dir, legacyExeName));
            if ((hasFleet || hasExe) && seen.Add(fleetPath))
                result.Add(fleetPath);
        }

        var envOverride = Environment.GetEnvironmentVariable("MINING_FLEET_CONFIG")
                          ?? Environment.GetEnvironmentVariable("XMRIG_FLEET_CONFIG");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride) && seen.Add(envOverride))
            result.Add(envOverride);

        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var usersDir = Path.Combine(root, "Users");
            if (Directory.Exists(usersDir))
            {
                try
                {
                    foreach (var userDir in Directory.EnumerateDirectories(usersDir))
                    {
                        foreach (var productDir in new[] { "mining-fleet", "xmrig-fleet" })
                        {
                            var fleetPath = Path.Combine(userDir, "AppData", "Local", "Programs", productDir, FleetConfigSync.FleetConfigFileName);
                            AddIfInstalled(fleetPath, "mining-fleet.exe", "xmrig-fleet.exe");
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        else
        {
            var localShare = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localShare))
            {
                foreach (var productDir in new[] { "mining-fleet", "xmrig-fleet" })
                {
                    var fleetPath = Path.Combine(localShare, productDir, FleetConfigSync.FleetConfigFileName);
                    AddIfInstalled(fleetPath, "mining-fleet", "xmrig-fleet");
                }
            }
        }

        return result;
    }

    private sealed record Candidate(FleetConfigMetaDto Meta, string Json, string SourcePath);

    private sealed class SyncState
    {
        public FleetConfigMetaDto? Current { get; set; }
        public Dictionary<string, LocalFileMark> LocalFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record LocalFileMark(string Sha256, DateTimeOffset LastWriteUtc);
}
