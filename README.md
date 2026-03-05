# Biome Dataset Collector (tModLoader)

<img width="470" height="496" alt="image" src="https://github.com/user-attachments/assets/bd2ab654-4adb-40e7-9969-48163e56aed0" />

Biome Dataset Collector helps you build labeled Terraria screenshot datasets for ML/CNN workflows.
It captures the current biome, saves a PNG, and appends metadata to `captures.csv`.

## What it does

- Manual capture hotkey (`F9` by default)
- Auto-capture toggle (`F10` by default)
- World-only capture timing (no HUD/UI in intended captures)
- Biome-labeled folder output
- CSV metadata logging for each image
- Dataset commands: status, clean, zip, merge, sync, UI toggle
- Floating in-game panel with live totals, per-biome counts, and quick action buttons

## Quick Start (Users)

1. Build and enable the mod in tModLoader.
2. Enter a world.
3. Press `F9` to capture one screenshot.
4. Check your dataset folder (see Output Location below).
5. Use `/dataset status` to verify counts.

Tip: use `/dataset ui` to open the panel and run common actions with buttons.

## Default Controls

- `F9`: Capture Dataset Screenshot
- `F10`: Toggle Auto Capture

Notes:
- The mod registers `F9`/`F10` as defaults.
- A one-time migration tries to restore those defaults if both controls are found unbound on load.
- You can always change controls in `Settings -> Controls -> Mod Controls`.

## Commands

Use `/dataset` with one of these subcommands:

- `/dataset status` - Show image totals, CSV row count, size, and per-biome counts.
- `/dataset clean` - Begin destructive cleanup flow.
- `/dataset clean confirm` - Confirm and execute cleanup.
- `/dataset zip` - Export dataset to timestamped zip.
- `/dataset merge <path>` - Merge from a zip (or external `captures.csv`) with UUID dedup.
- `/dataset sync` - Remove CSV rows whose image files no longer exist; also report orphan images that are missing CSV rows.
- `/dataset ui` - Toggle the floating panel.

If your merge path has spaces, wrap it in quotes.

## Output Location

Default root:

- `Documents/My Games/Terraria/tModLoader/BiomeCaptures/`

You can override this in Mod Config (`Output Directory`).

Typical structure:

```text
BiomeCaptures/
  captures.csv
  Forest/
  Desert/
  Snow/
  Jungle/
  Corruption/
  Crimson/
  Hallow/
  Ocean/
  Underground/
  Hell/
  Space/
  Mushroom/
  Dungeon/
```

## CSV Metadata

Header:

```csv
filename,biome,uuid,time_of_day,is_daytime,world_seed,world_name,world_x,world_y,timestamp,screen_width,screen_height
```

`uuid` is used for deduplication during merge.

## Biome Labels

Current class set (13):

- Forest
- Desert
- Snow
- Jungle
- Corruption
- Crimson
- Hallow
- Ocean
- Underground
- Hell
- Space
- Mushroom
- Dungeon

The classifier uses a strict priority order for overlapping zones.

## Data Quality Tips

- Keep a fixed game resolution during a capture session.
- Use `/dataset status` to monitor per-biome class balance.
- If you manually delete bad/boundary images, run `/dataset sync` afterward to keep `captures.csv` aligned with disk.
- Prefer multiple worlds and times of day for diversity.
- Avoid very fast intervals if your machine starts stuttering.

## Troubleshooting

- Controls show `<Unbound>`: open Mod Controls, use Reset to Default; if both were unbound, the mod also attempts a one-time auto-fix.
- Merge fails: verify the zip includes `captures.csv` and valid paths.
- Disk space errors on zip/merge: free space and retry.
- Count mismatch (`status`): run `/dataset sync` to drop rows for missing files, then run status again.
- Build error `Missing dll reference ... StbImageWriteSharp.dll`: ensure `lib/StbImageWriteSharp.dll` is present in `ModSources/BiomeDatasetCollector/lib/` and `build.txt` contains `dllReferences = StbImageWriteSharp` (without `.dll`).

## For Testers and Contributors

If you want to help test or improve the mod, this section is for you.

### Recommended test passes

1. Capture in several biomes and verify file routing + CSV rows.
2. Run auto-capture for a few minutes and watch for hitching.
3. Perform zip -> clean confirm -> merge roundtrip and verify counts/dedup.
4. Validate overlap biome cases (corrupted snow, hallowed desert, underground jungle).
5. Check panel live per-biome counts update behavior after rapid captures.
6. Manually delete a few captured PNGs, run `/dataset sync`, and verify CSV row count and sync report output.

### Local dev/build workflow

1. Develop in this repository.
2. Copy/sync to your tModLoader `ModSources/BiomeDatasetCollector` folder.
3. Make sure the `lib/` folder is synced too (it contains `StbImageWriteSharp.dll`, required for build/runtime).
4. Build with tModLoader's `Build + Reload`.

Note: `dotnet build` outside `ModSources` may fail because `tModLoader.targets` is not present in a standalone repo checkout.
