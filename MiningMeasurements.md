# Mining Measurements

What each machine actually earns, measured rather than estimated. Kept because calculator
estimates for this fleet have been wrong by 5–10x on three separate occasions, always in the
optimistic direction, and only pool credits have ever been trustworthy.

**Every figure here is labelled.** *Measured* means a pool credited it. *Benchmarked* means the
miner reported a rate with no pool involved. *Estimated* means a third party computed it and it
has not been checked against a payout.

**Last updated**: 2026-09-04 12:00

> No wallet addresses in this file. The Monero address lives in `miner.json` on each node and in
> `fleet.json` on the console; the Tari address lives in `C:\mining\tari-address.txt` on
> `mks68i7rtx`. All three are gitignored or off-repo, and this file is public.

---

## Fleet hardware

| Node | Tailnet | CPU | GPU | Role |
|------|---------|-----|-----|------|
| `re-7lqd67ahcm0r` | 100.89.154.125 | Intel i5 | RTX 3060 12 GB | Dev box, console, Ollama |
| `desktop-ib88isg` | 100.119.48.15 | Xeon E5-2680 v4 | RX 6500 XT 4 GB | Mining node |
| `mks68i7rtx` | 100.105.87.52 | i7-12700KF | RTX 4060 8 GB | Mining node, Ollama host |
| `moscow-enjoyer` | 100.114.217.79 | — | — | Planned; agent not installed |

---

## CPU mining — RandomX / Monero

Pool: Hashvault. This is the fleet's main and only reliably profitable activity.

| Node | Hashrate | Note |
|------|---------:|------|
| `mks68i7rtx` | 7,000–7,100 H/s | *Measured.* Needs a monitor window open in its session — see below |
| `desktop-ib88isg` | 6,273 H/s | *Measured.* Collapses to ~1,340 H/s when huge pages fragment |
| `re-7lqd67ahcm0r` | 990–2,210 H/s | *Measured.* Varies with desktop use |
| **Fleet** | **14.74 kH/s** | **≈46 ₽/day** *measured*, all three nodes up, XMR at 47,558 ₽ |

### CPU findings that move the number

- **A monitor window is worth ~60% on `mks68i7rtx`.** 4,380 H/s with nothing open, 7,092 H/s with
  Task Manager, 7,097 H/s with Resource Monitor. Eleven explanations tested and discarded; the
  cause is still unknown. `SessionMonitorService` keeps one open as a workaround.
- **Huge pages are worth 4.5x on the Xeon.** 5.97 kH/s at 100% allocation against 1.34 kH/s at 11%.
  The remedy is restarting the miner while RAM is free.
- **A hard CPU cap costs more than it saves.** On the i5: 2,210 H/s uncapped, 610 at rung 50
  (27.6%), 325 at rung 25 (14.7%), clean recovery to 2,390 on release. The step down from uncapped
  costs ~45% beyond proportionality — likely L3 eviction while the job's threads are frozen.

---

## GPU mining

### RTX 4060 8 GB — `mks68i7rtx`

Miner: lolMiner 1.98a unless noted. Shares the card with Ollama; mining stands down while the
model is in use. Driven by the fleet agent since 2026-09-04 — every figure above that date was
taken under a hand-built scheduled task instead, which is worth knowing when comparing.

| Algorithm | Pool | Rate | Income | Temp / fan | Verdict |
|-----------|------|-----:|-------:|-----------|---------|
| kheavyhash | unMineable | — | — | — | **Broken.** SRBMiner 3.6.1: `PARSE error: 'params' has wrong number of fields`. lolMiner 1.98a dropped Kaspa entirely |
| blake3_an (ALPH) | unMineable | — | — | — | **Broken.** Pool accepts TCP, never answers the handshake |
| Etchash | unMineable | 31.4 Mh/s | **1.32 ₽/day** *measured*, 86 min | 63 °C / 31% | Worst of the working set |
| FishHash | unMineable | 21.0 Mh/s | **2.96 ₽/day** *measured*, 50 min | 65 °C / 37% | Middle |
| NexaPoW | unMineable | 62–64.6 Mh/s | **4.0 ₽/day** *measured*, two windows | 81 °C / 100% | Best on unMineable; also the most heat |
| Cuckaroo29 (Tari) | Kryptex | 4.48 g/s | **1,039 XTM/day** *measured from five actual payouts* — ≈72 ₽/day at the 2026-09-05 price, ≈59 ₽ net | 69 °C / 40% | Current. Out-earns the entire CPU fleet, and the only GPU configuration here that beats its own electricity |

Cuckaroo29 benchmarked at **4.53 g/s** with no pool attached, against a third-party reference of
4.07 g/s — this card runs above spec.

#### What the miner leaves for everything else — 2026-09-06, 02:56

Taken while the operator reported the node freezing during a game, from Windows'
`\GPU Engine(*)\Utilization Percentage` and `\GPU Process Memory(*)\Dedicated Usage`, which are
the only per-process split available — `nvidia-smi --query-compute-apps` returns `[N/A]` for
memory on this consumer card, and the agent reports the GPU as one number per sensor.

| Process | 3D engine | Dedicated VRAM |
|---------|----------:|---------------:|
| lolMiner (CR29) | **97.6%** | **6,879 MB** |
| Don't Starve Together | **1.1%** | **270 MB** |
| dwm + explorer + Steam + browsers | ~0% | ~450 MB |
| **card total** | 100% | **8,152 of 8,188 MB** |

The card was full. DST at 270 MB is roughly a quarter of what it wants, so Windows was evicting
its textures to system memory — which is the stutter, rather than any shortage of frames.

The CPU was not involved and the node's own journal says so: `other avg=6.0–7.3%, peak ≤33.5%
(6.7 of 20 threads), miner=59.4%, mem=32%` across the whole half-hour, on 32 GB of RAM. **The
freeze was the card alone.**

Two things follow, and only one of them was a setting. The rule on the node watched TCP 11434
(Ollama), which a game never touches — but it would not have helped to name the game either,
because `IsBusy` returned on the first condition it found and never read the process name beside
a port. Fixed in 1.13.1; see `GpuPauseRule.Evaluate`.

The other is that **no partial measure would have worked**. Cuckaroo29 allocates its ~6.9 GB once
and holds it at any intensity, and a power limit (`nvidia-smi -pl`) leaves the miner owning 97.6%
of the shaders at lower clocks. For a card the honest options are mining or not mining. At
1,039 XTM/day that is **≈3 ₽ per hour** of standing down — an evening's play costs about ten
roubles, and the CPU miner keeps earning throughout.

##### The card was only half of it — 2026-09-06, 16:19

With the miner standing down for the game and the card at 20% / 849 MB / 40 °C, the node still
stuttered. The per-core split named the other half:

```
100 2 100 1 100 26 100 21 100 50 100 59 100 8 100 4 | 100 100 100 100
└──────── one thread of each of 8 P-cores ────────┘  └─ all 4 E-cores ─┘
```

Twelve RandomX threads at thread priority `Highest` inside a `High` process, on all 20 logical
CPUs. The game was left hyperthread siblings of saturated cores and nothing else, and its 24 MB of
RandomX scratchpad filled a 25 MB L3. The journal read `other avg=8.4%` throughout — a process that
cannot get scheduled cannot register load, which is exactly why a load-driven ladder was never
going to see this.

Both levers measured on the live miner, no restart, huge pages 1180/1180 the whole time:

| Miner state | Hashrate | Where it ran |
|---|---:|---|
| `High`, all 20 logical CPUs | 7,126 H/s | 8 P-threads + 4 E-cores |
| **`BelowNormal`** | **1,058 H/s** | **the 4 E-cores only** |
| `Normal`, two P-cores freed by affinity | **6,642 H/s** | 16 of 20 logical |

**`BelowNormal` costs 85% on Alder Lake.** Windows 11 reads a below-normal process as background
work and parks it on the efficiency cores; the P-cores sat idle. It is not a dial.

**Affinity costs 7%** and was what the operator confirmed as fixing the game. Nothing is frozen and
nothing is demoted — the miner simply runs on fewer CPUs — which is why it does not pay the
cache-eviction toll a job-object cap does (rung 50 keeps 27.6%, measured earlier on this fleet).

##### Reserving cores costs more than it looked, and runs hotter — 2026-09-06, 18:0x

The 7% above was measured while the game was running, which is the one condition under which the
reserved cores are being used. With the machine otherwise idle — game closed, card mining in both
arms, 15-second samples, means of the last four of seven:

| | `reservedCores=2` | `reservedCores=0` |
|---|---:|---:|
| Hashrate (60 s) | 6,418 H/s | **7,549 H/s** |
| Package temperature | **99.5 °C** | 95.3 °C |
| Hottest core | **99.8 °C** | 97.5 °C |
| Mean of all cores | 86.5 °C | 87.9 °C |
| Package power | 108.7 W | 117.3 W |

**The reservation costs 15%, not 7%**, when nobody is at the machine.

**And it runs the CPU hotter at the point that matters.** The mean core temperature is 1.4 °C
*lower* with two cores reserved while the peak is 4 °C *higher* — the signature of a hotspot. Twelve
RandomX threads squeezed onto ten physical cores means two P-cores carry both their hyperthreads,
and the package pins to TjMax; spread across twelve, the same work makes more total heat (117 W
against 109 W) and a cooler worst core. Confirmed by the per-core reading taken separately: the two
reserved cores were the two coolest on the die at 75 °C and 79 °C, while P-Core #6 sat at 100 °C
with `Distance to TjMax = 0`.

The conclusion is that a *static* reservation is a bad trade — it should hold cores back only while
somebody is actually there. Set back to 0 on this node on 2026-09-07: 7,557 H/s, 97 °C, huge pages
1180/1180.

> **This machine is thermally marginal either way.** Even unreserved it touches 97-100 °C at
> 117 W, which is a lot for an i7-12700KF, and its fans read `1781 1347 0 0 1677` RPM — two
> headers at zero. Worth a look inside before reading any more CPU measurements off this node.

##### After the fix, same session, game still running

Rule pushed as `{ tcpPort: 11434, processNames: ["dontstarve_steam_x64"], quietSeconds: 300 }`.
The card stood down on the next tick — `/gpu` answered `dontstarve_steam_x64 is running` five
seconds later.

| | Mining through the game | Stood down |
|---|---:|---:|
| GPU utilisation | 100% | **18%** |
| VRAM in use | 8,152 MB | **827 MB** |
| Temperature | 66 °C | **46 °C** |

The CPU miner was untouched across the whole exercise, including two agent restarts: same pid,
7,100 H/s, huge pages 1180/1180.

### RX 6500 XT 4 GB — `desktop-ib88isg`

| Algorithm | Pool | Rate | Income | Verdict |
|-----------|------|-----:|-------:|---------|
| pearlhash | unMineable | 7.9 TH/s reported | **0** *measured* | Ran hours at 89 W. **Zero accepted shares.** The card genuinely hashed; the pool credited nothing |
| autolykos2 | — | — | — | **Does not fit.** Needs 6,935 MB, card has 3,883 MB free |
| Cuckaroo29 | — | — | — | **Refused.** lolMiner reports `Active: false (Unsupported device or driver version)` |
| NexaPoW | unMineable | 21.0 Mh/s | ~1.4 ₽/day *estimated* from the 4060's measured rate | Current |
| SHA3x (Tari) | — | — | ~0.04 ₽/day *estimated* | Not attempted. Network 499 Th/s is ASIC-owned |

**This card is a third of the 4060 on the same algorithm** (21.0 against 62 Mh/s), and cannot run
the one algorithm that pays properly. Its ceiling is roughly 1.4 ₽/day.

> **Careful with this node.** It is simultaneously the subject of a separate investigation into a
> session-0 `explorer.exe` that bugchecks it, and any change to its GPU load is a confound for a
> before/after CPU hashrate measurement. It is also memory-tight — a recursive directory scan over
> SSH has knocked it offline. Keep remote commands narrow, and pause the GPU miner while anyone is
> measuring this node's CPU.

### RTX 3060 12 GB — `re-7lqd67ahcm0r`

Not mining by operator decision. Hosts a local model.

---

## Coin and pool economics

| Constant | Value | Date |
|----------|-------|------|
| XMR | 47,558 ₽ / $548.72 | 2026-09-04 11:56 |
| USD | 86.67 ₽ | 2026-09-04 11:56 |
| XTM | $0.00056797 / 0.0492 ₽ (CoinGecko `minotari`, rank 1850) | 2026-09-04 11:56 |
| NEXA | $0.00000101 (rank 1156) | 2026-09-04 |
| Electricity | 6.61 ₽/kWh ($0.076) | — |
| RTX 4060 draw | 115 W limit; `power.draw` unavailable via nvidia-smi on this card | — |
| RX 6500 XT draw | 88.8 W *measured* | — |

### unMineable takes roughly half

**Measured realisation factor: 0.454.** Theoretical gross for NexaPoW at 64.6 Mh/s is ~10 ₽/day;
the pool credited 4.0. Stated fees (1% pool + 2% miner dev fee) explain about three points of the
~55% gap; the rest is the conversion spread into XMR.

Consequence: **the algorithm was never the main lever.** Every live unMineable endpoint clusters
within ~1 ₽/day of every other one after the haircut. Leaving unMineable is worth more than any
algorithm choice inside it.

| Endpoint | State (2026-09-04) |
|----------|--------------------|
| nexapow, blake3, autolykos, etchash, fishhash, kheavyhash | Live (TCP 3333 answers) |
| pyrinhash, karlsenhash, sha3x, kawpow | Dead |

**Payout thresholds decide whether money exists at all.** unMineable's XMR threshold is 0.03 XMR —
about 290–300 days at the measured rate, so the accrued balance was effectively unreachable and was
abandoned at 0.0000088 XMR (≈0.40 ₽). Kryptex's Tari threshold is 200 XTM, roughly 6–13 hours,
paid automatically each hour, no payout fee, 1% pool fee.

---

## Mechanisms found the hard way

These cost hours to find and will cost them again if forgotten.

- **The throttle's stop rung cannot be reached, measured 2026-09-05.** The node's own load journal,
  six minutes after it first ran on `mks68i7rtx`, reads `miner=59.2..59.7%` — twelve mining threads
  on twenty logical CPUs, 60% to a tenth. The ladder is read against everything *except* the miner,
  so while the miner runs at full speed the figure it reacts to can never exceed ~41%:

  | Other CPU | Rung | Reachable from full speed? |
  |---:|---|---|
  | 0% | 100 | — |
  | 10% | 75 | yes |
  | 25% | 50 | yes |
  | 45% | 25 | only just |
  | **70%** | **0 (stop)** | **no** |

  So a fleet that switched throttling on expecting the miner to get out of the way would find it
  giving up 72% of its hashrate at rung 50 and never stopping at all. Combined with the measured
  cost of a cap, this is the strongest argument yet for the two-rung ladder.


- **Task Scheduler starts actions at priority 7 (BelowNormal).** With xmrig holding every CPU
  thread, the GPU miner could not submit shares before they went stale: **18% bad-share rate** on
  Cuckaroo29. Setting the task's `Priority` to 4 stopped it dead — valid shares went 18 → 137 with
  the stale count frozen at 4, so 119 consecutive shares landed without a single loss. Both
  `set-algo.ps1` and `set-pool.ps1` now carry `-Priority 4`.
- **On `mks68i7rtx` the GPU miner will not run in session 0.** Launched as SYSTEM it initialises
  both backends and then stops, never reaching worker-thread init. It must run under the logged-on
  account with `LogonType Interactive`. On `desktop-ib88isg` session 0 works fine — this is
  per-machine, not general.
- **On `mks68i7rtx`, `powershell.exe` launched by Task Scheduler as `local` dies instantly with
  `0xC0000005`.** Reproducible. The same script under SYSTEM runs fine. This may be related to that
  node's unexplained `taskmgr.exe` entry-point failure. Consequence: no PowerShell launcher in a
  task on that node — point the task at the executable, and run helper loops as SYSTEM.
- **`Get-NetTCPConnection` returns nothing from a service context on `mks68i7rtx`.** Read the TCP
  table directly with `[System.Net.NetworkInformation.IPGlobalProperties]` instead.
- **Ollama binds the tailnet address, not loopback** (`100.105.87.52:11434`). A watchdog polling
  `127.0.0.1` sees nothing.
- **sshd on `mks68i7rtx` resets roughly every other connection and has no working sftp.** Long
  sessions do not survive — a 30-minute measurement was lost this way. Push files as base64 in
  chunks, keep commands under the ~8 KB command-line limit, and retry everything.
- **A downloaded miner must be `Unblock-File`d** or cmd refuses to execute it with "access denied".

### GPU/model coexistence on `mks68i7rtx`

**Now the agent's job.** `GpuPauseService` in agent 1.10.1 replaced the hand-built
`gpu-guard.ps1`; the scheduled tasks `xmrig-fleet-gpu` and `xmrig-fleet-gpu-guard` are disabled
on that node as of 2026-09-04. The rule is the same one, generalised: it watches a TCP port or a
process name rather than Ollama specifically, because a game and a render want the card for the
same reason. Settings live in `fleet.json`, the node keeps them in `miner.json`, and the decision
is visible in `/gpu` as a notice rather than only in a log nobody opens.

Stops mining while any connection is open to Ollama's port, resumes after **300 s** of quiet. The
trigger is a connection, not a resident model: a model stays in VRAM for over twenty minutes after
one question and costs no GPU time while it does.

| Model speed | Measured |
|-------------|---------:|
| While the card mines | 19–24 tok/s |
| With the card free | **53 tok/s** |

Duty cycle measured over one hour of ordinary use: **90% mining**. Over a subsequent 9.4 h stretch:
**100%** — 453 samples, every one with the miner up, because nobody asked the model anything for
eleven hours. The feared 33% did not materialise, but both figures say more about how the model was
used that day than about the watchdog. Duty cycle must be read from `earn2.csv` over a working day
before it means anything.

### The session-0 story was wrong (2026-09-04)

The card was mined from a scheduled task with `LogonType Interactive` because `lolMiner` was
believed not to work under a service in session 0. It does. `lolMiner --list-devices` run from an
SSH shell — which on Windows is session 0 — enumerates the RTX 4060 through CUDA with no
complaint, and the agent, a service in the same session, has since started and run the miner
there. What actually fails on that node is PowerShell under Task Scheduler, with `0xC0000005`;
the two were conflated.

The same exception code turns up in a third place on that machine: Ollama's `llama-server`
terminates with `0xc0000005` on **every** request, including a 137 MB embedding model with the
card free and mining stopped. `/api/tags` and `/api/version` keep answering while it does, so an
API ping is not a health check here. Whether one fault explains all three is unknown.

### What the agent measured on the first day it owned the miner

| | |
|---|---|
| Started by the agent in session 0 | pid 19400, then 18544 after a service restart |
| Stand-down on a request to port 11434 | immediate — the next 1 s tick |
| Notice shown while paused | `paused, 295s of quiet still needed`, counting down |
| Resume | 16:43:33, after the full 300 s, at 4.27 g/s |
| Autostart across an agent restart | `GPU autostart: GPU miner started on CR29, pid 18544` |

Rate readings across the changeover: 3.32-4.27 g/s, against 4.42 g/s from the scheduled task just
before it. **Not yet a fair comparison.** lolMiner reports `Total_Performance` as a session average,
and this miner was restarted three times in half an hour while the pause, resume and autostart
paths were each exercised, so every reading is an average dominated by its own warm-up. A settled
figure needs an undisturbed hour; the task figure had 9 h 25 min behind it.

The shape of the readings supports that reading rather than a real loss. Left alone after the last
restart, the reported average climbed monotonically — 3.90 g/s at 16:55, 4.03 at 17:04, 4.09 at
17:06, 4.15 at 17:07 — which is what a session average does while it warms up, and not what a
miner held back by something does.

Shares: 8 accepted, 0 stale, 1 rejected in the first forty minutes — the 18% staleness that a
scheduled task's default priority once caused did not return, which is what `GpuMinerService`
setting `ProcessPriorityClass.Normal` explicitly is there to prevent.

---

## How measurements are taken

Two scripts on `mks68i7rtx`, both installed this session:

- `C:\mining\earn-log.ps1` → scheduled task `xmrig-fleet-earn`, SYSTEM. Appends to
  `C:\mining\earn2.csv` every minute: timestamp, algorithm, whether the miner was up, the
  unMineable balance (every 5th sample), and the miner's accepted-share count.
- `C:\mining\analyze.ps1` → groups the CSV by algorithm and reports XMR/day and ₽/day, counting
  only the minutes the miner was actually running.

`C:\mining\set-pool.ps1 -Algo X -Pool Y -User Z` re-registers the mining task for any algorithm and
pool. `set-algo.ps1` is the unMineable-shaped shortcut.

**The protocol that works**: switch, let it run at least an hour, read what the pool credited,
divide by minutes actually mined. Nothing else has been reliable.

---

## Open questions and future tests

Ordered by expected value, not by effort.

### 1. Monero + Tari merge mining — the largest unexplored lever, now with numbers

Tari is merge-mineable with Monero's RandomX, so the same hashes earn both. Computed 2026-09-05
from Kryptex's live network figures, at XMR 46,300 ₽ and XTM 0.069066 ₽:

| Per 1 kH/s of RandomX, per day | Yield | Value |
|---|---:|---:|
| Monero | 0.0000721 XMR | **3.34 ₽** |
| Tari on RandomX | 38.11 XTM | **2.63 ₽** |
| Both, merge-mined | — | **5.97 ₽** |

Two conclusions, and the first is the one worth saying out loud:

**Switching the CPUs to Tari would lose money.** Monero pays 27% more per hash than Tari does on
the same algorithm. Tari only wins when it is earned *as well as* Monero, not instead of it.

**Merge mining is worth +79%** — on this fleet's 14.87 kH/s, about **+39 ₽/day for no extra watts
at all**. That is nearly the entire current CPU income again.

**The method is cross-checked.** Applied to the RTX 4060 on Cuckaroo29 it predicts 944 XTM/day
where five real payouts measured 1,039 — it under-predicts by 10%, so the Tari-RandomX figure
above is if anything conservative.

**The break-even is 0.0876 ₽ per XTM.** Above that, Tari on RandomX out-earns Monero outright and
the question stops being about merge mining. XTM is at 0.069 today, so it would have to rise 27%.
It roughly doubled in the month before this was written, which is the whole reason to keep this
number written down rather than the conclusion.

**Test**: stand the stack up on one node — a full `monerod`, a Tari base node and the Tari
merge-mining proxy — point that node's xmrig at the proxy, and compare its XMR credit before and
after (merge mining must not reduce it) while watching XTM accrue. Note this is real
infrastructure, not a config change: `monerod` alone is a few hundred GB.

### 1a. Per watt, the card already beats every CPU in the fleet

| | Income/day | Draw | Per watt |
|---|---:|---:|---:|
| RTX 4060 on Cuckaroo29 | 71.8 ₽ *measured from payouts* | ~110 W | **0.652 ₽/W** |
| i7-12700KF on Monero | 24.2 ₽ | 124 W *measured* | **0.195 ₽/W** |

**3.3x**, and it was invisible until the payouts and PawnIO landed on the same day. This does not
mean sell the CPUs: the card's figure rests on a coin worth $0.0008 that recently doubled, and
Monero's does not. But it does mean a second card would earn more than a second CPU, and that the
fleet's shape was chosen when neither number was visible.

### 2. Does the Tari payout actually arrive? — answered on 2026-09-05: **yes**

Five payouts, every one `FINISHED` with a transaction id:

| Paid at | XTM |
|---|---:|
| 2026-09-04 15:05 | 202.61 |
| 2026-09-04 18:05 | 214.89 |
| 2026-09-05 00:05 | 255.32 |
| 2026-09-05 06:05 | 219.22 |
| 2026-09-05 13:05 | 263.07 |
| **paid** | **1,155.12** |
| still on the pool | 90.35 confirmed + 173.33 unconfirmed |

The four payouts after the first span **22 h and 952.50 XTM**, so the card earns **≈1,039 XTM/day**.
That figure is the measurement; the rouble one is it multiplied by a price that moves. At
$0.00080716 (Kryptex's own chart) and 85.78 ₽/$ that is **≈72 ₽/day**, against ~13 ₽/day of
electricity at 110 W and 5 ₽/kWh — so **≈59 ₽/day net**.

Note this is higher than the 49.5 ₽/day recorded above, and the coin flow is not what changed:
the hashrate is the same 4.4–4.5 g/s. Price and network difficulty are. **Always keep the XTM/day
separate from the ₽/day** — one is what the card did, the other is what the market did.

For scale: the whole CPU fleet earns about 45 ₽/day. This one card out-earns it, and Economics
shows none of it.

**Nothing can be verified on-chain.** Tari is private by default and its block explorer states
plainly that address balances are not visible. The pool's record and the operator's own Tari
Universe wallet are the only two places the money can be seen.

### 2a. Kryptex has a documented public API, and it needs no key

Found the same day, which makes the pool adapter in the roadmap a small job rather than a scrape.
`https://pool.kryptex.com/openapi.yaml` is the spec. The path shape puts the coin **first**, which
is why the obvious guesses all 404:

```
https://pool.kryptex.com/{coin}/api/v1/miner/balance/{address}
https://pool.kryptex.com/{coin}/api/v1/miner/payouts/{address}
https://pool.kryptex.com/{coin}/api/v1/miner/payouts/{address}/stats
https://pool.kryptex.com/api/v1/coin/{coin}/price/chart
```

`{coin}` is the algorithm-specific slug — `xtm-c29` here, not `xtm`. `balance` returns
`total / unconfirmed / confirmed / threshold / reached_pct`, `payouts/stats` returns
`reward.week / reward.month / paid / unpaid`, and the price chart returns USD points.

CoinGecko's id for the coin is **`minotari`**, not `tari` — the obvious one returns an empty
object rather than an error, which is exactly the shape of a bug nobody notices.

### 3. Is the 0.454 unMineable haircut real?

Measured once, on one algorithm. If it holds, direct pools are worth roughly 2x on every card.

**Test**: mine NexaPoW on a direct pool (Kryptex runs one) with a Nexa address for one hour, and
compare against the unMineable measurement for the same card and algorithm. Clean A/B — same
algorithm, same hardware, two pools.

### 4. Regional pool server

Kryptex's dashboard suggests the RU server would give a higher effective hashrate than the global
one currently in use (24 ms latency).

**Test**: one hour on each, compare valid-share rate. Cheap, and stale shares are unpaid work.

### 5. Watchdog cooldown

300 s was chosen deliberately, and one hour of observation showed 90% duty. But that hour was quiet.

**Test**: let `earn2.csv` accumulate a full 24 h, compute the real duty cycle from the `up` column,
then decide whether 90 s would recover meaningful time. The cooldown now lives in `fleet.json` as
`gpuMiner.pauseWhile.quietSeconds`, so changing it is a push rather than an edit on the node.

**Also settle the hashrate.** Every rate above for the agent-driven miner is a session average taken
within half an hour of the changeover, while the miner was being restarted to test each path. Leave
it alone for an hour and read it once, so the move off the scheduled task can be shown to cost
nothing — or shown to cost something.

### 6. Power limit versus heat

The 4060's `power.draw` is not readable through nvidia-smi on this card, so the 115 W figure is a
limit, not a measurement.

**Test**: step `nvidia-smi -pl` through 115/100/85/70 W, recording hashrate and temperature at each.
Produces the heat-per-rouble curve — which matters here because the heat is wanted.

### 7. PawnIO — answered on 2026-09-05

Installed 2.2.0 on `mks68i7rtx`. The sensors appear, and the numbers were worth having:

| | Before | After |
|---|---|---|
| `estimatedPowerWatts` | empty | **158.8 W** |
| `powerIsMeasured` | false | **true** |
| CPU package | — | **123.8 W** under a 7,246 H/s miner |
| CPU temperature | — | **92 °C** |

That node had been contributing **zero watts** to the fleet total while mining on both the CPU and
the card. 158.8 W is CPU package plus the flat 35 W board overhead; the RTX 4060 reports no power
sensor at all, at 100% load, so roughly 110 W of that node is still uncounted and only a wall meter
will settle it.

**Remaining**: `desktop-ib88isg` and `re-7lqd67ahcm0r` are still without it. The dev box is the
awkward one — Memory Integrity is on there, which is the combination PawnIO makes no compatibility
claim about, so expect either nothing to change or a `CodeIntegrity/Operational` event 3033.

### 8. Automatic huge-page recovery

The Xeon loses 4.5x when huge pages fragment, with no other symptom. The `Pages` column exposes it,
but recovery is still manual.

**Test**: a rule that restarts the miner when allocation drops below a threshold and free RAM allows
— and a measurement of how often that actually fires.

### 9. Generalise the recorder

`earn-log.ps1` only knows unMineable's balance API. Kryptex's balance lives behind a
client-rendered page and had to be read externally.

**Improvement**: record `(node, algorithm, pool, balance)` from a per-pool adapter, so any future
comparison is automatic rather than hand-assembled.

---

## The honest summary

At 6.61 ₽/kWh, a GPU drawing ~110 W costs about **17.5 ₽/day** to run.

**Every algorithm tried on unMineable was net-negative** against that, by 13–16 ₽/day. On that
evidence GPU mining here was defensible only as resistive heating that returned part of its cost.

**Tari on a direct pool broke that.** 49.5 ₽/day measured against 17.5 ₽/day of electricity is
**+32 ₽/day net** — the first configuration in this fleet where the card pays for itself and then
some. One RTX 4060 now earns roughly what all three CPUs earn together (46 ₽/day). The lever was
never the algorithm; it was leaving a pool that kept 55%.

Two things temper that. The 49.5 ₽/day is nine hours old and **no payout has arrived yet**, and the
9.4 h that produced it ran at 100% duty because nobody used the local model. Both need a full day
before the number is safe to plan around.

The CPU fleet remains the steady earner at **46 ₽/day** and needs nothing but the machines staying
on. The cheapest improvement available is still a node that is switched off, not an algorithm.
