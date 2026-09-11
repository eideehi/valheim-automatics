# Automatics Valheim Smoke Test

This directory contains optional developer tooling for in-game Automatics smoke
checks. It is intentionally not part of the root `Automatics.sln`, and it does
not add root-level MSBuild files or production project settings.

The smoke plugin compiles only its own sources (`SmokePlugin.cs`,
`WorldSmokeCommand.cs`, `LifecycleSmokeCommand.cs`, `ProcessingSmokeCommand.cs`,
`SmokeSession.cs`). Every
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

The console commands below run in the world you load, so they are written to
leave your save and your settings alone. The rule they follow is asymmetric: a
test item left behind, or a setting left as you last set it, is harmless;
removing one of your items or overwriting one of your settings is not. Wherever
the commands cannot prove that something is their own, they leave it.

- Only one smoke command runs at a time. Starting one while another is still
  running is refused with `... was not started: <reason>`.
- A run hands back what it owes — a module setting it changed and any test
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
  run. Before the lifecycle command or `automatics_smoke_processing toggle`
  changes a module setting it records the change in
  `BepInEx/config/AutomaticsSmoke/lifecycle-toggle-restore.txt`, and it does not
  change the setting if that record cannot be written. At the next start the
  smoke plugin logs one warning naming the command, the setting, its value before
  the check, the value the check set and when, and then deletes the record. It never
  changes the setting from that record: check it and set it yourself.
- Test objects that live longer than one frame are marked non-persistent, so the
  world save never contains them even if the game is saved mid-run.
- A chest the checks spawn is destroyed only while it is empty, or holds nothing
  but what the check put there itself (the refuel check's seeded fuel, the
  processing check's seeded materials and what they turned into). Otherwise it
  is left in the world and the log gives its coordinates and contents. A left
  object that was marked non-persistent is made persistent again, so what it
  holds is saved with the world.

### World smoke command

In addition to the automatic startup checks above, the plugin registers an
optional console command `automatics_smoke_world [storage]`. Run it from the
in-game console while a single-player world is loaded. It spawns real objects
(chest, wood door, smelter, item drop) next to the player and drives the live
Automatics entry points for Storage, Door, Pickup, and Processing, then asserts
the observable result of each. Each check spawns in a spot of its own: the
storage chest 1.5 m ahead, the door 2 m to your left, the item drop 1.5 m to your
right, and the smelter 3.5 m ahead with its chest 1.5 m to the right of it. Each
check also starts in a frame of its own, so the objects the previous check removed
are gone by then.

- `world_storage_store_moves_items` — opt-in: it runs only as
  `automatics_smoke_world storage`, and a plain `automatics_smoke_world` SKIPs it
  with a message saying so. `AutomaticStorage.Store` moves test Wood from the
  player into a spawned chest. `Store` moves every eligible item, not only the
  test Wood, so the check compares its reported count with everything that
  reached the chest. Afterwards everything in the chest is handed back to you,
  and the chest is destroyed only once it has been seen empty. Returned items may
  land in different slots, the hotbar included. If the chest fills up with your
  own items before any test Wood has moved, the check SKIPs and suggests a
  near-empty inventory. A FAIL message carries the counts: what is in the chest,
  and `result.StoredItems`. If something cannot be handed back, the chest is left
  in the world and the check FAILs, listing what is in it and its coordinates.
  The test Wood is then removed, but never so much that your own count of that
  Wood drops below what you had before.
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
required config flag is off, or when other containers are in range. The storage
check also SKIPs unless `storage` is given. The command never mutates config.
The processing container cache is reset both before the
refuel call and again during teardown, so running the command back-to-back is
safe.

### Lifecycle smoke command

`automatics_smoke_lifecycle` (no arguments) exercises the runtime module
lifecycle on Automatic Pickup; with the argument `mapping` it exercises Automatic
Mapping instead, and with `door`, `farming`, `feeding`, `mining`, `repair` or
`storage` it exercises Automatic Door, Automatic Farming, Automatic Feeding,
Automatic Mining, Automatic Repair or Automatic Storage through the module runner
the migrated modules share (described after this one). Run it from the
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

#### `automatics_smoke_lifecycle mapping`

Takes `automatic_mapping / module` from `Enabled` through three off → on cycles
by writing the setting, the way a user does, and makes sure it ends at its
starting value. It spawns nothing and changes no other setting. Before the first
cycle it records a baseline: the saved pins, the handler counts, the component
counts and the minimap's icon tables.

| Check | Asserts |
|---|---|
| `mapping_initial_state_settled` | The registry reports `Enabled` before anything is changed |
| `mapping_off_state_disabled` | The registry reports `Disabled` after the disable request |
| `mapping_off_stopped_at_request` | Right after the disable request the module is still applied but no longer running, which is what every entry point checks, and the static scan's pass counter does not move until `Disabled`: the stop takes hold at the request, not at the teardown |
| `mapping_off_patches_removed` | No patch is left under Mapping's Harmony id (SKIP when runtime patch removal is unavailable) |
| `mapping_off_components_removed` | No `FishCache`, `BirdCache`, `ShipCache`, `MineRock5CacheBinding`, `FloraNode` or `AutomaticsNavigationOverlay` is left in the scene |
| `mapping_off_saved_pins_kept` | The saved pins are the ones there were before the run: type, name, position to 0.1 m, owner and checked flag. A vehicle pin's position is left out, because it follows its vehicle |
| `mapping_off_unsaved_pins_removed` | None of the unsaved pins the module held just before the disable is on the map (SKIP when it held none) |
| `mapping_on_state_enabled` | The registry reports `Enabled` after the enable request |
| `mapping_on_patches_applied` | The patches under Mapping's Harmony id are exactly the (target, patch method) pairs its three patch classes declare |
| `mapping_on_components_attached` | Every fish, bird, ship and `MineRock5` with a ZDO, and every pickable that is a configured flora, carries its component from the running generation, and there is no other (SKIP, inconclusive, when the scene has none of them) |
| `mapping_on_counts_at_baseline` | The handler counts of the hooks, caches and registries Mapping subscribes to, the component counts, and the lengths of `Minimap.m_icons` and `m_visibleIconTypes` are what they were before the run |
| `mapping_on_scans_resumed` | Within 10 s the static scan has run again and, if it held pins before the run, it holds one again (SKIP when a setting keeps the static scan from running on its own, such as `static_object_mapping_key`) |
| `mapping_on_saved_pins_kept` | 4 s after the enable, once the flora pins are placed again, the saved pins are still the ones there were before the run |

The component counts are sampled again for up to 10 frames before a mismatch is
reported, because an object the game spawns or despawns in the same frame has
its component attached or removed a moment later.
| `mapping_restored_initial_state` | The setting is back at `Enabled` and the registry reports it |
| `mapping_no_errors_logged` | No error or exception was logged while the check ran |

The checks between the restore and the settle are made in every cycle, and pass
only when all three cycles pass; a FAIL names the cycle. The run stops at the
first state that is not reached and goes straight to the restore. The waits and
the log check follow the same rules as the lifecycle command and
`automatics_smoke_processing toggle`; the log check's deliberate entry is
`InvalidOperationException: AutomaticsObserverProbe-mapping: a deliberate test
entry …`, which is expected in `LogOutput.log` and is not an error.

Safety, on top of the rules above:

- The check only ever pauses Automatic Mapping, never turns it on for you. If
  `automatic_mapping / module` is not `Enabled` when it starts, every check SKIPs
  and nothing is written.
- Each disable removes the unsaved pins Mapping added and each enable adds them
  back as the scans reach their objects, so the map looks the same afterwards.
  Saved pins are never removed; vehicle pins are moved to where their vehicles
  are, as saving the map does.

A passing run looks like this:

```text
[Automatics Smoke] automatics_smoke_lifecycle mapping started: automatic_mapping/module is turned off and on 3 times through its setting, nothing is spawned, and the setting is restored
[Automatics Smoke] PASS mapping_initial_state_settled: nothing was changed
[Automatics Smoke] PASS mapping_off_state_disabled: all 3 cycles
[Automatics Smoke] PASS mapping_off_stopped_at_request: all 3 cycles; right after the disable request the module was still applied but no longer running, and the static scan's pass counter stayed at 41 until the registry reported Disabled
...
[Automatics Smoke] PASS mapping_no_errors_logged: no error or exception was logged while the check ran (the observer saw its own probe once)
[Automatics Smoke] Summary: total=15 passed=15 failed=0 skipped=0 failedChecks=[] skippedChecks=[]
```

#### `automatics_smoke_lifecycle storage`: the module runner

`storage` is the first argument of the module runner in
`LifecycleSmokeModules.cs`. A migrated module joins it as a descriptor rather
than as code of its own: the module, its applied-state flags, the patch classes
it applies, the numbers it changes (its probes), a precondition that is asked
before anything is written, and the checks only that module can make. Each
descriptor is one more argument of the command.

The runner takes the module's `module` setting from `Enabled` through three
off → on cycles by writing it, the way a user does, and makes sure it ends at its
starting value. It spawns nothing and changes no other setting. Before the first
cycle it reads every probe, again on each frame until two frames in a row agree
(at most 10), and keeps that as the baseline.

| Check | Asserts |
|---|---|
| `storage_initial_state_settled` | The registry reports `Enabled` before anything is changed |
| `storage_off_stopped_at_request` | In the frame of the disable request the module is still applied but no longer running, which is what its entry points check: the stop takes hold at the request, not at the teardown |
| `storage_off_state_disabled` | The registry reports `Disabled` after the disable request |
| `storage_off_patches_removed` | No patch is left under the module's Harmony id (SKIP when runtime patch removal is unavailable) |
| `storage_off_probes_as_expected` | Every probe reads what it should while the module is off. For Automatic Storage, `Hooks.OnPlayerUpdate` has one handler fewer than at the baseline, and `Hooks.OnPlayerAwake` and `Hooks.OnPlayerFixedUpdate` have as many as at the baseline |
| `storage_on_state_enabled` | The registry reports `Enabled` after the enable request |
| `storage_on_running` | The module is applied and running again |
| `storage_on_patches_applied` | The patches under the module's Harmony id are exactly the (target, patch method) pairs its patch classes declare. Automatic Storage declares none, so there must be none (SKIP when runtime patch removal is unavailable) |
| `storage_on_probes_at_baseline` | Every probe is back at its baseline |
| `storage_restored_initial_state` | The setting is back at `Enabled` and the registry reports it |
| `storage_no_errors_logged` | No error or exception was logged while the check ran |

The checks between the settle and the restore are made in every cycle, and pass
only when all three cycles pass; a FAIL names the cycle. The probes are read
again for up to 10 frames before a mismatch is reported. The run stops at the
first state that is not reached and goes straight to the restore. The waits and
the log check follow the same rules as `automatics_smoke_lifecycle mapping`; the
log check's deliberate entry is `InvalidOperationException:
AutomaticsObserverProbe-storage: a deliberate test entry …`, which is expected in
`LogOutput.log` and is not an error.

A probe is a handler count, a component count or any other number. While the
module is off it has to read 0, its baseline less a fixed number (0 for a count
the module must not touch), or anything at all; once the module is back on, its
baseline, or, for a component count with a target, the number of objects the
module should be on at that moment. A probe that can show nothing, such as a
component count that was 0 at the baseline or a target of 0, makes its check
SKIP as inconclusive. A module's own checks are reported after the checks of the
phase they run in (at the request, while off or after the enable), and each one
reports PASS, FAIL or SKIP.

Safety, on top of the rules above:

- The check only ever pauses the module, never turns it on for you. If its
  `module` setting is not `Enabled` when it starts, or its precondition does not
  hold, every check SKIPs and nothing is written.
- Automatic Storage moves items only when `Store Items` is pressed, and the
  check never presses it, so no item moves.
- The handler counts are shared with other modules, so turning another module on
  or off while the check runs makes a probe check FAIL. The message names the
  count that moved.

A passing run looks like this:

```text
[Automatics Smoke] automatics_smoke_lifecycle storage started: automatic_storage/module is turned off and on 3 times through its setting, nothing is spawned, and the setting is restored
[Automatics Smoke] PASS storage_initial_state_settled: nothing was changed
[Automatics Smoke] PASS storage_off_stopped_at_request: all 3 cycles; right after the disable request the module was still applied but no longer running
...
[Automatics Smoke] PASS storage_no_errors_logged: no error or exception was logged while the check ran (the observer saw its own probe once)
[Automatics Smoke] Summary: total=11 passed=11 failed=0 skipped=0 failedChecks=[] skippedChecks=[]
```

#### `automatics_smoke_lifecycle door`

`door` uses the same descriptor-driven runner for `automatic_door / module`. It
takes an enabled module through three off → on cycles, changes no other setting,
spawns nothing and restores the setting afterwards.

In addition to the runner's state, patch and error checks, the Door descriptor
checks:

| Check | Asserts |
|---|---|
| `door_state_snapshot_captured` | Immediately before the disable is written, synchronously and without waiting a frame, every readable Door ZDO id and state is snapshotted without changing any of them |
| `door_off_stopped_at_request` | In the frame of the disable request the module remains applied but no longer starts automatic door or shortcut work |
| `door_off_probes_as_expected` | The four Door hook handlers and every `AutomaticDoor` component are gone, the active-component registration count is zero, and each Door-owned handler's own occurrence in its hook (not just the hook's total handler count) is zero |
| `door_door_id_set_unchanged` | The readable Door ZDO ids after teardown are the same as immediately before it |
| `door_door_state_unchanged` | Every readable Door ZDO keeps the same open/closed state across teardown |
| `door_on_patches_applied` | The patches under Door's Harmony id exactly match `Automatics.AutomaticDoor.Patches` |
| `door_on_probes_at_baseline` | All four handlers are restored, every currently eligible Door has a component, the registration count matches the active components rather than a stale baseline, and each Door-owned handler's own occurrence in its hook is back at one |
| `door_components_current_generation` | Every eligible Door, including inactive objects, has an `AutomaticDoor` component stamped with the current activation generation |

The scene must contain at least one loaded Door with a readable ZDO; otherwise the
positive-control check fails. Inactive Doors count as eligible components, but do
not count as registered until Unity activates them and runs their `Awake` method.
Turning the module off starts no new opening or closing, allows an operation already
in progress to finish, and completes the switch over the next few frames. It does
not close any Door or call a Door API. Turning it back on reconciles eligible Doors
that appeared or remained inactive while it was off.

#### `automatics_smoke_lifecycle mining`

`mining` uses the descriptor-driven runner for `automatic_mining / module`. It
takes an enabled module through three off → on cycles, spawns nothing, changes no
Mining setting and restores the module setting afterwards.

Before anything is written, and again immediately before every re-enable, the runner
requires a local player and a condition in which normal automatic mining cannot
start during an on interval: either the player has no usable pickaxe (none equipped
when `Need To Equip Pickaxe For Mining` is on, or none in the inventory when it is
off), or no eligible allowed mineral is within the product's full candidate radius
(`Mining Range`, or 32 when it is 0, plus the 24 meter root-pivot margin and 2
meters of smoke-test clearance). This scan does not depend on the scoped component,
which is absent while the module is off. If the initial check
fails, nothing is written and every check SKIPs. If a later check fails, that cycle
and the remaining on-side checks SKIP without the normal re-enable write, and the
existing restoration path puts the original enabled setting back. It never changes
the inner Mining settings for you. Do not move or equip a pickaxe while the command
is running.

In addition to the runner's state, patch and error checks, the Mining descriptor
checks:

| Check | Asserts |
|---|---|
| `mining_timer_state_captured` | The timer count immediately before each disable request was recorded without changing it |
| `mining_off_stopped_at_request` | In the frame of the disable request the module remains applied but starts no new mining pass |
| `mining_off_probes_as_expected` | The three Mining hook handlers and every `AutomaticMining` component are gone, `AllInstance.Count` is zero, and each Mining-owned handler's own occurrence in its hook is zero |
| `mining_timers_cleared` | A non-empty `MiningTimers` table observed immediately before disabling is empty while the module is off; if it was already empty, cleanup was not observable and the check SKIPs |
| `mining_on_patches_applied` | The patches under Mining's Harmony id exactly match `Automatics.AutomaticMining.Patches` |
| `mining_on_probes_at_baseline` | All three handlers are restored, every currently eligible unique `Destructible`, `MineRock` and `MineRock5` GameObject with a ZDO has one component, and `AllInstance.Count` matches the active components rather than a stale baseline |
| `mining_components_current_generation` | Every eligible mineral object, including inactive objects, has an `AutomaticMining` component stamped with the current activation generation |
| `mining_interval_timer_resumed` | When mining runs on an interval, its hook takes up its timer again within 30 frames of the enable; SKIP when mining runs on a key or is turned off |

Turning the module off stops new mining immediately and lets a mining pass already
accepted by a player hook finish its selected hits, including damage and pickaxe
bookkeeping. The patches, subscriptions and components are removed over the next
few frames. Turning it back on also completes over the next few frames and scans
loaded inactive objects as well as active ones. Under the required stationary,
no-pickaxe conditions, the check does not strike a mineral, change tool durability
or create a drop.

#### `automatics_smoke_lifecycle feeding`

`feeding` uses the descriptor-driven runner for `automatic_feeding / module`. It
takes an enabled module through three off → on cycles, spawns nothing (no animal
and no food), changes no Feeding setting and restores the module setting
afterwards. Run it where some of your animals are loaded: the component checks
need at least one loaded `Tameable` with a ZDO, and SKIP as inconclusive without
one.

In addition to the runner's state, patch and error checks, the Feeding descriptor
checks:

| Check | Asserts |
|---|---|
| `feeding_off_stopped_at_request` | In the frame of the disable request the module remains applied but no longer running, which is what both of its entry points open with, so no new feeding starts |
| `feeding_off_probes_as_expected` | Every `AutomaticFeeding` component is gone, and the four hook handler counts are where they were at the baseline: Automatic Feeding subscribes to none |
| `feeding_on_patches_applied` | The patches under Feeding's Harmony id exactly match the three postfixes of `Automatics.AutomaticFeeding.Patches` (`Tameable.Awake`, `MonsterAI.UpdateConsumeItem` and `BaseAI.CanSeeTarget(StaticTarget)`) |
| `feeding_on_probes_at_baseline` | Every loaded `Tameable` with a ZDO, inactive ones included, has one `AutomaticFeeding` component, counted against the animals loaded at that moment rather than a stale baseline, and the four hook counts are still at the baseline |
| `feeding_components_current_generation` | Every eligible `Tameable` carries an `AutomaticFeeding` component stamped with the current activation generation; SKIP when none is loaded |

Turning the module off stops new feeding immediately and lets a feeding call
already accepted finish: an item that has left a container or the inventory is
always eaten. The patches and components are removed over the next few frames.
Turning it back on also completes over the next few frames and attaches the
component to every loaded animal, inactive ones included. While the module is on
between cycles, a hungry animal near food may eat as it normally would; the check
itself moves no item.

#### `automatics_smoke_lifecycle farming`

`farming` uses the descriptor-driven runner for `automatic_farming / module`. It
takes an enabled module through three off → on cycles, spawns nothing, harvests,
plants and designates nothing, changes no Farming or Pickup setting and restores
the module setting afterwards.

While Automatic Farming is off, Automatic Pickup collects the crops Farming would
have harvested and replanted, and nothing replants them. So before anything is
written, and again immediately before every re-enable, the runner requires a
local player and refuses to run while Automatic Pickup is running and any crop in
`Allow Farming Crop`, or any growing plant (which can ripen while the check
runs), is within `automatic_pickup_range` + 1 m of you. If the initial check
fails, nothing is written and every check SKIPs; a SKIP for this reason is not a
pass, so move and run it again. If a later check fails, that cycle and the
remaining on-side checks SKIP without the re-enable write, and the restoration
puts the original enabled setting back.

Stand 10 to 30 m from a field of grown crops, outside both Pickup's and Farming's
range but close enough that the field stays loaded: the claim checks need at least
one crop Farming claims, and SKIP as inconclusive without one.

In addition to the runner's state, patch and error checks, the Farming descriptor
checks:

| Check | Asserts |
|---|---|
| `farming_session_state_captured` | Immediately before each disable request, without waiting a frame, the claimed crops and the farming timers are counted, and the sapling lookup is built if no farming pass built it yet (a read of the loaded prefabs; nothing in the world changes) |
| `farming_off_stopped_at_request` | In the frame of the disable request the module remains applied but no longer running, which is what its two player hooks, its hover postfix and its crop claim open with |
| `farming_claims_released_at_request` | In that same frame, no loaded crop is claimed any more, so Automatic Pickup collects them from then on; SKIP when no crop was claimed before the request |
| `farming_off_probes_as_expected` | The three Farming hook handlers are gone, each Farming-owned handler's own occurrence in its hook is zero, and the number of containers with a farming role is unchanged |
| `farming_claims_released_while_off` | While off, no loaded crop is claimed; SKIP when none was claimed before the request |
| `farming_timers_cleared` | A non-empty farming timer table observed immediately before disabling is empty while off; SKIP when it was already empty |
| `farming_sapling_map_cleared` | The sapling lookup built before disabling is dropped while off |
| `farming_on_patches_applied` | The patches under Farming's Harmony id exactly match the `Container.GetHoverText` postfix of `Automatics.AutomaticFarming.Patches` |
| `farming_on_probes_at_baseline` | All three handlers are restored, each Farming-owned handler's own occurrence is back at one, and the number of containers with a farming role is still the same |
| `farming_claims_restored` | The positive control: a crop is claimed again once the module is back on; SKIP when none was claimed before the request |
| `farming_interval_timer_resumed` | When farming runs on an interval, its hook takes up its timer again within 30 frames of the enable; SKIP when farming runs on a key or is turned off |

Turning the module off stops new farming passes, container designation and the
hover line immediately, and a pass already accepted by a player hook finishes: a
crop it harvested is replanted in the same call. The patch and subscriptions are
removed over the next few frames, and container roles stay on their containers.
While the module is on between cycles, farming may harvest and replant within its
range as it normally would; the check itself moves no item.

#### `automatics_smoke_lifecycle repair`

`repair` uses the descriptor-driven runner for three off/on cycles of
`automatic_repair / module`. It checks Repair's one transpiler, both player
subscriptions and each named handler, immediate stopping, and the release of
both timer dictionaries, all item/piece/collider buffers and the
radius-lookup failure flag. It calls the three repair entries only while off,
with null arguments. It spawns nothing, damages or repairs nothing itself, and
changes no internal Repair setting. Normal repairs can continue while Repair is
enabled. A module already disabled in your settings is left untouched and SKIPs.

Each retained state has `<field>_observed` and `<field>_cleared` checks, prefixed
with `repair_`. The first reads the state immediately before disabling in each
cycle. Cleanup SKIPs if that state was already empty, FAILs if it cannot be read
or remains populated, and otherwise PASSes. `repair_stopped_entries` checks that
the stopped entries return without throwing. SKIP is unverified; this command
does not demonstrate successful repairs, tool payment or multiplayer RPC delivery.
The original module setting is restored under the shared session rules.

### Processing smoke command

`automatics_smoke_processing` takes one subcommand: `rpc`, `toggle` or `config`
(the last two are described after `rpc`). Each is one smoke run.

`automatics_smoke_processing rpc` checks that Automatic Processing keeps every
item when it moves one from a chest into a station. Processing does that by
removing the item from the chest and invoking the station's intake RPC —
`Smelter.RPC_AddOre(string, bool)`, `CookingStation.RPC_AddItem(string, bool)`
or `Fermenter.RPC_AddItem(int, bool)` — and these are the paths that break when
the game changes an RPC's payload. The command changes no setting.

What it does:

- It places a wood chest 4 m ahead of you, then a smelter to its right, a stone
  oven to its left and a fermenter beyond it, each 2.5 m from the chest. When
  Automatic Pickup may be running, everything is placed further out, beyond
  `automatic_pickup_range` + 2 m from you.
- It picks one material per station from that station's own conversions, one
  that no other test station would take and that is not a fuel, and puts
  `material_count_of_suppress_processing` + 3 of it into the chest (4 with the
  default settings).
- It sets up each station so that its intake can run. The smelter gets no fuel,
  so the ore stays queued. The oven's fuel is set to 1, and a fire requirement is
  lifted if the oven has one. The fermenter is held "not exposed". Only the test
  objects' own fields and ZDOs are changed.
- It calls each station's Craft entry once (`SmelterProcess.Craft`,
  `CookingStationProcess.Craft`, `FermenterProcess.Craft`). If one takes nothing,
  the station is set up again and called once more.
- For 30 frames it keeps a ledger per station: what is left in the chest + what
  the station holds in its ZDO + anything that turned into must equal what was
  put into the chest, on every frame. The first sample is taken on the frame of
  the call: the RPC goes to this peer, which handles it inline. The station's own
  update may feed it more meanwhile; the ledger still has to hold.

| Check | Station and RPC |
|---|---|
| `processing_rpc_smelter_add_ore` | Smelter, `RPC_AddOre(string, bool)` |
| `processing_rpc_cooking_station_add_item` | Stone oven, `RPC_AddItem(string, bool)` |
| `processing_rpc_fermenter_add_item` | Fermenter, `RPC_AddItem(int, bool)` |

A check reports one of four results. None but PASS counts as a pass:

- `PASS`: at least one item reached the station, and the ledger held on every
  frame.
- `FAIL`: the ledger broke (an item missing, or more than was put in), the
  station holds something the check did not put there (fuel included), the entry
  threw, or a test object disappeared. It also FAILs when a test item appears on
  the ground near the test objects even though the ledger held, or when the
  direct call moved more than one item. On the frame of the call no station has
  run an update of its own, so whatever arrived by then is the call's. The
  message gives the first broken frame, the figures and any new item drop near
  the test objects.
- `SKIP`: a precondition is missing, for example:
  - no world is loaded
  - it isn't a single-player world you host with nobody connected
  - Automatic Processing is off
  - a prefab is missing
  - the wood chest isn't in `allow_container`
  - the station's `allow_processing_by_*` has no Craft
  - no material can be kept apart
  - something that isn't the check's own is within reach
  - Automatic Pickup's range would push the stations beyond 16 m
  - Processing does not find the test chest
  - the materials don't fit into the chest

  A world that unloads mid-run makes the checks SKIP as inconclusive.
- `NOT-RUN`: Processing found a container other than the test chest within a
  station's reach on a watched frame (a cart, a ship, a chest just built). The
  message names it and its coordinates, and the test objects go through the
  keep rules below. Or: everything was in place, but the entry took nothing on
  both
  attempts and nothing reached the station within 30 frames. The message says
  what the station looked like.

Where to stand: in a single-player world, away from your own buildings. The
command SKIPs before placing anything if any container that isn't its own is
within a test station's `container_search_range_by_*` + 2 m. It also SKIPs if
any processing station that isn't its own has a container range (+ 2 m) that
reaches the test chest. It lists what it found in a warning and says how much
clear space it needs. After placing its objects it asks Processing itself which
containers each station finds; if that is anything but the test chest, the run
stops in the same frame and removes its objects before any station has run an
update.

A passing run looks like this in the console and in the BepInEx log (item names
depend on the game's conversions and your language):

```text
[Automatics Smoke] automatics_smoke_processing rpc started: it places a chest, a smelter, a stone oven and a fermenter ahead of you, drives each station's intake once from the chest, watches them for 30 frames and removes them; no setting is changed
[Automatics Smoke] automatics_smoke_processing rpc: the test chest is at (102.3, 31.0, -48.7), 7.0 m ahead of you, with the test stations 2.5 m from it
[Automatics Smoke] PASS processing_rpc_smelter_add_ore: Copper ore reached the smelter at frame 0 (the frame of the call); the ledger held on every frame from 0 to 30 (at frame 30: chest 3 + smelter 1 + products 0 = 4 put in)
[Automatics Smoke] PASS processing_rpc_cooking_station_add_item: Bread dough reached the stone oven at frame 0 (the frame of the call); the ledger held on every frame from 0 to 30 (at frame 30: chest 3 + stone oven 1 + products 0 = 4 put in)
[Automatics Smoke] PASS processing_rpc_fermenter_add_item: Mead base: Minor healing reached the fermenter at frame 0 (the frame of the call); the ledger held on every frame from 0 to 30 (at frame 30: chest 3 + fermenter 1 + products 0 = 4 put in)
[Automatics Smoke] Summary: total=3 passed=3 failed=0 skipped=0 notRun=0 failedChecks=[] skippedChecks=[] notRunChecks=[]
```

The figures at frame 30 vary. A station's own update can take more of its
material from the chest while it is watched. The sum still has to equal what was
put in.

Other results look like this:

```text
[Automatics Smoke] SKIP processing_rpc_smelter_add_ore: 2 container(s) or processing station(s) that are not the check's own are within reach of where its test objects go (the first: Chest at (98.1, 30.8, -44.0), 5.2 m from where the test smelter goes); stand where nothing of yours is within about 20 m of you and run it again
[Automatics Smoke] NOT-RUN processing_rpc_fermenter_add_item: the entry returned without taking anything on both attempts, and nothing reached the fermenter within 30 frames (the chest still holds 4 of the 4 put in; owner=True, content=0, exposed=False, material_count_of_suppress_processing=1, product_stacks_of_suppress_processing=0, supply_only_when_materials_run_out=False)
[Automatics Smoke] FAIL processing_rpc_smelter_add_ore: the ledger broke at frame 0: chest 3 + smelter 0 + products 0 = 3, but 4 were put in (1 missing); at frame 30: chest 3 + smelter 0 + products 0 = 3; no delivery was seen
```

Safety, on top of the rules above:

- It never writes the BepInEx config, and it never touches your inventory. The
  test materials are added only to its own chest, with the game's
  `Inventory.AddItem`.
- The chest and the stations are marked non-persistent and are spared support
  and rain wear. A piece that breaks would drop what it holds.
- A station is removed only after the contents the check caused (the queued ore,
  the oven's slots and fuel, the fermenter's content) have been cleared from its
  ZDO, so nothing drops. That happens only while everything the station holds is
  the test material or its product and its ledger balances exactly: the chest's
  units + the station's contents + products equal what was put in, with nothing
  foreign. Fuel counts too: any smelter fuel, or oven fuel above the 1 the check
  set, came from somewhere else. Otherwise the station is left, like a chest. A
  deficit or a surplus leaves the station and the chest both, since either could
  hold the difference. What
  stays is decided before anything is removed. A left oven gets its fire
  requirement back. A left fermenter checks its exposure again.
- Item drops that appear near the test objects are reported, never removed.
- The removal runs when the run ends or throws. If the smoke plugin is disabled
  or the game quits mid-run, the session runs the same removal, with the same
  rules. If removing an object throws, the object is left in the world, saved,
  and reported. If a left object cannot be made part of the save, the warning
  says it will vanish when the world is saved or unloaded, so take your items
  out first. While a removal is still outstanding, a new smoke run is refused.

#### `automatics_smoke_processing toggle`

Takes `automatic_processing / module` from `Enabled` through one off → on round
trip by writing the setting, the way a user does, and makes sure it ends at its
starting value. It runs only when the module is `Enabled`. It changes no other
setting.

| Check | Asserts |
|---|---|
| `processing_toggle_initial_state_settled` | The registry state matches the setting before anything is changed |
| `processing_toggle_off_state_disabled` | The registry reports `Disabled` after the disable request |
| `processing_toggle_off_patches_removed` | No patch is left under Processing's Harmony id (SKIP when runtime patch removal is unavailable) |
| `processing_toggle_on_state_enabled` | The registry reports `Enabled` after the enable request |
| `processing_toggle_on_patches_applied` | The patches under Processing's Harmony id are exactly the (target, patch method) pairs `Automatics.AutomaticProcessing.Patches` declares (13 on 11 methods, 4 of them transpilers) |
| `processing_toggle_on_quickrefuel_acts` | Within 10 s of the enable, the game's own `Smelter.UpdateSmelter` calls the injected `SmelterProcess.QuickRefuel` for a test smelter, and it refuels it from the test chest |
| `processing_toggle_restored_initial_state` | The setting is back at its starting value and the registry reports the matching state |
| `processing_toggle_no_errors_logged` | No error or exception was logged while the check ran |

The test smelter and its chest are placed while the module is off, under the
same rules as `rpc`: where to stand, what is SKIPped, the exact ledger, the
per-frame check that Processing finds only the test chest, and the removal.
Placement and isolation are both checked before the module is turned back on,
so its first update of the smelter can find only the test chest. If any of that
fails, whatever was placed is removed at once, before the enable. The per-frame
check runs on every frame the smelter stands, including the frames of the enable
wait and the last watched frame. When it finds another container, the smelter's
check is NOT-RUN and the test objects go through the keep rules at once. The
chest gets `fuel_count_of_suppress_processing` + 3 of the smelter's fuel and
nothing else. On the test smelter only, `m_maxFuel` is held at 1, so the
ordinary refuel stays out and only the injected `QuickRefuel` can refuel it. Its
catch-up time is set to 5 s, since `QuickRefuel` acts only while the smelter's
update is catching up. A postfix under the smoke plugin's own Harmony id counts
the calls to `SmelterProcess.QuickRefuel` for the test smelter. It patches the
mod's method, never a game method, and it is removed at the end. A direct call
made before the enable, which returns at once, checks that the postfix counts
at all.

The log check watches everything Unity logs as an error or exception while the
check runs. To show that it sees the log, it logs one deliberate entry as it
starts, `InvalidOperationException: AutomaticsObserverProbe-toggle: a deliberate
test entry …`. That line is expected in `LogOutput.log` and is not an error.

Safety:

- The setting goes through the same session as `automatics_smoke_lifecycle`. The
  change is recorded before it is made, and only one smoke command runs at a
  time. The setting is put back only while it still holds the value the check
  wrote. If you change it while the check runs, the run stops and your value is
  kept. A restore that could not be made blocks the next run until it is done.
  BepInEx saves `BepInEx/config/net.eidee.valheim.automatics.cfg` on each
  change.
- The check only ever pauses Automatic Processing, never turns it on for you.
  If `automatic_processing / module` is not `Enabled` when it starts, every check
  SKIPs and nothing is written. Turning Processing on for the check would let it
  act on your own stations, and no watch can stop that in the frame one of them
  loads. Turn it on yourself if you want this check, then run it again.
- The check's observer patch is removed only once `UnpatchSelf` succeeds. If
  removing it fails, a warning says so, the removal is kept as an obligation and
  retried at the next hand-back, and a new smoke run is refused meanwhile.

A passing run looks like this:

```text
[Automatics Smoke] automatics_smoke_processing toggle started: automatic_processing/module is turned off and on again through its setting, a test smelter is watched while it is on, and the setting is restored
[Automatics Smoke] automatics_smoke_processing toggle: the test chest is at (102.3, 31.0, -48.7), 7.0 m ahead of you, with the test stations 2.5 m from it
[Automatics Smoke] PASS processing_toggle_initial_state_settled
[Automatics Smoke] PASS processing_toggle_off_state_disabled
[Automatics Smoke] PASS processing_toggle_off_patches_removed: no patch is left under "net.eidee.valheim.automatics.automatic-processing"
[Automatics Smoke] PASS processing_toggle_on_state_enabled
[Automatics Smoke] PASS processing_toggle_on_patches_applied: 13 patches on 11 methods (4 transpilers) under "net.eidee.valheim.automatics.automatic-processing", exactly the pairs Automatics.AutomaticProcessing.Patches declares
[Automatics Smoke] PASS processing_toggle_on_quickrefuel_acts: 0.8 s after the enable, the game's own Smelter.UpdateSmelter called the injected SmelterProcess.QuickRefuel for the test smelter (1 call(s)) and it moved Coal from the test chest into the smelter's fuel; the ledger held on every frame (chest 3 + smelter fuel 1 = 4 put in)
[Automatics Smoke] PASS processing_toggle_restored_initial_state: automatic_processing/module is already back at Enabled
[Automatics Smoke] PASS processing_toggle_no_errors_logged: no error or exception was logged while the check ran (the observer saw its own probe once)
[Automatics Smoke] Summary: total=8 passed=8 failed=0 skipped=0 notRun=0 failedChecks=[] skippedChecks=[] notRunChecks=[]
```

The Harmony id, the timing and the item name depend on your build and language.

#### `automatics_smoke_processing config`

Reads the live config file and compares its `[automatic_processing]` section
with the entries Automatic Processing binds. It is read-only: it writes neither
the file nor any setting.

| Check | Asserts |
|---|---|
| `processing_config_section_matches_bound_entries` | The section's keys are exactly the bound entries, 160 in this build, with none written twice; it reports the section's line count |
| `processing_config_module_line_matches_setting` | The file's `module` line equals the setting's current value |

```text
[Automatics Smoke] PASS processing_config_section_matches_bound_entries: [automatic_processing] in net.eidee.valheim.automatics.cfg has 160 keys, the same as the 160 entries Automatic Processing binds (650 lines in the section)
[Automatics Smoke] PASS processing_config_module_line_matches_setting: automatic_processing/module = Enabled in net.eidee.valheim.automatics.cfg, the same as the setting
[Automatics Smoke] Summary: total=2 passed=2 failed=0 skipped=0 notRun=0 failedChecks=[] skippedChecks=[] notRunChecks=[]
```

## Remove

To uninstall the smoke plugin, remove the dedicated deploy directory:

```bash
rm -r /mnt/g/steam/steamapps/common/Valheim/BepInEx/plugins/AutomaticsSmoke
```

The smoke project is optional developer tooling. The fast static-invariant grep
scripts under `scripts/` are unaffected by this plugin.
