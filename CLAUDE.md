# SkuaSim — notes for Claude

Offline, deterministic test environment for Skua/VibeSkua bot scripts. It simulates the Skua API
(`IScriptInterface`), not AQW. Product docs: [README.md](README.md); roadmap and phase status: [PLAN.md](PLAN.md);
history and environment: [CONTEXT.md](CONTEXT.md). Keep PLAN/CONTEXT in sync when a phase item lands.

## Commands (run from the repo root; .NET 10 SDK, Windows only)

```powershell
dotnet build SkuaSim.slnx                                   # must stay at 0 warnings / 0 errors
dotnet run --project tests\SkuaSim.Collector.Tests          # ~10 s
dotnet run --project tests\SkuaSim.Core.Tests               # ~2-3 min, compiles real scripts
dotnet run --project src\SkuaSim.Cli -- run "Story/Lair.cs" --scenario scenarios\farmerjoe_lv1_mage.json --max-virtual-min 600 --report out.md
dotnet run --project src\SkuaSim.UI
```

- Tests are plain console runners (`Program.cs` with an `Assert` helper), not xUnit. Add a new numbered
  `TestXxx` method and call it from `RunAll`. They need a local VibeSkua/Skua install (`Skua.Core.*` DLLs,
  `Scripts\CoreBots.cs`, `QuestData.json`), so they cannot run in CI.
- The runner loads the **live** `data\gamedata.json` that the Collector plugin may be writing right now, so
  absolute numbers in test output (DPS, etc.) can drift between runs. Assert on behavior, not on such values.
- Script paths given to the CLI are relative to the VibeSkua `Scripts\` folder
  (`%APPDATA%\VibeSkua\Scripts`) or absolute. Env overrides: `SKUASIM_SKUA_ASSEMBLIES`, `SKUASIM_SKUA_DATA`.
- CLI `run` exit codes: 0 completed/stopped, 2 compile error or script exception, 3 stalled.

## Layout

- `src/SkuaSim.Collector` — Skua plugin. Parses game packets into anonymized `gamedata.json` (+ `packet_schemas.json`).
  Merges across sessions/clients under a global Mutex. Installed copies live in the VibeSkua/Skua `plugins\` folders
  listed in CONTEXT.md; rebuilding does not update them.
- `src/SkuaSim.Core`
  - `Compilation/` — `//cs_include` resolution, Roslyn compile, `TimeHookRewriter` (time APIs → virtual clock),
    `ExtractAstHints` (literal map/monster/shop names from `KillMonster`/`HuntMonster`/`KillQuest`/`BuyItem`).
  - `Runtime/` — `SimClock` (virtual time + scheduler), `SimContext` (per-run state, stop control, no-progress
    watchdog), `SimulationRunner` (compile → load → run thread → report), `SimulationTrace`.
  - `Proxies/ProxyHub.cs` — `DispatchProxy` that routes every Skua interface member to handlers in `Api/`.
  - `Api/` — the fake Skua APIs. Unimplemented members return defaults and are counted in the report.
  - `World/` — `SimWorld` (all game state and mechanics), `StatEngine` (AQW stat formulas).
  - `Scenarios/` — scenario JSON model + real AQW boost-gear and enhancement-unlock catalogs.
- `src/SkuaSim.Cli`, `src/SkuaSim.UI` (Avalonia 12 + CommunityToolkit.Mvvm) — thin front-ends over Core.
- `scenarios/` — sample scenarios. `data/` — local collected DB and reports (git-ignored, not backed up by git).

## Invariants — do not break these

- **Determinism: same script + scenario + seed ⇒ identical result, also across processes.**
  - All randomness goes through `ctx.Rng` (seeded). Never `new Random()`, `Random.Shared`, `Guid` in sim logic.
  - Never use `string.GetHashCode()` / `HashCode.Combine` for anything that becomes data (IDs, ordering, rolls):
    it is randomized per process. Use `World/StableHash.Of`. Core test 13 checks this in two processes.
  - Never read wall-clock time in sim logic; use `Clock.NowMs`. Limits/watchdogs that affect the outcome must be
    measured in virtual time (wall-clock is only for the real-time limit and the UI).
- **Threading:** the script's main thread drives virtual time (`SimClock.Advance`). Every `SimWorld` mutation
  happens under `World.Sync`. The current design assumes one player per run; Phase 4 (multi-client) needs a new
  clock coordinator and per-account RNG — discuss before changing this.
- **Data tiers:** scenario > collected data (`gamedata.json` + `QuestData.json`) > synthesized fallback.
  Anything invented must be reported with `Trace.Synthesize(key, description)` (shown in Violet in the UI).
  Never invent data for captured or scenario-defined maps.
- **Privacy:** the Collector must never persist account/player names, inventories, chat or IPs (`unm`/`uid`/
  `casterId`/`CharItemID`/`iQtyNow` are redacted; the username and the active quest list may be held in RAM only).
  Inventory, bank, friend, chat, login and house packets are never sampled.
- **The Collector is passive:** it never sends packets to the server. New data comes from packets the client
  already receives (e.g. `monsterDrops` only when another tool requests it).
- **Verify before parsing:** confirm real packet keys from `packet_schemas.json` samples (allowlist in
  `PacketSchemaSampler.ProtocolCommands`, plus the command census) before writing a parser, and build tests from
  those real anonymized samples, never from invented packets.
- **`gamedata.json` is versioned** (`GameDatabase.CurrentSchemaVersion`, now 2). A schema change needs a migration in
  `Storage/GameDatabaseMigrations.cs` that writes a dated backup first and only deletes provably wrong records.
  Drop rates are derived only in `DropRates.Recompute`; `EstimatedDropChance == null` means "unknown" and the
  simulator must never treat it as 0 %.
- Never modify the user's Skua scripts; SkuaSim only reads them.
- Everything (code, UI strings, logs, docs, tests) is in English. Files use CRLF line endings.

## Prior history

The project was built in Antigravity sessions; transcripts are under
`%USERPROFILE%\.gemini\antigravity\brain\<id>\.system_generated\logs\transcript.jsonl`
(origin `2d6d8a65…`, main development `d28451cc…`, OMNISCOPE plan `39b80778…`).
