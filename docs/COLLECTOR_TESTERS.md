# SkuaSim Collector — Guide for Testers

Thanks for helping. The SkuaSim Collector is a Skua/VibeSkua plugin that writes down game facts while you play or bot: maps, monsters, drops, shops, quests and class stats. SkuaSim uses those facts to test bot scripts offline, so they can be checked before they are released. The more maps, monsters and classes testers cover, the more scripts can be tested.

## What it does and does not do

- It only **reads** what your client already receives. It never sends anything to the AQW server, never clicks, moves or plays for you, and does not change your scripts.
- It never collects your account or character name, other players' names, your inventory, bank or house, chat, friends, or IP addresses.
- What it writes is game data, for example "Frost Spirit, level 255, respawns in about 2.4 s, dropped Item X in 12 of 400 kills", plus counts of your class's hits, crits and dodges grouped by level band and enhancements.

## Install

1. **Close Skua/VibeSkua.** The plugin file is locked while the client runs.
2. Copy `SkuaSim.Collector.dll` into the client's plugins folder:
   - VibeSkua: `%APPDATA%\VibeSkua\plugins\`
   - Skua: `%APPDATA%\Skua\plugins\`

   Paste the path into the File Explorer address bar to open it.
3. Start the client. The **SkuaSim Collector** button in the plugins menu shows the status and the collector version, which should be 1.3.1 or newer.

The collector was built and tested on VibeSkua. It should also work on official Skua, but that has not been fully verified yet. If you use Skua, please report any errors you see.

## Use

Play or run bots normally; the collector saves every 30 seconds. To help most:

- Visit maps and fight monsters that scripts use, especially maps nobody has covered yet.
- Turn in quests, buy and sell in shops, and rank up classes. Every class and level band counts.
- Running several clients at once is fine. Each client merges its data into the same file safely.

## Send your data

1. Open the **SkuaSim Collector** button and press **Export for Sharing**.
2. A message shows the file it created, `skuasim_share_<date>.zip`, inside the `exports` folder next to the data. **Open Folder** takes you there.
3. Send that `.zip` file to the SkuaSim maintainer.

The `.zip` contains:

- the game data, with every date cut to the day, so it does not show when you played;
- the anonymized packet samples (`packet_schemas.json`);
- a short summary.

It also contains a **random contributor ID** created the first time you export (stored in `contributor_id.txt`). The ID only lets SkuaSim replace your previous file when you send a newer one. Each export holds everything collected so far, so this keeps the same kills from being counted twice. The ID is not taken from your account, your name or your PC.

Send a new export whenever you like. The newest one always replaces the previous one.

## Options (plugin options window)

| Option | Default | What it does |
|---|---|---|
| Active Collection | On | Pause or resume collecting |
| Combat Details | On | Damage, intervals, boss cues, auras and procs |
| Save Interval (s) | 30 | How often data is saved |
| Mirror File Path | empty | An extra copy of the data file. Testers leave it empty |
| Packet Schema Sampler | On | Anonymized packet samples that help verify new game data |

## Uninstall

Close the client and delete `SkuaSim.Collector.dll` from the plugins folder. The collected data stays in `%APPDATA%\VibeSkua\SkuaSim\` (or `%APPDATA%\Skua\SkuaSim\`) until you delete that folder.

## For the maintainer

Import the files testers send with `skuasim import-data <file.zip> [more files...]`. The command:

- checks each file;
- refuses your own export, files without a contributor ID, and exports older than the stored one from the same contributor;
- stores each contributor's newest export in `data\contrib\<contributor>.json`.

Every simulation adds the files in `data\contrib\` to `gamedata.json` in memory (`gamedata.json` itself never changes), and lists them in the trace and in `skuasim db-stats`. To stop using a contributor's data, delete their file.
