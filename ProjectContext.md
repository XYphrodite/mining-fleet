# xmrig-fleet - Project Context

## Project Overview

**xmrig-fleet** is a console fleet manager for [XMRig](https://github.com/xmrig/xmrig)
mining rigs joined by a Tailscale tailnet. A small HTTP agent on every mining PC starts
and stops the miner, reports hashrate and hardware telemetry, and installs or updates
XMRig on demand; a Spectre.Console TUI on the operator machine polls the whole fleet,
prices the electricity it burns, and reads the pool balance from Hashvault.

**Platform**: .NET 8 (`net8.0`), Windows 10/11 today, Linux-ready
**Language**: C# 12
**Domain**: Cryptocurrency mining operations / fleet telemetry and control

---

## Technical Stack

### Runtime & Frameworks

| Layer | Technology | Notes |
|-------|-----------|-------|
| Node agent | ASP.NET Core Minimal API (`net8.0`) | `xmrig-fleet-agent.exe`, Kestrel on `0.0.0.0:47800` |
| Operator console | .NET 8 console + Spectre.Console | `xmrig-fleet.exe`, interactive TUI **and** one-shot commands |
| Shared contracts | .NET 8 class library | DTOs referenced by both sides |
| Transport | Plain HTTP over the tailnet | Shared-secret header, no TLS (see **Security Model**) |
| Service hosting | Windows Service / systemd | Same binary, both hosts registered unconditionally |
| Miner telemetry | XMRig HTTP API on loopback | Agent starts XMRig with a bearer token kept in `xmrig-api.token` |

### Key Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `Spectre.Console` | 0.57.2 | Tables, panels, prompts, live refresh in the console |
| `LibreHardwareMonitorLib` | 0.9.6 | CPU/GPU/RAM/board sensors on the agent |
| `Microsoft.Extensions.Hosting.WindowsServices` | 8.0.1 | Agent as a Windows service |
| `Microsoft.Extensions.Hosting.Systemd` | 8.0.1 | Agent as a systemd unit (Linux, untested) |
| `Microsoft.Win32.Registry` | 5.0.0 | PawnIO presence probe for sensor diagnostics |

### External Services

| Service | Endpoint | Used for |
|---------|----------|----------|
| Hashvault | `/v3/monero/pool/stats` | Difficulty, block reward, block time, pool hashrate, XMR price |
| Hashvault | `/v3/monero/wallet/{address}/stats` | Balance, shares, payouts, payout threshold |
| CoinGecko | `simple/price?ids=monero` | Fallback price when the pool lacks the currency |
| GitHub Releases | `repos/xmrig/xmrig/releases` | Miner install and update payloads |
| Tailscale CLI | `tailscale status --json` | Node discovery for the operator console |

### Codebase Size

Hand-written source only; excludes `bin`/`obj`, generated files, and documentation.

| Language | Files | Code lines |
|----------|------:|-----------:|
| C# (agent + console + contracts) | 37 | 5,887 |
| C# (tests) | 10 | 727 |
| PowerShell (`deploy/`) | 5 | 606 |
| **Total** | **52** | **7,220** |

---

## Architecture Overview

The console holds no state about a running miner: every screen is rendered from a live
poll of the agents, and every action is an HTTP call. The agent is the only component
that touches the XMRig process, and it always launches the miner with the loopback HTTP
API enabled, so hashrate never has to be scraped from stdout.

```
  OPERATOR MACHINE
  +-----------------------------------------------------------+
  |  xmrig-fleet.exe   (Spectre.Console TUI + one-shot CLI)    |
  |    Dashboard | Miner control | Nodes | Hardware            |
  |    Economics | Pool & wallet | Settings                    |
  |                                                           |
  |    FleetService --- fans out, one AgentClient per node     |
  |    MarketService -- Hashvault + price feed (30s cache)     |
  |    TailscaleService -- `tailscale status --json`           |
  |    fleet.json -- nodes, token, wallet, kWh price           |
  +------------------------+----------------------------------+
                           |  HTTP + X-Fleet-Token
                           |  (Tailscale, 100.64.0.0/10)
        +------------------+------------------+
        |                  |                  |
        v                  v                  v
  +-----------+      +-----------+      +-----------+
  |  MINING PC|      |  MINING PC|      |  MINING PC|
  |           |      |           |      |           |
  | agent :47800  (Windows service / systemd)       |
  |   MinerService ------ starts/stops xmrig.exe    |
  |        |  loopback :47801, random bearer token  |
  |        v                                        |
  |   xmrig.exe --http-enabled  ---> the mining pool|
  |                                                 |
  |   GpuMinerService ----- starts/stops lolMiner   |
  |        |  loopback :47802                       |
  |        v                                        |
  |   lolMiner ------------> the GPU coin's own pool|
  |   GpuPauseService ---- stands the card down     |
  |                                                 |
  |   HardwareService --- LibreHardwareMonitor      |
  |   InstallerService -- GitHub release -> disk    |
  |   miner.json -------- pushed pool/wallet/path   |
  +-------------------------------------------------+

  Console --- HTTPS ---> api.hashvault.pro   (balance, difficulty, price)
  Agent   --- HTTPS ---> api.github.com      (miner install / update)
```

### Design Principles

1. **The agent owns mining on its node.** `stop` terminates *every* `xmrig` process on
   the machine, not only the one this agent spawned. A fleet manager that leaves rogue
   miners running is worse than useless.
2. **Never present a guess as a measurement.** A missing or zero power sensor is
   reported as unmeasured, and the operator's `powerFallbackWatts` wins over the
   agent's own estimate — that number usually comes from a wall meter. For the same
   reason a price is left blank rather than fetched in a different currency.
3. **Cost is per machine.** Electricity is summed node by node at each node's own
   `pricePerKwh`, because rigs sit in different flats and tariff bands; one fleet
   average would misprice every rig that is not on it. Currency stays fleet-wide.
4. **Explain a blank cell.** When a sensor is absent the agent says why
   (`HardwareDto.SensorNotice`), probing the same registry value LibreHardwareMonitor
   itself reads, so the diagnostic cannot disagree with the library.
5. **Installing must not interrupt mining.** `InstallerService` stops the miner only
   when the target directory contains the executable that is currently running.
6. **Every screen has a scriptable twin.** Anything worth watching in the TUI is also a
   one-shot command with a meaningful exit code, for Task Scheduler and cron.
7. **Degrade, never crash.** Pool JSON is read field by field with tolerant fallbacks;
   a renamed field blanks one cell instead of breaking a screen.
8. **A graphics card belongs to whoever is at the machine.** The CPU can be throttled down a
   rung; a card cannot be shared at all, so GPU mining stands aside entirely and instantly when
   something else wants it, and only comes back after a long quiet period. The rule names ports
   and processes rather than applications, because a local model, a game and a render all want
   the card for the same reason. Any one condition is enough to stand the card down: a node that
   hands its card to a model must not thereby stop handing it to a game.

---

## Project Components

The solution ([XmrigFleet.slnx](XmrigFleet.slnx)) contains **four** projects — three that
ship, plus a test project.

### 1. **XmrigFleet.Agent**
**Type**: ASP.NET Core Minimal API -> `xmrig-fleet-agent.exe`
**Location**: `src/XmrigFleet.Agent/`
**Purpose**: Runs on every mining PC. The only component that controls XMRig or reads
hardware.

**Key features**:
- Shared-secret middleware comparing `X-Fleet-Token` with `CryptographicOperations.FixedTimeEquals`; an empty token logs a loud warning.
- Environment is forced to `Production` unless `ASPNETCORE_ENVIRONMENT` says otherwise, so stack traces are never served to the tailnet.
- `UseWindowsService()` + `UseSystemd()` are both registered; each is a no-op when the process was not started by that service manager.
- `MinerConfigStore` persists pushed settings to `miner.json` beside the binary; a corrupt file never blocks startup.

**Services**:

| Class | Responsibility |
|-------|----------------|
| `MinerService` | Start/stop/restart XMRig, read `/2/summary` and `/2/backends` off the loopback API, keep the last 200 output lines |
| `HardwareService` | LibreHardwareMonitor sensors, power estimate, PawnIO diagnostics |
| `InstallerService` | Resolve the right GitHub release asset, download, unpack, repoint the config |
| `AgentUpdateService` | Update the agent itself from an xmrig-fleet release and restart into it |
| `PerformanceCounterPump` | Polls Windows' performance counters. Tried as a fix for the hashrate gap below and did **not** work; kept only because it is harmless and rules the idea out |
| `SessionMonitorService` | Keeps one hidden monitor window in the node's logged-on session — Task Manager, or Resource Monitor if that will not run. Launches with `CreateProcessAsUser`, waits out the hand-over to the child process both of them exit into, and adopts one already open rather than starting a second |
| `ThrottleService` | Holds the miner back while somebody is using the machine: reads the ladder every second, caps or stops the miner, records every decision |
| `ThrottleLadder` | The rung rule itself — pure, clock-injected, and the part the tests drive |
| `MinerCpuLimit` | The CPU cap, a named job object so an agent restart can still lift its own limit |
| `CpuReservation` | Which logical CPUs the miner may use when whole cores are held back for the person at the machine. Reads the real topology through `GetLogicalProcessorInformationEx`, because "two logical per core" is wrong on a hybrid CPU — an i7-12700KF reports 12 cores and 20 logical processors, and four of those cores have one thread each |
| `CpuReservationService` | Keeps that mask applied. A background loop rather than something done at start-up: affinity dies with the process, so a miner restarted by the throttle, by an operator or by autostart after a reboot comes back holding the whole machine |
| `SystemLoadReader` | `GetSystemTimes` + `GlobalMemoryStatusEx`, with the miner's own CPU time subtracted |
| `ThrottleLog` | `throttle.log` beside the binary: one line a minute of what the CPU was doing, plus every rung change with the readings behind it |
| `LoadJournal` | Folds the once-a-second samples into that per-minute line. Runs whether or not throttling is on, which is the point: throttling ships off, so a node used to keep no record of its own load at all |
| `MinerConfigStore` | Durable per-node miner settings |
| `GpuMinerService` | Start/stop/restart lolMiner, read its loopback API, keep the last 200 output lines. A separate class from `MinerService` on purpose: it runs at Normal priority (Task Scheduler's default of 7 cost 18% of shares to staleness), it never touches an `xmrig` process, and it settles for five seconds rather than 700 ms because a card that will not mine fails quietly |
| `GpuPauseService` | Makes the observations the stand-down rule needs and acts on its answer. Reads the TCP table through `IPGlobalProperties` — `Get-NetTCPConnection` sees nothing from a service — and takes one snapshot of the process table per tick rather than one per watched name |
| `GpuPauseRule` | The stand-down rule itself, pure and clock-injected, and the part the tests drive. Both halves are here: which conditions are met (`Evaluate`) and how long the card waits before coming back |

### 2. **XmrigFleet.Console**
**Type**: Console application -> `xmrig-fleet.exe`
**Location**: `src/XmrigFleet.Console/`
**Purpose**: The operator interface — a Spectre.Console TUI, plus one-shot commands for
unattended use.

**Screens** (`Ui/`): `Dashboard` (live table + totals), `MinerScreen` (start/stop/
install/push/autostart/logs), `NodesScreen` (discover/add/edit/test), `HardwareScreen`,
`EconomicsScreen`, `PoolScreen`, `SettingsScreen`.

**Supporting services**:

| Class | Responsibility |
|-------|----------------|
| `FleetService` | Parallel poll and parallel command fan-out, error normalisation; resolves the throttle and GPU settings each node should get before pushing them |
| `AgentClient` | Typed HTTP client for one agent, long timeout for installs |
| `MarketService` | Hashvault wallet/pool parsing, atomic-unit scaling, price, 30s cache |
| `GpuPoolService` | What a card actually earned, which Hashvault cannot answer because the card mines another coin. Reads Kryptex, works the pool and address out of the node's own GPU settings, and takes its daily rate from payments the pool really made rather than from a counter |
| `Economics` | Electricity cost, expected income, per-node profit split |
| `TailscaleService` | Parses `tailscale status --json` for node discovery, storing the MagicDNS name rather than the address when this machine resolves it |
| `FleetConfig` | `fleet.json` load/save (override with `XMRIG_FLEET_CONFIG`) |
| `UpdateService` | GitHub release lookup, streaming download, in-place file swap |
| `Updater` | The `update` command, its progress bar, and the start-up "newer version" notice |

### 3. **XmrigFleet.Contracts**
**Type**: Class library
**Location**: `src/XmrigFleet.Contracts/`
**Purpose**: The wire contract shared by both sides — `NodeSnapshotDto`,
`MinerStatusDto`, `HardwareDto`, `MinerConfigDto`, `InstallRequestDto`,
`CommandResultDto`, `GpuMinerSettingsDto`, `GpuMinerStatusDto`, `GpuPauseRuleDto`, plus
`ApiVersion.Current` so the console can warn on a mismatch.

### 4. **XmrigFleet.Console.Tests**
**Type**: xUnit test project
**Location**: `tests/XmrigFleet.Console.Tests/`
**Purpose**: Guards the three contracts that have actually broken in use, rather than
chasing coverage.

| File | Guards |
|------|--------|
| `AgentUpdateTests` | The agent picks its own release asset and never the console's, the two never want the same file, the node's token files are excluded from the swap, and a `v1.4.0` tag is recognised as the `1.4.0.0` build already running |
| `ThrottleTests` | Coming down is immediate and going up waits; a burst restarts the wait; the floor holds; a ladder typed out of order still reads correctly; switching the limit on does not discard the tuned ladder; per-node overrides replace only what they name |
| `MarkupSafetyTests` | Prompts and badges render data holding `[` — the crash that reached the operator twice. Drives real prompts through `Spectre.Console.Testing`, and asserts escaping never reaches the stored value |
| `EconomicsTests` | Per-node tariffs summed separately, the income formula, idle nodes not charged, measured power beating the configured fallback |
| `UpdateAssetTests` | `update` matches the console asset and never the agent one that sits beside it in the same release |
| `TailnetDiscoveryTests` | Discovery stores the MagicDNS name only when it resolves here, falls back to the address when it does not or when the tailnet has MagicDNS off, and skips a machine with no tailnet address |
| `AutoStartTests` | An autostart push keeps the tuned ladder and the rest of the node's config; the setting survives an agent restart; the node's own answer beats the installed default while an untold node still follows it; autostart does not restart a miner the throttle stopped; and "unset" reads differently from "off" |
| `GpuMiningTests` | A push that turns the card on keeps the lolMiner path and the session flag the node already knew; a push replaces the whole set of pause conditions rather than inheriting one nobody asked for; a node override replaces only what it names; and the stand-down is immediate while the return waits out the quiet period, restarted by any interruption. Since 1.13.1 also: a named process stands the card down even when the rule watches a port too — the defect that let a game freeze `mks68i7rtx` — every reason is reported rather than the first, a quiet rule names what it was watching, and the singular process field is folded in beside the list |
| `LoadJournalTests` | A minute closes once and only when the next one starts; a peak survives an average that hides it; an unusable sample is counted rather than averaged in as an idle machine; a minute nobody could measure still produces a line; throttling off reads differently from a rung of 0%; a gap does not merge two minutes; and memory survives a sample whose CPU delta did not |
| `GpuPoolTests` | The pool and coin slug are read out of the stratum host and the worker name is not mistaken for part of the address; a pool this console cannot read is declined rather than guessed at, because a wrong address answers with somebody else's zero instead of an error; the daily rate is measured over the window the payouts cover, is refused on a single payout, and reads a long history over its last day only |
| `MenuNavigationTests` | Arrows wrap in both directions; Escape answers with the menu's own way out and never with one of a two-answer menu's answers; the node picker backs out to null and the multi-picker to an empty list rather than the whole fleet; and a menu cannot be built with a cancel value it does not offer |

`AnsiConsole.Console` is a global that the markup tests swap, so
[AssemblyInfo.cs](tests/XmrigFleet.Console.Tests/AssemblyInfo.cs) disables parallel runs.

---

## Agent HTTP API

All routes live under `/api/v1` and require the `X-Fleet-Token` header.

| Method | Route | Purpose |
|--------|-------|---------|
| `GET` | `/info` | Hostname, OS, agent version, API version, uptime, elevation |
| `GET` | `/status` | `NodeSnapshotDto` — info, miner, GPU miner and hardware in one call |
| `GET` | `/miner` | Miner status only |
| `POST` | `/miner/start` | Start XMRig with the stored config |
| `POST` | `/miner/stop` | Stop **all** XMRig processes on the node |
| `POST` | `/miner/restart` | Stop, settle, start |
| `GET` | `/hardware` | Sensors, components, power estimate, sensor notice |
| `GET` / `PUT` | `/config` | Read or patch the stored miner config |
| `POST` | `/install` | Install or update XMRig into a target directory |
| `GET` | `/logs` | Last 200 captured output lines |
| `GET` | `/throttle` | Current power rung, the reason for it, and the load behind it |
| `GET` | `/throttle/log` | The node's own record: one line a minute of CPU load whether or not throttling is on, plus every rung change and its readings |
| `GET` | `/gpu` | What the graphics card is mining, its shares and its per-device readings |
| `POST` | `/gpu/start` | Start lolMiner with the stored GPU config |
| `POST` | `/gpu/stop` | Stop **all** lolMiner processes on the node; XMRig is untouched |
| `POST` | `/gpu/restart` | Stop, settle, start |
| `GET` | `/gpu/logs` | Last 200 captured lolMiner output lines |
| `POST` | `/agent/update` | Update the agent itself and restart into the new build |

---

## Configuration

### Console — `fleet.json`

```json
{
  "token": "shared-secret",
  "agentPort": 47800,
  "pollIntervalSeconds": 5,
  "electricity": { "pricePerKwh": 5.0, "currency": "RUB" },
  "pool": {
    "apiBase": "https://api.hashvault.pro/v3/monero",
    "url": "pool.hashvault.pro:443",
    "wallet": "4..."
  },
  "throttle": {
    "enabled": false,
    "floorLevel": 0,
    "rampUpSeconds": 120,
    "steps": [
      { "otherCpuPercent": 0,  "level": 100 },
      { "otherCpuPercent": 10, "level": 75 },
      { "otherCpuPercent": 25, "level": 50 },
      { "otherCpuPercent": 45, "level": 25 },
      { "otherCpuPercent": 70, "level": 0 }
    ]
  },
  "gpuMiner": {
    "enabled": false,
    "pauseWhile": { "tcpPort": 11434, "processNames": ["dontstarve_steam_x64"],
                    "quietSeconds": 300 }
  },
  "nodes": [
    { "name": "rig-1", "host": "100.100.10.11", "port": 47800,
      "enabled": true, "powerFallbackWatts": 220 },
    { "name": "rig-2", "host": "100.100.10.12", "port": 47800,
      "enabled": true, "powerFallbackWatts": 310, "pricePerKwh": 7.2,
      "reservedCores": 2,
      "throttle": { "enabled": true, "floorLevel": 25 },
      "gpuMinerPath": "C:\\mining\\lolMiner",
      "gpuMiner": { "enabled": true, "algorithm": "CR29",
                    "poolUrl": "pool.example.com:4444", "user": "address.worker" } }
  ]
}
```

A node's `host` is put into the URL as it stands, so it can equally be a MagicDNS name
(`rig-1.tailnet-name.ts.net`), which is what discovery stores when the operator's machine
resolves those names. A name survives a node's address changing; an address survives the
operator's resolver not being Tailscale's, which is why discovery checks before choosing.

The `throttle` block is fleet-wide; a node's own block overrides only the fields it names, and
the console resolves the two before pushing the result to that node. The ladder is read against
CPU used by **everything except the miner** — reading total load would make capping the miner
lower the very figure the cap responds to, and the machine would oscillate instead of settling.

`gpuMiner` resolves the same way, and the override matters more here than it does for the
throttle: the algorithm belongs to the card. An RTX 4060 earns on Cuckaroo29; a 4 GB RX 6500 XT
answers `Unsupported device` to the same request, so a fleet-wide algorithm is only ever half an
answer. `user` is stored whole, exactly as the pool wants it — `XMR:address.worker` for
unMineable, `address/worker` for Kryptex — because no two pools agree on the shape and a console
that assembled it would be wrong somewhere.

`pauseWhile` names **ports and processes**, not applications. A local model, a game and a render
all want the card for the same reason, and the miner has no business telling them apart. Every
condition named is watched and any one of them is enough; the card comes back only once the last
of them lets go. `processNames` takes a list, and the older singular `processName` is folded in
beside it rather than ignored, because every node already deployed has it written into its own
`miner.json`. Standing down is immediate; coming back waits out `quietSeconds`, which defaults to
five minutes because a model stays resident in VRAM between requests and a miner returning after
ten seconds evicts it — the next question then waits for a reload instead of being answered.

### Agent — `appsettings.json`

```json
{
  "Agent": {
    "Token": "shared-secret",
    "ListenUrl": "http://0.0.0.0:47800",
    "XmrigApiPort": 47801,
    "AutoStartMiner": false
  }
}
```

`AutoStartMiner` here is only the installed default. Once an operator sets autostart from the
console — **Miner control → Start mining when the node boots**, or `xmrig-fleet autostart --on` —
the answer lives in that node's `miner.json` and this value stops being consulted. The setting
belongs in the console because it decides whether a node that came back on its own returns to
work or sits idle until somebody notices, and that is a fleet-wide judgement, not an install-time
one. A node the throttle stopped is left down either way: autostart exists so a rebooted rig
resumes, not so a machine somebody is using starts mining under them.

⚠️ **Both files hold secrets** (fleet token, wallet address). They are listed in
[.gitignore](.gitignore) and must stay out of version control.

---

## Build & Deployment

### Local build

```powershell
dotnet build                                    # whole solution
dotnet run --project src/XmrigFleet.Agent       # agent in the foreground
dotnet run --project src/XmrigFleet.Console     # interactive TUI
```

### Publish and roll out

```powershell
# 1. Self-contained binaries; nodes need no .NET runtime
.\deploy\publish.ps1                            # -Runtime linux-x64 for Linux nodes

# 2. On each mining PC, from an elevated PowerShell
.\install-agent.ps1 -Token "<fleet token>" -SourcePath .\agent
```

`install-agent.ps1` copies the payload to `C:\Program Files\xmrig-fleet-agent`, writes
`appsettings.json`, registers the service with SCM restart actions, opens the port to
`100.64.0.0/10` **only**, and verifies the API answers. It also kills any agent left running
outside the service — one started by hand for diagnosis holds port 47800, and the service then
refuses to start with a message that names no cause — and puts the install directory on the
machine `PATH`, which is safe only because the agent reads `appsettings.json` from its own
directory rather than the current one. On failure it points at `agent.log`, not the Windows
event log: the agent stopped writing there after a node whose Event Log service answers
"RPC server unavailable" was taken down by the logging call itself. For Linux nodes,
[deploy/xmrig-fleet-agent.service](deploy/xmrig-fleet-agent.service) is the systemd unit.

### Installing the console

```powershell
irm https://raw.githubusercontent.com/XYphrodite/xmrig-fleet/master/deploy/install.ps1 | iex
```

Unpacks the newest release into `%LOCALAPPDATA%\Programs\xmrig-fleet` and puts it on PATH.
No administrator rights: this is the operator machine, not a mining node. Afterwards the
console updates itself with `xmrig-fleet update`.

[deploy/release.ps1](deploy/release.ps1) builds, packages and publishes a release; the tag
stamps the assembly version, which is what `update` compares against.

### One-shot commands

```bash
xmrig-fleet status              # exit 1 if any node is unreachable
xmrig-fleet start  [node ...]   # no names = every enabled node
xmrig-fleet stop   [node ...]
xmrig-fleet restart
xmrig-fleet economics
xmrig-fleet pool
xmrig-fleet update [--check]    # --check reports and exits 1 without installing
xmrig-fleet upgrade-agents [node ...] [--version=v1.5.0] [--force]
xmrig-fleet throttle [node ...] [--sync|--set=N|--auto] [--log]
xmrig-fleet autostart [node ...] [--on|--off]
xmrig-fleet gpu [node ...] [--sync|--start|--stop]
xmrig-fleet version
```

---

## Security Model

- **Transport is not encrypted.** Confidentiality comes from the tailnet; the token
  only stops other tailnet or LAN hosts from driving the miner. Restrict access further
  with Tailscale ACLs.
- **Token comparison is constant-time**, and a blank token is logged as a warning at
  startup rather than silently accepted.
- **The agent needs Administrator/root** to read sensors and to kill a miner started by
  another user. Elevation is reported in `/info` and surfaced in **Test connection**.
- **XMRig's own API is bound to loopback** with a random per-process bearer token, so
  nothing else on the node can drive the miner through it.

---

## Directory Structure

```
xmrig-fleet/
├── src/
│   ├── XmrigFleet.Agent/          # node agent -> xmrig-fleet-agent.exe
│   │   ├── Program.cs             # minimal API, token middleware, service hosting
│   │   ├── MinerService.cs        # XMRig process control + loopback API reader
│   │   ├── HardwareService.cs     # sensors, power estimate, PawnIO diagnostics
│   │   ├── InstallerService.cs    # GitHub release -> unpack -> repoint config
│   │   ├── GpuMinerService.cs     # lolMiner process control + its loopback API
│   │   ├── GpuPauseService.cs     # hands the card back while somebody needs it
│   │   ├── GpuPauseRule.cs        # the stand-down rule itself, pure and clock-injected
│   │   └── AgentOptions.cs        # options + miner.json store
│   ├── XmrigFleet.Console/        # operator console -> xmrig-fleet.exe
│   │   ├── Ui/                    # Dashboard, Miner, Nodes, Hardware, Economics, Pool, Settings
│   │   ├── FleetService.cs        # parallel poll and command fan-out
│   │   ├── MarketService.cs       # Hashvault + price parsing
│   │   ├── Economics.cs           # cost / income / profit maths
│   │   ├── TailscaleService.cs    # tailnet discovery
│   │   ├── UpdateService.cs       # release lookup, download, in-place file swap
│   │   ├── Updater.cs             # the update command and its progress bar
│   │   └── Cli.cs                 # one-shot commands
│   └── XmrigFleet.Contracts/      # DTOs shared by both sides
├── tests/
│   └── XmrigFleet.Console.Tests/  # markup, money and update-asset contracts
├── deploy/
│   ├── install.ps1                # one-line operator install (irm ... | iex)
│   ├── release.ps1                # build, package and publish a GitHub release
│   ├── publish.ps1                # self-contained publish for agent + console
│   ├── install-agent.ps1          # service + firewall + verification on a node
│   ├── install-openssh.ps1        # Windows OpenSSH server on a node, tailnet-scoped
│   └── xmrig-fleet-agent.service  # systemd unit for Linux nodes
├── README.md                      # operator guide (Russian)
├── ProjectContext.md              # this document
└── XmrigFleet.slnx
```

---

## Development Status

### Implemented & Verified Live ✅
- [x] Agent HTTP API with shared-secret auth (unauthenticated request returns `401`)
- [x] Hardware read on real silicon — CPU model, 6c/12t split, motherboard, RAM, GPU temperature/power/VRAM
- [x] Physical-core counting that survives SMT thread-named sensors
- [x] Power reported as measured vs estimated, with the node fallback taking precedence
- [x] PawnIO diagnostic using LibreHardwareMonitor's own detection key
- [x] Adopting a miner started outside the agent (image path read from the live process)
- [x] Install/update: downloaded XMRig `v6.26.0`, unpacked, resolved the executable, and left a running miner untouched
- [x] Release-asset matching checked for all six OS/architecture pairs against the live
      GitHub asset list; no cross-architecture fallback, so `linux/arm64` (which xmrig
      does not ship) fails with a message naming what was sought
- [x] Hashvault parsing against live data — balance, payout threshold, paid total, network hashrate, XMR price in the configured currency
- [x] Per-node electricity tariff: two nodes at 200 W on 5.00 and 12.00 RUB/kWh summed
      to 81.60 RUB/day, not the 48.00 a single fleet rate would have produced
- [x] Income estimate cross-checked against `hashrate × 86400 × reward / difficulty`
      computed independently from the live pool figures (0.000772 XMR/day at 10 kH/s),
      and shown beside the pool's own `dailyCredited` with a ratio
- [x] Price falls back to the external feed only for currencies the pool omits, and is
      left blank rather than substituted: verified `GBP` via the feed and `KZT` blank
      (no source carries it)
- [x] One-shot CLI (`status`, `economics`, `pool`, `help`) with meaningful exit codes
- [x] Clean solution build, zero warnings

- [x] `start` / `stop` / `restart` against a real miner, with the operator's own pool,
      wallet and arguments carried over; hashrate became readable (2.29 kH/s) because the
      agent now owns the process and its API token
- [x] The XMRig API token now survives an agent restart, so a service restart or an
      update no longer leaves a node reporting `mining (no api)`
- [x] A miner started by the agent survives the agent being killed — restarting or
      updating the agent does not stop mining
- [x] One-line install (`irm ... | iex`) from a published release, and self-update
      1.1.1 -> 1.1.2 replacing the running executable
- [x] RandomX tuning state reported per node — huge-page allocation, mining thread count and
      the MSR mod, read from `/2/summary` and `/2/backends`. Verified on `re-7lqd67ahcm0r`:
      `1174/1174` pages, 6 threads on 12 MB L3, `msr=intel`. Huge pages decide RandomX
      throughput far more than the CPU model does, and were previously invisible
- [x] `upgrade-agents` against a live node: `desktop-ib88isg` rolled 1.9.2.0 -> 1.9.3 (345 files
      replaced), the agent restarted into the new build and the miner kept running throughout —
      6.34 kH/s before, 6.26 kH/s after, huge pages at 100% across the restart
- [x] The session monitor tracks the window that survives the launch, not the process
      `CreateProcessAsUser` returns. Both monitors hand over to a child and exit within a
      second — Task Manager restarts itself under the unfiltered administrator token, and
      `resmon.exe` is a stub that starts `perfmon.exe`. Measured on `desktop-ib88isg`: the agent
      launched pid 26852 and the live window was pid 30052, its child. Verified after the fix by
      killing the window and watching one launch line name the pid that was still standing
- [x] `NodeSnapshotDto.MonitorNotice` read back from both live nodes — `mks68i7rtx` answered
      "adopted the Task Manager already open in session 2 as pid 13848" and `desktop-ib88isg`
      "Task Manager already running as pid 27228". Before this the console printed "session
      monitor on" whatever the node had done
- [x] Discovery by MagicDNS name, run against the live tailnet: all eight machines came back
      with their `.ts.net` name and the resolution probe kept it. The agent answers on the
      stored form — `http://mks68i7rtx.tail08a9a5.ts.net:47800/api/v1/info` returned `401` from
      `100.105.87.52`. Names also survive a hostname a DNS label cannot hold: that tailnet has
      a `moscow_enjoyer` and a `TECNO CAMON 20`, whose labels are `moscow-enjoyer` and
      `tecno-camon-20`, so the name is read from `DNSName` and never built from `HostName`
- [x] **GPU mining driven end to end by the agent**, on `mks68i7rtx` (RTX 4060) on 2026-09-04. The
      node was moved off the hand-built scheduled tasks it had been mining Tari under: agent
      upgraded 1.9.5.0 -> 1.10.1 in place (345 files), `xmrig-fleet-gpu` and `xmrig-fleet-gpu-guard`
      disabled, settings read out of the task being replaced and pushed to the agent, and the miner
      started from `/gpu/start` — pid 19400, mining Cuckaroo29, 2 shares accepted and none stale.
      The API port was deliberately kept at the task's 21556 so the node's existing `earn2.csv`
      history does not break
- [x] **The miner runs in session 0**, which the whole `RunInInteractiveSession` escape hatch was
      built for and, on this node at least, is not needed. `lolMiner --list-devices` from an SSH
      shell — session 0 — enumerates the RTX 4060 through CUDA, and the agent has since run the
      miner there. The old belief conflated it with PowerShell scheduled tasks on that node dying
      in `0xC0000005`, which is a real and separate fault
- [x] **The pause rule, watched through a full cycle.** A request to the node's Ollama on 11434
      stood the miner down on the next one-second tick; `/gpu` reported
      `paused, 295s of quiet still needed` and counted down; the miner came back by itself at
      16:43:33 after the full 300 s. A second request mid-countdown reset it to 299 s, which is the
      restart-the-wait behaviour the unit tests assert, seen on real traffic
- [x] **GPU autostart across an agent restart.** With the miner stopped and every scheduled task
      disabled, restarting the agent service brought it back on its own —
      `GPU autostart: GPU miner started on CR29, pid 18544` in the node's log. This is what makes
      the agent a replacement for a task with a boot trigger rather than a downgrade from one
- [x] **The card came back after a real reboot, and the pause fired on real traffic.** On
      2026-09-05 `mks68i7rtx` restarted for a driver install and the node's log reads
      `15:37:50 GPU autostart: GPU miner started on CR29, pid 6512` — twelve seconds after boot,
      with every scheduled task disabled and nobody watching. Four minutes later
      `15:42:12 GPU mining paused: port 11434 busy, 1 connection(s)`: somebody asked the model
      something and the miner stood down, unprompted. The service-restart test above proved the
      code path; this proves the feature
- [x] **PawnIO restores CPU temperature and package power.** Installed 2.2.0 on `mks68i7rtx`
      (signed by `CN=namazso.eu`, Microsoft countersignature) and restarted the agent, because
      LibreHardwareMonitor only opens the driver at start-up. The node went from
      `estimatedPowerWatts` empty and `powerIsMeasured=false` to **158.8 W measured** — 123.8 W of
      CPU package under a 7,246 H/s miner, at **92 °C**, a temperature this project had never once
      been able to read. `HardwareDto.SensorNotice` cleared itself, which is the diagnostic
      agreeing with the library as designed. The card's own draw is still absent: an RTX 4060
      reports no power sensor at all, at 100% load, so ~110 W of that node remains uncounted
- [x] **A node keeps a record of its own load, throttling on or off.** Verified on `mks68i7rtx`
      minutes after the 1.12.0 roll-out: six consecutive per-minute lines through `/throttle/log`,
      each carrying the average, the peak, the busy-thread translation and memory, with `off` in
      the rung column because throttling is not on there. The first line reads `[1 unusable]` —
      the sample taken immediately after the agent restarted, which cannot yield a percentage
      because a percentage needs two. That it says so rather than printing a zero is the whole
      design in one line
- [x] **The throttle's stop rung is unreachable, and the journal proved it in six minutes.** Those
      same lines read `miner=59.2..59.7%` — twelve mining threads on twenty logical CPUs. The
      ladder reacts to everything except the miner, so that figure cannot exceed ~41% at full
      speed and the shipped `70% -> stop` rung never fires. This had been an argument from
      arithmetic; it is now a reading
- [x] **What a card earned, read from the pool and shown in Economics.** Verified against the live
      Kryptex API on 2026-09-05: the RTX 4060's target was derived from the node's own GPU settings
      (`xtm-c29.kryptex.network` → provider and coin slug; `address/worker` → address), and the
      adapter returned `paid=1155.12 XTM`, `confirmed=104.62`, `unconfirmed=159.07`, five payouts,
      and **1,039.10 XTM/day over a 22 h window** — matching a figure computed by hand from the raw
      JSON to a tenth. Priced through CoinGecko's `minotari` at 0.069066 ₽, that is ≈71.8 ₽/day the
      fleet had been earning invisibly

- [x] **The card stands down for a game, watched live on `mks68i7rtx` on 2026-09-06.** The rule
      had watched only TCP 11434 because `IsBusy` returned on the first condition it found and
      never read the process name beside a port — so a node set to hand its card to a language
      model mined straight through a game. Measured mid-freeze with Windows' per-process GPU
      counters, which are the only per-process split there is (`nvidia-smi --query-compute-apps`
      answers `[N/A]` for memory on this card): lolMiner held **97.6% of the 3D engine and
      6,879 MB of an 8,188 MB card**, Don't Starve Together got **1.1% and 270 MB** — a quarter
      of what it wants, so Windows was evicting its textures to system RAM. The CPU was not
      involved and the node's own journal says so: `other avg=6.0–7.3%` across the same half-hour
      on 32 GB. After pushing `{ tcpPort: 11434, processNames: ["dontstarve_steam_x64"] }` the
      card stood down **within five seconds** — `/gpu` reported `dontstarve_steam_x64 is running`
      — and the card went from 100% and 8,152 MB at 66 °C to **18% and 827 MB at 46 °C**. The CPU
      miner was untouched throughout: same pid, 7,100 H/s, huge pages 1180/1180
- [x] **The freeze was the CPU too, and reserving whole cores fixed it.** With the card already
      standing down for the game, `mks68i7rtx` still stuttered. The per-core split said why:
      `100 2 100 1 100 26 100 21 100 50 100 59 100 8 100 4 | 100 100 100 100` — the miner held one
      thread of every P-core and all four E-cores, at thread priority `Highest` inside a `High`
      process, leaving the game only hyperthread siblings of saturated RandomX cores. The journal
      still read `other avg=8.4%`, because a process that cannot get scheduled cannot register
      load; a ladder watching that figure would never have moved. Two levers were measured on the
      live miner without restarting it: **`BelowNormal` cost 85%** (7,126 → 1,058 H/s, Windows
      parking it on the E-cores), while **freeing two P-cores by affinity cost 7%** (7,126 → 6,642)
      and the operator confirmed the stutter gone. Huge pages stayed at 1180/1180 throughout
- [x] **A self-update can leave a node mining with no agent, and it did.** `ScheduleRestart` waited
      with `timeout /t 5 /nobreak`, which refuses to run without a console on stdin — *"ERROR:
      Input redirection is not supported, exiting the process immediately"* — and a service's child
      process never has one. The five-second cushion never existed: `sc start` fired about a second
      later, while the old process was still alive and the service still `RUNNING`, and lost with
      error 1056. Every self-update was therefore a race. `mks68i7rtx` won it going to 1.12.0 and
      lost it going to 1.13.1: files replaced, both miners still running, service `Stopped`, node
      unmanageable until somebody ran `sc start` by hand. Reproduced off the node by running the
      same command line with a redirected stdin — 1 s and an error, against 6.1 s for `ping`.
      Fixed in 1.13.2 by delaying with `ping` and trying three times
- [x] **`reservedCores` pushed, stored and re-asserted**, on `mks68i7rtx` on 2026-09-06. The node
      answered with `reservedCores=2` read back from its own `miner.json`, and the claim that the
      service keeps the mask applied was tested rather than assumed: the miner's affinity was
      forced back to the whole machine (1048575) by hand, and the agent had restored 1048560
      **within six seconds**, logging both the lift and the re-application. Hashrate 6,732 H/s,
      huge pages 1180/1180. The topology reader's struct offsets are covered by a test that reads
      the machine it runs on, because wrong offsets would not throw — they would return
      plausible masks and pin the miner to cores nobody chose
- [x] **The restart helper's fix, watched on all three nodes.** The 1.14.0 rollout was the first
      update to run the repaired helper, and every node came back on its own: `mks68i7rtx`
      StartPending → Running by 20 s, the other two answering within 10. Before it, the same
      operation had left `mks68i7rtx` down and needed `sc start` by hand
- [x] **Rolled 1.13.2 across the whole fleet**, all three nodes, miners untouched:
      `mks68i7rtx` 1.13.1 → 1.13.2 (service lost the race again and was started by the rollout),
      `desktop-ib88isg` 1.13.0 → 1.13.2 and `re-7lqd67ahcm0r` 1.13.0 → 1.13.2 (both came back on
      their own inside ten seconds). Every xmrig kept its pid, its huge pages and its shares
      across the restart, and the pushed pause rule survived the update on `mks68i7rtx`

### Implemented, Not Yet Verified Live ⏳
- [ ] **A reserved core surviving a reboot.** Everything up to that is verified (see above): the
      setting is stored on the node, and the service re-applies it within six seconds of the mask
      being taken away. What nobody has watched is `mks68i7rtx` coming back from a cold boot with
      the reservation already on the miner autostart started
- [ ] **Whether a minute is the right bucket for the load journal.** The journal itself is
      verified (see above), but only over six minutes of an idle machine. Nobody has yet read a
      working day of it back and asked whether a per-minute peak catches what an operator
      complains about, or whether the interesting events are shorter than that
- [ ] **Autostart from the console.** Whether a node mines as soon as its agent starts is now a
      pushed per-node setting rather than a hand edit to `appsettings.json` on the machine, with
      **Miner control → Start mining when the node boots** and `xmrig-fleet autostart` as its
      scriptable twin. Prompted by `desktop-ib88isg`: it lost mains power four times in ten days
      and bugchecked twice, came back on its own every time, and mined nothing afterwards because
      the flag was off and nobody was there to notice. Both sides read the setting back from the
      node rather than echoing the request, so an agent too old to know the field says so instead
      of reporting success. Unit-tested and built; no node has been rebooted to watch it work
- [ ] **Adaptive power limit.** Five rungs (100/75/50/25/0) chosen from the CPU load of everything
      except the miner. A rung is a share of the **miner's own full speed**, not of the machine:
      six mining threads on twelve logical CPUs want about half the machine, so a job object told
      to allow 50% would be a cap the miner never reaches. Measured on that node before the
      conversion existed — pinning 50% moved the hashrate by nothing — so the agent asks the miner
      for its thread count and converts. 25-100% is a hard cap on a named job object, applied
      instantly and leaving
      the RandomX dataset and huge pages untouched; 0% stops the miner outright, because a capped
      miner still holds ~2.3 GB and on a 16 GB node that memory is what makes the machine feel
      slow. Coming down is immediate, going up waits for `rampUpSeconds` of quiet. Rules live in
      `fleet.json` with per-node overrides; every rung change is recorded on the node with the
      readings behind it, because the shipped thresholds are a guess meant to be corrected from
      that log. Unit-tested, built, but not yet run against a live node
- [ ] Session-side signals for the same feature: user input idle, the foreground window and the
      running program list, none of which a service in session 0 can see. Planned as a helper
      launched into the interactive session by the machinery that already places Task Manager
      there — which would also settle whether polling counters from *that* session is what the
      Task Manager workaround has really been doing all along
- [ ] Interactive TUI rendering in a real terminal (the development session had
      redirected output; the console correctly refuses and prints CLI usage instead)
- [ ] `install-agent.ps1` service registration and firewall scoping on a clean node
- [ ] `install-openssh.ps1` on `mks68i7rtx`. Its account/administrator detection was checked
      against a live node — the group is named in Russian there, so membership is compared by
      SID — but nothing has been installed yet. The node has no shell, which is why the Task
      Manager entry-point failure on it is still undiagnosed
- [ ] Linux agent: systemd unit, `linux-static-x64` install path, `/sys` sensors

### Planned 📋
- [ ] **Finish GPU mining out of the CLI.** Four pieces, in the order they hurt: an interactive
      session launcher (without it one node cannot be driven from the console at all), a
      `GpuInstallerService` so lolMiner arrives the way XMRig does, a `GpuScreen` in the TUI, and a
      per-node record of every pause and resume the way `throttle.log` records rungs
- [ ] **Fold the cards into the profit line, and read the other pool.** `GpuPoolService` reads
      Kryptex and Economics shows it in its own table, but the headline profit still counts a
      card's electricity without its revenue — on this fleet, roughly 72 ₽/day missing from a
      45 ₽/day figure. Doing it properly means deciding how two coins share one column when one
      figure is a measurement and the other an estimate, which is why it was not done by simply
      adding them. unMineable is the other half: it publishes a balance API too, and its login
      shape (`XMR:address.worker`) is exactly why `TargetFor` declines it today rather than
      deriving a confident wrong address.
- [ ] **Roll PawnIO out to the rest of the fleet.** Proven on `mks68i7rtx` (see the verified
      list); `desktop-ib88isg` and `re-7lqd67ahcm0r` still report no CPU power at all. The dev box
      is the awkward one: Memory Integrity is on there (`SecurityServicesRunning=2`), which is the
      combination PawnIO's authors make no claim about
- [ ] Roll `powerFallbackWatts` out from real wall-meter readings, so the economics stop
      resting on estimates
- [ ] Hashrate history with a sparkline per node
- [ ] Alerting: node offline, miner dead, temperature over threshold
- [ ] Per-node XMRig config templates (thread pinning, huge pages, MSR flags)
- [ ] Automatic miner restart when a node reports zero hashrate while mining
- [ ] **Move the nodes already in `fleet.json` onto their MagicDNS names.** Discovery stores a
      name now, but only for machines it adds: a fleet built before that still polls by address,
      and the only way across is **Nodes → Edit node**, one node at a time. Offer the swap during
      discovery for machines already known, or as a one-shot command, and confirm a whole poll
      runs over names — what has been checked live is `/info`, not a full `status` fan-out

### Known Issues / Risks ⚠️
- **Lowering the miner's priority is a switch, not a dial, on a hybrid CPU.** Dropped to
  `BelowNormal` on `mks68i7rtx` (i7-12700KF), Windows 11 read the miner as background work and
  parked it on the four E-cores: **7,126 H/s became 1,058**, an 85% loss, with the eight P-cores
  sitting idle. Priority is therefore not a lever this project offers, and `CpuReservation` says
  so where somebody would otherwise reach for it. The related trap is already recorded above: a
  job-object cap keeps 27.6% of the hashrate at rung 50, because freezing threads costs RandomX
  its scratchpad. Affinity is the one lever that does neither — freeing two cores cost 7%.
- **A reserved core is reserved from the miner, not for anybody in particular.** Nothing stops
  another program taking it, and nothing pins the game to it; the machine simply has two cores
  the miner cannot touch. That is enough on a desktop and would not be on a busy server.
- **`reservedCores` is static, and on an idle machine that is a bad trade.** Measured on
  `mks68i7rtx` with the game closed and the card mining in both arms: two reserved cores cost
  **15% of the hashrate** (7,549 → 6,418 H/s), not the 7% measured while the game was running.
  Worse, they run the CPU *hotter where it counts* — the package pinned to 99.5 °C against
  95.3 °C, while the mean core temperature was 1.4 °C lower. That is a hotspot: twelve RandomX
  threads on ten physical cores means two P-cores carry both hyperthreads, and the per-core read
  showed the two reserved cores as the coolest on the die (75 and 79 °C) beside a P-core at 100 °C
  with `Distance to TjMax = 0`. The setting should hold cores back only while somebody is at the
  machine — the same shape as the GPU pause rule — and until it does, a node with a reservation
  gives that up around the clock. Set back to 0 on that node on 2026-09-07.
- **Nothing in the fleet can see GPU contention, so a pause rule has to be told what to watch.**
  The agent reports the card's temperature, load and VRAM as one number each; it cannot say who
  is using them. That is why the rule names ports and processes at all — and why it is only ever
  as complete as the list an operator remembers to write. The measurement that found the freeze
  on `mks68i7rtx` came from Windows' `\GPU Engine(*)\Utilization Percentage` and
  `\GPU Process Memory(*)\Dedicated Usage` counters, run by hand over SSH, which give exactly the
  per-process split the agent lacks. Reading them from the agent would let the rule notice *any*
  program wanting the card instead of the named ones — the honest generalisation — but the
  counters have not been read from session 0 here, and this project has already been caught once
  by an API that returns nothing from a service context (`Get-NetTCPConnection`).
- **GPU mining cannot start in a node's logged-on session**, and `GpuMinerService` refuses
  `RunInInteractiveSession` with a message saying so rather than reporting a start that never
  happens. As of 2026-09-04 no node is known to need it: `mks68i7rtx` was the reason the setting
  exists, and it turns out to mine perfectly well from session 0 under the agent. Closing the gap
  properly would mean lifting the `CreateProcessAsUser` machinery out of `SessionMonitorService`,
  where it is private, entangled with monitor adoption, and worth ~60% of that node's CPU hashrate
  if broken — so the setting stays a documented refusal until some node actually needs it. Note it
  would also need a fix that path does not currently need: `lpCommandLine` is passed as null today,
  and `CreateProcessW` writes into that buffer, so a managed string handed straight to it corrupts
  interned memory.
- **The agent does not keep lolMiner's own log file.** The scheduled task it replaced on
  `mks68i7rtx` passed `--log on --logfile`, leaving `gpu.log` on disk across restarts; the agent
  captures the last 200 lines in memory and serves them from `/gpu/logs`, which is gone the moment
  the agent restarts. Nothing depends on the file today, but a miner that failed overnight now
  leaves less behind than it used to.
- **lolMiner is installed by hand.** `gpuMinerPath` records where it went; there is no
  `/gpu/install` to match the CPU miner's. A node whose path is wrong reports a clear failure
  rather than mining nothing quietly, but somebody still has to walk the file over.
- **A card's earnings are shown but not yet folded into the profit line.** `GpuPoolService` reads
  what the pool has actually paid and Economics shows it in its own table, deliberately beside the
  Monero figures rather than inside them: two coins summed into one "income" column lose the thing
  that distinguishes them, and one is a measurement while the other is an estimate. The consequence
  is that the headline **Profit/day still counts the cards' electricity and not their revenue** —
  on this fleet that is roughly 72 ₽/day missing from a 45 ₽/day figure. Only Kryptex is read;
  unMineable spells its login differently and is declined rather than guessed at.
- **A hidden monitor window makes that monitor unopenable for the person at the machine.** Task
  Manager is single-instance per session, so a hidden instance does not sit quietly beside a new
  one - it swallows it. Measured with nothing running to begin with: starting one hidden gives one
  process and no window; starting another normally gives the *same* process, still no window, and
  no new one. The operator presses Ctrl+Shift+Esc and nothing at all happens, which is exactly how
  it was reported. `SessionMonitorService` therefore prefers Resource Monitor, worth the same
  hashrate (7,097 H/s against 7,092) and missed far less often. Both are single-instance, so this
  moves the nuisance rather than removing it; removing it means a purpose-built helper nobody ever
  wants to open. Note that a node already holding a hidden Task Manager keeps holding it after the
  update, because the agent adopts what it finds - switching that node's monitor off and on again
  is what replaces it.
- **A CPU cap costs more hashrate than it saves CPU, and the penalty is mostly a fixed toll for
  capping at all.** Measured on the i5 with the miner untouched between readings and its huge
  pages intact throughout: 2,210 H/s uncapped, 610 at rung 50 (27.6%), 325 at rung 25 (14.7%),
  and a clean recovery to 2,390 on release. Between 50 and 25 the fall is exactly proportional;
  the step down from uncapped is not, and costs roughly 45% beyond it. The likely cause is cache:
  a hard cap freezes the job's threads, RandomX wants 2 MB of scratchpad per thread, and six
  threads on a 12 MB L3 fill it exactly - so anything else running evicts the lot during the
  freeze. The practical consequence is that intermediate rungs are a poor trade (rung 75 gives up
  ~59% of the hashrate to free a quarter of the miner's CPU time), and a two-rung ladder of full
  speed and stopped is the honest shape unless fine control is really wanted.
- **The ladder's top rung is unreachable, and this is now measured rather than argued.** The
  journal's first six minutes on `mks68i7rtx` read `miner=59.2..59.7%` — twelve mining threads on
  twenty logical CPUs, which is 60% to a tenth. Since the ladder is read against everything
  *except* the miner, "other" cannot exceed ~41% while the miner runs at full speed, and the
  shipped `70% -> stop` rung therefore never fires from full speed. `45% -> 25` is reachable only
  just. A ladder whose stop rung cannot be reached is not a safety net, which strengthens the case
  the hashrate measurements already made for a two-rung shape over a tuning exercise.
- **The ladder is read against the whole machine's CPU, which hides single-threaded work on a
  many-core node.** One busy thread is 8% of a 12-thread node but 3.6% of the 28-thread Xeon, so
  the same rung means different amounts of interference on different rigs, and on the Xeon a
  person's single-threaded work may never move the miner at all. The decision log records busy
  threads alongside the percentage so this can be corrected from readings rather than guesses.
- **`stop` kills every `xmrig` process on the node**, including one an operator started
  by hand. Deliberate, but destructive if a node is shared.
- **Hashrate is unreadable for a miner the agent did not start** — it holds its own API
  token, and only a miner this agent launched can be read. The console shows `mining (no api)`
  until the miner is restarted through the fleet.
- **Income is an expectation, not a payout.** It reduces to
  `hashrate × 86400 × blockReward / difficulty`, extrapolating the hashrate measured right
  now across a whole day. **Economics** now shows it next to the pool's `dailyCredited`
  with a ratio, but the two legitimately diverge: the pool figure is a rolling 24h for the
  entire wallet, including machines outside this fleet, and variance alone moves it.
- **Pool fees are not subtracted.** Hashvault reports `pplns_fee: 0`, so the estimate is
  correct for the default pool; point `pool.apiBase` at a pool that charges a fee and the
  estimate will be high by that fee.
- **CPU temperature and package power need PawnIO** ([pawnio.eu](https://pawnio.eu)).
  Without it LibreHardwareMonitor starts and reports only what needs no kernel access,
  which is why GPU figures appear and CPU figures do not. PawnIO's compatibility with
  Windows Memory Integrity (HVCI) is **not stated by its authors** — verify per machine,
  and check `CodeIntegrity/Operational` event 3033 if a driver is blocked.
- **Pushing pool settings does not restart the miner**; the operator must restart it for
  new settings to apply.
- **A node mines at roughly 60% of its rate unless a monitor window is open in its logged-on
  session, and nobody knows why.** Measured on an i7-12700KF: 4,380 H/s with nothing watching,
  7,092 H/s with Task Manager open, 7,097 H/s with Resource Monitor open. Eleven explanations
  were tested and discarded, each by a controlled A/B on that node: huge pages (constant at
  1180/1180 throughout), free memory, CPU frequency, competing processes (nothing above 0.5%),
  xmrig's priority (worth +26% on its own, not this), the High Performance power plan, a 1 ms
  timer resolution, opting out of EcoQoS, polling the same counters from the agent's service,
  `Win32PrioritySeparation`, and simply having a window open - Notepad changes nothing. What
  survives is that the effect tracks a ~7 GB swing in memory in use, the same signature as the
  Xeon's `explorer.exe` leak, so one mechanism may explain both machines. `SessionMonitorService`
  keeps Task Manager open as an opt-in per-node workaround, falling back to Resource Monitor on
  a node whose Task Manager will not run — the two were measured at 7,092 and 7,097 H/s, so the
  choice costs nothing. It is a remedy without a diagnosis and both the code and the console
  say so.
- **`mks68i7rtx` showed `taskmgr.exe` failing to load with a missing `ImageList_CoCreateInstance`
  entry point, and the cause is still unknown.** The loader dialog reached the operator during a
  spell when the agent was relaunching Task Manager every thirty seconds, so it appeared at that
  same cadence. The relaunch loop is fixed; the load failure is not explained. Everything ruled
  out so far was ruled out on `desktop-ib88isg`, which turned out to be the wrong machine — its
  WinSxS Common-Controls v6 assemblies are present, it logs no `SideBySide` events, no
  `comctl32.dll` shadows the real one on `PATH`, and `AppInit_DLLs` is empty — so none of it
  counts as evidence about `mks68i7rtx`, which has no SSH server and cannot be inspected the
  same way. Reproducing it means closing the window that node currently has open, which costs
  its hashrate while the attempt runs. The Resource Monitor fallback exists so a node in this
  state still gets a window; it is not a fix for whatever this is.
- **A node that loses its huge pages runs several times slower with no other symptom.**
  Measured on the Xeon E5-2680 v4: 5.97 kH/s at 100% allocation against 1.34 kH/s at 11%,
  a 4.5x swing from memory fragmentation alone. The `Pages` column now exposes it; the
  remedy is restarting the miner while RAM is free, which the operator must authorise.
- **Test coverage is narrow.** [tests/](tests/) covers the markup, money and update-asset
  contracts that have actually broken; the agent, the pool parser and the HTTP layer are
  still verified only by hand against a live node.

---

## Glossary

| Term | Meaning |
|------|---------|
| **Node** | One mining PC running the agent, addressed by its Tailscale IP |
| **Fleet token** | Shared secret in `X-Fleet-Token`; must match `Agent:Token` on every node |
| **Fallback watts** | Operator-supplied power figure used when no sensor measures draw |
| **Sensor notice** | Agent-generated explanation for a missing sensor, shown in the console |
| **PawnIO** | Separately installed kernel driver LibreHardwareMonitor uses to reach MSRs |
| **Atomic units** | Hashvault reports XMR scaled by `config.sigDivisor` (10^12) |
| **Adopted miner** | An XMRig process the agent found rather than started |

---

## Document Information

**Document Version**: v1.2
**Last Updated**: 2026-09-06
**Product Version**: 1.14.0
**Status**: Active
**Repository**: `c:\Repos\xmrig-fleet` (branch `master`), published at
[github.com/XYphrodite/xmrig-fleet](https://github.com/XYphrodite/xmrig-fleet)
**Related docs**: [README.md](README.md) (operator guide, Russian)
