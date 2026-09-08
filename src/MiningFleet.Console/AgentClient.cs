using System.Net.Http.Json;
using System.Text.Json;
using MiningFleet.Contracts;

namespace MiningFleet.Console;

/// <summary>Talks to one node agent over the tailnet.</summary>
public sealed class AgentClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public AgentClient(NodeConfig node, string token, TimeSpan? timeout = null)
    {
        Node = node;
        // Agents live on private tailnet addresses, so a system proxy has no business in the
        // middle. Without this, a machine running a local VPN/proxy client sends fleet traffic
        // into it and every node comes back as an HTTP error that looks like the agent's fault.
        var handler = new HttpClientHandler { UseProxy = false };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{node.Endpoint}/api/v1/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(8),
        };
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Add("X-Fleet-Token", token);
    }

    public NodeConfig Node { get; }

    public Task<NodeSnapshotDto?> GetStatusAsync(CancellationToken ct) =>
        _http.GetFromJsonAsync<NodeSnapshotDto>("status", JsonOptions, ct);

    public Task<AgentInfoDto?> GetInfoAsync(CancellationToken ct) =>
        _http.GetFromJsonAsync<AgentInfoDto>("info", JsonOptions, ct);

    public Task<MinerConfigDto?> GetConfigAsync(CancellationToken ct) =>
        _http.GetFromJsonAsync<MinerConfigDto>("config", JsonOptions, ct);

    public Task<LogTailDto?> GetLogsAsync(CancellationToken ct) =>
        _http.GetFromJsonAsync<LogTailDto>("logs", JsonOptions, ct);

    /// <summary>
    /// The node's own record of every rung it moved to and the readings behind it. Returns null
    /// on an agent that predates throttling, which the caller reports rather than treats as an
    /// error - a mixed-version fleet is normal while a roll-out is in progress.
    /// </summary>
    public async Task<LogTailDto?> GetThrottleLogAsync(int lines, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"throttle/log?lines={lines}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LogTailDto>(JsonOptions, ct);
    }

    public Task<CommandResultDto?> StartAsync(CancellationToken ct) => PostAsync("miner/start", ct);
    public Task<CommandResultDto?> StopAsync(CancellationToken ct) => PostAsync("miner/stop", ct);
    public Task<CommandResultDto?> RestartAsync(CancellationToken ct) => PostAsync("miner/restart", ct);

    public Task<CommandResultDto?> GpuStartAsync(CancellationToken ct) => PostAsync("gpu/start", ct);
    public Task<CommandResultDto?> GpuStopAsync(CancellationToken ct) => PostAsync("gpu/stop", ct);
    public Task<CommandResultDto?> GpuRestartAsync(CancellationToken ct) => PostAsync("gpu/restart", ct);

    /// <summary>
    /// The GPU miner's own view of itself. Returns null on an agent that predates GPU mining,
    /// which the caller reports rather than treats as an error, the same way
    /// <see cref="GetThrottleLogAsync"/> does - a mixed-version fleet is normal mid-roll-out.
    /// </summary>
    public async Task<GpuMinerStatusDto?> GetGpuAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("gpu", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GpuMinerStatusDto>(JsonOptions, ct);
    }

    /// <inheritdoc cref="GetGpuAsync"/>
    public async Task<LogTailDto?> GetGpuLogsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("gpu/logs", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LogTailDto>(JsonOptions, ct);
    }

    public async Task<MinerConfigDto?> PutConfigAsync(MinerConfigDto patch, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync("config", patch, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MinerConfigDto>(JsonOptions, ct);
    }

    /// <summary>Downloads and unpacks xmrig on the node. Slow, so it gets its own long timeout.</summary>
    public async Task<InstallResultDto?> InstallAsync(InstallRequestDto request, CancellationToken ct)
    {
        using var http = LongRunning();
        using var message = new HttpRequestMessage(HttpMethod.Post, "install")
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        using var response = await http.SendAsync(message, ct);
        return await response.Content.ReadFromJsonAsync<InstallResultDto>(JsonOptions, ct);
    }

    /// <summary>
    /// A client for calls that take minutes rather than seconds.
    ///
    /// HttpClient.Timeout covers the whole request and cannot be raised per call, so extending
    /// only the CancellationToken achieves nothing: the eight-second default fires first and
    /// kills the transfer mid-download. That is exactly how `upgrade-agents` failed on every
    /// node while the same request, made by hand with a longer timeout, succeeded. Slow calls
    /// own their timeout here instead of trusting whoever constructed this client to have
    /// guessed it right.
    /// </summary>
    private HttpClient LongRunning()
    {
        var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = _http.BaseAddress,
            Timeout = TimeSpan.FromMinutes(10),
        };

        foreach (var header in _http.DefaultRequestHeaders)
            http.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);

        return http;
    }

    /// <summary>
    /// Updates the agent itself. The node answers before it restarts, so a normal timeout is
    /// enough; the connection may still drop mid-reply if the swap is fast, which the caller
    /// treats as success-pending rather than failure.
    /// </summary>
    public async Task<AgentUpdateResultDto?> UpdateAgentAsync(AgentUpdateRequestDto request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "agent/update")
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        using var http = LongRunning();
        using var response = await http.SendAsync(message, ct);

        // An agent from before this feature has no such route and answers 404 with an empty
        // body. During a roll-out that is the normal case, not an error worth a stack trace,
        // so it has to read as a plain instruction rather than crash the run.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new AgentUpdateResultDto(
                false,
                "this agent is too old to update itself - install it once by hand, then the console can do the rest",
                null, null, false);

        if (!response.IsSuccessStatusCode)
            return new AgentUpdateResultDto(false, $"the agent returned HTTP {(int)response.StatusCode}", null, null, false);

        return await response.Content.ReadFromJsonAsync<AgentUpdateResultDto>(JsonOptions, ct);
    }

    private async Task<CommandResultDto?> PostAsync(string path, CancellationToken ct)
    {
        using var response = await _http.PostAsync(path, content: null, ct);
        return await response.Content.ReadFromJsonAsync<CommandResultDto>(JsonOptions, ct);
    }

    public void Dispose() => _http.Dispose();
}
