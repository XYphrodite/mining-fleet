using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MiningFleet.Agent;
using MiningFleet.Contracts;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Without this a `dotnet run` agent defaults to Development and answers errors with a
    // full stack trace, which would be served to anything on the tailnet that can reach it.
    EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production,
    // Configuration follows the content root, which otherwise defaults to the current
    // directory. Run the agent by hand from anywhere else and it silently reads no
    // appsettings.json at all, then warns that its token is empty - which sends whoever is
    // debugging after a missing secret that is in fact sitting right beside the binary.
    ContentRootPath = AppContext.BaseDirectory,
});
var basePath = AppContext.BaseDirectory;

// Lets the same binary run in the foreground for debugging and as a service on a node.
// Both calls are no-ops when the process was not started by the respective service manager.
builder.Host.UseWindowsService(options => options.ServiceName = "xmrig-fleet-agent");
builder.Host.UseSystemd();

// UseWindowsService installs the event-log provider, which throws out of ILogger.Log when the
// Event Log service is unreachable. On one node it answers "RPC server unavailable", and the
// agent died mid-warning - twice - leaving a machine that had to be visited in person. Logging
// must never be able to do that, so the provider goes and a file beside the binary takes over.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(basePath, "agent.log")));

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddSingleton(new MinerConfigStore(basePath));
builder.Services.AddSingleton<MinerService>();
builder.Services.AddSingleton<HardwareService>();
builder.Services.AddSingleton<InstallerService>();
builder.Services.AddSingleton<AgentUpdateService>();
builder.Services.AddSingleton<SessionMonitorService>();
builder.Services.AddSingleton<MinerCpuLimit>();
builder.Services.AddSingleton(new ThrottleLog(basePath));
builder.Services.AddSingleton<ThrottleService>();
builder.Services.AddSingleton<GpuMinerService>();
builder.Services.AddSingleton<GpuPauseService>();
// Same instance both ways: the config endpoint calls Apply for an immediate response, the
// background loop keeps the window alive and covers a logon that happens later.
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionMonitorService>());
builder.Services.AddHostedService<PerformanceCounterPump>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ThrottleService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<GpuPauseService>());
builder.Services.AddHostedService<CpuReservationService>();
builder.Services.AddHostedService<CpuBudgetService>();
builder.Services.AddSingleton<MinerPauseService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MinerPauseService>());
builder.Services.AddHttpClient("github", client =>
{
    // The GitHub API rejects requests without a User-Agent.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("xmrig-fleet-agent");
    client.Timeout = TimeSpan.FromMinutes(5);
});
// Deliberately left on the system proxy. Bypassing it looked right - the console and the xmrig
// API reader both bypass it, because tailnet addresses must never go through a VPN client - but
// those are local addresses. GitHub is not: on desktop-ib88isg the proxy is the only route out,
// and disabling it turned every self-update into "cannot connect to github.com:443". A node that
// reaches GitHub directly loses nothing by having a proxy configured it does not need.

var options = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();
builder.WebHost.UseUrls(options.ListenUrl);

var app = builder.Build();
var startedAt = Stopwatch.GetTimestamp();
var agentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

// Files displaced by a previous self-update are dead weight once the new binary is running.
AgentUpdateService.CleanUpPreviousUpdate();

if (string.IsNullOrWhiteSpace(options.Token))
{
    app.Logger.LogWarning(
        "Agent:Token is empty - every host that can reach {Url} may control this miner. Set a token in appsettings.json.",
        options.ListenUrl);
}

// Shared-secret auth. Tailscale already restricts who can reach this port; the token
// stops anything else on the tailnet (or the LAN) from driving the miner.
app.Use(async (context, next) =>
{
    // Sanitised on both sides: PowerShell writes appsettings.json as UTF-8 with a BOM, and a
    // stray BOM or trailing newline in the token is invisible in an editor but locks the console
    // out of the node with a 401 that looks like a wrong secret. The console trims the same way.
    var configured = Sanitize(context.RequestServices.GetRequiredService<IOptions<AgentOptions>>().Value.Token);
    if (!string.IsNullOrWhiteSpace(configured))
    {
        var presented = Sanitize(context.Request.Headers["X-Fleet-Token"].ToString());
        if (!FixedTimeEquals(presented, configured))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(CommandResultDto.Failure("Invalid or missing X-Fleet-Token."));
            return;
        }
    }
    await next();
});

var api = app.MapGroup("/api/v1");

api.MapGet("/info", () => Info());

api.MapGet("/status", async (MinerService miner, HardwareService hw, ThrottleService throttle, SessionMonitorService monitor, GpuMinerService gpu, CancellationToken ct) =>
{
    // Sensors and the two miner APIs are independent, so read them together. Both miner reads are
    // capped by their own three-second client timeout, so this stays inside the console's eight.
    var minerTask = miner.GetStatusAsync(ct);
    var hardwareTask = hw.ReadAsync(ct);
    var gpuTask = gpu.GetStatusAsync(ct);
    await Task.WhenAll(minerTask, hardwareTask, gpuTask);
    return new NodeSnapshotDto(Info(), minerTask.Result, hardwareTask.Result)
    {
        Throttle = throttle.Status(),
        MonitorNotice = monitor.Notice,
        GpuMiner = gpuTask.Result,
    };
});

api.MapGet("/miner", (MinerService miner, CancellationToken ct) => miner.GetStatusAsync(ct));
api.MapPost("/miner/start", (MinerService miner, CancellationToken ct) => miner.StartAsync(ct));
api.MapPost("/miner/stop", (MinerService miner, CancellationToken ct) => miner.StopAsync(ct));
api.MapPost("/miner/restart", (MinerService miner, CancellationToken ct) => miner.RestartAsync(ct));

api.MapGet("/hardware", (HardwareService hw, CancellationToken ct) => hw.ReadAsync(ct));

api.MapGet("/config", (MinerConfigStore store) => store.Current);
api.MapPut("/config", (MinerConfigDto patch, MinerConfigStore store, SessionMonitorService monitor) =>
{
    var saved = store.Update(patch);

    // Only act when the push actually carried the flag: a pool-settings push leaves it null and
    // must not silently tear down a node's session monitor.
    if (patch.KeepMonitorOpen is { } wanted) monitor.Apply(wanted);

    return saved;
});

api.MapPost("/install", (InstallRequestDto request, InstallerService installer, CancellationToken ct) =>
    installer.InstallAsync(request, ct));

api.MapGet("/logs", (MinerService miner) => new LogTailDto("xmrig", miner.RecentOutput));

api.MapGet("/throttle", (ThrottleService throttle) => throttle.Status());

// The decisions this node made and the readings behind them. The shipped thresholds are a guess,
// and this is what they are meant to be corrected from.
api.MapGet("/throttle/log", (ThrottleLog decisions, int? lines) =>
    new LogTailDto("throttle", decisions.Tail(lines ?? 100)));

// The graphics card. A separate miner from xmrig in every respect - its own executable, pool,
// coin and API - so it gets its own routes rather than a flag on the miner ones. Stopping GPU
// mining must never be able to stop the CPU miner by accident.
api.MapGet("/gpu", (GpuMinerService gpu, CancellationToken ct) => gpu.GetStatusAsync(ct));
api.MapPost("/gpu/start", (GpuMinerService gpu, CancellationToken ct) => gpu.StartAsync(ct));
api.MapPost("/gpu/stop", (GpuMinerService gpu, CancellationToken ct) => gpu.StopAsync(ct));
api.MapPost("/gpu/restart", (GpuMinerService gpu, CancellationToken ct) => gpu.RestartAsync(ct));

api.MapGet("/gpu/logs", (GpuMinerService gpu) => new LogTailDto("lolminer", gpu.RecentOutput));

// Updates the agent itself and restarts into the new binary. The miner is a separate process
// and keeps hashing; the node's token files are deliberately left untouched.
api.MapPost("/agent/update", (AgentUpdateRequestDto request, AgentUpdateService updater, CancellationToken ct) =>
    updater.UpdateAsync(request, ct));

app.Logger.LogInformation("mining-fleet agent {Version} listening on {Url}", agentVersion, options.ListenUrl);

// What the operator set from the console wins over what the installer wrote; see
// MinerConfigStore.ShouldAutoStart. Deciding it here rather than reading options.AutoStartMiner
// is the whole point of the setting: a node that reboots on its own - after a mains failure or
// a bugcheck - has nobody to sign in and start it.
if (app.Services.GetRequiredService<MinerConfigStore>().ShouldAutoStart(options.AutoStartMiner))
{
    var autoMiner = app.Services.GetRequiredService<MinerService>();
    var autoResult = await autoMiner.StartAsync(CancellationToken.None);
    app.Logger.LogInformation("Autostart: {Message}", autoResult.Message);
}

// The card needs no setting of its own to answer the same question. `gpuMiner.enabled` already
// says this card is meant to be working, so a node that came back on its own puts it back to work;
// a card the operator disabled stays idle. Without this, moving a node off a scheduled task with a
// boot trigger would be a downgrade - it would mine until the next reboot and never again.
var gpuStore = app.Services.GetRequiredService<MinerConfigStore>();
if (gpuStore.Current.GpuMiner?.Enabled == true)
{
    var autoGpu = app.Services.GetRequiredService<GpuMinerService>();
    var gpuResult = await autoGpu.StartAsync(CancellationToken.None);
    app.Logger.LogInformation("GPU autostart: {Message}", gpuResult.Message);

    // GpuStoppedByPause describes a miner this agent stopped and owes a restart to. The miner is
    // running again, so the debt is paid; leaving the flag set would let a later quiet tick
    // "resume" a miner that nobody had paused.
    if (gpuResult.Ok && gpuStore.Current.GpuStoppedByPause == true)
        gpuStore.Update(new MinerConfigDto { GpuStoppedByPause = false });
}

app.Run();
return;

AgentInfoDto Info() => new(
    Environment.MachineName,
    Environment.OSVersion.VersionString,
    agentVersion,
    ApiVersion.Current,
    Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
    HardwareService.IsElevated());

/// <summary>Strips whitespace and a byte-order mark, which editors and PowerShell add invisibly.</summary>
static string Sanitize(string? value) => (value ?? "").Trim().Trim('﻿');

static bool FixedTimeEquals(string a, string b)
{
    var left = System.Text.Encoding.UTF8.GetBytes(a);
    var right = System.Text.Encoding.UTF8.GetBytes(b);
    return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}
