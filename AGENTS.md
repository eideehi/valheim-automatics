# Build Notes

## Project layout
- Solution: `Automatics.sln`
- Project: `Automatics/Automatics.csproj`
- Target framework: `.NET Framework 4.8`

## Repository notes

- `Automatics/Libraries/mod-utils` is a read-only library dependency. Do not edit files under this directory unless the task is explicitly to update the submodule.
- When preparing a release, create the Git tag that matches the release/package version so versioned README and Thunderstore links resolve correctly.
- Every release commit must update `distributor/thunderstore/manifest.json`. Never release with it unchanged. Set `version_number` to the release version, and set each dependency to the version the release was built and checked against. For `denikson-BepInExPack_Valheim` that is the latest version on Thunderstore; if the local BepInEx install is older, update it first. `scripts/check-version.sh` checks only `version_number`, so check the dependencies by hand.
- Check behaviour in game with the smoke plugin wherever possible, and keep what a person has to check by hand to a minimum. When a change can be observed in game, add or extend a smoke check for it (see `smoke/README.md`) instead of writing manual steps, and ask a person to check only what a smoke check cannot observe, such as visuals or a real multiplayer session.
- `docs/` holds end-user-facing documentation, which is tracked. Local-only working documents live in fixed subdirectories of `docs/` that are gitignored and never committed: `docs/specs/` (requirements and specifications), `docs/plans/` (implementation plans), `docs/reports/` (audits, research notes and findings; deferred findings go in `docs/reports/findings/`) and `docs/decisions/` (decision records). Do not put other non-end-user documents directly under `docs/`, and do not create `plans/`, `specs/` or `.plans/` at the repository root.

## Required local dependencies
- .NET SDK 8.0 or newer
- A local Valheim installation
- BepInEx installed inside the Valheim directory

The project resolves game references from the Valheim install directory. Set the following environment variable before building:

- `VALHEIM_DIR`: absolute path to the Valheim root directory that contains `valheim_Data/Managed`

The expected directory shape is:

- `<Valheim>/valheim_Data/Managed`
- `<Valheim>/BepInEx/core`

## Build commands
Use `scripts/build.sh` for normal local builds. It validates `VALHEIM_DIR` and BepInEx core files, passes `FrameworkPathOverride`, and runs `dotnet msbuild`.

Debug build:

```bash
scripts/build.sh
scripts/build.sh Debug
```

Release build:

```bash
scripts/build.sh Release
```

Clean before building:

```bash
scripts/build.sh Debug clean
```

In-game smoke test plugin (optional developer tooling; see `smoke/README.md`).
Build the main plugin first, then build and deploy the smoke plugin to
`<Valheim>/BepInEx/plugins/AutomaticsSmoke`:

```bash
scripts/build.sh Debug
scripts/build.sh smoke Debug [clean] [deploy|no-deploy]
```

Besides the auto-run startup invariant checks, the smoke plugin registers four optional in-game console commands: `automatics_smoke_world` drives the live Storage/Door/Pickup/Processing entry points against freshly spawned objects, `automatics_smoke_lifecycle` toggles `automatic_pickup/module` at runtime to check patch application/removal and restores the original value afterwards (`automatics_smoke_lifecycle mapping` takes `automatic_mapping/module` through three off/on cycles instead, checking patches, components, scans and pins, and that the compass, which it turns on for the run, is gone from the frame of the stop request and when only its option is turned off, and spawns nothing; `automatics_smoke_lifecycle storage` does the same for `automatic_storage/module` through the descriptor-driven runner the migrated modules share, and moves no item; `automatics_smoke_lifecycle door` uses that runner for `automatic_door/module`, checks hooks, components and unchanged door states, and spawns nothing; `automatics_smoke_lifecycle mining` uses it for `automatic_mining/module`, checks hooks, components, timers, the area-mining swing state and component generations, and changes no Mining setting; `automatics_smoke_lifecycle feeding` uses it for `automatic_feeding/module`, checks patches, unchanged hook counts, components and component generations, and spawns nothing; `automatics_smoke_lifecycle farming` uses it for `automatic_farming/module`, checks hooks, the crop claim Automatic Pickup reads, timers and the sapling lookup, runs only while Automatic Pickup cannot reach a crop or growing plant, and harvests, plants and spawns nothing; `automatics_smoke_lifecycle repair` checks Repair patches, both player hooks, stopped entries and session cleanup through that runner, and spawns or repairs nothing itself; `automatics_smoke_lifecycle supply` uses that runner for `automatic_supply/module`, turns the module on first when it is off (it is off by default; the write is recorded and put back through the session), checks its patches, its player hook and that no counting scope or open action is left while off, and spawns and moves nothing; `automatics_smoke_lifecycle woodcutting` uses that runner for `automatic_woodcutting/module`, refuses to start or re-enable while an axe is equipped, turns the module on first when it is off (it is off by default; the write is recorded and put back through the session), checks its two patches, hooks, components, timers and component generations, and spawns nothing), and `automatics_smoke_processing` checks Automatic Processing: `rpc` that its station intake RPCs keep every item on stations and a chest it spawns, `toggle` a runtime off/on of `automatic_processing/module` restored afterwards, `config` (read-only) its section of the live `.cfg`, and `skill` that Craft and Store at a stone oven of its own raise the oven's skill and give bonus food only when you are its builder (it puts your skill level and the game's bonus chance back), and `automatics_smoke_features all|<section>` checks the manual-assist features section by section (`pickup` checks the Pickup All Nearby default, hover line, key pass, a key pass sharing its pass with the interval, and periodic pickup with the key bound; `pin` checks the ray pin action on a spawned drop and player-built piece, bare ground outside loaded locations and an empty ray, recognized location pins from a nearby loaded early-content location’s static structure and from terrain inside exactly one location area, a separately spawned non-persistent piece that never gets a creator (location pin) and the player-built test piece with its creator verified (piece pin) inside a loaded location’s area, that a spawned non-persistent tree, deposit and creature there pin as themselves, and, only with the camera inside a dungeon interior, an interior surface pinning its zone’s location (SKIP with a reason when a suitable loaded area, unambiguous point or ray is unavailable; no location is spawned or modified, and every test object is removed before the next check), the duplicate check, that manual pins are never hidden as automatic, and removes only the pins it added; `mining` refuses to start with a building piece or other destructible within reach, swings a spawned pickaxe with a real attack at spawned non-persistent deposits whose sections cannot break while a temporary guard drops the player's hits on anything else and the terrain operations they spawn, and checks that area mining adds extra hits within the radius and cap, leaves a deposit just beyond the radius alone, wears the pickaxe once per extra target, raises the skill once, and does not expand from Automatic Mining's own hit or a club swing, then removes the added tools, re-equips your items and puts your view pitch and Pickaxes skill back; `planting` checks manual grid requests with test tree saplings that need no cultivated ground against cell positions it computes itself, the half-cell offset of an even grid, per-crop seed/stamina/durability charges, limited seeds, stamina and durability, a blocked cell, a swapped tool, a failed or non-crop placement, and the released and unbound modifier through the real postfix, then plants a first grid and checks against its own lattice oracle that a nearby aim snaps (directly, and through real `Player.UpdatePlacementGhost` calls with the camera briefly pointed at the aimed point, the reach widened and the test sapling selected, all put back in the same frame: Valid on a free cell, not Valid on an occupied one, no snap when the lattice point is beyond reach, plus `Player.TryPlacePiece` with `PlacePiece` intercepted, the placement counted as cheated and the build statistics put back), that a second grid planted there lies on the lattice, and that nothing snaps without the modifier, beyond the search radius or once both grids are removed, then removes its plants and items and restores equipment; `guide` navigates to an unsaved test pin about 60 m behind the camera and checks the guide swarm's material, particles, height over the ground, sweep toward the target, hiding on the large map, stop within the arrival distance, runtime option and teardown on clear, then removes only its test pin and puts back the map mode and your navigation target; `compass` adds unsaved test pins at known bearings and distances and, in one frame with the camera turned to known headings, checks the compass's letters and marker positions and icons, the range and view cuts, the cap of 24 markers applied to the pins in view (also after turning before the pins are collected again), the icon-type toggle, the category and unexplored-area filters with a manual pin as control, hiding on the large map, without the small map and with the HUD hidden, and that the strip sits centred in the top band of the screen, then removes only its test pins and puts back the camera, the map mode, the HUD and the icon filter; `supply` turns Automatic Supply on for the section when it is off, sets its range to 4 m, lifts the player's no-cost toggle, checks that opening the inventory on its own hides the last chest's container panel in the same frame while closing it leaves the panel to vanilla, and against a spawned chest checks the scoped requirement count per quality, that the build panel and check agree, partial insertion (by capacity, and an insertion that falls short after the removal, whose remainder returns to the chest), the pulled − debited refund, no refund and no loss after an exception injected into a real intake, a real `InventoryGui.DoCrafting` with a spawned workbench as the current station (a complete take crafts and the chest pays, an incomplete one crafts nothing and leaves the chest unchanged), `Player.TryPlacePiece` refusing an incomplete take through the module's own guard while vanilla's placement status is Valid (with `PlacePiece` intercepted) and returning a failed placement's take, intake by a fireplace, a smelter's ore and fuel switches and a cooking station, and that an in-use or warded chest is untouched, then removes only the test units it put in, keeping anything it cannot account for, and its test objects; `woodcutting` sets `woodcutting_interval` to 0 before it equips or spawns anything, spawns a non-persistent tree that cannot fall and adds a test axe, and through the module's per-target entry checks one hit (attacker, level direction from the player to the tree, no push, the swing's skill raised once, durability drain), the refusals with no weapon, another weapon, a worn-out axe, a raised tool tier, a spawned wall across the line of sight and a range set below the hit distance, that the interval hook hits nothing at interval 0 and hits the test tree once at a positive interval, and one pass through the key's call, both when the test tree is the nearest (SKIP otherwise), all in one frame under a guard that drops every damage call on other trees and logs, then removes the added tools, re-equips your items, puts the skills back and removes the tree); it binds an unset Pickup All Nearby key and a 0 interval to their defaults only for the run, `pin` turns hiding unexplored automatic pins on for one check, `mining` turns interval Automatic Mining off for its section, `planting` temporarily uses a 3×3 grid, spacing factor 2.5 and Left Alt, `guide` turns `enable_navigation_guide` off for one check, `compass` turns `enable_compass` and `enable_map_enhancements` on, sets `compass_pin_range`, clears the hidden map categories and turns hiding unexplored automatic pins off for its section, changing the range, the categories and the unexplored filter further for single checks, `supply` changes Automatic Supply's module toggle and range as described, and `woodcutting` sets Automatic Woodcutting's interval to 0 for its section and lowers its range for one check, each recorded and restored through the same session as the module toggles. Only one runs at a time.

Version consistency check:

```bash
scripts/check-version.sh
```

## Direct MSBuild fallback
Use direct MSBuild commands only when debugging the build script or project file.

Debug build:

```bash
dotnet msbuild Automatics.sln /restore /t:Build /p:Configuration=Debug "/p:Platform=Any CPU" \
  /p:FrameworkPathOverride=$VALHEIM_DIR/valheim_Data/Managed
```

Release build:

```bash
dotnet msbuild Automatics.sln /restore /t:Build /p:Configuration=Release "/p:Platform=Any CPU" \
  /p:FrameworkPathOverride=$VALHEIM_DIR/valheim_Data/Managed
```

## Output
- Debug output: `Automatics/bin/Debug/Automatics.dll`
- Release output without packaging: `Automatics/bin/Release/Automatics.dll`
- Release output with `SEVENZIP_PATH`: package archives under `Automatics/bin/Release`
- Installed mod path: `<Valheim>/BepInEx/plugins/Automatics`

## Debug build auto-deploy
Debug builds through `scripts/build.sh` or direct MSBuild trigger `DeployModFiles` when `VALHEIM_DIR` points to a valid BepInEx Valheim install.

Release builds never deploy, so a Release build alone leaves the game running an older plugin. Before asking anyone to check a change in game (by hand or with a smoke command), close the game, run `scripts/build.sh Debug` and, when smoke checks are involved, `scripts/build.sh smoke Debug deploy`, and confirm that `<Valheim>/BepInEx/plugins/Automatics` (and `AutomaticsSmoke`) now holds that build, for example by its timestamp or by a name the change added.

## Release repack
Release builds use `ILRepack.Lib.MSBuild.Task` to merge `LitJSON` and `NDesk.Options` into the output assembly. If the ILRepack build targets are missing, the Release build fails.

## Release packaging
Set `SEVENZIP_PATH` to a `7z` executable to create release archives after ILRepack:

- `Automatics - <Version>.7z`: Nexus package with mod files in an `Automatics/` subfolder
- `Automatics - <Version>.zip`: Thunderstore package with `plugins/`, `icon.png`, `manifest.json`, `README.md`, and `CHANGELOG.md`

Thunderstore assets (`icon.png`, `manifest.json`, `README.md`) are copied from `distributor/thunderstore/`. `CHANGELOG.md` is copied into the Thunderstore package as a separate root file.

## Manual install
Copy the following into `<Valheim>/BepInEx/plugins/Automatics`:

- `Automatics.dll`
- `Data/`
- `Languages/`

## Troubleshooting
- If assembly references fail, verify that the chosen Valheim directory really contains `valheim_Data/Managed`.
- If BepInEx references fail, verify that `BepInEx/core/0Harmony.dll` and `BepInEx/core/BepInEx.dll` exist under the same Valheim directory.
- If the Release build fails because ILRepack targets are missing, run restore before building Release.
