using System.Text.Json;
using System.Text.Json.Serialization;
using XmrigFleet.Contracts;

namespace XmrigFleet.Console;

/// <summary>
/// The whole fleet definition, persisted as fleet.json next to the console binary
/// (override with the XMRIG_FLEET_CONFIG environment variable).
/// </summary>
public sealed class FleetConfig
{
    /// <summary>Default X-Fleet-Token, used for every node that does not carry its own.</summary>
    public string Token { get; set; } = "";

    /// <summary>Port newly added nodes default to.</summary>
    public int AgentPort { get; set; } = 47800;

    public int PollIntervalSeconds { get; set; } = 5;

    public ElectricityConfig Electricity { get; set; } = new();
    public PoolConfig Pool { get; set; } = new();

    /// <summary>
    /// Where to read the XMR spot price when the pool does not publish the configured
    /// currency. Must return CoinGecko-shaped JSON. `{currency}` is replaced with the
    /// lower-cased currency code, so the feed is asked for the currency actually in use.
    /// </summary>
    public string PriceApiUrl { get; set; } = "https://api.coingecko.com/api/v3/simple/price?ids=monero&vs_currencies={currency}";

    public UpdateConfig Update { get; set; } = new();

    /// <summary>Fleet-wide throttle rules. Individual nodes override parts of this.</summary>
    public ThrottleConfig Throttle { get; set; } = new();

    /// <summary>
    /// Fleet-wide GPU mining defaults. Mostly a place to keep the pause rule and the pool login;
    /// the algorithm usually belongs on the node, because it is a property of the card.
    /// </summary>
    public GpuMinerConfig GpuMiner { get; set; } = new();

    public List<NodeConfig> Nodes { get; set; } = [];

    [JsonIgnore]
    public string Path { get; private set; } = "";

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("XMRIG_FLEET_CONFIG")
        ?? System.IO.Path.Combine(AppContext.BaseDirectory, "fleet.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static FleetConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        FleetConfig config;
        if (File.Exists(path))
        {
            try
            {
                config = JsonSerializer.Deserialize<FleetConfig>(File.ReadAllText(path), JsonOptions) ?? new FleetConfig();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{path} is not valid JSON: {ex.Message}", ex);
            }
        }
        else
        {
            config = new FleetConfig();
        }

        config.Path = path;
        return config;
    }

    public void Save()
    {
        var path = string.IsNullOrEmpty(Path) ? DefaultPath : Path;
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        Path = path;
    }

    public NodeConfig? FindNode(string name) =>
        Nodes.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public string TokenFor(NodeConfig node) =>
        Sanitize(string.IsNullOrWhiteSpace(node.Token) ? Token : node.Token);

    /// <summary>
    /// Strips whitespace and a byte-order mark from a token. A token pasted from a file or
    /// a terminal easily carries a trailing newline or a BOM, and those characters make the
    /// HTTP header itself invalid: the request then fails at the transport level and the node
    /// is reported unreachable rather than unauthorized, which sends the operator hunting the
    /// wrong problem.
    /// </summary>
    private static string Sanitize(string token) => token.Trim().Trim('﻿', '​');

    /// <summary>The tariff that actually applies to a node: its own, else the fleet default.</summary>
    public double PricePerKwhFor(NodeConfig node) => node.PricePerKwh ?? Electricity.PricePerKwh;

    /// <summary>
    /// The throttle rules a node should actually run: the fleet's, with that node's exceptions
    /// laid over them.
    ///
    /// Resolved here rather than on the node so the machines cannot drift apart. A rig only ever
    /// receives a finished answer, and the file on the operator's machine stays the single place
    /// the rules are read from and edited.
    /// </summary>
    public ThrottleSettingsDto ThrottleFor(NodeConfig node)
    {
        var own = node.Throttle;

        return new ThrottleSettingsDto
        {
            Enabled = own?.Enabled ?? Throttle.Enabled,
            Steps = (own?.Steps ?? Throttle.Steps) is { Count: > 0 } steps
                ? steps.Select(s => new ThrottleStepDto(s.OtherCpuPercent, s.Level)).ToList()
                : ThrottleSettingsDto.DefaultSteps,
            FloorLevel = own?.FloorLevel ?? Throttle.FloorLevel,
            RampUpSeconds = own?.RampUpSeconds ?? Throttle.RampUpSeconds,
        };
    }

    /// <summary>
    /// What a node's graphics card should mine: the fleet's answer with that node's exceptions
    /// laid over it, resolved here for the same reason <see cref="ThrottleFor"/> is.
    ///
    /// The per-node half carries more weight here than it does for the throttle, because the
    /// algorithm belongs to the card rather than to the fleet. A fleet-wide "mine Tari" is
    /// meaningless on a 4 GB card that cannot run Cuckaroo29 at all.
    /// </summary>
    public GpuMinerSettingsDto GpuMinerFor(NodeConfig node)
    {
        var own = node.GpuMiner;

        return new GpuMinerSettingsDto
        {
            Enabled = own?.Enabled ?? GpuMiner.Enabled,
            Algorithm = own?.Algorithm ?? GpuMiner.Algorithm,
            PoolUrl = own?.PoolUrl ?? GpuMiner.PoolUrl,
            User = own?.User ?? GpuMiner.User,
            Password = own?.Password ?? GpuMiner.Password ?? "x",
            ApiPort = own?.ApiPort ?? GpuMiner.ApiPort ?? DefaultGpuApiPort,
            RunInInteractiveSession = own?.RunInInteractiveSession ?? GpuMiner.RunInInteractiveSession,
            PauseWhile = PauseRuleFor(own?.PauseWhile ?? GpuMiner.PauseWhile),
        };
    }

    /// <summary>
    /// lolMiner's loopback API port when nobody has chosen one. One above the xmrig API's 47801,
    /// so the two miners cannot collide on a node running both.
    /// </summary>
    public const int DefaultGpuApiPort = 47802;

    /// <summary>
    /// Converts a pause rule, or returns null when the rule names no condition — a block with only
    /// a quiet time in it would stand a node down forever with nothing to wake it.
    /// </summary>
    private static GpuPauseRuleDto? PauseRuleFor(GpuPauseConfig? rule)
    {
        if (rule is null) return null;

        var resolved = new GpuPauseRuleDto
        {
            TcpPort = rule.TcpPort,
            ProcessName = string.IsNullOrWhiteSpace(rule.ProcessName) ? null : rule.ProcessName.Trim(),
            ProcessNames = rule.ProcessNames is { Count: > 0 } ? rule.ProcessNames : null,
            QuietSeconds = rule.QuietSeconds,
        };

        return resolved.NamesACondition ? resolved : null;
    }
}

/// <summary>
/// What a graphics card mines. Every field is nullable at node level so one rig can name a
/// different algorithm without restating the pool, the login or the pause rule.
/// </summary>
public sealed class GpuMinerConfig
{
    /// <summary>Off unless asked for, like the throttle and for the same reason.</summary>
    public bool? Enabled { get; set; }

    /// <summary>lolMiner's algorithm name, e.g. <c>CR29</c> or <c>NEXA</c>.</summary>
    public string? Algorithm { get; set; }

    /// <summary>host:port of the pool.</summary>
    public string? PoolUrl { get; set; }

    /// <summary>
    /// The pool login exactly as that pool wants it — <c>XMR:address.worker</c> for unMineable,
    /// <c>address/worker</c> for Kryptex. Written whole because no two pools agree on the shape.
    /// </summary>
    public string? User { get; set; }

    public string? Password { get; set; }

    /// <summary>Loopback port for lolMiner's API. Null uses <see cref="FleetConfig.DefaultGpuApiPort"/>.</summary>
    public int? ApiPort { get; set; }

    /// <summary>
    /// Start the miner in the node's logged-on session rather than the agent's session 0. Needed
    /// on some machines and not others; see <see cref="GpuMinerSettingsDto.RunInInteractiveSession"/>.
    /// </summary>
    public bool? RunInInteractiveSession { get; set; }

    /// <summary>When the card should be handed back to whoever is using the machine.</summary>
    public GpuPauseConfig? PauseWhile { get; set; }
}

/// <summary>
/// When GPU mining stands down. Names ports and processes, not applications, because a local
/// model, a game and a render all want the card for the same reason.
///
/// Every condition named is watched and any one of them is enough, so a node can hand its card
/// both to a model and to a game without the operator having to choose which matters more.
/// </summary>
public sealed class GpuPauseConfig
{
    /// <summary>Stand down while anything holds a connection to this local port, e.g. 11434 for Ollama.</summary>
    public int? TcpPort { get; set; }

    /// <summary>Stand down while a process of this name runs. No extension.</summary>
    public string? ProcessName { get; set; }

    /// <summary>
    /// Stand down while any of these processes run. No extensions. Kept alongside the singular
    /// field above, which older configs use and which is folded in rather than ignored.
    /// </summary>
    public List<string>? ProcessNames { get; set; }

    /// <summary>Seconds of quiet before mining resumes. Standing down is immediate.</summary>
    public int? QuietSeconds { get; set; }
}

/// <summary>
/// How hard a miner may run while somebody is using its machine. Every field is nullable at node
/// level so an exception can name one setting without restating the rest.
/// </summary>
public sealed class ThrottleConfig
{
    /// <summary>Off unless asked for: throttling a rig nobody sits at only loses money.</summary>
    public bool? Enabled { get; set; }

    /// <summary>The ladder, read against CPU used by everything except the miner.</summary>
    public List<ThrottleStepConfig>? Steps { get; set; }

    /// <summary>Never go below this level. 0 lets the miner stop and hand its memory back.</summary>
    public int? FloorLevel { get; set; }

    /// <summary>Seconds of quiet before climbing a rung. Coming down is always immediate.</summary>
    public int? RampUpSeconds { get; set; }
}

public sealed class ThrottleStepConfig
{
    public double OtherCpuPercent { get; set; }
    public int Level { get; set; }
}

public sealed class NodeConfig
{
    public string Name { get; set; } = "";

    /// <summary>Tailscale IP or MagicDNS name.</summary>
    public string Host { get; set; } = "";

    public int Port { get; set; } = 47800;

    /// <summary>Per-node override of the fleet token. Null falls back to <see cref="FleetConfig.Token"/>.</summary>
    public string? Token { get; set; }

    /// <summary>Excluded nodes are kept in the file but never polled or controlled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Directory xmrig lives in on that node, used to prefill the install prompt.</summary>
    public string? MinerPath { get; set; }

    /// <summary>Watts assumed when the node reports no power sensor.</summary>
    public double? PowerFallbackWatts { get; set; }

    /// <summary>
    /// Electricity tariff at this machine, when it differs from the fleet default — rigs
    /// often sit in different flats, regions or tariff bands. Null uses
    /// <see cref="ElectricityConfig.PricePerKwh"/>. The currency stays fleet-wide, because
    /// totals across nodes are only meaningful in one currency.
    /// </summary>
    public double? PricePerKwh { get; set; }

    /// <summary>
    /// This machine's exceptions to the fleet throttle rules. Only the fields that differ need
    /// setting; the rest come from <see cref="FleetConfig.Throttle"/>. A gaming rig and a
    /// headless one want different answers, and the Xeon's 16 GB wants a different one again.
    /// </summary>
    public ThrottleConfig? Throttle { get; set; }

    /// <summary>
    /// This machine's graphics card settings. Usually where the algorithm actually lives: an
    /// RTX 4060 mines Cuckaroo29 and a 4 GB RX 6500 XT cannot, so the fleet default rarely fits
    /// every card at once.
    /// </summary>
    public GpuMinerConfig? GpuMiner { get; set; }

    /// <summary>Directory lolMiner lives in on that node, used to prefill the install prompt.</summary>
    public string? GpuMinerPath { get; set; }

    public string Endpoint => $"http://{Host}:{Port}";

    public override string ToString() => $"{Name} ({Host}:{Port})";
}

public sealed class UpdateConfig
{
    /// <summary>GitHub repository holding the releases, as `owner/name`.</summary>
    public string Repository { get; set; } = "XYphrodite/xmrig-fleet";

    /// <summary>Personal access token, needed only when the repository is private.</summary>
    public string? Token { get; set; }

    /// <summary>Check for a newer release when the interactive console starts.</summary>
    public bool CheckOnStart { get; set; } = true;
}

public sealed class ElectricityConfig
{
    public double PricePerKwh { get; set; } = 5.0;

    /// <summary>The currency every amount is counted in: the tariff, costs and income.</summary>
    public string Currency { get; set; } = "RUB";

    /// <summary>
    /// Optional second currency each amount is echoed in. Blank shows amounts once.
    /// The rate comes from the pool quoting XMR in both currencies, so both columns move
    /// together instead of drifting apart on two different feeds.
    /// </summary>
    public string? SecondaryCurrency { get; set; } = "USD";
}

public sealed class PoolConfig
{
    /// <summary>Hashvault REST base for the coin you mine.</summary>
    public string ApiBase { get; set; } = "https://api.hashvault.pro/v3/monero";

    /// <summary>Stratum URL the agents are pointed at.</summary>
    public string Url { get; set; } = "pool.hashvault.pro:443";

    /// <summary>Wallet address, used both for the miner user and for the pool/balance lookups.</summary>
    public string Wallet { get; set; } = "";

    public string? Password { get; set; }
}
