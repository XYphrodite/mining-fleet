using System.Net;

namespace MiningFleet.Contracts;

/// <summary>
/// Uses the configured system/environment proxy when available, and transparently falls back to a
/// direct connection (<c>UseProxy = false</c>) when a local loopback proxy (<c>127.0.0.1</c>,
/// <c>localhost</c>, <c>[::1]</c>) refuses connections or fails to establish a tunnel — for example,
/// after a reboot or when a local VPN client (v2rayN, Clash, Happ) is closed while its
/// <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c> or WinINET proxy settings remain configured.
/// </summary>
public sealed class ProxyFallbackHandler : HttpMessageHandler
{
    public static readonly TimeSpan DefaultBypassDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultPooledConnectionLifetime = TimeSpan.FromMinutes(5);

    private readonly HttpMessageInvoker _proxied;
    private readonly HttpMessageInvoker _direct;
    private readonly IWebProxy _proxyInspector;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _bypassDuration;
    private long _bypassUntilUtcTicks;

    public ProxyFallbackHandler()
        : this(
            CreateSocketsHandler(useProxy: true),
            CreateSocketsHandler(useProxy: false),
            HttpClient.DefaultProxy,
            TimeProvider.System,
            DefaultBypassDuration,
            disposeHandlers: true)
    {
    }

    public ProxyFallbackHandler(
        HttpMessageHandler proxiedHandler,
        HttpMessageHandler directHandler,
        IWebProxy? proxyInspector = null,
        TimeProvider? clock = null,
        TimeSpan? bypassDuration = null,
        bool disposeHandlers = true)
    {
        ArgumentNullException.ThrowIfNull(proxiedHandler);
        ArgumentNullException.ThrowIfNull(directHandler);

        _proxied = new HttpMessageInvoker(proxiedHandler, disposeHandlers);
        _direct = new HttpMessageInvoker(directHandler, disposeHandlers);
        _proxyInspector = proxyInspector ?? HttpClient.DefaultProxy;
        _clock = clock ?? TimeProvider.System;
        _bypassDuration = bypassDuration ?? DefaultBypassDuration;
    }

    public bool IsBypassActive =>
        _clock.GetUtcNow().UtcTicks < Interlocked.Read(ref _bypassUntilUtcTicks);

    public bool HasLoopbackProxy(Uri? destination)
    {
        if (destination is null) return false;
        try
        {
            if (_proxyInspector.IsBypassed(destination)) return false;
            var proxyUri = _proxyInspector.GetProxy(destination);
            return proxyUri is not null && proxyUri != destination && proxyUri.IsLoopback;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!HasLoopbackProxy(request.RequestUri))
            return await _proxied.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var fallbackCopy = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
        var copyUsed = false;
        try
        {
            if (IsBypassActive)
            {
                try
                {
                    return await _direct.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (CanFallback(ex, cancellationToken))
                {
                    // Direct failed while bypass was cached (e.g. VPN was started on a network that
                    // requires it); clear the cooldown and immediately retry through the proxy.
                    Interlocked.Exchange(ref _bypassUntilUtcTicks, 0);
                    copyUsed = true;
                    return await _proxied.SendAsync(fallbackCopy, cancellationToken).ConfigureAwait(false);
                }
            }

            try
            {
                return await _proxied.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (CanFallback(ex, cancellationToken))
            {
                copyUsed = true;
                var response = await _direct.SendAsync(fallbackCopy, cancellationToken).ConfigureAwait(false);
                var until = _clock.GetUtcNow() + _bypassDuration;
                Interlocked.Exchange(ref _bypassUntilUtcTicks, until.UtcTicks);
                return response;
            }
        }
        finally
        {
            if (!copyUsed)
                fallbackCopy.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _proxied.Dispose();
            _direct.Dispose();
        }

        base.Dispose(disposing);
    }

    private static bool CanFallback(Exception ex, CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested
        && (ex is HttpRequestException or OperationCanceledException);

    private static SocketsHttpHandler CreateSocketsHandler(bool useProxy) =>
        new()
        {
            UseProxy = useProxy,
            ConnectTimeout = DefaultConnectTimeout,
            PooledConnectionLifetime = DefaultPooledConnectionLifetime,
        };

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in request.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Content = content;
        }

        return clone;
    }
}
