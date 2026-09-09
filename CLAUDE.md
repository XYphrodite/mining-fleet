# CLAUDE.md

Project instructions for Claude Code. Auto-loaded every session.

## Project context

The canonical project overview is auto-loaded via the import below. Keep it current
(see Documentation rules).

@ProjectContext.md

## Documentation rules

- `ProjectContext.md` is the canonical overview. Keep its **Last Updated** date and
  **Development Status** current when components change; match its existing section
  order and formatting rather than inventing a new layout.
- Its status section deliberately separates **verified live** from **implemented but
  unverified**. Never promote an item across that line without an actual run against a
  live agent, and never quietly merge the two lists.
- `README.md` is the operator guide and is written in Russian. Code, code comments, and
  `ProjectContext.md` are in English. Commit messages are Russian.

## Project notes

- This repository is standalone. It shares no code, backend, or release process with
  anything under `c:\Repos\Reborn` (ClubCore). Do not cross-reference the two.
- **Never call `stop` or `restart` against a node without asking first.** The agent
  terminates *every* `xmrig` process on that machine by design, including one the
  operator started by hand. Stopping a rig costs real mining revenue.
- **Kill `xmrig-fleet-agent.exe` (and `mining-fleet.exe` from this tree) before rebuilding.**
  A running agent or console locks its own executable and `dotnet build` fails with
  `MSB3021` / `MSB3027`.
- The product is **mining-fleet**. GitHub is `XYphrodite/mining-fleet`. The console is
  `mining-fleet.exe` with an `xmrig-fleet.exe` shim. Do not rename `xmrig-fleet-agent.exe`
  or the Windows service without an identity-migration plan — those are what live nodes
  and `upgrade-agents` look up. Release zips are published under both names.
- **Run `dotnet test` after touching a screen.** Spectre renders prompts and widgets as
  markup, so any text the app did not author — a hostname, an OS name, a path, an error
  message — must go through `UiHelpers.Escape` or `UiHelpers.Text`. Unescaped `[` has
  crashed the console twice; `MarkupSafetyTests` drives the real prompts to catch it.
- `fleet.json` (console) and `miner.json` (agent) hold the fleet token and wallet
  address. Both are gitignored — never commit them or paste their contents.
- The tracked `src/MiningFleet.Agent/appsettings.json` is a **template**: its token must
  stay the `CHANGE-ME` placeholder. Real tokens are written on the node by
  `install-agent.ps1`, never committed here.
- The agent needs Administrator/root for sensors and for killing another user's miner.
  CPU temperature and package power additionally require PawnIO on the node; without it
  those sensors are absent, and the node falls back to `powerFallbackWatts`.
- Hashvault nests everything (`pool_statistics.collective`, `network_statistics`,
  `revenue`) and reports XMR in atomic units scaled by `config.sigDivisor`. Parse
  defensively: a renamed field should blank one cell, not break a screen.
