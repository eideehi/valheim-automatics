# Automatics Valheim Smoke Test

This directory contains optional developer tooling for in-game Automatics smoke
checks. It is intentionally not part of the root `Automatics.sln`, and it does
not add root-level MSBuild files or production project settings.

The smoke plugin compiles only its own sources (`SmokePlugin.cs`,
`WorldSmokeCommand.cs`, `LifecycleSmokeCommand.cs`, `SmokeSession.cs`). Every
Automatics type it exercises (including the `StoragePlanner` routing types) is
bound from the already-loaded `Automatics.dll` via
`[assembly: InternalsVisibleTo("Automatics.SmokeTest")]`. It does **not** ship a
second copy of `Automatics.dll` or `ModUtils.dll`, and it declares
`[BepInDependency("net.eidee.valheim.automatics")]` so the main plugin loads
first and the CLR binds the smoke assembly's `Automatics`-typed references to the
single loaded copy.

It replaces the deleted headless harnesses
(`scripts/ObjectValidationHarness.cs` + `scripts/test-object-validation.sh` and
`scripts/StorageRoutingHarness.cs` + `scripts/test-storage-routing.sh`) by
running the same behaviour against real game state plus additional checks for the
mod's main features.

The fast static-invariant checks that `test-object-validation.sh` ran alongside
the harness (all-files object-data JSON schema validation, the Runestone matcher,
the no-duplicate-exact-matchers guarantee, and the object/icon source-pattern
guards) were not in-game behaviour and were preserved as
`scripts/test-object-data-static-invariants.sh`, not deleted.

Because the in-game logger has no capture buffer, the harnesses' warning-text
assertions (invalid object data skipped, duplicate exact matcher, null/invalid
custom entries) are re-expressed as observable-state checks — the valid sibling
or entry still resolves while the invalid ones are skipped — rather than
asserting the exact warning strings.

## Build

The main plugin must be built in the same configuration first, because the smoke
project references `Automatics/bin/<Configuration>/Automatics.dll` at compile time:

```bash
VALHEIM_DIR=/mnt/g/steam/steamapps/common/Valheim scripts/build.sh Debug
VALHEIM_DIR=/mnt/g/steam/steamapps/common/Valheim scripts/build.sh smoke Debug
```

`scripts/build.sh smoke Debug [clean] [deploy|no-deploy]` builds (and, on Debug,
deploys) the smoke plugin. You can also build the smoke-only solution directly:

```bash
VALHEIM_DIR=/mnt/g/steam/steamapps/common/Valheim dotnet msbuild smoke/Automatics.SmokeTest.sln /p:Configuration=Debug "/p:Platform=Any CPU"
```

Pass `no-deploy` (or `/p:DeploySmoke=false`) to build without installing:

```bash
VALHEIM_DIR=/mnt/g/steam/steamapps/common/Valheim scripts/build.sh smoke Debug no-deploy
```

## Deploy

The deploy target writes only the smoke `dll`/`pdb` to:

```text
<VALHEIM_DIR>/BepInEx/plugins/AutomaticsSmoke/
```

It never copies `Automatics.dll`, `ModUtils.dll`, `LitJSON.dll`, or
`NDesk.Options.dll` — those are already loaded next to the running Automatics
plugin and the CLR resolves them there.

Debug builds deploy automatically when the directory is available. Pass
`DeploySmoke=true` to force deployment for another configuration:

```bash
VALHEIM_DIR=/mnt/g/steam/steamapps/common/Valheim dotnet msbuild smoke/Automatics.SmokeTest.sln /p:Configuration=Release "/p:Platform=Any CPU" /p:DeploySmoke=true
```

If `DeploySmoke=true` is set and the Valheim or BepInEx directory cannot be
resolved, the build fails before copying files.

## Run And Logs

Launch Valheim after deploying. The plugin waits for `Localization.instance`
(with a timeout), then logs individual smoke results and one summary line to the
BepInEx log:

```text
[Automatics Smoke] Summary: total=17 passed=17 failed=0 skipped=0 failedChecks=[] skippedChecks=[]
```

The `vanilla_label_localizes_via_game` check (and, when the translation cache is
unseeded, `automatics_translation_keys_loaded`) SKIP rather than FAIL when run at
the main menu before a world loads. The smoke checks create temporary object JSON
files only under `BepInEx/config/AutomaticsSmoke/` and clean them up on a
best-effort basis.

### Safety of the console commands

Both console commands below run in the world you load, so they are written to
leave your save and your settings alone. The rule they follow is asymmetric: a
test item left behind, or a setting left as you last set it, is harmless;
removing one of your items or overwriting one of your settings is not. Wherever
the commands cannot prove that something is their own, they leave it.

- Only one smoke command runs at a time. Starting one while the other is still
  running is refused with `... was not started: <reason>`.
- A run hands back what it owes — the Automatic Pickup setting and any test
  object it spawned — when it completes or throws (each command's body runs
  inside a guard that does this either way), and when the smoke plugin is
  disabled or destroyed or the game quits (the plugin's `OnDisable` /
  `OnDestroy` / `OnApplicationQuit`). A logout does not stop a run: the plugin
  outlives the world, and the run notices the world is gone and goes straight to
  its restore.
- Something that could not be handed back — a setting that could not be
  written, say — is kept and tried again at the next of those moments, and a new
  run is refused while it stands.
- If the game itself stops mid-run (a crash or a killed process), nothing can
  run. Before the lifecycle command changes the setting it records the change in
  `BepInEx/config/AutomaticsSmoke/lifecycle-toggle-restore.txt`, and it does not
  change the setting if that record cannot be written. At the next start the
  smoke plugin logs one warning naming the setting, its value before the check,
  the value the check set and when, and then deletes the record. It never
  changes the setting from that record: check it and set it yourself.
- Test objects that live longer than one frame are marked non-persistent, so the
  world save never contains them even if the game is saved mid-run.
- A chest the checks spawn is destroyed only while it is empty, or holds nothing
  but what the check put there itself (the refuel check's seeded fuel). Otherwise
  it is left in the world and the log gives its coordinates and contents.

### World smoke command

In addition to the automatic startup checks above, the plugin registers an
optional console command `automatics_smoke_world` (no arguments). Run it from the
in-game console while a single-player world is loaded. It spawns real objects
(chest, wood door, smelter, item drop) next to the player and drives the live
Automatics entry points for Storage, Door, Pickup, and Processing, then asserts
the observable result of each:

- `world_storage_store_moves_items` — `AutomaticStorage.Store` moves test Wood
  from the player into a spawned chest. `Store` moves every eligible item, not
  only the test Wood, so the check compares its reported count with everything
  that reached the chest. Afterwards everything in the chest is handed back to
  you, and the chest is destroyed only once it has been seen empty. Returned
  items may land in different slots. If something cannot be handed back, the
  chest is left in the world and the check FAILs, listing what is in it and its
  coordinates. The test Wood is then removed, but never so much that your own
  count of that Wood drops below what you had before.
- `world_door_open_sets_zdo_state` — `AutomaticDoor.TryOpenNearby` flips the
  spawned door's ZDO `state` to non-zero.
- `world_pickup_itemdrop_to_inventory` — the shared pickup gate plus
  `ItemDrop.Pickup` moves a spawned drop into the inventory. Vanilla refuses a
  drop for 0.5 s after it spawns, so the check waits (up to 5 s) until the drop
  can be picked up, then makes the call Automatic Pickup makes for any drop
  already lying in range. The drop's `m_autoPickup` is cleared first so that
  neither vanilla auto-pickup nor Automatic Pickup can take it meanwhile, and it
  is kept out of vanilla auto-stacking so that no real drop can merge into it.
  The inventory is counted immediately before and after that one call, and the
  Wood is removed again only when the drop was consumed by the call and exactly
  its one Wood arrived; otherwise nothing is removed and a leftover is reported.
  A drop still in the world afterwards is destroyed only while it holds the one
  Wood the check gave it; if it grew, it is left where it is and the log says
  where. A wait during which the game was paused SKIPs as inconclusive.
- `world_processing_smelter_refuel` — `SmelterProcess.QuickRefuel` raises the
  smelter's ZDO `fuel` by one and consumes fuel from a nearby chest.

Run it away from your own containers. The storage check SKIPs when any other
container is within `storage_search_range`, and the refuel check SKIPs when any
other container is within the smelter's container search range (both 8 m by
default). Otherwise the storage check would move your items into your own
chests, and the refuel check could burn your fuel and fail for a reason that is
not a regression.

The command runs as a coroutine on the smoke plugin and prints its results about
a second after it is typed. Each check logs a `PASS`/`FAIL`/`SKIP` line plus a
`[Automatics Smoke] Summary: total=4 ...` line to both the BepInEx log and the
in-game console (out of scope: Farming, Feeding, Mining). A `PASS` line can carry
a note, for example when a test item had to be left in the inventory. Checks SKIP
(never FAIL) when run at the main menu, when a module is disabled, when a
required config flag is off, or when other containers are in range; the command
never mutates config. The processing container cache is reset both before the
refuel call and again during teardown, so running the command back-to-back is
safe.

### Lifecycle smoke command

`automatics_smoke_lifecycle` (no arguments) exercises the runtime module
lifecycle on Automatic Pickup, the one module migrated to it. Run it from the
in-game console while a single-player world is loaded. It drives the toggle the
way a user does, by writing `automatic_pickup / module`, and takes the module
through off → on → off:

| Check | Asserts |
|---|---|
| `lifecycle_initial_state_settled` | The registry state matches the toggle before anything is changed |
| `lifecycle_off_state_disabled` | The registry reports `Disabled` after the disable request |
| `lifecycle_off_patches_removed` | `Harmony.HasAnyPatches("net.eidee.valheim.automatics.automatic-pickup")` is false (AC-R3; SKIP when runtime patch removal is unavailable) |
| `lifecycle_off_components_removed` | No `AutomaticPickup` or `PickableItemCache` component remains in the scene, and the cache is empty (AC-R4) |
| `lifecycle_off_spawned_item_uncached` | A `PickableItem` spawned while off gets no cache (the control for AC-R5) |
| `lifecycle_on_state_enabled` | The registry reports `Enabled` after the enable request |
| `lifecycle_on_patches_applied` | The patches under the module's Harmony id are exactly the (target, patch method) pairs declared by `Automatics.AutomaticPickup.Patches` |
| `lifecycle_on_local_player_attached` | The local Player carries an `AutomaticPickup` stamped with the running generation (AC-R5) |
| `lifecycle_on_item_spawned_while_off_cached` | The `PickableItem` spawned while off is now cached, so the reconciliation scan reached it (AC-R5). SKIP (inconclusive) when its control failed |
| `lifecycle_on_player_awake_subscribed` | `Hooks.OnPlayerAwake` has exactly one more handler than while off (AC-R6) |
| `lifecycle_reoff_*` | The disabled-state checks again after the second disable, plus the handler count back at the first disabled value |
| `lifecycle_restored_initial_state` | The toggle is back at its starting value and the registry reports the matching state |

The run stops at the first state that is not reached and goes straight to the
restore. Every wait is bounded (10 s per transition); a timeout is a FAIL that
names the state the registry reported, never a hang. A `Faulted` state fails the
wait at once; the reason is in the Automatics log as a warning. A world that
unloads mid-run makes the remaining checks SKIP as inconclusive. The test
`PickableItem` is placed straight above the player, 10 m beyond
`automatic_pickup_range`, so the module cannot pick it up while it is on, and it
is removed at the end.

Unlike `automatics_smoke_world`, this command changes a config value, and the
config is shared by every world you play. The setting is put back to the value it
had when the command started, but only if nobody else has written it since and it
still holds the value the check itself last wrote. If you change
`automatic_pickup / module` while the check runs (in the settings window or by
reloading the config), the run stops, your value is kept, and
`lifecycle_restored_initial_state` SKIPs saying why. BepInEx saves
`Automatics.cfg` on each change, so the file is rewritten while the command runs.
What happens if the game stops mid-run is described under *Safety of the console
commands* above.

If the module starts disabled it is on only for a few frames; with an unusually
low frame rate it could pick up items near the player in that window.

A passing run looks like this in the console and in the BepInEx log:

```text
[Automatics Smoke] automatics_smoke_lifecycle started: automatic_pickup/module is turned off, on and off again, then restored
[Automatics Smoke] PASS lifecycle_initial_state_settled
[Automatics Smoke] PASS lifecycle_off_state_disabled
[Automatics Smoke] PASS lifecycle_off_patches_removed
...
[Automatics Smoke] PASS lifecycle_restored_initial_state
[Automatics Smoke] Summary: total=15 passed=15 failed=0 skipped=0 failedChecks=[] skippedChecks=[]
```

A failing check names what it saw, for example:

```text
[Automatics Smoke] FAIL lifecycle_off_patches_removed: Harmony.HasAnyPatches("net.eidee.valheim.automatics.automatic-pickup") is still true after the disable (AC-R3)
[Automatics Smoke] FAIL lifecycle_on_state_enabled: timed out after 10.0s (612 frames) waiting for Enabled; the registry reports Disabled and the toggle is Enabled
[Automatics Smoke] SKIP lifecycle_restored_initial_state: restoration skipped: automatic_pickup/module was changed outside the check while it ran and is now Enabled; it was left as it is instead of being put back to Disabled
```

## Remove

To uninstall the smoke plugin, remove the dedicated deploy directory:

```bash
rm -r /mnt/g/steam/steamapps/common/Valheim/BepInEx/plugins/AutomaticsSmoke
```

The smoke project is optional developer tooling. The fast static-invariant grep
scripts under `scripts/` are unaffected by this plugin.
