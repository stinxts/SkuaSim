# SkuaSim — Project Context

> This document continues the original design conversation started in the ArmyFarmerJoeDoAll workspace.
> Origin: an earlier Antigravity design conversation (local transcripts, not part of this repository).
> The original detailed plan artifact also lives with those local transcripts.

## Objective
An **offline, reproducible test environment** for **any Skua/VibeSkua script**, designed to reproduce bug reports in detail and validate fixes. It does **not** recreate AQW or use copyrighted game assets/servers. Instead, it simulates the **Skua API** (`IScriptInterface`: Player, Map, Monsters, Inventory, Quests, Drops, Shops, Boosts, Config, Wait...) backed by an in-memory simulated game world.

## Key Decisions
1. **Two Separate Components:**
   - **Collector (Plugin)**: runs passively inside Skua/VibeSkua alongside any script (or manual play). Listens to packets already received by the client (maps, monsters, HP, combat damage, auras/stacks, class skills, drops, shops, quests, map items) and saves anonymized `.json` data locally.
   - **Simulator**: standalone program that runs without Skua, without the game, and without an internet connection. Reads the collected game database (`gamedata.json` + `QuestData.json`) and a scenario configuration.
2. **Three-Tier Data Fidelity:** generic fallback (default) → captured data (from the Collector + `QuestData.json`) → scenario override. Any synthesized/fallback data is explicitly flagged in **Violet** in the UI diagnostics and execution reports.
3. **Virtual Clock**: `Sleep`/`Wait`/`Thread.Sleep`/`Task.Delay`/`DateTime`/`Stopwatch` advance simulated time deterministically (via seed), supporting minutes, hours, days, weeks, and **Unlimited (`0`)** virtual & wall-clock real time.
4. **Multi-Client (Army), Live Loadouts & Potions (Phase 4 — next)**: up to **20 concurrent simulated players** sharing rooms/monsters/sync files, scenario role classes (`Solo`, `Farm`, `Dodge`, `Boss`) with live mid-run class & enhancement swaps (CBO/`CoreBots`), full Potion/Elixir/Tonic/Scroll simulation (e.g. `UltrasLW`), and active aura/boss mechanics.
5. **Data Sharing:** starts **local-only (Option A)**, followed by **manual export/merge (Option B)** via Git. Automatic server (Option C) only if user volume justifies it. Strictly opt-in and anonymized (never stores account names, player names, personal inventory, chat, or IP addresses; redacts `unm`/`uid`/`casterId`).
6. **Bug Report Workflow:** structured bug report template plus a planned "Generate diagnostic file" option in scripts that converts directly into a simulator scenario (Phase 5).
7. **Collector stays passive (2026-11-20):** it never sends packets. Official drop lists (`monsterDrops`) are captured only when another tool (e.g. the Weekly/Merge generators) requests them; an active request mode needs an explicit decision.
8. **Verify before parsing (2026-11-20):** packet keys are confirmed from anonymized live samples (`packet_schemas.json`) before parsers are written, and tests use those real samples — an invented test packet once hid that monster levels were never captured.

## Phases (see `PLAN.md` for details)
1. **MVP**: `//cs_include` loader + Roslyn compiler against real Skua DLLs + core `FakeScriptInterface` + virtual clock + execution trace (**✅ Done**)
2. **Data-Driven World**: import `QuestData.json` + Collector database (`gamedata.json`) + JSON scenario format + AQW `StatEngine` (**✅ Done**)
3. **Visual Interface (`SkuaSim.UI`)**: standalone Avalonia 12 dashboard ("Skua without Flash") with multi-resolution scaling, live client/AQW stats state, real AQW gear & enhancement scenario editor, Skua-faithful `Script Options` + `.cfg` import, color-coded Logs/Errors/Trace, and `.md`/`.json` report export (**✅ Done**)
3.4. **Simulator Robustness**: `KillQuest` AST hints + on-demand monsters on synthesized maps, no-progress (`Stalled`) detector, process-stable hashing + cross-process determinism test, `CLAUDE.md` (**✅ Done 2026-11-20**)
3.5. **Collector Corrections & Schema v2/v3**: protocol verification sampling, monster level/race/wDPS fix, drop attribution fix, schema v2 (migration + backup, quest-gated rates, passive official drops, map-item quests, reward types), schema v3 (real XP/gold/rep per kill, respawn times, quest-set kill counting, room occupancy, quest base rewards, faction names) (**🔄 Steps 1–4 done; Step 5 next**)
3.6. **Simulator Consumes Real Data + AE Calendar**: measured respawn/XP/gold/rep, reputation ranks, house-item accept requirements, quest-gated map items, wDPS damage estimate, scenario `startDate`, daily/weekly/monthly resets (00:00 US Eastern), AE calendar server boosts (`calendar-update`) (**✅ Done 2026-11-21**)
4. **Multi-Client (Army), Live Loadouts, Potions & Aura/Boss Combat**: up to 20 concurrent clients sharing rooms/monsters/party, Army sync coordination, `Solo`/`Farm`/`Dodge`/`Boss` role classes + live class/enhancement swaps, Potions/Elixirs/Tonics/Scrolls (`UltrasLW`), and active aura/buff & Ultra boss simulation (**🔜 After 3.5; architecture to be discussed first**)
5. **Fault Injection, Assertions & Templates**: lag/disconnect/drop failure injection, stall/infinite-loop detector, scenario assertions, diagnostic generator & bug report template (**🔜 Planned**)
6. **Replay & Public Release**: real session log replay + external developer distribution packaging (CLI flags, official Skua parity, starter DB, license, release archive) (**🔜 Planned**)

Execution order: **Collector (Plugin)** ✅ → **Simulator MVP & Data World** ✅ → **Visual UI (`SkuaSim.UI`) & Polish** ✅ → **Simulator Robustness (3.4)** ✅ → **Collector Corrections & Schema v2/v3 (3.5)** 🔄 → **Simulator Real Data & AE Calendar (3.6)** ✅ → **Multi-Client (Army) & Aura/Boss Combat** 🔜 → **Fault Injection & Assertions** → **Replay & Release Packaging**.

## Environment Discovered
- **.NET SDK 10.0.400** installed.
- **VibeSkua** (Avalonia fork of Skua):
  - Application: `%LOCALAPPDATA%\VibeSkua\current\` (`VibeSkua.exe`, installed via Velopack)
    - API Assemblies (`Assemblies\`): `Skua.Core.dll`, `Skua.Core.Interfaces.dll`, `Skua.Core.Models.dll`, `Skua.Core.Utils.dll`, `Skua.Core.Generators.dll`, `Westwind.Scripting.dll`, `CommunityToolkit.Mvvm.dll`, `Newtonsoft.Json.dll`
    - Built-in plugins (`plugins\`): `ButlerV5.dll`, `Skua.Plugin.Cosmetics.dll`, `Skua.Plugin.DailyTracker.dll`
  - User Data: `%APPDATA%\VibeSkua\` (`Scripts` with 1,992 `.cs` scripts, `options`, `plugins`, `QuestData.json`)
- **`%APPDATA%\Skua\`**: actively used by `ButlerV5` (`character_locations\`, `army-control-jump.json`) and contains `plugins\`.

## Confirmed Decisions
- **Target Compatibility:** Both **VibeSkua** and **official Skua** (sharing the `Skua.Core.*` architecture without bundling third-party client binaries).
- **Project Location:** Everything lives self-contained inside this folder (`SkuaSim\`).
- **Language:** All code, UI strings, logs, tests, and documentation in **English**.
- **Reference Scripts:** `%APPDATA%\VibeSkua\Scripts` (1,992 `.cs` scripts accessible for validation).

## Implementation Status
### Stage 1 — Collector (`SkuaSim.Collector`) ✅ Completed, Live-Audited & Installed
- **Source Code:** `src/SkuaSim.Collector/` (`SkuaSimCollectorPlugin.cs`, `Parsing/PacketCollectorEngine.cs`, `Diagnostics/PacketSchemaSampler.cs`, `Storage/GameDatabaseStore.cs`, `Models/CapturedDataModels.cs`)
- **Automated Tests:** `tests/SkuaSim.Collector.Tests/` (**10/10 passing**; suites 7–10 use real anonymized packets and cover sampling privacy and schemas v2/v3)
- **Live Protocol Hardening:** `ct.sara[].actionResult` parsing (monster hits/dodges + HoT/DoT `"typ": "d"`), VHL self-damage guard, instant `updateClass`/`equipItem` class sync + 350ms debounced refresh, `"stk"` aura stack counts, `"Attack1,Attack2"` auto-attack filtering, legacy noise cleanup, `casterId`/`uid`/`unm` redaction, and cross-session `PacketSchemaSampler` (`packet_schemas.json`, up to 12 distinct shapes per category, Mutex-merged).
- **Installed Binary Locations** (last deployed 2026-11-24: collector 1.3.0, SHA-256 prefix EB505D2CFC44 (version without commit hash, built before the commit) — server warning/message templates (`GameDatabase.ServerMessages`); 1.2.0 (2026-11-23): per-match PvP records with the items received (`CapturedPvpStats.Matches`); 1.1.0 (2026-11-22): schema v3 + account-switch reset, cell numbers (mtcid), measured drop sizes (dQty), class stats by level band/rank/gear, PvP events/scores, Export for Sharing, no built-in mirror (this PC lists the workspace in `mirror_paths.txt` in the collector data folder), SHA-256 prefix 747CCBF386BA (1.1.0+36120d3, Release with the local path mapped out); deploy only with all clients closed — the DLL is locked while they run):
  - `%APPDATA%\VibeSkua\plugins\SkuaSim.Collector.dll`
  - `%LOCALAPPDATA%\VibeSkua\current\plugins\SkuaSim.Collector.dll`
  - `%APPDATA%\Skua\plugins\SkuaSim.Collector.dll`
  - `dist\plugins\SkuaSim.Collector.dll`
- **Collected Data Locations:**
  - Primary: `%APPDATA%\VibeSkua\SkuaSim\gamedata.json` (+ `packet_schemas.json`)
  - Automatic mirror in workspace: `data\gamedata.json` (+ `data\packet_schemas.json`)
  - Schema v2 since 2026-11-20 (migrated from v1: 52 impossible drop records removed, 369 temporary quest-required drops marked quest-gated). Backups: `SkuaSim\gamedata.v1-backup-20261120-203127.json` and `SkuaSim\backups\gamedata.pre-v2-manual-20261120-173107.json`. Schema v3 since 2026-11-21 (v2 backup `gamedata.v2-backup-20261121-010501.json`, nothing deleted).
  - AE calendar snapshot: `data\ae_calendar.json` (from `skuasim calendar-update`; public artix.com data, read offline by the simulator).

### Stage 2 — Simulator Core & CLI (`SkuaSim.Core` + `SkuaSim.Cli`) ✅ Completed & Verified
- **Source Code:**
  - `src/SkuaSim.Core/Hosting/`: `SkuaInstallation.cs`, `SkuaAssemblyResolver.cs`
  - `src/SkuaSim.Core/Compilation/`: `ScriptDirectives.cs`, `TimeHookRewriter.cs`, `ReferenceCatalog.cs`, `ScriptCompiler.cs`
  - `src/SkuaSim.Core/Runtime/`: `SimHooks.cs`, `SimClock.cs`, `SimulationTrace.cs`, `SimContext.cs`, `SimulationSession.cs`, `SimulationRunner.cs`, `SimulationReport.cs`
  - `src/SkuaSim.Core/Proxies/`: `ProxyHub.cs` (`DispatchProxy` + `DynamicMethod` non-virtual default interface method invoker)
  - `src/SkuaSim.Core/Scenarios/`: `Scenario.cs`, `AqwBoostGearCatalog.cs`, `EnhancementUnlockCatalog.cs`
  - `src/SkuaSim.Core/World/`: `SimWorld.cs`, `SimWorld.StatsAndCombat.cs`, `StatEngine.cs`
  - `src/SkuaSim.Core/Api/`: `BotApi.cs`, `PlayerMapApis.cs`, `CombatKillHuntApis.cs`, `InventoryBankDropsApis.cs`, `QuestsShopsWaitApis.cs`, `SystemSupportApis.cs` (including `BoostsApi` and `ConfigApi`)
  - `src/SkuaSim.Cli/`: `Program.cs` (`skuasim db-stats`, `skuasim compile`, `skuasim run`)
- **Automated Tests:** `tests/SkuaSim.Core.Tests/` (**20/20 passing**; includes the PvP match test (19), the server warnings test (20), the cross-process determinism test, the Stalled detector, schema v2 drop rules and real-data/calendar consumption)
- **Sample Scenarios:** `scenarios/farmerjoe_lv1_mage.json`, `scenarios/leveling_lv10.json`, `scenarios/eldersblood_done.json`, `scenarios/swagtokena_example.json`, `scenarios/VHL_farm_max lvl.json`, `scenarios/UnlockForgeEnhancements_scenario.json` (all defaulted to `maxRealSeconds: 0` = infinite).

### Stage 3 — Visual Desktop Dashboard (`SkuaSim.UI`) & Documentation ✅ Completed & Verified
- **Source Code:** `src/SkuaSim.UI/` (Avalonia 12.1.6 + CommunityToolkit.Mvvm 8.4.2)
  - Adaptive multi-resolution `LayoutTransformControl` (`Auto`, HD 720p, Full-HD 1080p, 2K 1440p, 4K 2160p, 8K 4320p) + standardized typography scale in `App.axaml`.
  - Fixed, overlap-proof `GridSplitter` layouts across `MainWindow`, `ClientStateView`, `ScenarioEditorView`, and `DiagnosticsView` (with `ColumnDefinition`/`RowDefinition` min-bounds, `ZIndex="20"`, and hover highlights).
  - Live Client view ("Skua without Flash") + Scenario Editor with Databank Class dropdown, live real-stats preview, real AQW boosted item pickers per slot (`Weapon`/`Armor`/`Helm`/`Cape`/`Pet`), 2x consumable/server boosts, Awe & Forge checklists (with master + per-category `All Weapon`/`All Helm`/`All Cape`), customizable virtual duration (minutes–weeks/unlimited) & infinite real-time limit (`0 = ∞`), and realistic UI (unrealistic overrides & god mode removed).
  - Skua-faithful `Script Options` panel: real checkboxes for `System.Boolean`, `ComboBox` for enums, `TextBox` for strings/numbers, whitespace explanation banners (`Option<string>(" ", ...)`), `TabControl` grouping in exact `MultiOptions` order, **Reset Defaults**, and 1-click **Load Skua .cfg**.
  - Color-coded Logs, Errors & Diagnostics, and searchable API Trace with signature **Violet (`SYNTH`)** styling for synthesized/invented data.
- **Documentation:** `README.md` created at repository root (revised 2026-11-20 to match the code), `CLAUDE.md` with commands and invariants.

### Stage 3.4 — Simulator Robustness ✅ Completed (2026-11-20, Claude Code session)
- `ExtractAstHints` reads `KillQuest(id, map, "name" | new[] {...})`; `SimWorld.EnsureRequestedMonster` spawns a monster requested by name on a synthesized map. The `/lair` case that looped for 38 virtual hours now completes in ~29 virtual minutes.
- No-progress watchdog (`SimContext.CheckProgress`, virtual-time based): outcome `Stalled`, `limits.maxNoProgressMinutes` (default 60), UI field and banner, CLI `--max-no-progress-min`, exit code 3.
- `World/StableHash` replaces `string.GetHashCode()` for monster/faction IDs; Core test 13 compares two separate processes.

### Stage 3.5 — Collector Corrections & Schemas v2/v3 🔄 Steps 1–4 completed (2026-11-20/21)
- **Audit** of the live `gamedata.json`: monster level never captured (0/821), quest rewards credited as monster drops (49 impossible records), diluted quest-gated rates (82 drops < 1 %), no real XP/gold per kill or respawn data.
- **Step 1:** protocol verification sampling (allowlisted world packets, command census, value-free skeletons; house/inventory never sampled). Verified keys are listed in PLAN.md.
- **Step 2:** `moveToArea` parsed with the real keys (`monBranch.iLvl`, `sRace`, `wDPS`); drops credited only to the monster killed in the last 5 s; quest turn-in rewards excluded; `getQuests` rewards with type/rate.
- **Step 3:** schema v2 — dated backup + migration, quest-gated rates measured with the quest active (`Bot.Quests.Active`, RAM only), passive `monsterDrops`, quest-gated map items, nullable `EstimatedDropChance`; the simulator never rolls an unknown rate as 0 % and respects quest gating.
- **Step 4 (schema v3, 2026-11-21):** from a live session — `addGoldExp` totals include the bonus (`bonusExp`/`bonusGold`/`bonusRep`), so base XP/gold/rep per kill are stored per monster (zeros from capped characters counted apart); respawn = kill → `["respawnMon", "-1", MonMapID]`, per spawn and per monster; the client has no room cap (`world.areaData.intMaxPlayers` missing in 58/58 probes), replaced by peak `Bot.Map.PlayerCount`; quest-gated denominators no longer double-count kills made with several required quests active (`KillsByActiveQuestSet`). Migration v2 → v3 dry-run on live data: 24 monsters recomputed, nothing deleted.
- **Live v3 session (2026-11-21):** base gold = 3 × monster level and XP = 10 × level on every measured monster (lv 1–90); respawn ~2.4 s (269 samples); `ccqr.rewardObj` is the quest's base reward; the +100 % gold seen on 2026-11-20 was the AE calendar's Double Gold boost.
- **Next:** Step 5 — see PLAN.md "Next Implementations & Open Decisions".

### Stage 3.6 — Simulator Consumes Real Data + AE Calendar ✅ Completed (2026-11-21)
- Respawn, kill XP/gold/rep and quest rep/CP from collected data (rules by level and official ~4 s respawn as flagged fallbacks); reputation gain with AQW's rank table (rank 10 = 302,500; the old table was wrong); accept requirements count house items (45 quests, e.g. Prime Fiend Shard → 9555); quest-gated `GetMapItem`; damage estimate from `wDPS` calibrated by level; captured quests used when missing from QuestData.
- Scenario `startDate`, daily reset 00:00 US Eastern / weekly Friday / monthly 1st (times from VibeSkua.Manager), AE calendar server boosts (`skuasim calendar-update` / `calendar-show`, scenario `boosts.aeCalendarServerBoosts`, CLI `--ae-calendar`, UI checkbox). Core test 15.
- Collector (additive v3 fields): quest `ObservedReward` from `ccqr`, faction ID → name catalog (no player reputation).
- Reward rules from the user (2026-11-21): XP = quests + kills, CP = kills, gold = quests + kills + selling, rep = quests; boosts add up (highest same-type gear bonus + consumable +100 % + server +100 %). Selling returns 25 % of the shop price (flagged rule). Collector key census + new-key-set sampling to find the CP field; collector 40A9EA285999 installed 2026-11-22 (key census, levelUp XP table, CP per kill split by class rank and level gap, sell prices). CP rule in the simulator: 230 + 25 x class rank; level-gap penalty verified 2026-11-22: 100 % up to 10 levels below, -10 %/level from 11, 5 % floor, rounded up.
