# Skua script bug report

Use this template to report a bug in a Skua/VibeSkua script. With the diagnostic file, a developer can run the
script on a copy of your character in SkuaSim, offline, and check the fix before it reaches you.

## Before you write: make the diagnostic file

1. Stop the script that has the bug, but stay logged in on that character. If you can, stand where you usually start
   the script.
2. In Skua, load and start `SkuaSim_Diagnostic.cs` (from SkuaSim's `tools\skua\` folder).
3. In its options, set **Reported script** to the script with the bug, as it appears under the Scripts folder
   (for example `Story/Lair.cs`). Leave the other options as they are.
4. The script logs where it saved the file. By default the file is
   `Documents\SkuaSim Diagnostics\diagnostic_<date>_<script>.json`. Attach that file to the report.

**What the file contains:** your level, class and rank, gold and ACs, member status, location, inventory, bank, house
and temporary items (with enhancements and what is equipped), story progress, dailies done today, reputation, running
boosts, Awe/Forge unlocks, Skua's client options, and the reported script's saved options.

**What it never contains:** your character name (it is written as `Reporter`), guild, password or server. It also
leaves out every script option that may hold an account or player name (for example the `player1`…`player7` names
of Army scripts), and any option whose value is your character's name. The file is plain JSON, so you can read it
before you send it.

## What went wrong ✏️
- **What the script did:**
- **What it should have done:**
- **Where / when it happens** (map, step, after how long, every time or sometimes):

## Real run ✏️
- **Script version** (Skua's script list date or the Scripts repository commit):
- **Other scripts or plugins running at the same time:**
- **Skua log** (the last lines before the problem):
```text

```

## Attachments
- [ ] `diagnostic_<date>_<script>.json`
- [ ] Screenshot or video, if the problem is visible in game (optional)

---

## For developers: reproduce, fix, keep it fixed

```powershell
# 1. Run the script on the reporter's character and fill this template automatically
#    (writes <diagnostic>.bugreport.md next to the file; add --report full.md for the full execution report)
skuasim bug-report diagnostic_20261125-183000_Lair.json

# 2. Iterate on the fix with the same character, seed and data
skuasim run --scenario diagnostic_20261125-183000_Lair.json --data 2026-11-24

# 3. Keep it fixed: copy the diagnostic into scenarios\, set "dataSnapshot", add "asserts" for what the fixed script
#    must achieve (the generated report suggests a starting set), then
skuasim test
```

The generated report has a section called **How much to trust this reproduction**. It lists the maps that were never
collected, other invented data, unimplemented API members, and items the script checked that the character does not
own. Close those gaps first (run the Collector on those maps), or the run may differ from the real game for reasons
that have nothing to do with the bug.
