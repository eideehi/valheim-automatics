# Build Notes

## Project layout
- Solution: `Automatics.sln`
- Project: `Automatics/Automatics.csproj`
- Target framework: `.NET Framework 4.8`

## Repository notes

- `Automatics/Libraries/mod-utils` is a read-only library dependency. Do not edit files under this directory unless the task is explicitly to update the submodule.
- When preparing a release, create the Git tag that matches the release/package version so versioned README and Thunderstore links resolve correctly.
- Keep `docs/` for end-user-facing documentation. Do not place implementation plans, internal audits, research notes, or other non-end-user documents under `docs/`.

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

Besides the auto-run startup invariant checks, the smoke plugin registers four optional in-game console commands: `automatics_smoke_world` drives the live Storage/Door/Pickup/Processing entry points against freshly spawned objects, `automatics_smoke_lifecycle` toggles `automatic_pickup/module` at runtime to check patch application/removal and restores the original value afterwards (`automatics_smoke_lifecycle mapping` takes `automatic_mapping/module` through three off/on cycles instead, checking patches, components, scans and pins, and spawns nothing; `automatics_smoke_lifecycle storage` does the same for `automatic_storage/module` through the descriptor-driven runner the migrated modules share, and moves no item; `automatics_smoke_lifecycle door` uses that runner for `automatic_door/module`, checks hooks, components and unchanged door states, and spawns nothing; `automatics_smoke_lifecycle mining` uses it for `automatic_mining/module`, checks hooks, components, timers, the area-mining swing state and component generations, and changes no Mining setting; `automatics_smoke_lifecycle feeding` uses it for `automatic_feeding/module`, checks patches, unchanged hook counts, components and component generations, and spawns nothing; `automatics_smoke_lifecycle farming` uses it for `automatic_farming/module`, checks hooks, the crop claim Automatic Pickup reads, timers and the sapling lookup, runs only while Automatic Pickup cannot reach a crop or growing plant, and harvests, plants and spawns nothing; `automatics_smoke_lifecycle repair` checks Repair patches, both player hooks, stopped entries and session cleanup through that runner, and spawns or repairs nothing itself), and `automatics_smoke_processing` checks Automatic Processing: `rpc` that its station intake RPCs keep every item on stations and a chest it spawns, `toggle` a runtime off/on of `automatic_processing/module` restored afterwards, and `config` (read-only) its section of the live `.cfg`, and `automatics_smoke_features all|<section>` checks the manual-assist features section by section (`pickup` checks the Pickup All Nearby default, hover line, key pass, a key pass sharing its pass with the interval, and periodic pickup with the key bound; `pin` checks the ray pin action on a spawned drop and building piece, bare ground and an empty ray, the duplicate check, that manual pins are never hidden as automatic, and removes only the pins it added; `mining` refuses to start with a building piece or other destructible within reach, swings a spawned pickaxe with a real attack at spawned non-persistent deposits whose sections cannot break while a temporary guard drops the player's hits on anything else and the terrain operations they spawn, and checks that area mining adds extra hits within the radius and cap, leaves a deposit just beyond the radius alone, wears the pickaxe once per extra target, raises the skill once, and does not expand from Automatic Mining's own hit or a club swing, then removes the added tools, re-equips your items and puts your view pitch and Pickaxes skill back; `planting` checks manual grid requests with test tree saplings that need no cultivated ground against cell positions it computes itself, the half-cell offset of an even grid, per-crop seed/stamina/durability charges, limited seeds, stamina and durability, a blocked cell, a swapped tool, a failed or non-crop placement, and the released and unbound modifier through the real postfix, then removes its plants and items and restores equipment; `guide` navigates to an unsaved test pin about 60 m behind the camera and checks the guide swarm's material, particles, height over the ground, sweep toward the target, hiding on the large map, stop within the arrival distance, runtime option and teardown on clear, then removes only its test pin and puts back the map mode and your navigation target; `supply` logs SKIP until it has checks); it binds an unset Pickup All Nearby key and a 0 interval to their defaults only for the run, `pin` turns hiding unexplored automatic pins on for one check, `mining` turns interval Automatic Mining off for its section, `planting` temporarily uses a 3×3 grid, spacing factor 2.5 and Left Alt, and `guide` turns `enable_navigation_guide` off for one check, each recorded and restored through the same session as the module toggles. Only one runs at a time.

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
