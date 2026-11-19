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

## Phases (see `PLAN.md` for details)
1. **MVP**: `//cs_include` loader + Roslyn compiler against real Skua DLLs + core `FakeScriptInterface` + virtual clock + execution trace (**✅ Done**)
2. **Data-Driven World**: import `QuestData.json` + Collector database (`gamedata.json`) + JSON scenario format + AQW `StatEngine` (**✅ Done**)
3. **Visual Interface (`SkuaSim.UI`)**: standalone Avalonia 12 dashboard ("Skua without Flash") with multi-resolution scaling, live client/AQW stats state, real AQW gear & enhancement scenario editor, Skua-faithful `Script Options` + `.cfg` import, color-coded Logs/Errors/Trace, and `.md`/`.json` report export (**✅ Done**)
4. **Multi-Client (Army), Live Loadouts, Potions & Aura/Boss Combat**: up to 20 concurrent clients sharing rooms/monsters/party, Army sync coordination, `Solo`/`Farm`/`Dodge`/`Boss` role classes + live class/enhancement swaps, Potions/Elixirs/Tonics/Scrolls (`UltrasLW`), and active aura/buff & Ultra boss simulation (**🔜 Next**)
5. **Fault Injection, Assertions & Templates**: lag/disconnect/drop failure injection, stall/infinite-loop detector, scenario assertions, diagnostic generator & bug report template (**🔜 Planned**)
6. **Replay & Public Release**: real session log replay + external developer distribution packaging (CLI flags, official Skua parity, starter DB, license, release archive) (**🔜 Planned**)

Execution order: **Collector (Plugin)** ✅ → **Simulator MVP & Data World** ✅ → **Visual UI (`SkuaSim.UI`) & Polish** ✅ → **Multi-Client (Army) & Aura/Boss Combat** 🔜 → **Fault Injection & Assertions** → **Replay & Release Packaging**.

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
- **Automated Tests:** `tests/SkuaSim.Collector.Tests/` (**6/6 passing**)
- **Live Protocol Hardening:** `ct.sara[].actionResult` parsing (monster hits/dodges + HoT/DoT `"typ": "d"`), VHL self-damage guard, instant `updateClass`/`equipItem` class sync + 350ms debounced refresh, `"stk"` aura stack counts, `"Attack1,Attack2"` auto-attack filtering, legacy noise cleanup, `casterId`/`uid`/`unm` redaction, and cross-session `PacketSchemaSampler` (`packet_schemas.json`, up to 12 distinct shapes per category, Mutex-merged).
- **Installed Binary Locations:**
  - `C:\Users\lucas\AppData\Roaming\VibeSkua\plugins\SkuaSim.Collector.dll`
  - `C:\Users\lucas\AppData\Local\VibeSkua\current\plugins\SkuaSim.Collector.dll`
  - `C:\Users\lucas\AppData\Roaming\Skua\plugins\SkuaSim.Collector.dll`
  - `dist\plugins\SkuaSim.Collector.dll`
- **Collected Data Locations:**
  - Primary: `C:\Users\lucas\AppData\Roaming\VibeSkua\SkuaSim\gamedata.json` (+ `packet_schemas.json`)
  - Automatic mirror in workspace: `data\gamedata.json` (+ `data\packet_schemas.json`)

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
- **Automated Tests:** `tests/SkuaSim.Core.Tests/` (**11/11 passing**)
- **Sample Scenarios:** `scenarios/farmerjoe_lv1_mage.json`, `scenarios/leveling_lv10.json`, `scenarios/eldersblood_done.json`, `scenarios/swagtokena_example.json` (all defaulted to `maxRealSeconds: 0` = infinite).

### Stage 3 — Visual Desktop Dashboard (`SkuaSim.UI`) & Documentation ✅ Completed & Verified
- **Source Code:** `src/SkuaSim.UI/` (Avalonia 12.1.6 + CommunityToolkit.Mvvm 8.4.2)
  - Adaptive multi-resolution `LayoutTransformControl` (`Auto`, HD 720p, Full-HD 1080p, 2K 1440p, 4K 2160p, 8K 4320p) + standardized typography scale in `App.axaml`.
  - Fixed, overlap-proof `GridSplitter` layouts across `MainWindow`, `ClientStateView`, `ScenarioEditorView`, and `DiagnosticsView` (with `ColumnDefinition`/`RowDefinition` min-bounds, `ZIndex="20"`, and hover highlights).
  - Live Client view ("Skua without Flash") + Scenario Editor with Databank Class dropdown, live real-stats preview, real AQW boosted item pickers per slot (`Weapon`/`Armor`/`Helm`/`Cape`/`Pet`), 2x consumable/server boosts, Awe & Forge checklists (with master + per-category `All Weapon`/`All Helm`/`All Cape`), customizable virtual duration (minutes–weeks/unlimited) & infinite real-time limit (`0 = ∞`), and realistic UI (unrealistic overrides & god mode removed).
  - Skua-faithful `Script Options` panel: real checkboxes for `System.Boolean`, `ComboBox` for enums, `TextBox` for strings/numbers, whitespace explanation banners (`Option<string>(" ", ...)`), `TabControl` grouping in exact `MultiOptions` order, **Reset Defaults**, and 1-click **Load Skua .cfg**.
  - Color-coded Logs, Errors & Diagnostics, and searchable API Trace with signature **Violet (`SYNTH`)** styling for synthesized/invented data.
- **Documentation:** `README.md` created at repository root.
