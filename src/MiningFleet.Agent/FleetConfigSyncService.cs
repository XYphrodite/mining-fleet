using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Periodically asks peer agents in <c>fleet.json</c> who holds the newest <c>fleet.json</c>,
/// pulls any newer copy onto this PC, and immediately pushes newly accepted updates out to all
/// lagging peers.
/// </summary>
public sealed class FleetConfigSyncService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(4);

    private readonly FleetConfigSyncStore _store;
    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger<FleetConfigSyncService> _log;
    private readonly Func<FleetPeerEndpoint, HttpClient> _clientFactory;
    private readonly Func<string, bool> _isSelfHost;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public string NodeInstanceId { get; } = Guid.NewGuid().ToString("N");

    public FleetConfigSyncService(
        FleetConfigSyncStore store,
        IOptions<AgentOptions> options,
        ILogger<FleetConfigSyncService> log,
        Func<FleetPeerEndpoint, HttpClient>? clientFactory = null,
        Func<string, bool>? isSelfHost = null)
    {
        _store = store;
        _options = options;
        _log = log;
        _clientFactory = clientFactory ?? DefaultClientFactory;
        _isSelfHost = isSelfHost ?? IsLocalMachineHost;
    }

    /// <summary>Wakes the background loop to fan out a newly received or locally edited config.</summary>
    public void TriggerFanOut() => _wake.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Fleet config peer sync cycle failed.");
            }

            try
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delayTask = Task.Delay(PollInterval, delayCts.Token);
                var wakeTask = _wake.Reader.ReadAsync(stoppingToken).AsTask();
                var completed = await Task.WhenAny(delayTask, wakeTask);
                if (completed == wakeTask) delayCts.Cancel();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs one full ask-and-propagate round across peers:
    /// 1) ingest any local edits, 2) ask every peer who is newer and pull the newest copy,
    /// 3) push the newest copy to any peer that is missing it or behind.
    /// </summary>
    public async Task SyncOnceAsync(CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            _store.RefreshFromLocalFiles(out _);
            var current = _store.GetCurrentSync();
            if (current is null) return;

            var fallbackToken = FleetConfigSync.SanitizeToken(_options.Value.Token);
            var peers = FleetConfigSync.ParsePeers(current.Json, fallbackToken)
                .Where(p => !FleetConfigSync.IsLoopbackHost(p.Host) && !_isSelfHost(p.Host))
                .ToList();
            if (peers.Count == 0) return;

            var probes = await Task.WhenAll(peers.Select(p => ProbePeerAsync(p, ct)));

            var currentMeta = new FleetConfigMetaDto(current.UpdatedAtUtc, current.Sha256, current.OriginHost);

            // Phase 1: Pull from the peer holding the strictly newest fleet.json.
            var newerPeers = probes
                .Where(p => !p.IsSelf && p.Supported && p.Meta is not null && FleetConfigSync.IsNewer(p.Meta, currentMeta))
                .OrderByDescending(p => p.Meta!.UpdatedAtUtc)
                .ThenByDescending(p => p.Meta!.Sha256, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var winner in newerPeers)
            {
                var pulled = await FetchPeerConfigAsync(winner.Peer, ct);
                if (pulled is null) continue;

                var applied = _store.ApplyRemote(pulled);
                if (applied.Accepted)
                {
                    current = _store.GetCurrentSync();
                    if (current is not null)
                        currentMeta = new FleetConfigMetaDto(current.UpdatedAtUtc, current.Sha256, current.OriginHost);
                    break;
                }
            }

            if (current is null) return;

            // If a newer fleet.json was just pulled from a peer, peer list might have grown.
            var latestPeers = FleetConfigSync.ParsePeers(current.Json, fallbackToken)
                .Where(p => !FleetConfigSync.IsLoopbackHost(p.Host) && !_isSelfHost(p.Host))
                .ToList();
            var probedByEndpoint = probes.ToDictionary(p => p.Peer.Endpoint, StringComparer.OrdinalIgnoreCase);

            // Phase 2: Push the newest fleet.json to every peer that is behind or has none yet.
            var pushTargets = new List<FleetPeerEndpoint>();
            foreach (var peer in latestPeers)
            {
                if (!probedByEndpoint.TryGetValue(peer.Endpoint, out var probe))
                {
                    // Newly added peer from the pulled fleet.json: probe before pushing.
                    probe = await ProbePeerAsync(peer, ct);
                }

                if (probe.IsSelf || !probe.Supported) continue;
                if (probe.Meta is null || FleetConfigSync.IsNewer(currentMeta, probe.Meta))
                    pushTargets.Add(peer);
            }

            if (pushTargets.Count > 0)
            {
                await Task.WhenAll(pushTargets.Select(p => PushPeerConfigAsync(p, current, ct)));
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<PeerProbe> ProbePeerAsync(FleetPeerEndpoint peer, CancellationToken ct)
    {
        try
        {
            using var client = _clientFactory(peer);
            using var response = await client.GetAsync("fleet-config/meta", ct);
            if (response.Headers.TryGetValues(FleetConfigSync.NodeIdHeader, out var ids)
                && ids.Any(id => string.Equals(id, NodeInstanceId, StringComparison.Ordinal)))
            {
                return new PeerProbe(peer, Supported: false, IsSelf: true, Meta: null);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new PeerProbe(peer, Supported: false, IsSelf: false, Meta: null);

            if (response.StatusCode == HttpStatusCode.NoContent)
                return new PeerProbe(peer, Supported: true, IsSelf: false, Meta: null);

            if (!response.IsSuccessStatusCode)
                return new PeerProbe(peer, Supported: false, IsSelf: false, Meta: null);

            var meta = await response.Content.ReadFromJsonAsync<FleetConfigMetaDto>(JsonOptions, ct);
            return new PeerProbe(peer, Supported: true, IsSelf: false, Meta: meta);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new PeerProbe(peer, Supported: false, IsSelf: false, Meta: null);
        }
    }

    private async Task<FleetConfigSyncDto?> FetchPeerConfigAsync(FleetPeerEndpoint peer, CancellationToken ct)
    {
        try
        {
            using var client = _clientFactory(peer);
            using var response = await client.GetAsync("fleet-config", ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<FleetConfigSyncDto>(JsonOptions, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task PushPeerConfigAsync(FleetPeerEndpoint peer, FleetConfigSyncDto payload, CancellationToken ct)
    {
        try
        {
            using var client = _clientFactory(peer);
            using var response = await client.PutAsJsonAsync("fleet-config", payload, JsonOptions, ct);
            if (!response.IsSuccessStatusCode) return;

            var result = await response.Content.ReadFromJsonAsync<FleetConfigPushResultDto>(JsonOptions, ct);
            if (result is { Accepted: true, Status: "updated" })
            {
                _log.LogInformation("Pushed newer fleet.json to peer {Peer} ({Endpoint}).", peer.Name, peer.Endpoint);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
        }
    }

    private static HttpClient DefaultClientFactory(FleetPeerEndpoint peer)
    {
        // Bypass system proxy for tailnet peers, matching AgentClient.
        var handler = new HttpClientHandler { UseProxy = false };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{peer.Endpoint}/api/v1/"),
            Timeout = PeerTimeout,
        };
        if (!string.IsNullOrWhiteSpace(peer.Token))
            client.DefaultRequestHeaders.Add("X-Fleet-Token", peer.Token);
        return client;
    }

    private static bool IsLocalMachineHost(string host)
    {
        var machine = Environment.MachineName;
        if (host.Equals(machine, StringComparison.OrdinalIgnoreCase)
            || host.StartsWith(machine + ".", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out var parsed)) return false;
        if (IPAddress.IsLoopback(parsed)) return true;

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
                        && ua.Address.Equals(parsed))
                        return true;
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return false;
    }

    private sealed record PeerProbe(FleetPeerEndpoint Peer, bool Supported, bool IsSelf, FleetConfigMetaDto? Meta);
}
