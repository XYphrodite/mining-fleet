using System.Net;
using System.Net.Sockets;
using System.Text;
using MiningFleet.Console;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

public sealed class ProxyFallbackHandlerTests
{
    private static readonly Uri PoolUri = new("https://api.hashvault.pro/v3/monero/pool/stats");

    [Fact]
    public async Task Working_loopback_proxy_is_used_without_touching_direct()
    {
        var proxiedCalls = 0;
        var directCalls = 0;
        using var handler = new ProxyFallbackHandler(
            new StubHandler(_ =>
            {
                proxiedCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("via-proxy") };
            }),
            new StubHandler(_ =>
            {
                directCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("direct") };
            }),
            new FixedProxy(new Uri("http://127.0.0.1:10809")));

        using var http = new HttpClient(handler);
        var body = await http.GetStringAsync(PoolUri);

        Assert.Equal("via-proxy", body);
        Assert.Equal(1, proxiedCalls);
        Assert.Equal(0, directCalls);
        Assert.False(handler.IsBypassActive);
    }

    [Fact]
    public async Task Dead_loopback_proxy_falls_back_to_direct_and_caches_bypass_until_cooldown_expires()
    {
        var clock = new ManualClock();
        var proxyAlive = false;
        var proxiedCalls = 0;
        var directCalls = 0;

        using var handler = new ProxyFallbackHandler(
            new StubHandler(req =>
            {
                proxiedCalls++;
                Assert.Contains("mining-fleet-test", req.Headers.UserAgent.ToString());
                if (!proxyAlive)
                    throw new HttpRequestException("Connection refused (127.0.0.1:10809)");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("via-proxy") };
            }),
            new StubHandler(req =>
            {
                directCalls++;
                Assert.Contains("mining-fleet-test", req.Headers.UserAgent.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("via-direct") };
            }),
            new FixedProxy(new Uri("http://127.0.0.1:10809")),
            clock,
            bypassDuration: TimeSpan.FromSeconds(30));

        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("mining-fleet-test");

        // 1. First call hits dead proxy, falls back to direct, activates bypass window.
        Assert.Equal("via-direct", await http.GetStringAsync(PoolUri));
        Assert.Equal(1, proxiedCalls);
        Assert.Equal(1, directCalls);
        Assert.True(handler.IsBypassActive);

        // 2. Second call within 30s goes straight to direct without waiting on the dead proxy.
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("via-direct", await http.GetStringAsync(PoolUri));
        Assert.Equal(1, proxiedCalls);
        Assert.Equal(2, directCalls);

        // 3. After cooldown expires and VPN comes back up, proxy is probed and used again.
        clock.Advance(TimeSpan.FromSeconds(25));
        proxyAlive = true;
        Assert.False(handler.IsBypassActive);
        Assert.Equal("via-proxy", await http.GetStringAsync(PoolUri));
        Assert.Equal(2, proxiedCalls);
        Assert.Equal(2, directCalls);
    }

    [Fact]
    public async Task Cached_bypass_recovers_immediately_when_direct_fails_and_proxy_comes_online()
    {
        var clock = new ManualClock();
        var proxyAlive = false;
        var directAlive = true;

        using var handler = new ProxyFallbackHandler(
            new StubHandler(_ => proxyAlive
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("via-proxy") }
                : throw new HttpRequestException("Proxy down")),
            new StubHandler(_ => directAlive
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("via-direct") }
                : throw new HttpRequestException("Direct blocked")),
            new FixedProxy(new Uri("http://localhost:10809")),
            clock,
            bypassDuration: TimeSpan.FromSeconds(30));

        using var http = new HttpClient(handler);
        Assert.Equal("via-direct", await http.GetStringAsync(PoolUri));
        Assert.True(handler.IsBypassActive);

        // Network changes 5s later: direct is now blocked, local VPN client is now running.
        clock.Advance(TimeSpan.FromSeconds(5));
        directAlive = false;
        proxyAlive = true;

        Assert.Equal("via-proxy", await http.GetStringAsync(PoolUri));
        Assert.False(handler.IsBypassActive);
    }

    [Fact]
    public async Task Non_loopback_or_absent_proxy_does_not_retry_on_network_failure()
    {
        var proxiedCalls = 0;
        var directCalls = 0;

        using var noProxyHandler = new ProxyFallbackHandler(
            new StubHandler(_ =>
            {
                proxiedCalls++;
                throw new HttpRequestException("Origin unreachable");
            }),
            new StubHandler(_ =>
            {
                directCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            new FixedProxy(null));

        using var http = new HttpClient(noProxyHandler);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(PoolUri));
        Assert.Equal(1, proxiedCalls);
        Assert.Equal(0, directCalls);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_mistaken_for_dead_proxy()
    {
        var directCalls = 0;
        using var cts = new CancellationTokenSource();

        using var handler = new ProxyFallbackHandler(
            new StubHandler(_ =>
            {
                cts.Cancel();
                throw new TaskCanceledException("Cancelled by caller", null, cts.Token);
            }),
            new StubHandler(_ =>
            {
                directCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            new FixedProxy(new Uri("http://127.0.0.1:10809")));

        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync(PoolUri, cts.Token));
        Assert.Equal(0, directCalls);
    }

    [Fact]
    public async Task Real_sockets_handler_falls_back_past_closed_local_proxy_port_for_MarketService()
    {
        // Bind and immediately close a loopback listener to obtain a guaranteed-unused local port
        // that refuses TCP connections, reproducing HTTP_PROXY=http://127.0.0.1:10809 after reboot.
        int deadProxyPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            deadProxyPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;

        var serveTask = Task.Run(async () =>
        {
            using var client = await origin.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buf = new byte[1024];
            _ = await stream.ReadAsync(buf);

            const string payload = """
                {
                  "config": { "sigDivisor": 1000000000000, "coinDiffTarget": 120 },
                  "pool_statistics": { "collective": { "hashRate": 150000000, "miners": 4200 } },
                  "network_statistics": { "difficulty": 360000000000, "height": 3200000, "value": 600000000000 },
                  "market": { "price_rub": 28500.5 }
                }
                """;
            var bodyBytes = Encoding.UTF8.GetBytes(payload);
            var headerBytes = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headerBytes);
            await stream.WriteAsync(bodyBytes);
        });

        var deadProxy = new WebProxy(new Uri($"http://127.0.0.1:{deadProxyPort}"))
        {
            BypassProxyOnLocal = false,
        };
        using var fallbackHandler = new ProxyFallbackHandler(
            new SocketsHttpHandler { UseProxy = true, Proxy = deadProxy, ConnectTimeout = TimeSpan.FromSeconds(3) },
            new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) },
            deadProxy);

        var config = new FleetConfig
        {
            Pool = new PoolConfig { ApiBase = $"http://127.0.0.1:{originPort}" },
            Electricity = new ElectricityConfig { Currency = "RUB" },
        };
        using var market = new MarketService(config, fallbackHandler);

        var stats = await market.GetNetworkStatsAsync(CancellationToken.None);
        await serveTask;

        Assert.NotNull(stats);
        Assert.Equal(28500.5, stats!.Price);
        Assert.Equal("RUB", stats.PriceCurrency);
        Assert.True(fallbackHandler.IsBypassActive);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class FixedProxy(Uri? proxyUri) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => proxyUri ?? destination;
        public bool IsBypassed(Uri host) => proxyUri is null;
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 7, 1, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
