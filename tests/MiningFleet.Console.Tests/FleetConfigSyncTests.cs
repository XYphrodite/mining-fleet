using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiningFleet.Agent;
using MiningFleet.Console;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

public sealed class FleetConfigSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fleet-sync-tests-{Guid.NewGuid():N}");

    public FleetConfigSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public void Ordering_prefers_newer_timestamp_ignores_identical_hash_and_tiebreaks_deterministically()
    {
        var t1 = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(5);

        var a = new FleetConfigMetaDto(t1, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var b = new FleetConfigMetaDto(t2, "1111111111111111111111111111111111111111111111111111111111111111");
        var aSameHashLater = new FleetConfigMetaDto(t2, a.Sha256);
        var tieHigh = new FleetConfigMetaDto(t1, "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        Assert.True(FleetConfigSync.IsNewer(a, null));
        Assert.True(FleetConfigSync.IsNewer(b, a));
        Assert.False(FleetConfigSync.IsNewer(a, b));
        Assert.False(FleetConfigSync.IsNewer(aSameHashLater, a));

        Assert.True(FleetConfigSync.IsNewer(tieHigh, a));
        Assert.False(FleetConfigSync.IsNewer(a, tieHigh));
    }

    [Fact]
    public void Validation_and_peer_parsing_reject_invalid_json_and_extract_enabled_peers()
    {
        Assert.False(FleetConfigSync.TryValidateJson("", out _));
        Assert.False(FleetConfigSync.TryValidateJson("{not json", out _));
        Assert.False(FleetConfigSync.TryValidateJson("{\"token\":\"abc\"}", out _));

        var valid = """
            {
              "token": " \uFEFFsecret-token\r\n",
              "agentPort": 47800,
              "nodes": [
                { "name": "node-1", "host": "100.64.0.1", "enabled": true },
                { "name": "node-2", "host": "node-2.tail.ts.net", "port": 47900, "token": "override-token", "enabled": true },
                { "name": "disabled", "host": "100.64.0.99", "enabled": false },
                { "name": "dup", "host": "100.64.0.1", "enabled": true }
              ]
            }
            """;

        Assert.True(FleetConfigSync.TryValidateJson(valid, out var err), err);
        var peers = FleetConfigSync.ParsePeers(valid);
        Assert.Equal(2, peers.Count);
        Assert.Equal("http://100.64.0.1:47800", peers[0].Endpoint);
        Assert.Equal("secret-token", peers[0].Token);
        Assert.Equal("http://node-2.tail.ts.net:47900", peers[1].Endpoint);
        Assert.Equal("override-token", peers[1].Token);
    }

    [Fact]
    public void Store_mirrors_newest_remote_to_local_console_without_pingpong_and_detects_local_edits()
    {
        var agentDir = Path.Combine(_dir, "agent");
        var consoleDir = Path.Combine(_dir, "console");
        Directory.CreateDirectory(agentDir);
        Directory.CreateDirectory(consoleDir);

        var consoleFleet = Path.Combine(consoleDir, "fleet.json");
        var v1Json = """{"token":"t","nodes":[{"name":"n1","host":"100.64.0.1","enabled":true}]}""";
        var t1 = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        FleetConfigSync.WriteFileAtomic(consoleFleet, v1Json, t1);

        var store = new FleetConfigSyncStore(agentDir, () => [consoleFleet]);
        Assert.NotNull(store.CurrentMeta);
        Assert.Equal(FleetConfigSync.ComputeSha256(v1Json), store.CurrentMeta!.Sha256);
        Assert.True(File.Exists(Path.Combine(agentDir, "fleet.json")));

        // Older remote push is rejected as stale.
        var staleResult = store.ApplyRemote(new FleetConfigSyncDto
        {
            UpdatedAtUtc = t1.AddMinutes(-10),
            Json = """{"token":"t","nodes":[]}""",
            OriginHost = "old-pc",
        });
        Assert.False(staleResult.Accepted);
        Assert.Equal("stale", staleResult.Status);

        // Newer remote push updates both agent and console copies.
        var v2Json = """{"token":"t","nodes":[{"name":"n1","host":"100.64.0.1","enabled":true},{"name":"n2","host":"100.64.0.2","enabled":true}]}""";
        var t2 = t1.AddMinutes(10);
        var updatedResult = store.ApplyRemote(new FleetConfigSyncDto
        {
            UpdatedAtUtc = t2,
            Json = v2Json,
            OriginHost = "new-pc",
        });
        Assert.True(updatedResult.Accepted);
        Assert.Equal("updated", updatedResult.Status);
        Assert.Equal(v2Json, File.ReadAllText(consoleFleet));
        Assert.Equal(v2Json, File.ReadAllText(Path.Combine(agentDir, "fleet.json")));

        // No ping-pong on refresh after applying remote.
        Assert.False(store.RefreshFromLocalFiles(out _));

        // Direct local edit on disk (even with slightly skewed older clock) is detected as newer.
        var v3Json = """{"token":"t","nodes":[{"name":"n3","host":"100.64.0.3","enabled":true}]}""";
        FleetConfigSync.WriteFileAtomic(consoleFleet, v3Json, t2.AddSeconds(-5));
        Assert.True(store.RefreshFromLocalFiles(out var localUpdated));
        Assert.NotNull(localUpdated);
        Assert.Equal(FleetConfigSync.ComputeSha256(v3Json), localUpdated!.Sha256);
        Assert.True(localUpdated.UpdatedAtUtc > t2);
        Assert.Equal(v3Json, File.ReadAllText(Path.Combine(agentDir, "fleet.json")));
    }

    [Fact]
    public async Task Agent_sync_service_asks_peers_pulls_newest_and_pushes_to_lagging_nodes()
    {
        var agentADir = Path.Combine(_dir, "agentA");
        var consoleADir = Path.Combine(_dir, "consoleA");
        Directory.CreateDirectory(agentADir);
        Directory.CreateDirectory(consoleADir);
        var consoleAFleet = Path.Combine(consoleADir, "fleet.json");

        var tOld = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        var tNewest = tOld.AddHours(2);

        var oldJson = """
            {
              "token": "tok",
              "nodes": [
                { "name": "pc-a", "host": "127.0.0.1", "enabled": true },
                { "name": "pc-b", "host": "100.64.0.2", "enabled": true },
                { "name": "pc-c", "host": "100.64.0.3", "enabled": true },
                { "name": "pc-old", "host": "100.64.0.4", "enabled": true }
              ]
            }
            """;
        var newestJson = """
            {
              "token": "tok",
              "nodes": [
                { "name": "pc-a", "host": "100.64.0.1", "enabled": true },
                { "name": "pc-b", "host": "100.64.0.2", "enabled": true },
                { "name": "pc-c", "host": "100.64.0.3", "enabled": true },
                { "name": "pc-old", "host": "100.64.0.4", "enabled": true },
                { "name": "pc-e", "host": "100.64.0.5", "enabled": true }
              ]
            }
            """;
        var newestSha = FleetConfigSync.ComputeSha256(newestJson);

        FleetConfigSync.WriteFileAtomic(consoleAFleet, oldJson, tOld);
        var storeA = new FleetConfigSyncStore(agentADir, () => [consoleAFleet]);

        FleetConfigSyncDto? pushedToC = null;
        FleetConfigSyncDto? pushedToE = null;
        var pushedToOld = false;

        HttpClient CreatePeerClient(FleetPeerEndpoint peer)
        {
            var handler = new StubHandler(async req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (peer.Host == "100.64.0.2")
                {
                    if (path.EndsWith("/fleet-config/meta"))
                        return JsonResponse(HttpStatusCode.OK, new FleetConfigMetaDto(tNewest, newestSha, "pc-b"));
                    if (path.EndsWith("/fleet-config") && req.Method == HttpMethod.Get)
                        return JsonResponse(HttpStatusCode.OK, new FleetConfigSyncDto
                        {
                            UpdatedAtUtc = tNewest,
                            Sha256 = newestSha,
                            Json = newestJson,
                            OriginHost = "pc-b",
                        });
                }
                else if (peer.Host == "100.64.0.3")
                {
                    if (path.EndsWith("/fleet-config/meta"))
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    if (path.EndsWith("/fleet-config") && req.Method == HttpMethod.Put)
                    {
                        pushedToC = await req.Content!.ReadFromJsonAsync<FleetConfigSyncDto>();
                        return JsonResponse(HttpStatusCode.OK, new FleetConfigPushResultDto(true, "updated", new FleetConfigMetaDto(tNewest, newestSha)));
                    }
                }
                else if (peer.Host == "100.64.0.4")
                {
                    if (path.EndsWith("/fleet-config/meta"))
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    if (path.EndsWith("/fleet-config") && req.Method == HttpMethod.Put)
                        pushedToOld = true;
                }
                else if (peer.Host == "100.64.0.5")
                {
                    if (path.EndsWith("/fleet-config/meta"))
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    if (path.EndsWith("/fleet-config") && req.Method == HttpMethod.Put)
                    {
                        pushedToE = await req.Content!.ReadFromJsonAsync<FleetConfigSyncDto>();
                        return JsonResponse(HttpStatusCode.OK, new FleetConfigPushResultDto(true, "updated", new FleetConfigMetaDto(tNewest, newestSha)));
                    }
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            return new HttpClient(handler) { BaseAddress = new Uri($"{peer.Endpoint}/api/v1/") };
        }

        var service = new FleetConfigSyncService(
            storeA,
            Options.Create(new AgentOptions { Token = "tok" }),
            NullLogger<FleetConfigSyncService>.Instance,
            CreatePeerClient,
            host => host == "100.64.0.1");

        await service.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(newestSha, storeA.CurrentMeta?.Sha256);
        Assert.Equal(newestJson, File.ReadAllText(consoleAFleet));
        Assert.NotNull(pushedToC);
        Assert.Equal(newestSha, pushedToC!.Sha256);
        Assert.NotNull(pushedToE);
        Assert.Equal(newestSha, pushedToE!.Sha256);
        Assert.False(pushedToOld);
    }

    [Fact]
    public async Task Console_fleet_service_syncs_from_newest_node_and_pushes_to_lagging_node()
    {
        var consolePath = Path.Combine(_dir, "console", "fleet.json");
        var tOld = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);
        var tNewest = tOld.AddHours(3);

        var initialConfig = FleetConfig.Load(consolePath);
        initialConfig.Token = "tok";
        initialConfig.Nodes.AddRange([
            new NodeConfig { Name = "pc-b", Host = "100.64.0.2", Enabled = true },
            new NodeConfig { Name = "pc-c", Host = "100.64.0.3", Enabled = true },
        ]);
        initialConfig.Save();
        // Backdate initial file so pc-b wins cleanly.
        var initialRaw = File.ReadAllText(consolePath);
        FleetConfigSync.WriteFileAtomic(consolePath, initialRaw, tOld);
        var loaded = FleetConfig.Load(consolePath);

        var newestConfig = new FleetConfig { Token = "tok" };
        newestConfig.Pool.Url = "pool.example.com:443";
        newestConfig.Nodes.AddRange([
            new NodeConfig { Name = "pc-b", Host = "100.64.0.2", Enabled = true },
            new NodeConfig { Name = "pc-c", Host = "100.64.0.3", Enabled = true },
        ]);
        var newestJson = JsonSerializer.Serialize(newestConfig, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        var newestSha = FleetConfigSync.ComputeSha256(newestJson);

        FleetConfigSyncDto? receivedOnC = null;

        var fleet = new FleetService(loaded, (node, _) => new AgentClient(node, new StubHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (node.Name == "pc-b")
            {
                if (path.EndsWith("/fleet-config/meta"))
                    return JsonResponse(HttpStatusCode.OK, new FleetConfigMetaDto(tNewest, newestSha, "pc-b"));
                if (path.EndsWith("/fleet-config") && req.Method == HttpMethod.Get)
                    return JsonResponse(HttpStatusCode.OK, new FleetConfigSyncDto
                    {
                        UpdatedAtUtc = tNewest,
                        Sha256 = newestSha,
                        Json = newestJson,
                        OriginHost = "pc-b",
                    });
            }
            else if (node.Name == "pc-c")
            {
                if (path.EndsWith("/fleet-config/meta"))
                    return JsonResponse(HttpStatusCode.OK, new FleetConfigMetaDto(tOld, FleetConfigSync.ComputeSha256(initialRaw), "pc-c"));
                if (path.EndsWith("/fleet-config") && req.Method == HttpMethod.Put)
                {
                    receivedOnC = await req.Content!.ReadFromJsonAsync<FleetConfigSyncDto>();
                    return JsonResponse(HttpStatusCode.OK, new FleetConfigPushResultDto(true, "updated", new FleetConfigMetaDto(tNewest, newestSha)));
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));

        var report = await fleet.SyncFleetConfigAsync(ct: CancellationToken.None);

        Assert.Equal("pc-b", report.PulledFrom);
        Assert.Equal(["pc-c"], report.PushedTo);
        Assert.Equal(["pc-b"], report.UpToDate);
        Assert.Equal("pool.example.com:443", loaded.Pool.Url);
        Assert.NotNull(receivedOnC);
        Assert.Equal(newestSha, receivedOnC!.Sha256);

        // Snapshots matching current meta must not trigger another sync.
        var states = new[]
        {
            SnapshotState(loaded.Nodes[0], new FleetConfigMetaDto(tNewest, newestSha)),
            SnapshotState(loaded.Nodes[1], new FleetConfigMetaDto(tNewest, newestSha)),
        };
        Assert.Null(await fleet.ObserveSnapshotsForSyncAsync(states, CancellationToken.None));
    }

    [Fact]
    public void Protected_file_lists_keep_fleet_json_and_sync_metadata()
    {
        Assert.Contains("fleet.json", AgentUpdateService.ProtectedFileNames);
        Assert.Contains("fleet.sync.json", AgentUpdateService.ProtectedFileNames);
        Assert.Contains("fleet.json", FleetConsoleUpdate.ProtectedFileNames);
        Assert.Contains("fleet.sync.json", FleetConsoleUpdate.ProtectedFileNames);
    }

    private static NodeState SnapshotState(NodeConfig node, FleetConfigMetaDto meta) =>
        new(
            node,
            new NodeSnapshotDto(
                new AgentInfoDto(node.Name, "windows", "1.17.48.0", ApiVersion.Current, 60, true),
                new MinerStatusDto(),
                new HardwareDto())
            {
                FleetConfig = meta,
            },
            Error: null,
            PolledAt: DateTimeOffset.UtcNow);

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode code, T payload) =>
        new(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
