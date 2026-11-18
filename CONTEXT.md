# SkuaSim — Project Context

> This document continues the original design conversation started in the ArmyFarmerJoeDoAll workspace.
> Origin conversation: `2d6d8a65-4a2d-4461-afda-7f4efa4981b4` (Antigravity).
> Detailed plan artifact: `C:\Users\lucas\.gemini\antigravity\brain\2d6d8a65-4a2d-4461-afda-7f4efa4981b4\plano_ambiente_testes.md`

## Objective
An **offline, reproducible test environment** for **any Skua/VibeSkua script**, designed to reproduce bug reports in detail and validate fixes. It does **not** recreate AQW or use copyrighted game assets/servers. Instead, it simulates the **Skua API** (`IScriptInterface`: Player, Map, Monsters, Inventory, Quests, Drops, Shops, Wait...) backed by an in-memory simulated game world.

## Key Decisions
1. **Two Separate Components:**
   - **Collector (Plugin)**: runs passively inside Skua/VibeSkua alongside any script (or manual play). It listens to packets already received by the client (maps, monsters, HP, observed damage, drops, shops, quests, map items) and saves anonymized `.json` data locally.
   - **Simulator**: standalone program that runs without Skua, without the game, and without an internet connection. Reads the collected game database and a bug scenario file.
2. **Three-Tier Data Fidelity:** generic fallback (default) → captured data (from the Collector) → scenario override. Any synthesized/fallback data is explicitly flagged in the execution report.
3. **Virtual Clock**: `Sleep`/`Wait` advance simulated time so multi-hour farms execute in seconds and remain deterministic (via seed).
4. **Multi-Client (Army)**: N simulated players running concurrently in the same simulated world to test Army scripts and synchronization.
5. **Data Sharing:** starts **local-only (Option A)**, followed by **manual export/merge (Option B)** via Git. Automatic server (Option C) only if user volume justifies it. Strictly opt-in and anonymized (never stores account names, player names, personal inventory, chat, or IP addresses). Merging sums counters, tracks timestamps, and filters outliers.
6. **Bug Report Workflow:** structured bug report template (script/version, options, account table, relevant state, actual vs. expected behavior, frequency, per-account logs) plus a planned "Generate diagnostic file" option in scripts that converts directly into a simulator scenario.

## Phases (see `PLAN.md` for details)
1. **MVP**: `//cs_include` loader + Roslyn compiler against real Skua DLLs + core `FakeScriptInterface` + virtual clock + execution trace (✅ Done)
2. **Data-Driven World**: import `QuestData.json` + Collector database (`gamedata.json`) + JSON scenario format (✅ Done)
3. **Visual Interface (`SkuaSim.UI`)**: standalone Avalonia dashboard ("Skua without Flash") with live state, logs, and error/trace inspector
4. **Multi-Client (Army)**: N clients, shared rooms, party & synchronization testing
5. **Fault Injection, Assertions & Templates**: lag/disconnect/drop failure injection, stall/infinite-loop detector, scenario assertions, diagnostic generator & bug report template
6. **Replay & Public Release**: real session log replay + external developer distribution packaging (CLI flags, official Skua parity, starter DB, README, license)

Execution order: **Collector (Plugin)** ✅ → **Simulator MVP & Data World** ✅ → **Visual UI / Multi-Client / Asserts / Release Packaging**.

## Environment Discovered (2026-11-18)
- **.NET SDK 10.0.400** installed.
- **VibeSkua** (Avalonia fork of Skua):
  - Application: `C:\Users\lucas\AppData\Local\VibeSkua\current\` (`VibeSkua.exe`, installed via Velopack)
    - API Assemblies (`Assemblies\`): `Skua.Core.dll`, `Skua.Core.Interfaces.dll`, `Skua.Core.Models.dll`, `Skua.Core.Utils.dll`, `Skua.Core.Generators.dll`, `Westwind.Scripting.dll`, `CommunityToolkit.Mvvm.dll`, `Newtonsoft.Json.dll`
    - Built-in plugins (`plugins\`): `ButlerV5.dll`, `Skua.Plugin.Cosmetics.dll`, `Skua.Plugin.DailyTracker.dll`
  - User Data: `C:\Users\lucas\AppData\Roaming\VibeSkua\` (`Scripts` with 1,992 `.cs` scripts, `options`, `plugins`, `QuestData.json`, `player1..4.txt` logs)
- **`C:\Users\lucas\AppData\Roaming\Skua\`**: actively used by `ButlerV5` (`character_locations\`, `army-control-jump.json`) and contains `plugins\`.

## Confirmed Decisions (2026-11-18)
- **Target Compatibility:** Both **VibeSkua** and **official Skua** (sharing the `Skua.Core.*` architecture without requiring a separate official Skua installation).
- **Project Location:** Everything lives self-contained inside this folder (`SkuaSim\`).
- **Language:** All code, UI strings, logs, tests, and documentation in **English**.
- **Reference Scripts:** `C:\Users\lucas\AppData\Roaming\VibeSkua\Scripts` (1,992 `.cs` scripts accessible for validation).

## Implementation Status
### Stage 1 — Collector (`SkuaSim.Collector`) ✅ Completed & Installed
- **Source Code:** `src/SkuaSim.Collector/` (`SkuaSimCollectorPlugin.cs`, `Parsing/PacketCollectorEngine.cs`, `Storage/GameDatabaseStore.cs`, `Models/CapturedDataModels.cs`)
- **Automated Tests:** `tests/SkuaSim.Collector.Tests/`
- **Installed Binary Locations:**
  - `C:\Users\lucas\AppData\Roaming\VibeSkua\plugins\SkuaSim.Collector.dll`
  - `C:\Users\lucas\AppData\Local\VibeSkua\current\plugins\SkuaSim.Collector.dll`
  - `C:\Users\lucas\AppData\Roaming\Skua\plugins\SkuaSim.Collector.dll`
  - `dist\plugins\SkuaSim.Collector.dll`
- **Collected Data Locations:**
  - Primary: `C:\Users\lucas\AppData\Roaming\VibeSkua\SkuaSim\gamedata.json`
  - Automatic mirror in this workspace: `C:\Users\lucas\OneDrive\Área de Trabalho\SkuaSim\data\gamedata.json`
  - Multi-client (Army) & multi-session safe: merges incremental deltas using a global cross-process `Mutex` (`Global\SkuaSim_GameDatabase_Mutex_v1`) and atomic `.tmp` → `.bak` → `.json` file replacement.

### Stage 2 — Simulator Phase 1 & 2 (`SkuaSim.Core` + `SkuaSim.Cli`) ✅ Completed & Verified
- **Source Code:**
  - `src/SkuaSim.Core/Hosting/`: `SkuaInstallation.cs`, `SkuaAssemblyResolver.cs`
  - `src/SkuaSim.Core/Compilation/`: `ScriptDirectives.cs`, `TimeHookRewriter.cs`, `ReferenceCatalog.cs`, `ScriptCompiler.cs`
  - `src/SkuaSim.Core/Runtime/`: `SimHooks.cs`, `SimClock.cs`, `SimulationTrace.cs`, `SimContext.cs`, `SimulationRunner.cs`, `SimulationReport.cs`
  - `src/SkuaSim.Core/Proxies/`: `ProxyHub.cs` (`DispatchProxy` + `DynamicMethod` non-virtual default interface method invoker)
  - `src/SkuaSim.Core/World/`: `SimWorld.cs` (3-tier fidelity world: Scenario → Collector `gamedata.json` + `QuestData.json` → Synthesized with AST hints and report warnings)
  - `src/SkuaSim.Core/Api/`: `BotApi.cs`, `PlayerMapApis.cs`, `CombatKillHuntApis.cs`, `InventoryBankDropsApis.cs`, `QuestsShopsWaitApis.cs`, `SystemSupportApis.cs`
  - `src/SkuaSim.Cli/`: `Program.cs` (`skuasim db-stats`, `skuasim compile`, `skuasim run`)
- **Automated Tests:** `tests/SkuaSim.Core.Tests/`
- **Sample Scenarios:** `scenarios/swagtokena_example.json`
