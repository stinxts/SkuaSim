# SkuaSim — Project Context

> This document continues the original design conversation started in the ArmyFarmerJoeDoAll workspace.
> Origin conversation: `2d6d8a65-4a2d-4461-afda-7f4efa4981b4` (Antigravity).
> Detailed plan artifact: `C:\Users\lucas\.gemini\antigravity\brain\2d6d8a65-4a2d-4461-afda-7f4efa4981b4\plano_ambiente_testes.md`

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
3.5. **Collector Corrections & Schema v2**: protocol verification sampling, monster level/race/wDPS fix, drop attribution fix, schema v2 (migration + backup, quest-gated rates, passive official drops, map-item quests, reward types, experimental room cap) (**🔄 Steps 1–3 done; Step 4 next: real XP/gold per kill + respawn times**)
4. **Multi-Client (Army), Live Loadouts, Potions & Aura/Boss Combat**: up to 20 concurrent clients sharing rooms/monsters/party, Army sync coordination, `Solo`/`Farm`/`Dodge`/`Boss` role classes + live class/enhancement swaps, Potions/Elixirs/Tonics/Scrolls (`UltrasLW`), and active aura/buff & Ultra boss simulation (**🔜 After 3.5; architecture to be discussed first**)
5. **Fault Injection, Assertions & Templates**: lag/disconnect/drop failure injection, stall/infinite-loop detector, scenario assertions, diagnostic generator & bug report template (**🔜 Planned**)
6. **Replay & Public Release**: real session log replay + external developer distribution packaging (CLI flags, official Skua parity, starter DB, license, release archive) (**🔜 Planned**)

Execution order: **Collector (Plugin)** ✅ → **Simulator MVP & Data World** ✅ → **Visual UI (`SkuaSim.UI`) & Polish** ✅ → **Simulator Robustness (3.4)** ✅ → **Collector Corrections & Schema v2 (3.5)** 🔄 → **Multi-Client (Army) & Aura/Boss Combat** 🔜 → **Fault Injection & Assertions** → **Replay & Release Packaging**.

## Environment Discovered
- **.NET SDK 10.0.400** installed.
- **VibeSkua** (Avalonia fork of Skua):
  - Application: `C:\Users\lucas\AppData\Local\VibeSkua\current\` (`VibeSkua.exe`, installed via Velopack)
    - API Assemblies (`Assemblies\`): `Skua.Core.dll`, `Skua.Core.Interfaces.dll`, `Skua.Core.Models.dll`, `Skua.Core.Utils.dll`, `Skua.Core.Generators.dll`, `Westwind.Scripting.dll`, `CommunityToolkit.Mvvm.dll`, `Newtonsoft.Json.dll`
    - Built-in plugins (`plugins\`): `ButlerV5.dll`, `Skua.Plugin.Cosmetics.dll`, `Skua.Plugin.DailyTracker.dll`
  - User Data: `C:\Users\lucas\AppData\Roaming\VibeSkua\` (`Scripts` with 1,992 `.cs` scripts, `options`, `plugins`, `QuestData.json`)
- **`C:\Users\lucas\AppData\Roaming\Skua\`**: actively used by `ButlerV5` (`character_locations\`, `army-control-jump.json`) and contains `plugins\`.

## Confirmed Decisions
- **Target Compatibility:** Both **VibeSkua** and **official Skua** (sharing the `Skua.Core.*` architecture without bundling third-party client binaries).
- **Project Location:** Everything lives self-contained inside this folder (`SkuaSim\`).
- **Language:** All code, UI strings, logs, tests, and documentation in **English**.
- **Reference Scripts:** `C:\Users\lucas\AppData\Roaming\VibeSkua\Scripts` (1,992 `.cs` scripts accessible for validation).

## Implementation Status
### Stage 1 — Collector (`SkuaSim.Collector`) ✅ Completed, Live-Audited & Installed
- **Source Code:** `src/SkuaSim.Collector/` (`SkuaSimCollectorPlugin.cs`, `Parsing/PacketCollectorEngine.cs`, `Diagnostics/PacketSchemaSampler.cs`, `Storage/GameDatabaseStore.cs`, `Models/CapturedDataModels.cs`)
- **Automated Tests:** `tests/SkuaSim.Collector.Tests/` (**9/9 passing**; suites 7–9 use real anonymized packets and cover sampling privacy and schema v2)
- **Live Protocol Hardening:** `ct.sara[].actionResult` parsing (monster hits/dodges + HoT/DoT `"typ": "d"`), VHL self-damage guard, instant `updateClass`/`equipItem` class sync + 350ms debounced refresh, `"stk"` aura stack counts, `"Attack1,Attack2"` auto-attack filtering, legacy noise cleanup, `casterId`/`uid`/`unm` redaction, and cross-session `PacketSchemaSampler` (`packet_schemas.json`, up to 12 distinct shapes per category, Mutex-merged).
- **Installed Binary Locations** (last deployed 2026-11-20 with schema v2; deploy only with all clients closed — the DLL is locked while they run):
  - `C:\Users\lucas\AppData\Roaming\VibeSkua\plugins\SkuaSim.Collector.dll`
  - `C:\Users\lucas\AppData\Local\VibeSkua\current\plugins\SkuaSim.Collector.dll`
  - `C:\Users\lucas\AppData\Roaming\Skua\plugins\SkuaSim.Collector.dll`
  - `dist\plugins\SkuaSim.Collector.dll`
- **Collected Data Locations:**
  - Primary: `C:\Users\lucas\AppData\Roaming\VibeSkua\SkuaSim\gamedata.json` (+ `packet_schemas.json`)
  - Automatic mirror in workspace: `data\gamedata.json` (+ `data\packet_schemas.json`)
  - Schema v2 since 2026-11-20 (migrated from v1: 52 impossible drop records removed, 369 temporary quest-required drops marked quest-gated). Backups: `SkuaSim\gamedata.v1-backup-20261120-203127.json` and `SkuaSim\backups\gamedata.pre-v2-manual-20261120-173107.json`.

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
- **Automated Tests:** `tests/SkuaSim.Core.Tests/` (**14/14 passing**; includes the cross-process determinism test, the Stalled detector and schema v2 drop rules)
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

### Stage 3.5 — Collector Corrections & Schema v2 🔄 Steps 1–3 completed (2026-11-20)
- **Audit** of the live `gamedata.json`: monster level never captured (0/821), quest rewards credited as monster drops (49 impossible records), diluted quest-gated rates (82 drops < 1 %), no real XP/gold per kill or respawn data.
- **Step 1:** protocol verification sampling (allowlisted world packets, command census, value-free skeletons; house/inventory never sampled). Verified keys are listed in PLAN.md.
- **Step 2:** `moveToArea` parsed with the real keys (`monBranch.iLvl`, `sRace`, `wDPS`); drops credited only to the monster killed in the last 5 s; quest turn-in rewards excluded; `getQuests` rewards with type/rate.
- **Step 3:** schema v2 — dated backup + migration, quest-gated rates measured with the quest active (`Bot.Quests.Active`, RAM only), passive `monsterDrops`, quest-gated map items, experimental room cap, nullable `EstimatedDropChance`; the simulator never rolls an unknown rate as 0 % and respects quest gating.
- **Next:** Step 4 (real XP/gold/CP/rep per kill from `addGoldExp`, per-monster respawn from `respawnMon`, confirm the room-cap path), then Step 5 and simulator consumption — see PLAN.md "Next Implementations & Open Decisions".
