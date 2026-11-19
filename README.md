# SkuaSim

**SkuaSim** is an offline, deterministic simulation and testing environment for **Skua** and **VibeSkua** C# scripts.

Instead of running AdventureQuest Worlds (AQW), Flash, or connecting to live game servers, SkuaSim compiles real Skua scripts with Roslyn and executes them against an in-memory simulation of Skua's `IScriptInterface` (`Player`, `Map`, `Monsters`, `Combat`, `Kill`, `Hunt`, `Inventory`, `Bank`, `Drops`, `Quests`, `Shops`, `Reputation`, `Boosts`, `Wait`, `Config`, `Send`, etc.) backed by anonymized game metadata passively captured during normal play.

> **Disclaimer:** SkuaSim does **not** redistribute AQW game assets, servers, or Skua/VibeSkua binaries. It resolves your local Skua/VibeSkua assemblies at runtime and operates entirely offline.

---

## Architecture & How It Works

SkuaSim is split into two independent halves: a **passive Collector plugin** that runs inside your live Skua/VibeSkua client, and a **standalone offline Simulator** (Core + Desktop UI + CLI) that runs without Flash or an internet connection.

```mermaid
flowchart LR
    A["Live Skua / VibeSkua"] -->|"Passive FlashCall &amp; Packet Listener"| B["SkuaSim.Collector.dll"]
    B -->|"Mutex-safe Delta Merge (Anonymized)"| C["gamedata.json + packet_schemas.json"]
    D["QuestData.json (Skua)"] --> E["SkuaSim.Core Engine"]
    C --> E
    F["Scenario .json + Script .cs"] --> E
    E -->|"Roslyn //cs_include + TimeHookRewriter"| G["Isolated Script Execution (Virtual Clock)"]
    G --> H["SkuaSim.UI (Avalonia 12 Dashboard)"]
    G --> I["SkuaSim.Cli (Headless Runner)"]
```

### Key Mechanics

1. **Real Roslyn Compilation (`//cs_include`):** Scripts (including deep dependency trees with 100+ includes such as `CoreBots.cs`, `CoreFarms.cs`, `CoreAdvanced.cs`, and `ArmyFarmerJoeDoAll.cs`) are compiled directly against your installed `Skua.Core.*` assemblies inside a collectible per-run `AssemblyLoadContext`.
2. **Virtual Clock & AST Time Rewriting (`TimeHookRewriter`):** Calls to `Thread.Sleep`, `Task.Delay`, `DateTime.Now/UtcNow`, `Stopwatch`, and `Bot.Sleep`/`Bot.Wait` are intercepted and driven by a deterministic virtual clock (`SimClock`). Multi-hour, multi-day, or multi-week farms can execute in seconds while remaining 100% reproducible via RNG seed.
3. **Three-Tier Data Fidelity:**
   - **Tier 1 — Scenario Overrides:** Explicit starting character, class, gear boosts, unlocked enhancements, script options, and custom map/quest rules.
   - **Tier 2 — Real Game Data:** Captured AQW data from `SkuaSim.Collector` (`gamedata.json`) plus Skua's `QuestData.json` (10,000+ quests).
   - **Tier 3 — Synthesized Fallback:** When a script visits a map, monster, item, or shop never observed by the collector, SkuaSim infers safe defaults (assisted by static AST hints from the script) and **explicitly flags every invented entity in Violet** in the UI diagnostics and execution reports so you always know what was real vs. synthesized.
4. **Authentic AQW Stat & Combat Engine (`StatEngine`):** Computes real AQW primary/secondary stats (`STR`, `INT`, `END`, `DEX`, `WIS`, `LCK`, `AP`, `SP`, `Crit`, `Crit Multiplier`, `50% Haste cap`, `Dodge/Evasion`), calibrated by captured class skills (`sAct`) and combat telemetry (`ct`), alongside Awe/Forge enhancement procs and AQW's highest-in-category gear boost stacking rules.

---

## Requirements & Dependencies

### Runtime & Build Prerequisites
- **OS:** Windows (10 / 11)
- **.NET SDK:** `.NET 10.0` (`TargetFramework: net10.0`)
- **Skua or VibeSkua Installation:**
  - Automatically detected at runtime via `SkuaInstallation.Detect()` (checks `%LOCALAPPDATA%\VibeSkua\current\Assemblies`, `%APPDATA%\VibeSkua`, and standard Skua directories), or selectable manually in the UI (**Skua Dir...**).
  - Required assemblies provided by your local Skua/VibeSkua install (never bundled in this repo):
    - `Skua.Core.dll`, `Skua.Core.Interfaces.dll`, `Skua.Core.Models.dll`, `Skua.Core.Utils.dll`
    - `CommunityToolkit.Mvvm.dll`, `Newtonsoft.Json.dll`
    - `Microsoft.CodeAnalysis.dll`, `Microsoft.CodeAnalysis.CSharp.dll`
  - If your Skua/VibeSkua assemblies are in a custom directory when building from source, pass `-p:VibeSkuaDir="C:\path\to\Assemblies"`.

### NuGet Dependencies (`SkuaSim.UI`)
- `Avalonia` `12.1.6`
- `Avalonia.Desktop` `12.1.6`
- `Avalonia.Themes.Fluent` `12.1.6`
- `Avalonia.Fonts.Inter` `12.1.6`
- `CommunityToolkit.Mvvm` `8.4.2`

---

## Implemented Features

### 1. `SkuaSim.Collector` — Passive Data Collector Plugin
Runs silently inside Skua or VibeSkua on a non-blocking background `Channel<Action>` without interfering with gameplay or running scripts.
- **World & Economy Capture:** Maps, cells, pads, map items (`getMapItem`), monsters (ID, name, race, level, HP, spawn cells), drops (`dropItem` with observed rates/quantities), shops (`loadShop`, merge recipes, costs, AC/Member flags), and quests (`getQuests`, `ccqr` completion rates & rewards).
- **Combat, Class & Boss Telemetry:** Captures real class skills (`sAct`), stat snapshots (`stu`), player hits/crits/heals/dodges, monster attacks (`ct.sara[].actionResult`), HoT/DoT ticks (`typ: "d"`), auras & stack counts (`stk`), boss animations (`animStr`), event zones (`event`), and weapon/enhancement procs.
- **Multi-Client (Army) & Multi-Session Safe:** Merges incremental deltas atomically across simultaneous clients using a global cross-process `Mutex` (`Global\SkuaSim_GameDatabase_Mutex_v1`) and `.tmp → .bak → .json` atomic writes, with optional automatic mirroring to the `SkuaSim\data\` workspace folder.
- **Cross-Session Packet Schema Sampler (`packet_schemas.json`):** Accumulates strictly anonymized structural samples of combat/boss/event packets across sessions (up to 12 distinct shapes per category) to support protocol inspection and simulator calibration.
- **Zero Personal Data:** Never records usernames, character names, account IDs, chat messages, IP addresses, or personal inventory lists; automatically redacts `unm`, `uid`, `casterId`, and player names from sampled payloads.

### 2. `SkuaSim.Core` — Offline Simulation Engine
- **Script Compilation & Inspection:** Recursive `//cs_include` resolver, Roslyn compiler with full diagnostic reporting (`file(line,col) [ID]`), and standalone `InspectScript()` discovery for `Options`, `MultiOptions`, `OptionsStorage`, enums, defaults, and header rows.
- **Dynamic API Proxy (`ProxyHub`):** Implements Skua interfaces via `DispatchProxy` with `DynamicMethod` non-virtual default-interface-method invocation and automatic tracking of unimplemented members (`STUB` diagnostics).
- **AQW StatEngine & Equipment Boosts:**
  - Authentic Level 1–100 stat scaling, enhancement patterns (Lucky, Wizard, Fighter, Thief, Hybrid, Healer, Spellbreaker, Forge Helms/Capes, Awe & Forge Weapon procs), and live `%xt%zm%enhanceItemShop%` packet support.
  - **Real AQW Boosted Gear Catalog (`AqwBoostGearCatalog`):** Curated per-slot (`Weapon`, `Armor`, `Helm`, `Cape`, `Pet`) real AQW items with verified multi-boost `Meta` tags (e.g., *Necrotic Sword of Doom*, *Empowered Higure*, *Radiant Goddess of War*, *Infernal Flame Pyromancer*, *ArchFiend DoomLord*, *Head of the Legion Beast*, *Empowered Bonfire Altar*, *Awescended Omni Wings*, *Cape of Awe*, *Chaorrupter Unlocked*, *Polly Roger*, etc.) following AQW's highest-in-category stacking rules.
  - **Awe & Forge Enhancement Unlocks (`EnhancementUnlockCatalog`):** Automatically unlocks the corresponding AQW enhancement quests (`#2937` for Awe; exact Forge Weapon/Helm/Cape quest IDs verified from `CoreAdvanced.cs`, plus `Grimskull Trolling` Rank 7 for Hearty) and equips class-appropriate starting enhancements.
  - **Consumable & Server Boosts (`Bot.Boosts`):** Full `2x` XP, Class Points, Gold, Reputation, and Server boost multipliers.
- **Flexible Simulation Limits:**
  - Customizable virtual time limits from minutes to hours, days, weeks, or **Unlimited (`0`)** with multi-day clock formatting (`Xd hh:mm:ss.fff`).
  - Wall-clock real execution time defaults to **Infinite (`0`)** (and excludes cold Roslyn compile duration when a finite limit is set), while keeping real-time stall detection active for frozen pure-CPU loops.
  - Live `Pause` / `Resume` (stall-immune while paused), `Stop`, and configurable speed pacing (`Instant`, `1x`, `5x`, `20x`, `100x`).

### 3. `SkuaSim.UI` — Avalonia 12 Desktop Dashboard ("Skua without Flash")
- **Multi-Resolution & Typography Scaling:** Built-in adaptive scaling (`Auto (Screen)`, `HD 720p`, `Full-HD 1080p`, `2K 1440p`, `4K 2160p`, `8K 4320p`) with standardized typography resources (`Display`, `Heading`, `Body`, `Mono`, `Caption`) and responsive resizable splitters (`GridSplitter`) across every pane.
- **Live Client Tab:** Real-time view of Map/Cell/Pad, Target, Monsters (with HP bars and `[SYNTH]` markers), Player Level/HP/Class/Rank/Gold/ACs/Stats, Inventory, Bank, Temp Inventory, Pending Drops, Quests, Factions, and Map Cells.
- **Scenario & Script Options Tab:**
  - **Databank Class Picker & Live Real Stats Preview:** Pick any class captured in `gamedata.json` (or built-in archetype) and preview computed starting HP, DPS, Crit, Haste, Dodge, and boost multipliers immediately.
  - **Boosts & Real AQW Equipment Selector:** Toggle `2x` consumable/server boosts and equip real AQW boosted items per slot (plus 1-click **Endgame Meta** and **Clear Gear** presets).
  - **Awe & Forge Checklists:** Individual enhancement checkboxes plus **Select All Awe**, **Select All Forge**, and per-category **All Weapon**, **All Helm**, and **All Cape** toggles.
  - **Skua-Faithful `Script Options` Panel:** Matches Skua's native options UI — clean two-column layout with real `CheckBox`es for `Option<bool>`, `ComboBox`es for enums, `TextBox`es for strings/numbers, non-editable info banners for `Option<string>(" ", ...)` explanation headers, automatic `TabControl` grouping in exact `MultiOptions` order, **Reset Defaults**, and 1-click **Load Skua .cfg** to import your saved Skua script settings.
  - Quick templates (**Lv 1 Starter**, **Lv 100 Endgame**), **Save / Save As...** JSON persistence, and instant application on **Run**.
- **Color-Coded Diagnostics & Report Export:**
  - **Script Logs**, **Errors & Diagnostics**, and searchable **API Call & World Trace** timeline with semantic severity badges (`ERR`, `ALERT`, `WARN`, `OK`, `WORLD`, `API`, `STUB`) and signature **Violet (`SYNTH`)** highlighting for synthesized/invented data.
  - Export full execution reports as **Markdown (`.md`)** or **JSON (`.json`)**.

### 4. `SkuaSim.Cli` — Headless Command-Line Runner
Fast terminal interface for scripting, CI checks, and automated regression runs (`db-stats`, `compile`, `run`).

---

## Project Structure

```text
SkuaSim/
├── SkuaSim.slnx                    # Solution file (.NET 10)
├── src/
│   ├── SkuaSim.Collector/          # Passive Skua/VibeSkua plugin (ISkuaPlugin)
│   │   ├── Diagnostics/            # PacketSchemaSampler (packet_schemas.json)
│   │   ├── Models/                 # Captured maps, monsters, items, quests, classes, combat
│   │   ├── Parsing/                # PacketCollectorEngine (JSON/XT packet parsers)
│   │   └── Storage/                # GameDatabaseStore (Mutex-safe delta merge & atomic save)
│   ├── SkuaSim.Core/               # Offline compiler, virtual runtime, StatEngine & SimWorld
│   │   ├── Api/                    # Simulated IScriptInterface sub-APIs
│   │   ├── Compilation/            # //cs_include loader, Roslyn compiler, TimeHookRewriter
│   │   ├── Data/                   # GameDataCatalog (gamedata.json + QuestData.json)
│   │   ├── Hosting/                # SkuaInstallation detector & SkuaAssemblyResolver
│   │   ├── Proxies/                # DispatchProxy hub for Skua interfaces
│   │   ├── Runtime/                # SimClock, SimContext, SimulationRunner, SimulationSession
│   │   ├── Scenarios/              # Scenario schema, AqwBoostGearCatalog, EnhancementUnlockCatalog
│   │   └── World/                  # SimWorld & AQW StatEngine
│   ├── SkuaSim.UI/                 # Avalonia 12 desktop application
│   └── SkuaSim.Cli/                # Headless CLI executable (skuasim)
├── tests/
│   ├── SkuaSim.Collector.Tests/    # Plugin discovery, packet parsing & merge tests (6 suites)
│   └── SkuaSim.Core.Tests/         # Compilation, determinism, stats, boosts & runtime tests (11 suites)
├── scenarios/                      # Ready-to-use JSON scenarios (Starter, Leveling, Dailies, etc.)
├── data/                           # Local gamedata.json mirror, packet_schemas.json & sample reports
└── dist/plugins/                   # Pre-built Release SkuaSim.Collector.dll for Skua/VibeSkua
```

---

## Getting Started

### 1. Build the Solution
```powershell
dotnet build SkuaSim.slnx
```
*(Optional: if your Skua/VibeSkua assemblies live in a custom folder, append `-p:VibeSkuaDir="C:\path\to\Assemblies"`.)*

### 2. Install the Passive Collector Plugin (Optional, Recommended)
Build `SkuaSim.Collector` in Release mode and copy `SkuaSim.Collector.dll` into your client's `plugins/` folder (or use the pre-built copy in `dist\plugins\SkuaSim.Collector.dll`):
```powershell
dotnet build src\SkuaSim.Collector\SkuaSim.Collector.csproj -c Release
```
Copy `SkuaSim.Collector.dll` to:
- **VibeSkua:** `%APPDATA%\VibeSkua\plugins\` (and/or `%LOCALAPPDATA%\VibeSkua\current\plugins\`)
- **Official Skua:** `%APPDATA%\Skua\plugins\`

Launch Skua/VibeSkua and play or run bots normally — collected anonymized data is saved automatically to `%APPDATA%\VibeSkua\SkuaSim\gamedata.json` and mirrored to `data\gamedata.json`.

### 3. Launch the Desktop UI
```powershell
dotnet run --project src\SkuaSim.UI\SkuaSim.UI.csproj
```
Or launch the compiled binary directly at `src\SkuaSim.UI\bin\Debug\net10.0\SkuaSim.UI.exe`.

### 4. Run from the CLI
```powershell
# Inspect detected Skua install & captured database statistics
dotnet run --project src\SkuaSim.Cli\SkuaSim.Cli.csproj -- db-stats

# Compile a script and verify all //cs_include dependencies
dotnet run --project src\SkuaSim.Cli\SkuaSim.Cli.csproj -- compile "Other/FarmJoeKits/0FarmerJoeDoAll.cs"

# Execute a headless simulation with a scenario, fixed seed, and Markdown + JSONL outputs
dotnet run --project src\SkuaSim.Cli\SkuaSim.Cli.csproj -- run "Other/FarmJoeKits/0FarmerJoeDoAll.cs" `
    --scenario scenarios\farmerjoe_lv1_mage.json `
    --seed 42 `
    --max-virtual-min 360 `
    --max-real-sec 0 `
    --report data\last_run_report.md `
    --trace data\last_run_trace.jsonl
```

### 5. Run the Automated Test Suites
```powershell
dotnet run --project tests\SkuaSim.Collector.Tests\SkuaSim.Collector.Tests.csproj
dotnet run --project tests\SkuaSim.Core.Tests\SkuaSim.Core.Tests.csproj
```

---

## Roadmap

See [PLAN.md](PLAN.md) and [CONTEXT.md](CONTEXT.md) for the full design specification.

- [x] **Phase 1 — Collector Plugin (`SkuaSim.Collector`):** Passive packet capture, class/combat/boss telemetry, cross-process Mutex merge, anonymization, and cross-session packet schema sampling.
- [x] **Phase 2 — Core Simulator & Data World (`SkuaSim.Core` + `SkuaSim.Cli`):** Roslyn `//cs_include` compiler, virtual clock AST rewriter, 3-tier fidelity world, AQW `StatEngine`, real boosted gear & Awe/Forge unlocks, CLI runner.
- [x] **Phase 3 — Desktop Dashboard (`SkuaSim.UI`):** Avalonia 12 live client state, multi-resolution scaling, scenario editor, Skua-faithful Script Options, color-coded diagnostics, and report export.
- [ ] **Phase 4 — Multi-Client (Army) Simulation:** N concurrent simulated players sharing rooms, monster HP pools, party sync, and Army coordination files.
- [ ] **Phase 5 — Fault Injection & Scenario Assertions:** Simulated lag/disconnects/drop failures, automated pass/fail assertions, and one-click bug-report diagnostic templates.
- [ ] **Phase 6 — Session Replay & Public Release Packaging:** Real session log replay and standalone distribution bundles.
