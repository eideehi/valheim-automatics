# Automatics Valheim Smoke Test

This directory contains optional developer tooling for in-game Automatics smoke
checks. It is intentionally not part of the root `Automatics.sln`, and it does
not add root-level MSBuild files or production project settings.

The smoke plugin compiles only its own sources (`SmokePlugin.cs`,
`WorldSmokeCommand.cs`, `LifecycleSmokeCommand.cs`, `ProcessingSmokeCommand.cs`,
`SmokeSession.cs`, and the feature sections under `Features/`). Every
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

The fast data checks that `test-object-validation.sh` ran alongside the harness
(all-files object-data JSON schema validation, the Runestone matcher and the
no-duplicate-exact-matchers guarantee) were not in-game behaviour and were
preserved as `scripts/test-object-data-static-invariants.sh`, not deleted.

Because the in-game logger has no capture buffer, the harnesses' warning-text
assertions (invalid object data skipped, duplicate exact matcher, null/invalid
custom entries) are re-expressed as observable-state checks — the valid sibling
or entry still resolves while the invalid ones are skipped — rather than
asserting the exact warning strings.

## Build

The main plugin must be built in the same configuration first, because the smoke
project references `Automatics/bin/<Configuration>/Automatics.dll` at compile time:

```bash
VALHEIM_DIR=/path/to/Valheim scripts/build.sh Debug
VALHEIM_DIR=/path/to/Valheim scripts/build.sh smoke Debug
```

`scripts/build.sh smoke Debug [clean] [deploy|no-deploy]` builds (and, on Debug,
deploys) the smoke plugin. You can also build the smoke-only solution directly:

```bash
VALHEIM_DIR=/path/to/Valheim dotnet msbuild smoke/Automatics.SmokeTest.sln /p:Configuration=Debug "/p:Platform=Any CPU"
```

Pass `no-deploy` (or `/p:DeploySmoke=false`) to build without installing:

```bash
VALHEIM_DIR=/path/to/Valheim scripts/build.sh smoke Debug no-deploy
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
VALHEIM_DIR=/path/to/Valheim dotnet msbuild smoke/Automatics.SmokeTest.sln /p:Configuration=Release "/p:Platform=Any CPU" /p:DeploySmoke=true
```

If `DeploySmoke=true` is set and the Valheim or BepInEx directory cannot be
resolved, the build fails before copying files.

## Run And Logs

Launch Valheim after deploying. The plugin waits for `Localization.instance`
(with a timeout), then logs individual smoke results and one summary line to the
BepInEx log:

```text
[Automatics Smoke] Summary: total=19 passed=19 failed=0 skipped=0 failedChecks=[] skippedChecks=[]
```

The `vanilla_label_localizes_via_game` check (and, when the translation cache is
unseeded, `automatics_translation_keys_loaded`) SKIP rather than FAIL when run at
the main menu before a world loads. The smoke checks create temporary object JSON
files only under `BepInEx/config/AutomaticsSmoke/` and clean them up on a
best-effort basis.

When a console command below, or one of its sections, has a precondition for
running, state it in that command's or section's description, where the
description starts. This covers anything that must hold before the run: the kind
of world, enabled modules or settings, free inventory slots or carried items, and
how much open ground the run needs around the player, in which direction and how
far.

### Safety of the console commands

The console commands below run in the world you load, so they are written to
hand your save and your settings back as they found them: a setting, a module
or the player's position or inventory changed for a run is changed back when
the run ends. The rule they follow is asymmetric: a
test item left behind, or a setting left as you last set it, is harmless;
removing one of your items or overwriting one of your settings is not. Wherever
the commands cannot prove that something is their own, they leave it.

- Only one smoke command runs at a time. Starting one while another is still
  running is refused with `... was not started: <reason>`.
- A run hands back what it owes — any setting it changed and any test
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
  run. Before a smoke command changes a setting it records the change in
  `BepInEx/config/AutomaticsSmoke/lifecycle-toggle-restore.txt`, one record per
  setting, and it does not change the setting if that record cannot be written.
  At the next start the smoke plugin logs one warning per record naming the
  command, the setting, its value before the check, the value the check set and
  when, and then deletes the record. It never
  changes the setting from that record: check it and set it yourself.
- Test objects that live longer than one frame are marked non-persistent, so the
  world save never contains them even if the game is saved mid-run.
- A chest the checks spawn is destroyed only while it is empty, or holds nothing
  but what the check put there itself (the refuel check's seeded fuel, the
  processing check's seeded materials and what they turned into). Otherwise it
  is left in the world and the log gives its coordinates and contents. A left
  object that was marked non-persistent is made persistent again, so what it
  holds is saved with the world.

Shared helpers (`SmokeSetup.cs`) let a command set up what its checks need
instead of asking you to; a command or section that uses one says so where its
checks are described. Each of these is undone the same way as above: at the end
of the run, and at every hand-back trigger when the run is cut short, the later
obligation before the earlier one.

- A module a check needs that is off is turned on for the run, through the
  same recorded setting change, and the run waits (up to 600 frames) until the
  module reports running; the setting goes back with the others.
- A check that needs clear ground around the player may move the player: it
  tries the spot the player stands on (only when they stand on the ground
  itself), then rings 15, 30 and 45 m out, 12 bearings each from the direction
  the player faces, and takes the first that is loaded, on dry, safe and fairly
  level ground, outside every location and free of whatever the check names
  (building pieces, containers, creatures, drops, pickables, other
  destructibles, map pins). The move is a short teleport, the kind a dungeon
  entrance makes; one that does not finish is cancelled, and the player is put
  back where it started. The spot the command started from is recorded at the
  first move, and the player is returned there when the command ends or is cut
  short; the return is a teleport too, retried on its own until the player has
  arrived or two attempts have failed (then a warning says where they are), so
  it can end a few seconds after the Summary line, and a command started
  meanwhile is refused once. A command or section may also return the player
  itself before its Summary line; `automatics_smoke_features` returns them after
  every section, one that stopped on an exception included, before the next one
  starts. A check that teleports elsewhere (a location, say)
  may turn god mode and ghost mode on for the trip, and both are turned off
  again afterwards.
- A check that needs free inventory slots, free carry weight or none of some
  item in the inventory puts the items in the way aside - never an equipped one
  - and puts the same stacks back afterwards, never merged into another stack:
  into the slots they came from when those are free, into other free slots
  otherwise, and dropped at the player's feet (with a log line) when nothing
  fits. They are lost only when the game stops before that, or when the
  character is saved while they are aside - a logout mid-run does that - which
  the dedicated test character accepts.

### World smoke command

In addition to the automatic startup checks above, the plugin registers an
optional console command `automatics_smoke_world [storage]`. Run it from the
in-game console while a single-player world is loaded. It spawns real objects
(chest, wood door, smelter, item drop) next to the player and drives the live
Automatics entry points for Storage, Door, Pickup, and Processing, then asserts
the observable result of each. Each check spawns in a spot of its own: the
storage chest 1.5 m ahead, the door 2 m to your left, the item drop 1.5 m to your
right, the smelter 3.5 m ahead with its chest 1.5 m to the right of it, for the
two container checks a smelter 3.5 m behind you (the second one also 3.5 m to
your left), each with its chest 1.5 m to the right of it, and for the charging
check the piece that takes ammunition 3.5 m ahead and 3.5 m to your left, with
its chest 1.5 m to the right of it. Each check also starts in a frame of its
own, so the objects the previous check removed are gone by then.

The command sets up what its checks need and puts it back when the run ends:

- Before the first check it turns on, for the run, every module whose checks
  run that is off: Automatic Door and Automatic Processing, and Automatic
  Storage with `storage`.
- When a container that isn't the run's own is within 10 m of you, it moves you
  to clear ground first (see "Safety of the console commands" above) and moves
  you back before the Summary.
- Each check sets, for the run, the settings it depends on, and only when they
  do not already hold the value it needs: for the storage check
  `storage_search_range` 2 and the Material type and the wood chest in
  `allowed_item_types` and `allow_container`; for the door checks `WoodDoor` in
  `allow_automatic_door`, and for the open check `distance_for_automatic_opening`
  2.5 (its default, past the door 2 m away); for the processing checks the
  process under test in the piece's `allow_processing_by_*`, the wood chest in
  Processing's `allow_container` and the piece's `container_search_range_by_*`
  at 4 (so only the check's chest, 1.5 m away, is in reach), and besides that for
  the smelter `*_fuel_count_of_suppress_processing` 1 (the refuel check seeds 4
  above it) and `*_refuel_only_when_materials_supplied` and (for the first
  container check) `*_store_only_if_product_exists` off, and for the charging
  piece `*_number_of_items_to_stop_charge` 1 (its seeds are the reserve + 2, the
  reserve and the reserve + 2).
- The storage check puts every item outside the hotbar, and every stack of
  Wood, aside for the check, and the pickup check puts aside what it takes to
  free a slot and the weight of one Wood; both put them back when they end (see
  "Safety of the console commands" above).

The run ends with `world_settings_restored`: PASS when every setting it wrote is
back, SKIP when you changed one meanwhile (yours is kept) or the run was
abandoned, FAIL when one could not be put back. It is missing when the run wrote
nothing. A setting that cannot be written makes the check that needs it SKIP and
name it.

- `world_storage_store_moves_items` — opt-in: it runs only as
  `automatics_smoke_world storage`, and a plain `automatics_smoke_world` SKIPs it
  with a message saying so. `AutomaticStorage.Store` moves test Wood from the
  player into a spawned chest. `Store` moves every eligible item, not only the
  test Wood, so the check compares its reported count with everything that
  reached the chest. Afterwards everything in the chest is handed back to you,
  and the chest is destroyed only once it has been seen empty. The test Wood is
  added as a stack of its own in an empty, unlocked slot outside the hotbar, and
  since everything else outside the hotbar is put aside, Store can reach only
  the test Wood and, when `store_hotbar_items` is on, your hotbar items. Returned
  items may land in different slots, the hotbar included. If the chest still
  fills up with your own items before any test Wood has moved, the check SKIPs.
  A FAIL message carries the counts: what is in the chest,
  and `result.StoredItems`. If something cannot be handed back, the chest is left
  in the world and the check FAILs, listing what is in it and its coordinates.
  The test Wood is then removed, but never so much that your own count of that
  Wood drops below what you had before.
- `world_door_open_sets_zdo_state` — `AutomaticDoor.TryOpenNearby` flips the
  spawned door's ZDO `state` to non-zero. It SKIPs when
  `distance_for_automatic_opening` could not be set for the run and is short of
  the door.
- `world_door_interior_left_alone` — a door in a dungeon is never closed. It
  spawns two wood doors, one on your left beyond the reach in which a player holds
  a door open and one 2 m to your left but 5000 m up, where the game puts dungeon
  interiors, opens both as a hand does and runs `AutomaticDoor.TryCloseDoors`
  once. The door at ground level must close, and the one up high must stay open.
  Like the module's own close pass, this closes any other open allowed door in the
  loaded area that nobody who could open it is near. It SKIPs when you are indoors.
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
  where. A wait during which the game was paused SKIPs as inconclusive. When the
  inventory has no free slot or too little carry weight, items of yours are put
  aside first, and the PASS note says how many.
- `world_processing_smelter_refuel` — `SmelterProcess.QuickRefuel` raises the
  smelter's ZDO `fuel` by one and consumes fuel from a nearby chest.
- `world_container_store_after_spawn_keeps_stored_items` — a chest whose saved
  data holds items its inventory has not loaded yet, as when it has just spawned
  with stored items, keeps them when a module writes it. The check writes three
  of the smelter's product into the spawned chest's saved data (`ZDO` `items`)
  without loading them, then `SmelterProcess.Store` stores one more: the chest's
  inventory and its saved data must both hold four. It SKIPs when
  `store_only_if_product_exists` is on for the smelter and could not be turned
  off for the run.
- `world_container_store_after_close_keeps_saved_changes` — a chest another
  game owns is left to it, and a chest that comes back keeps that game's changes.
  The check seeds the spawned chest with two of the smelter's product, then plays
  another player's game: it becomes the chest's owner (a made-up session id),
  marks it in use, writes five into its saved data without loading them, and
  clears the in-use mark again. `SmelterProcess.Store` must store nothing while
  the chest is open there and nothing after it is closed there, and must not take
  the chest. Then the chest is this game's again (`ClaimOwnership`, as the
  server's reassignment would leave it), and a store in the same frame must leave
  six in both the inventory and the saved data, not this game's older copy plus
  one. However the check ends, the chest is this game's again before it is
  removed, because only the owner removes its saved data with it.
- `world_processing_charge_takes_accepted_ammunition` — automatic charging takes
  only ammunition the piece accepts and keeps each kind's reserve
  (`number_of_items_to_stop_charge`). It spawns the first piece that charges with
  three kinds of ammunition of its own ammunition type and takes one kind off
  that spawned piece's accepted list, so the chest can hold an item of the right
  type that the piece refuses. The chest holds, in this order, the refused kind
  (reserve + 2), an accepted kind at its reserve, and another accepted kind at
  reserve + 2; one `TurretProcess.Charge` call must leave the piece holding one
  of the last kind, and the chest with the refused kind and the kind at its
  reserve untouched, with the reserve at 1 for the run. It SKIPs when no such
  piece exists, or when the reserve or the allowed processes could not be set
  for the run. The log names none of the items.

Your own containers are kept out of reach. The storage check SKIPs when any
other container is within `storage_search_range` (2 m for the run), and the
refuel and container checks SKIP when any other container is within the
smelter's container search range, and the charging check when any is within
the piece's (4 m for the run). Otherwise the storage check would move your items
into your own chests, the refuel check could burn your fuel and fail for a
reason that is not a regression, the container checks would store the product
in your chests, and the charging check could take your ammunition. The move to
clear ground at the start makes these SKIPs rare; they are left for a container
that comes into reach later, or when no clear ground was found within 45 m.

Left to you: run it outdoors (the interior door check SKIPs indoors).

The command runs as a coroutine on the smoke plugin and prints its results about
a second after it is typed. Each check logs a `PASS`/`FAIL`/`SKIP` line plus a
`[Automatics Smoke] Summary: total=9 ...` line (8 when the run wrote no setting)
to both the BepInEx log and the in-game console (out of scope: Farming, Feeding,
Mining). A `PASS` line can carry a note, for example when a test item had to be
left in the inventory or items of yours were put aside. Checks SKIP (never FAIL)
when run at the main menu, when a module could not be turned on or a setting
could not be set for the run, or when other containers are in range. The
storage check also SKIPs unless `storage` is given.
The processing container cache is reset both before the
refuel call and again during teardown, so running the command back-to-back is
safe.

### Lifecycle smoke command

`automatics_smoke_lifecycle` (no arguments) exercises the runtime module
lifecycle on Automatic Pickup; with the argument `mapping` it exercises Automatic
Mapping instead, and with `door`, `farming`, `feeding`, `mining`, `repair`,
`storage`, `supply` or `woodcutting` it exercises Automatic Door, Automatic
Farming, Automatic Feeding, Automatic Mining, Automatic Repair, Automatic Storage,
Automatic Supply or Automatic Woodcutting through the module runner
the migrated modules share (described after this one); with `all` it runs every
one of these in turn (described at the end of this section). Run it from the
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

Takes `automatic_mapping / module` through three off → on cycles by writing the
setting, the way a user does, and makes sure it ends at its starting value. When
the module is off in your settings, the run turns it on first.

Before the baseline it sets up what its checks need, and puts it back at the end:

- It writes, for the run: `enable_automatic_mapping` on, and
  `static_object_mapping_range` and `static_object_mapping_interval` at their
  defaults when they are 0, so the static scan runs on its own;
  `save_static_object_pins` off, so a pin the scan adds during the run stays out
  of the saved pins the checks compare; `enable_navigation_guide` on, so a
  guide swarm shows for the temporary target; and `pin_object_range` at least
  50 m, so the looked-at label's ray reaches as far as the label's own range.
- When no `MineRock5` is loaded within 20 m of you, it places a test copper
  deposit 12 m ahead of you, on the ground, for the module's components to attach
  to and the looked-at label to find, and removes it at the end.

It also turns `enable_compass` on for the run, as it does for the looked-at
object bar and map filter settings it uses. `mapping_initial_state_settled`'s
note lists what was set up. Before the first cycle it records a baseline: the
saved pins and which of them are recognized as automatic, the handler counts, the
component counts and the minimap's icon tables.

| Check | Asserts |
|---|---|
| `mapping_initial_state_settled` | The registry reports `Enabled` once the setup is done, before any check changes anything |
| `mapping_initial_player_awake_handlers_none` | Before anything is changed, no handler on `Hooks.OnPlayerAwake` or `Hooks.OnLocalPlayerAwake` is declared by a type of Automatic Mapping (a nested type or closure included), whatever the method is called |
| `mapping_off_state_disabled` | The registry reports `Disabled` after the disable request |
| `mapping_off_stopped_at_request` | Right after the disable request the module is still applied but no longer running, which is what every entry point checks, and the static scan's pass counter does not move until `Disabled`: the stop takes hold at the request, not at the teardown |
| `mapping_off_patches_removed` | No patch is left under Mapping's Harmony id (SKIP when runtime patch removal is unavailable) |
| `mapping_off_player_awake_handlers_none` | The same after the disable: Mapping declares no handler on either player-awake hook |
| `mapping_off_components_removed` | No `FishCache`, `BirdCache`, `ShipCache`, `MineRock5CacheBinding`, `FloraNode`, `AutomaticsNavigationOverlay` or `AutomaticsNavigationGuide` is left in the scene, and no handler of Navigation or of the destroyed-object pin removal is left on the current world's `ZDOMan.m_onZDODestroyed` (each counted by the type that declares it) |
| `mapping_off_held_swarm_removed` | Once in the run, just before the first disable request at which a guide swarm is shown for the temporary target, that swarm is hidden and held the way a map click that clears navigation holds it. PASS when the held swarm is that swarm and no `AutomaticsNavigationGuide` is left after that disable; FAIL when no held swarm was made from the shown swarm or a guide object is left. Checked once, not per cycle (SKIP, with the reason, when no swarm was shown before any disable, which happens when the looked-at label finds no object to show for) |
| `mapping_off_compass_removed` | The compass, seen on the small map before the large map is opened for the filter checks, is gone in the frame of the disable request once the compass update of the player's frame has run there, and no `AutomaticsCompass` is left once the module is `Disabled` |
| `mapping_off_saved_pins_kept` | The saved pins are the ones there were before the run: type, name, position to 0.1 m, owner and checked flag. A vehicle pin's position is left out, because it follows its vehicle |
| `mapping_off_unsaved_pins_removed` | None of the unsaved pins the module held just before the disable is on the map (SKIP when it held none) |
| `mapping_on_state_enabled` | The registry reports `Enabled` after the enable request |
| `mapping_on_patches_applied` | The patches under Mapping's Harmony id are exactly the (target, patch method) pairs its three patch classes declare |
| `mapping_on_player_awake_handlers_none` | The same with the module running again |
| `mapping_on_components_attached` | Every fish, bird, ship and `MineRock5` with a ZDO, and every pickable that is a configured flora, carries its component from the running generation, and there is no other (SKIP, inconclusive, when the scene has none of them) |
| `mapping_on_counts_at_baseline` | The handler counts of the hooks, caches and registries Mapping subscribes to, the component counts, and the lengths of `Minimap.m_icons` and `m_visibleIconTypes` are what they were before the run. The counts include the `ZDOMan.m_onZDODestroyed` handlers of Navigation and of the destroyed-object pin removal, which the running module subscribes again on its own, without being enabled for it |
| `mapping_on_scans_resumed` | Within 10 s the static scan has run again and, if it held pins before the run, it holds one again (SKIP when `enable_automatic_mapping` is off or `static_object_mapping_range` or `static_object_mapping_interval` is 0, which the run's own settings rule out unless one is changed while it runs) |
| `mapping_on_saved_pins_kept` | 4 s after the enable, once the flora pins are placed again, the saved pins are still the ones there were before the run |
| `mapping_on_saved_automatic_pins_recognized` | As soon as the registry reports `Enabled`, every saved pin that was recognized as automatic before the run and is still on the map is recognized as automatic again (SKIP when no saved pin was recognized before the run, or none of them is on the map) |
| `mapping_on_compass_option_off_removed` | With the module running, the compass is shown on the small map, is gone in the frame `enable_compass` is turned off and is not left in the scene two frames later; the option is then turned back on for the next cycle |
| `mapping_restored_initial_state` | The setting is back at its starting value and the registry reports the matching state |
| `mapping_settings_restored` | Every other setting the run wrote is back at its starting value (SKIP when one was left as you changed it during the run; FAIL when one could not be put back) |
| `mapping_no_errors_logged` | No error or exception was logged while the check ran |

The component counts are sampled again for up to 10 frames before a mismatch is
reported, because an object the game spawns or despawns in the same frame has
its component attached or removed a moment later.

The checks between the restore and the settle are made in every cycle, and pass
only when all three cycles pass; a FAIL names the cycle. The run stops at the
first state that is not reached and goes straight to the restore. The waits and
the log check follow the same rules as the lifecycle command and
`automatics_smoke_processing toggle`; the log check's deliberate entry is
`InvalidOperationException: AutomaticsObserverProbe-mapping: a deliberate test
entry …`, which is expected in `LogOutput.log` and is not an error.

Safety, on top of the rules above:

- When `automatic_mapping / module` is off in your settings, the run turns it on
  and puts it back off at the end, unless you changed it meanwhile. While it is
  on, it adds pins to your map as it normally would; they are not saved, and go
  when it is off again.
- Each disable removes the unsaved pins Mapping added and each enable adds them
  back as the scans reach their objects, so the map looks the same afterwards.
  Saved pins are never removed; vehicle pins are moved to where their vehicles
  are, as saving the map does.

A passing run looks like this:

```text
[Automatics Smoke] automatics_smoke_lifecycle mapping started: automatic_mapping/module is turned off and on 3 times through its setting, and the setting and whatever the run sets up for its checks are restored
[Automatics Smoke] PASS mapping_initial_state_settled: set pin_object_range=100, enable_automatic_mapping=true, static_object_mapping_range=32, static_object_mapping_interval=0.25, save_static_object_pins=false, enable_navigation_guide=true for the run
[Automatics Smoke] PASS mapping_off_state_disabled: all 3 cycles
[Automatics Smoke] PASS mapping_off_stopped_at_request: all 3 cycles; right after the disable request the module was still applied but no longer running, and the static scan's pass counter stayed at 41 until the registry reported Disabled
...
[Automatics Smoke] PASS mapping_no_errors_logged: no error or exception was logged while the check ran (the observer saw its own probe once)
[Automatics Smoke] Summary: total=27 passed=27 failed=0 skipped=0 failedChecks=[] skippedChecks=[]
```

#### `automatics_smoke_lifecycle storage`: the module runner

`storage` is the first argument of the module runner in
`LifecycleSmokeModules.cs`. A migrated module joins it as a descriptor rather
than as code of its own: the module, its applied-state flags, the patch classes
it applies, the numbers it changes (its probes), a precondition that is asked
before anything is written, and the checks only that module can make. Each
descriptor is one more argument of the command.

The runner takes the module's `module` setting through three off → on cycles by
writing it, the way a user does, and makes sure it ends at its starting value; a
module that is off in your settings is turned on for the run first. Before that it
sets up what the descriptor says the module's checks need, and puts it all back
at the end, on a throw and at any hand-back trigger:

- settings written for the run, reported by `<module>_settings_restored`;
- the module's tool taken out of your hands, and put back once the module and its
  settings are back as they were;
- a test object placed 12 m ahead of you, on the ground and facing you, when no
  object of the kind the probes count is loaded, and removed at the end.

`<module>_initial_state_settled`'s note lists what was set up. A module that
keeps an interval timer then gets up to 30 frames to take it up, so the first
cycle's timer check has one to see. Automatic Storage needs none of this: the run
spawns nothing and changes no other setting. Before the first cycle the runner
reads every probe, again on each frame until two frames in a row agree (at most
10), and keeps that as the baseline.

| Check | Asserts |
|---|---|
| `storage_initial_state_settled` | The registry reports `Enabled` once the setup is done, before any check changes anything |
| `storage_off_stopped_at_request` | In the frame of the disable request the module is still applied but no longer running, which is what its entry points check: the stop takes hold at the request, not at the teardown |
| `storage_off_state_disabled` | The registry reports `Disabled` after the disable request |
| `storage_off_patches_removed` | No patch is left under the module's Harmony id (SKIP when runtime patch removal is unavailable) |
| `storage_off_probes_as_expected` | Every probe reads what it should while the module is off. For Automatic Storage, `Hooks.OnPlayerUpdate` has one handler fewer than at the baseline, and `Hooks.OnPlayerAwake` and `Hooks.OnPlayerFixedUpdate` have as many as at the baseline |
| `storage_on_state_enabled` | The registry reports `Enabled` after the enable request |
| `storage_on_running` | The module is applied and running again |
| `storage_on_patches_applied` | The patches under the module's Harmony id are exactly the (target, patch method) pairs its patch classes declare: for Automatic Storage the three slot-lock patches `AutomaticStorage.Patches` puts on the inventory grid (SKIP when runtime patch removal is unavailable) |
| `storage_on_probes_at_baseline` | Every probe is back at its baseline |
| `storage_restored_initial_state` | The setting is back at its starting value and the registry reports the matching state |
| `storage_no_errors_logged` | No error or exception was logged while the check ran |

A module the run writes settings for also reports `<module>_settings_restored`
after `<module>_restored_initial_state`: every setting the run wrote is back at
its starting value (SKIP when one was left as you changed it during the run; FAIL
when one could not be put back).

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

The session-reset handlers of Door, Farming, Mining, Repair, Supply and Woodcutting
sit on `Hooks.OnLocalPlayerAwake`, which is raised only for a player character the
local game owns, so their probes count that hook. Each of those modules also has a
`<module>.Hooks.OnPlayerAwake.declared` probe: the handlers the module's own types
declare on `Hooks.OnPlayerAwake`, whatever the method is called, which must read 0
before the run, while it runs and while it is off, so a reset handler left on the
all-player hook fails it. Automatic Pickup attaches to every
player character and stays on `Hooks.OnPlayerAwake`. The run does not create a
character that another game owns, so that a remote player's character leaves the
session state alone is not checked in game.

Safety, on top of the rules above:

- A module that is off in your settings is turned on for the run, and the
  recorded setting is put back off at the end, unless you changed it meanwhile.
  While it is on, it acts in your world as it normally would.
- A precondition is asked after the setup and before the module is turned on or
  a test object placed. If it does not hold, every check SKIPs and what was set up
  is put back.
- Automatic Storage moves items only when `Store Items` is pressed, and the
  check never presses it, so no item moves.
- The handler counts are shared with other modules, so turning another module on
  or off while the check runs makes a probe check FAIL. The message names the
  count that moved.

A passing run looks like this:

```text
[Automatics Smoke] automatics_smoke_lifecycle storage started: automatic_storage/module is turned off and on 3 times through its setting, and the setting and whatever the run sets up for its checks are restored
[Automatics Smoke] PASS storage_initial_state_settled: nothing was changed
[Automatics Smoke] PASS storage_off_stopped_at_request: all 3 cycles; right after the disable request the module was still applied but no longer running
...
[Automatics Smoke] PASS storage_no_errors_logged: no error or exception was logged while the check ran (the observer saw its own probe once)
[Automatics Smoke] Summary: total=11 passed=11 failed=0 skipped=0 failedChecks=[] skippedChecks=[]
```

#### `automatics_smoke_lifecycle door`

`door` uses the same descriptor-driven runner for `automatic_door / module`. It
takes the module through three off → on cycles, changes no other setting and
restores the setting afterwards.

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

The checks need at least one loaded Door with a readable ZDO. When there is none,
the run places a test wooden door 12 m ahead of you, beyond the largest opening
distance, so the module never opens it, kept from wearing for lack of support,
and removes it at the end. Inactive Doors count as eligible components, but do
not count as registered until Unity activates them and runs their `Awake` method.
Turning the module off starts no new opening or closing, allows an operation already
in progress to finish, and completes the switch over the next few frames. It does
not close any Door or call a Door API. Turning it back on reconciles eligible Doors
that appeared or remained inactive while it was off.

#### `automatics_smoke_lifecycle mining`

`mining` uses the descriptor-driven runner for `automatic_mining / module`. For
the run it turns `need_to_equip_pickaxe` and `enable_automatic_mining` on, sets
`mining_interval` to its default when it is 0, so the interval timer can be seen,
and takes every pickaxe out of your hands; all of it goes back at the end. When
no mineral object is loaded it places a test copper deposit 12 m ahead of you and
removes it at the end.

After that setup, before the module is turned on, and again immediately before
every re-enable, the runner requires a local player and a condition in which normal automatic mining cannot
start during an on interval: either the player has no usable pickaxe (none equipped
when `Need To Equip Pickaxe For Mining` is on, or none in the inventory when it is
off), or no eligible allowed mineral is within the product's full candidate radius
(`Mining Range`, or 32 when it is 0, plus the 24 meter root-pivot margin and 2
meters of smoke-test clearance). This scan does not depend on the scoped component,
which is absent while the module is off. The setup makes the
first condition hold, so the check fails only when a pickaxe is equipped again. If
the initial check fails, every check SKIPs and what was set up is put back. If a
later check fails, that cycle and the remaining on-side checks SKIP without the
normal re-enable write, and the existing restoration path puts the original
setting back. Do not move or equip a pickaxe while the command is running.

In addition to the runner's state, patch and error checks, the Mining descriptor
checks:

| Check | Asserts |
|---|---|
| `mining_timer_state_captured` | The timer count immediately before each disable request was recorded without changing it |
| `mining_off_stopped_at_request` | In the frame of the disable request the module remains applied but starts no new mining pass |
| `mining_off_probes_as_expected` | The three Mining hook handlers and every `AutomaticMining` component are gone, `AllInstance.Count` is zero, and each Mining-owned handler's own occurrence in its hook is zero |
| `mining_timers_cleared` | A non-empty `MiningTimers` table observed immediately before disabling is empty while the module is off; if it was already empty, cleanup was not observable and the check SKIPs |
| `mining_area_swing_cleared` | No area-mining swing is open, and no extra hit is being applied, while the module is off |
| `mining_on_patches_applied` | The patches under Mining's Harmony id exactly match `Automatics.AutomaticMining.Patches`, including the swing and rock damage patches of area mining |
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
takes the module through three off → on cycles, changes no Feeding setting and
restores the module setting afterwards. The component checks need at least one
loaded `Tameable` with a ZDO: when none is loaded, the run places a tamed test
boar 12 m ahead of you and removes it at the end. It spawns no food.

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
takes the module through three off → on cycles, spawns nothing, harvests, plants
and designates nothing itself, and restores the module setting afterwards. For the
run it sets `automatic_pickup_range` to 1, its least, turns
`enable_automatic_farming` on and sets `farming_interval` to its default when it
is 0, so the interval timer can be seen; the settings go back at the end.

While Automatic Farming is off, Automatic Pickup collects the crops Farming would
have harvested and replanted, and nothing replants them. So after that setup,
before the module is turned on, and again immediately before every re-enable, the
runner requires a local player and refuses to run while Automatic Pickup is running and any crop in
`Allow Farming Crop`, or any growing plant (which can ripen while the check
runs), is within `automatic_pickup_range` + 1 m of you: 2 m, with the run's range
of 1. If the initial check fails, every check SKIPs and the settings are put
back; a SKIP for this reason is not a pass, so step away from your crops and run
it again. If a later check fails, that cycle and the remaining on-side checks
SKIP without the re-enable write, and the restoration puts the original setting
back.

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
| `farming_off_probes_as_expected` | The three Farming hook handlers and the one `ValheimObject.RegistryChanged` handler are gone, each Farming-owned handler's own occurrence in its hook is zero, and the number of containers with a farming role is unchanged |
| `farming_claims_released_while_off` | While off, no loaded crop is claimed; SKIP when none was claimed before the request |
| `farming_timers_cleared` | A non-empty farming timer table observed immediately before disabling is empty while off; SKIP when it was already empty |
| `farming_sapling_map_cleared` | The sapling lookup built before disabling is dropped while off |
| `farming_on_patches_applied` | The patches under Farming's Harmony id exactly match the `Container.GetHoverText` postfix of `Automatics.AutomaticFarming.Patches` |
| `farming_on_probes_at_baseline` | All three hook handlers and the `ValheimObject.RegistryChanged` handler are restored, each Farming-owned handler's own occurrence is back at one, and the number of containers with a farming role is still the same |
| `farming_claims_restored` | The positive control: a crop is claimed again once the module is back on; SKIP when none was claimed before the request |
| `farming_interval_timer_resumed` | When farming runs on an interval, its hook takes up its timer again within 30 frames of the enable; SKIP when farming runs on a key or is turned off |

Turning the module off stops new farming passes, container designation and the
hover line immediately, and a pass already accepted by a player hook finishes: a
crop it harvested is replanted in the same call. The patch and subscriptions are
removed over the next few frames, and container roles stay on their containers.
While the module is on between cycles, farming may harvest and replant within its
range as it normally would; the check itself moves no item.

#### `automatics_smoke_lifecycle supply`

`supply` uses the descriptor-driven runner for three off/on cycles of
`automatic_supply / module`. It checks Supply's patches (the requirement checks
and panels, the inventory counts, the removal overloads it measures, crafting,
placement and the station intakes), its one `OnLocalPlayerAwake` subscription and
named handler, and, while off, that no counting scope, suppression or open action
is left (`supply_session_idle_while_off`). It spawns nothing, changes no other
setting and moves no item. Like every module the runner takes, it is turned on for
the run when it is off in your settings: the write is recorded through the session
before it is made, and the setting is put back off at the end, on a throw and at
any hand-back trigger, unless you changed it meanwhile.

#### `automatics_smoke_lifecycle woodcutting`

`woodcutting` uses the descriptor-driven runner for three off/on cycles of
`automatic_woodcutting / module`. For the run it turns `need_to_equip_axe` and
`enable_automatic_woodcutting` on, sets `woodcutting_interval` to its default when
it is 0, so the interval timer can be seen, and takes every axe out of your hands;
all of it goes back at the end. When no tree, log or stump is loaded it places a
test beech 12 m ahead of you and removes it at the end. Like every module the
runner takes, it is turned on for the run when it is off in your settings: the
write is recorded through the session before it is made, and the setting is put
back off at the end, on a throw and at any hand-back trigger, unless you changed
it meanwhile.

After that setup, before the module is turned on, and again immediately before
every re-enable, the runner requires a local player with no axe the passes could
use: none equipped while `Need To Equip Axe For Woodcutting` is on, and none
anywhere in the inventory while it is off. Every woodcutting pass needs such an
axe, so while the module is on for the check it cannot hit a tree, log or stump.
The check never swings, so area woodcutting has nothing to expand. The setup makes
this hold, so it fails only when an axe is equipped again: then every check SKIPs
and what was set up is put back; if one turns up later, that cycle and the
remaining on-side checks SKIP without the re-enable write, and the existing
restoration path puts the setting back. Do not equip or pick up an axe while the
command is running.

In addition to the runner's state, patch and error checks, the Woodcutting
descriptor checks:

| Check | Asserts |
|---|---|
| `woodcutting_timer_state_captured` | The timer count immediately before each disable request was recorded without changing it |
| `woodcutting_off_stopped_at_request` | In the frame of the disable request the module remains applied but starts no new woodcutting pass |
| `woodcutting_off_probes_as_expected` | The three Woodcutting hook handlers, the one `ValheimObject.RegistryChanged` handler and every `AutomaticWoodcutting` component are gone, `AllInstance.Count` is zero, and each Woodcutting-owned handler's own occurrence in its hook or event is zero |
| `woodcutting_timers_cleared` | A non-empty timer table observed immediately before disabling is empty while the module is off; if it was already empty, cleanup was not observable and the check SKIPs |
| `woodcutting_area_swing_cleared` | No area-woodcutting swing is open, and no extra hit is being applied, while the module is off |
| `woodcutting_on_patches_applied` | The patches under Woodcutting's Harmony id exactly match `Automatics.AutomaticWoodcutting.Patches`: the `Awake` postfixes of `TreeBase` and `TreeLog`, the `Destructible.Start` postfix for stumps and small trees, and the swing and damage patches of area woodcutting |
| `woodcutting_on_probes_at_baseline` | All three hook handlers and the `ValheimObject.RegistryChanged` handler are restored, every currently eligible unique `TreeBase`, `TreeLog`, stump and small tree GameObject with a ZDO has one component, and `AllInstance.Count` matches the active components rather than a stale baseline. A stump is a `Destructible` whose prefab is the stump prefab of a standing tree prefab, and a small tree a `Destructible` whose own prefab name the tree data names |
| `woodcutting_components_current_generation` | Every eligible tree, small tree, log and stump object, including inactive objects, has an `AutomaticWoodcutting` component stamped with the current activation generation |
| `woodcutting_interval_timer_resumed` | When `enable_automatic_woodcutting` is on and `woodcutting_interval` is above 0, its hook takes up its timer again within 30 frames of the enable; SKIP otherwise |

Turning the module off stops new woodcutting immediately and lets a pass already
accepted by a player hook finish its hit, including the damage and the axe's wear.
The patches, subscriptions and components are removed over the next few frames.
Turning it back on scans loaded inactive objects as well as active
ones. With no usable axe, the check hits no tree, wears no tool and creates no
drop.

#### `automatics_smoke_lifecycle repair`

`repair` uses the descriptor-driven runner for three off/on cycles of
`automatic_repair / module`. It checks Repair's one transpiler, both player
subscriptions and each named handler, immediate stopping, and the release of
both timer dictionaries, all item/piece/collider buffers and the
radius-lookup failure flag. It calls the three repair entries only while off,
with null arguments. It spawns nothing, damages or repairs nothing itself, and
changes no internal Repair setting. Normal repairs can continue while Repair is
enabled. A module that is off in your settings is turned on for the run and put
back off at the end. Repair clears both timer tables when it is turned off and
again when it is turned on, so before the baseline, and after every enable
(`repair_timers_resumed`), the run gives the module up to 30 frames to take both
timers up again; each cycle's timer cleanup then has something to see.
`repair_timers_resumed` FAILs when the running module does not take them up, and
SKIPs while the game is paused.

Each retained state has `<field>_observed` and `<field>_cleared` checks, prefixed
with `repair_`. The first reads the state immediately before disabling in each
cycle. Cleanup SKIPs if that state was already empty, FAILs if it cannot be read
or remains populated, and otherwise PASSes. `repair_stopped_entries` checks that
the stopped entries return without throwing. SKIP is unverified; this command
does not demonstrate successful repairs, tool payment or multiplayer RPC delivery.
The original module setting is restored under the shared session rules.

#### `automatics_smoke_lifecycle all`

`all` runs the command without an argument, named `pickup` in its lines, and then
with each module argument, in the order the usage lists them: `pickup`,
`mapping`, `door`, `farming`, `feeding`, `mining`, `repair`, `storage`, `supply`,
`woodcutting`. The cases run one after another, never side by side, and each is
a smoke run of its own, exactly as when you type it: it has its own `started`
line, sets up, checks, restores and hands back what it changed as described
above, and ends with its own Summary line.

Between cases nothing is held. The next case starts the way a command you type
starts, and only when such a command would be accepted: once the case before
has put its settings back, removed its test objects and finished every
hand-back. A hand-back still under way, or one that could not be done, makes
the next case wait; it is tried again once a second for up to 150 s. A failed
check, or a case that stopped on an exception, does not stop the cases after
it.

`all` stops before the next case when that case cannot start (its module
cannot be described, or it is still refused after 150 s) and when the player it
started with is no longer in the world (a logout or a death). The case that was
running then reports as it would alone, and a warning names the reason and the
cases that were not started. Since nothing is held between cases, a stopped
`all` leaves nothing behind that would refuse the next command. Starting `all`
while another smoke command runs is refused with `... all was not started:
<reason>`, as for any command.

The run ends with one more Summary line, over every case that ran: `cases`
counts them, the totals add up their checks, each failed and skipped check is
named after its case as `<case>:<check>`, and `casesNotStarted` lists the cases
a stop left out. A case that stopped before its own Summary line counts as one
FAIL, `<case>:case_reported`. A run cut short by a logout during `mapping` ends like this,
for example:

```text
[Automatics Smoke] automatics_smoke_lifecycle all stopped before door: the player the command started with is no longer in the world (a logout or a death); not started: door, farming, feeding, mining, repair, storage, supply, woodcutting
[Automatics Smoke] Summary (all): cases=2 total=42 passed=17 failed=0 skipped=25 failedChecks=[] skippedChecks=[mapping:mapping_off_state_disabled, mapping:mapping_off_stopped_at_request, …] casesNotStarted=[door, farming, feeding, mining, repair, storage, supply, woodcutting]
```

### Processing smoke command

`automatics_smoke_processing` takes one subcommand: `rpc`, `toggle`, `config`,
`skill`, `store` or `collect` (the last five are described after `rpc`). Each is
one smoke run. `all` runs each of them in turn (described after `collect`).

Every subcommand but `config` sets up what it needs before it places anything,
and puts it back when the run ends:

- Automatic Processing is turned on for the run when it is off (`toggle` turns
  it on through the setting it round-trips; see there).
- `automatic_pickup_range` is set to 1, its minimum, so that the test objects can
  stand 4 m ahead of you without Automatic Pickup reaching what a station drops.
- The settings a check depends on are set for the stations it uses: the wood
  chest is added to `allow_container`, the process under test to the station's
  `allow_processing_by_*`, the station's `container_search_range_by_*` is set to
  4 (past the chest, 2.5 m away, and short enough that little else is in reach),
  and the counts the check seeds above (`material_count_of_suppress_processing`,
  `fuel_count_of_suppress_processing`) are lowered to 1 when they are higher.
  The subcommands below name what else they set.
- When a container or processing station that isn't the check's own is within
  reach of where the test objects go, you are moved to clear ground first (see
  "Safety of the console commands" above), and moved back before the Summary.

Each setting is written only when it does not already hold the value the check
needs. The run ends with `processing_settings_restored`: PASS when every setting
it wrote is back, SKIP when you changed one meanwhile (yours is kept) or the run
was abandoned, FAIL when one could not be put back. It is missing when the run
wrote nothing. When a setting cannot be written, the checks SKIP and name it.

`automatics_smoke_processing rpc` checks that Automatic Processing keeps every
item when it moves one from a chest into a station. Processing does that by
removing the item from the chest and invoking the station's intake RPC —
`Smelter.RPC_AddOre(string, bool)`, `CookingStation.RPC_AddItem(string, bool)`
or `Fermenter.RPC_AddItem(int, bool)` — and these are the paths that break when
the game changes an RPC's payload. It sets up what it needs as described above.

What it does:

- It places a wood chest 4 m ahead of you, then a smelter to its right, a stone
  oven to its left and a fermenter beyond it, each 2.5 m from the chest. When
  Automatic Pickup may be running and its range could not be set for the run,
  everything is placed further out, beyond `automatic_pickup_range` + 2 m from
  you.
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
  - Automatic Processing could not be turned on for the run, or did not start
  - a setting could not be set for the run (the message names it)
  - a prefab is missing
  - no material can be kept apart
  - something that isn't the check's own is within reach, and no clear ground
    to move you to was found within 45 m
  - Processing does not find the test chest (a ward or the chest's access rules
    can hide it)
  - the materials don't fit into the chest

  A world that unloads mid-run makes the checks SKIP as inconclusive.
- `NOT-RUN`: Processing found a container other than the test chest within a
  station's reach on a watched frame (a cart, a ship, a chest just built). The
  message names it and its coordinates, and the test objects go through the
  keep rules below. Or: everything was in place, but the entry took nothing on
  both
  attempts and nothing reached the station within 30 frames. The message says
  what the station looked like.

Where to stand: anywhere in a single-player world; the run finds its own clear
ground. Before placing anything it looks for any container that isn't its own
within a test station's `container_search_range_by_*` + 2 m, and any processing
station that isn't its own whose container range (+ 2 m) reaches the test chest.
If it finds one, it moves you to the first spot 15, 30 or 45 m away (12
bearings each) where nothing like that is in reach, keeping your facing, and
places its objects from there. Only when no such spot exists does it SKIP; it
then lists what it found in a warning and says how much clear space it needs.
After placing its objects it asks Processing itself which containers each
station finds; if that is anything but the test chest, the run stops in the
same frame and removes its objects before any station has run an update.

A passing run looks like this in the console and in the BepInEx log (item names
depend on the game's conversions and your language):

```text
[Automatics Smoke] automatics_smoke_processing rpc started: it places a chest, a smelter, a stone oven and a fermenter ahead of you, drives each station's intake once from the chest, watches them for 30 frames and removes them; the settings it needs are set for the run and put back afterwards
[Automatics Smoke] automatics_smoke_processing rpc: automatic_processing/module was off, so it is turned on for the run and put back afterwards
[Automatics Smoke] automatics_smoke_processing rpc: the test chest is at (102.3, 31.0, -48.7), 4.0 m ahead of you, with the test stations 2.5 m from it
[Automatics Smoke] PASS processing_rpc_smelter_add_ore: Copper ore reached the smelter at frame 0 (the frame of the call); the ledger held on every frame from 0 to 30 (at frame 30: chest 3 + smelter 1 + products 0 = 4 put in)
[Automatics Smoke] PASS processing_rpc_cooking_station_add_item: Bread dough reached the stone oven at frame 0 (the frame of the call); the ledger held on every frame from 0 to 30 (at frame 30: chest 3 + stone oven 1 + products 0 = 4 put in)
[Automatics Smoke] PASS processing_rpc_fermenter_add_item: Mead base: Minor healing reached the fermenter at frame 0 (the frame of the call); the ledger held on every frame from 0 to 30 (at frame 30: chest 3 + fermenter 1 + products 0 = 4 put in)
[Automatics Smoke] PASS processing_settings_restored: automatic_processing/module was put back from Enabled to Disabled; automatic_pickup/automatic_pickup_range was put back from 1 to 4; automatic_processing/container_search_range_by_piece_smelter was put back from 4 to 8; automatic_processing/container_search_range_by_piece_oven was put back from 4 to 8; automatic_processing/container_search_range_by_piece_fermenter was put back from 4 to 8
[Automatics Smoke] Summary: total=4 passed=4 failed=0 skipped=0 notRun=0 failedChecks=[] skippedChecks=[] notRunChecks=[]
```

The figures at frame 30 vary. A station's own update can take more of its
material from the chest while it is watched. The sum still has to equal what was
put in.

Other results look like this:

```text
[Automatics Smoke] SKIP processing_rpc_smelter_add_ore: 2 container(s) or processing station(s) that are not the check's own are within reach of where its test objects go (the first: Chest at (98.1, 30.8, -44.0), 5.2 m from where the test smelter goes), and no clear ground to move you to was found (no clear site within 45 m of you (37 spots tried: ...)); stand where nothing of yours is within about 17 m of you and run it again
[Automatics Smoke] NOT-RUN processing_rpc_fermenter_add_item: the entry returned without taking anything on both attempts, and nothing reached the fermenter within 30 frames (the chest still holds 4 of the 4 put in; owner=True, content=0, exposed=False, material_count_of_suppress_processing=1, product_stacks_of_suppress_processing=0, supply_only_when_materials_run_out=False)
[Automatics Smoke] FAIL processing_rpc_smelter_add_ore: the ledger broke at frame 0: chest 3 + smelter 0 + products 0 = 3, but 4 were put in (1 missing); at frame 30: chest 3 + smelter 0 + products 0 = 3; no delivery was seen
```

Safety, on top of the rules above:

- It writes only the settings named above, through the session, and it never
  touches your inventory. The test materials are added only to its own chest,
  with the game's `Inventory.AddItem`. While Automatic Processing is on for the
  run, it also acts on your own stations nearby, as it would if you had turned
  it on.
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
starting value. When the module is off in your settings, the run turns it on
first through the same setting, and it ends back off. The other settings the
test smelter needs are set for the run as described above, together with the
smelter's `*_refuel_only_when_materials_supplied`, turned off; they go back
with the module setting and are reported as `processing_settings_restored`.

| Check | Asserts |
|---|---|
| `processing_toggle_initial_state_settled` | The registry reports `Enabled`, matching the setting, before the round trip starts (after the module was turned on for the run, when it was off; the note says so) |
| `processing_toggle_off_state_disabled` | The registry reports `Disabled` after the disable request |
| `processing_toggle_off_patches_removed` | No patch is left under Processing's Harmony id (SKIP when runtime patch removal is unavailable) |
| `processing_toggle_on_state_enabled` | The registry reports `Enabled` after the enable request |
| `processing_toggle_on_patches_applied` | The patches under Processing's Harmony id are exactly the (target, patch method) pairs `Automatics.AutomaticProcessing.Patches` declares (13 on 11 methods, 3 of them transpilers) |
| `processing_toggle_on_quickrefuel_acts` | Within 10 s of the enable, the game's own `Smelter.UpdateSmelter` calls the injected `SmelterProcess.QuickRefuel` for a test smelter, and it refuels it from the test chest |
| `processing_toggle_on_quickrefuel_catchup_no_per_unit_rpc` | After the refuel above passed, the same smelter is caught up over 3 fuel units in one frame: 3 direct `SmelterProcess.QuickRefuel` calls each take one fuel from the test chest and add one to the smelter, and together they invoke the smelter's fuel RPC (`RPC_AddFuel`) at most once and play its fuel-added effect once (the first refuel's), not once per unit |
| `processing_toggle_restored_initial_state` | The setting is back at its starting value and the registry reports the matching state |
| `processing_toggle_no_errors_logged` | No error or exception was logged while the check ran |
| `processing_settings_restored` | The other settings the run wrote are back (missing when it wrote none) |

The test smelter and its chest are placed while the module is off, under the
same rules as `rpc`: where to stand (and the move to clear ground), what is
SKIPped, the exact ledger, the per-frame check that Processing finds only the
test chest, and the removal. They are removed as the run ends, before the
settings are put back.
Placement and isolation are both checked before the module is turned back on,
so its first update of the smelter can find only the test chest. If any of that
fails, whatever was placed is removed at once, before the enable. The per-frame
check runs on every frame the smelter stands, including the frames of the enable
wait and the last watched frame. When it finds another container, the smelter's
check is NOT-RUN and the test objects go through the keep rules at once. The
chest gets `fuel_count_of_suppress_processing` + 5 of the smelter's fuel and
nothing else. On the test smelter only, `m_maxFuel` is held at 1, so the
ordinary refuel stays out and only the injected `QuickRefuel` can refuel it. Its
catch-up time is set to 5 s, since `QuickRefuel` acts only while the smelter's
update is catching up. A postfix under the smoke plugin's own Harmony id counts
the calls to `SmelterProcess.QuickRefuel` for the test smelter. It patches the
mod's method, never a game method, and it is removed at the end. A direct call
made before the enable, which returns at once, checks that the postfix counts
at all.

The catch-up check runs right after the refuel check has seen the smelter refuelled
and its ledger hold, with the module still on. It waits until a second has passed
since the last call that refuelled the smelter, because `QuickRefuel` plays its
effect once per smelter and second, and then calls `SmelterProcess.QuickRefuel`
3 times in one frame. Each call gets no fuel left, as the smelter's loop does after
burning the unit before, and every unit but the last is burnt between the calls the
same way, so the ordinary refuel stays out. The ledger counts the burnt units as what
the fuel turned into. The check counts the calls of the smelter's `RPC_AddFuel`
handler and the plays of its fuel-added effect without patching a game method: for
the length of the check it wraps the handler through the smelter's own network view
and swaps the smelter's effect list for one that spawns a counting object, and it
puts both back afterwards. A control first invokes the game's own `RPC_AddFuel` once,
which both counts have to see once, or the check is NOT-RUN. It FAILs when the fuel
that left the chest differs from the fuel added, when the smelter does not hold
exactly the last unit afterwards, when the fuel RPC was invoked more than once, or
when the effect played more than once (or not exactly once, when the wait for a fresh
second finished). It is NOT-RUN when the 3 calls did not all add fuel.

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
- When `automatic_processing / module` is not `Enabled` when it starts, the run
  turns it on through the same session and puts it back off at the end. While
  it is on, Processing also acts on your own stations nearby, as it would if
  you had turned it on.
- The check's observer patch is removed only once `UnpatchSelf` succeeds. If
  removing it fails, a warning says so, the removal is kept as an obligation and
  retried at the next hand-back, and a new smoke run is refused meanwhile.

A passing run looks like this:

```text
[Automatics Smoke] automatics_smoke_processing toggle started: automatic_processing/module is turned off and on again through its setting, a test smelter is watched while it is on, and the setting and the others the smelter needs for the run are restored
[Automatics Smoke] automatics_smoke_processing toggle: the test chest is at (102.3, 31.0, -48.7), 4.0 m ahead of you, with the test stations 2.5 m from it
[Automatics Smoke] PASS processing_toggle_initial_state_settled
[Automatics Smoke] PASS processing_toggle_off_state_disabled
[Automatics Smoke] PASS processing_toggle_off_patches_removed: no patch is left under "net.eidee.valheim.automatics.automatic-processing"
[Automatics Smoke] PASS processing_toggle_on_state_enabled
[Automatics Smoke] PASS processing_toggle_on_patches_applied: 13 patches on 11 methods (3 transpilers) under "net.eidee.valheim.automatics.automatic-processing", exactly the pairs Automatics.AutomaticProcessing.Patches declares
[Automatics Smoke] PASS processing_toggle_on_quickrefuel_acts: 0.8 s after the enable, the game's own Smelter.UpdateSmelter called the injected SmelterProcess.QuickRefuel for the test smelter (1 call(s)) and it moved Coal from the test chest into the smelter's fuel; the ledger held on every frame (chest 2 + smelter fuel 1 + burnt in the catch-up trial 3 = 6 put in)
[Automatics Smoke] PASS processing_toggle_on_quickrefuel_catchup_no_per_unit_rpc: 3 QuickRefuel calls in one frame moved 3 Coal from the test chest into the smelter's fuel; the fuel RPC was invoked 0 time(s) and the fuel-added effect played 1 time(s); the game's own RPC, invoked once as a control, ran the handler 1 time(s) and played the effect 1 time(s)
[Automatics Smoke] PASS processing_toggle_restored_initial_state: automatic_processing/module is already back at Enabled
[Automatics Smoke] PASS processing_toggle_no_errors_logged: no error or exception was logged while the check ran (the observer saw its own probe once)
[Automatics Smoke] PASS processing_settings_restored: automatic_pickup/automatic_pickup_range is back at 4; automatic_processing/container_search_range_by_piece_smelter is back at 8
[Automatics Smoke] Summary: total=10 passed=10 failed=0 skipped=0 notRun=0 failedChecks=[] skippedChecks=[] notRunChecks=[]
```

The Harmony id, the timing and the item name depend on your build and language.
With the module off in your settings, `processing_toggle_initial_state_settled`
carries the note that it was turned on for the run, and
`processing_toggle_restored_initial_state` reports it put back to `Disabled`.

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

#### `automatics_smoke_processing skill`

Checks who gains the skill and the bonus food for what Automatic Processing does
at a cooking station. The player who built the station gains what the game gives
for putting food in by hand (a skill raise of 0.4) and for taking cooked food out
(a raise of 0.6 and a bonus roll), and only in that player's own game. Besides
the setup described above, it allows Store for the oven and turns its
`*_store_only_if_product_exists` off for the run.

What it does:

- It places a wood chest and a stone oven as `rpc` does, with the same rules for
  where to stand, and puts `material_count_of_suppress_processing` + 5 of one of
  the oven's materials into the chest.
- It sets who built the oven through the piece's creator field: nobody, a player
  id that isn't yours, and then you. For each, it calls
  `CookingStationProcess.Craft`, turns the food the oven took into its cooked
  product as the oven's own update would, and calls
  `CookingStationProcess.Store` for that slot. A Harmony prefix on
  `Player.RaiseSkill` records every raise of the oven's skill meanwhile, with the
  value it was called with, before status effects scale it.
- It makes the bonus roll certain by raising the game's
  `InventoryGui.m_craftBonusChance`, or impossible by setting it to 0. Before the
  first call, your skill level is lifted to at least 1, so the roll has a skill
  factor whoever the builder is.
- Once, it fills the chest with the cooked product first, so that the chest has
  room for exactly one more item.
- Cooked food that `Store` could not put away when it was finished is put away
  by the oven's update on a later pass. For that, the run leaves cooked food in
  one slot of the oven and burns every other slot (so the oven has no free slot
  to feed), makes the bonus roll certain, and runs the game's own
  `CookingStation.UpdateCooking`, which Processing patches: with the chest full
  of the product, then, once the chest has room for exactly the item and its
  bonus, twice more; and once more for a second cooked item with the oven's fuel
  at 0, when the game's own cooking loop does not run. Before each of the two
  cooked items it puts one more of the material into the chest, so that after
  the Craft that cooks it the chest holds only the count Processing leaves alone
  and the update has nothing to feed to a free slot.
- Everything happens in the frame the objects are placed, so the oven's own
  update runs only when the run calls it. The bonus food and the filler are
  taken out of the chest right after each call, also when the call throws, so
  only the cooked item that left the oven's slot stays, and the burnt slots the
  run made are emptied. The chest and the oven are then removed under the `rpc`
  rules.

| Check | Asserts |
|---|---|
| `processing_skill_no_builder` | With nobody as the builder, Craft and Store raise no skill, and Store puts exactly one item in even with a certain bonus roll |
| `processing_skill_other_builder` | The same, with another player as the builder |
| `processing_skill_builder` | With you as the builder and an impossible bonus roll, Craft raises the skill once by 0.4, and Store raises it once by 0.6 and puts exactly one item in |
| `processing_skill_builder_bonus` | With you as the builder and a certain bonus roll, Store puts in one item plus the game's bonus amount, with the same raises |
| `processing_skill_builder_bonus_no_room` | With room for one item and a certain bonus roll, Store still puts the cooked item in, without its bonus, empties the slot and raises the skill by 0.6, and nothing appears on the ground |
| `processing_skill_done_slot_waits_for_room` | With the chest full of the product and cooked food in a slot, the oven's update leaves that slot and the burnt slots as they are, puts nothing in the chest and raises no skill |
| `processing_skill_done_slot_stored_on_later_pass` | Once the chest has room for the item and its bonus, the next update stores the cooked food once, empties the slot, puts the item and one bonus roll's worth in the chest and raises the skill once by 0.6; a second update stores and raises nothing more; the burnt slots stay as they were |
| `processing_skill_done_slot_stored_without_fuel` | With the oven's fuel at 0, one update stores the cooked food the same way, with one raise and the burnt slots untouched |
| `processing_skill_state_restored` | Your skill level and progress, the bonus chance and the prefix are back as they were |

A precondition that is missing SKIPs the run under `processing_skill_setup`.
Besides the `rpc` preconditions, these count: the oven raises no skill in this
game build, the bonus roll cannot be made certain, or your character has no
player id yet, which the run cannot give it. The two bonus checks SKIP when
the oven gives no bonus food in this game build. The three finished-slot checks
SKIP when the test chest does not hold exactly the one more material they need
(after taking out what an earlier check left unused), when the oven has no
burnt item in this game build, or when `CookingStation.UpdateCooking` is not
found; the one with no fuel SKIPs when the oven uses no fuel. A check is NOT-RUN when Craft took nothing, a slot of the
oven was not empty or the chest could not be filled to room for exactly one item
(for the finished-slot checks, to be full, or to hold room for exactly the item
and its bonus).

```text
[Automatics Smoke] automatics_smoke_processing skill started: it places a chest and a stone oven ahead of you, drives the oven's Craft and Store entries from the chest with nobody, another player and you as the oven's builder and runs the oven's own update over cooked food that could not be stored, and removes them; your cooking skill, the game's bonus chance and the settings it needs for the run are put back afterwards
[Automatics Smoke] automatics_smoke_processing skill: the test chest is at (102.3, 31.0, -48.7), 4.0 m ahead of you, with the test stations 2.5 m from it
[Automatics Smoke] PASS processing_skill_no_builder: with no builder, Craft took one Bread dough and Store put one Bread into the chest with the bonus roll made certain, and neither raised the Cooking skill
[Automatics Smoke] PASS processing_skill_other_builder: with another player as the builder, Craft took one Bread dough and Store put one Bread into the chest with the bonus roll made certain, and neither raised the Cooking skill
[Automatics Smoke] PASS processing_skill_builder: with you as the builder and the bonus roll made impossible, Craft raised the Cooking skill by 0.4 and Store by 0.6 and put exactly one Bread into the chest
[Automatics Smoke] PASS processing_skill_builder_bonus: with you as the builder and the bonus roll made certain, Store put 2 Bread (one and the bonus of 1) into the chest, and the Cooking skill rose by 0.4 and 0.6; the bonus was taken out again
[Automatics Smoke] PASS processing_skill_builder_bonus_no_room: with room for one item in the chest and the bonus roll made certain, Store put the cooked Bread in without its bonus, emptied the slot and raised the Cooking skill by 0.6; the filler was taken out again
[Automatics Smoke] PASS processing_skill_done_slot_waits_for_room: with the chest full of Bread, the oven's update left the finished slot and the burnt slots as they were, put nothing in the chest and raised no skill
[Automatics Smoke] PASS processing_skill_done_slot_stored_on_later_pass: once the chest had room, the oven's update stored the finished Bread (2 with the bonus), emptied its slot and raised the Cooking skill once by 0.6; a second update stored nothing more and raised nothing; the burnt slots were left as they were
[Automatics Smoke] PASS processing_skill_done_slot_stored_without_fuel: with no fuel in the oven, one update stored the finished Bread (2 with the bonus), emptied its slot with one raise of the Cooking skill by 0.6, and left the burnt slots as they were
[Automatics Smoke] PASS processing_skill_state_restored: the Cooking skill is back at level 12 with progress 3.5, the bonus chance at 0.25, and the Player.RaiseSkill prefix is removed
[Automatics Smoke] PASS processing_settings_restored: automatic_pickup/automatic_pickup_range was put back from 1 to 4; automatic_processing/container_search_range_by_piece_oven was put back from 4 to 8
[Automatics Smoke] Summary: total=10 passed=10 failed=0 skipped=0 notRun=0 failedChecks=[] skippedChecks=[] notRunChecks=[]
```

The item names, the bonus amount, the skill figures and the bonus chance depend
on your game build, language and character.

Safety, on top of the `rpc` rules:

- Your skill level and progress, the bonus chance and the prefix are put back
  when the run ends or throws, and by the session if the smoke plugin stops
  mid-run. A raise during the run can show the game's skill-up message; the
  level is put back all the same. If you have never used the skill, the skills
  panel lists it at level 0 afterwards. If your character is gone before the
  skill can be put back (a logout mid-run), a warning says so; check the skill
  yourself.
- The run does not touch your inventory. The filler and the bonus food are
  added to and taken from the check's own chest only.

#### `automatics_smoke_processing store`

Checks that Automatic Processing stores a product only where it fits. Before,
a store into a full chest still called the game's `Inventory.AddItem`, which
logged a warning and saved the chest on every attempt. Besides the setup
described above, it allows Store for the smelter for the run.

What it does:

- It places a wood chest and a smelter as `rpc` does, with the same rules for
  where to stand, and fills every slot of the chest with full stacks of the
  product of one of the smelter's conversions. Filling never calls an add that
  does not fit.
- It calls `SmelterProcess.Store`, the entry the game's `Smelter.Spawn` reaches,
  once with the chest full and once after taking one product out, so the chest
  has room for exactly one, offering three.
- While each call runs, a container observer counts every call of
  `Inventory.AddItem(ItemDrop.ItemData)` on the chest's inventory (the adds by
  prefab go through it) and every `Container.Save` of the chest, through Harmony
  prefixes applied before the call and removed right after it. A warning
  listener records every warning or error Unity logs meanwhile. Before each
  call it logs a deliberate warning, `AUTOMATICS_SMOKE_PROCESSING_STORE_PROBE`,
  which it must receive, so an empty list proves something. The deliberate line
  in the log is not a problem.
- Everything happens in the frame the objects are placed. The product the call
  drops at the smelter is the run's own and is removed at once; the filler and
  the stored product are taken out of the chest before the run ends, and the
  chest and the smelter are removed under the `rpc` rules.

| Check | Asserts |
|---|---|
| `processing_store_full_chest_untouched` | With the chest full, Store returns false, the chest gets nothing, no add is called on its inventory, it is not saved, nothing is dropped, and no warning is logged |
| `processing_store_stores_what_fits` | With room for one, Store returns true, puts one of three into the chest with exactly one add and no warning, and drops the other two at the smelter as it always has |

A precondition that is missing SKIPs the run under `processing_store_setup`.
Besides the `rpc` preconditions, a chest that cannot be filled counts. A check is
NOT-RUN when the observer cannot patch the game or the chest cannot be left
with room for exactly one. A check FAILs when the warning listener did not
receive its own probe.

```text
[Automatics Smoke] automatics_smoke_processing store started: it places a chest and a smelter ahead of you, fills the chest with the smelter's product, calls the smelter's Store entry with the chest full and with room for one, and removes them; the settings it needs are set for the run and put back afterwards
[Automatics Smoke] automatics_smoke_processing store: the test chest is at (102.3, 31.0, -48.7), 4.0 m ahead of you, with the test stations 2.5 m from it
[Automatics Smoke] PASS processing_store_full_chest_untouched: with the chest full of Copper, Store returned false and the chest got no add, no save and no warning
[Automatics Smoke] PASS processing_store_stores_what_fits: with room for one Copper, Store put one of 3 into the chest with one add and no warning, and dropped the other 2 at the smelter, which the check removed
[Automatics Smoke] PASS processing_settings_restored: automatic_pickup/automatic_pickup_range was put back from 1 to 4; automatic_processing/container_search_range_by_piece_smelter was put back from 4 to 8
[Automatics Smoke] Summary: total=3 passed=3 failed=0 skipped=0 notRun=0 failedChecks=[] skippedChecks=[] notRunChecks=[]
```

The item name depends on the game's conversions and your language.

Safety, on top of the `rpc` rules:

- The run does not touch your inventory. The filler is added to and taken from
  the check's own chest only.
- The drop Store makes at the smelter for what does not fit is removed in the
  same frame; it is the only drop the run removes.
- The Harmony prefixes are removed right after each call, also when it throws.

#### `automatics_smoke_processing collect`

Checks that what Automatic Processing stores from a piece that collects an item
over time (a beehive, a sap extractor, a wisp fountain) is what taking it out by
hand yields, the world's resource rate included, and that it stores only whole
units. Before, one stored item stood for each unit at any resource rate, a
beehive could store more units than it holds, and a sap extractor that filled up
while nothing could be stored never stored again. The command sets the game's
resource rate for the run and puts it back. Besides the setup described above,
it allows Store for each piece and, for the run, turns each piece's
`*_store_only_if_product_exists` off and sets its
`*_product_stacks_of_suppress_processing` to 0.

What it does:

- It places a wood chest, a beehive, a sap extractor and a wisp fountain as
  `rpc` does, with the same rules for where to stand.
- It sets the world's resource rate (`Game.m_resourceRate`) to 3, after
  registering the hand-back that puts it back.
- For each piece it takes one unit out as a player's hand does, through the
  piece's own RPC: `Beehive.RPC_Extract` or `SapCollector.RPC_Extract` with one
  unit in the piece, and `Pickable.RPC_Pick` (without a skill bonus) on a wisp the
  fountain lures, which the check spawns. What that drops is the yield of one unit
  at that rate. The drops and the wisp are removed at once.
- It calls each piece's Store entry (`BeehiveProcess.Store`,
  `SapCollectorProcess.Store`, `WispSpawnerProcess.Store`) against the chest. For
  the sap extractor it also runs the piece's own update tick,
  `SapCollector.UpdateTick`, through reflection, so the patches on it run too. To
  leave the chest with an exact room, it fills every slot with full stacks of the
  piece's item and takes that room out again.
- Everything happens in the frame the objects are placed, so no piece runs an
  update of its own. What the run stored is taken out of the chest and every
  piece is emptied before the chest and the pieces are removed under the `rpc`
  rules.

| Check | Asserts |
|---|---|
| `processing_collect_beehive_yield` | With one unit in the beehive and one new unit, Store returns true, puts twice the hand yield of one unit into the chest and empties the hive |
| `processing_collect_beehive_whole_units` | With room in the chest for one unit and all but one item of a second, and 3 new units, Store puts one unit's yield in and leaves 2 units in the hive; no part of a unit is stored |
| `processing_collect_beehive_clamped_to_maximum` | With 3 more new units than the hive holds, Store puts the hive's maximum in and keeps none of the rest, as the game's own `Beehive.IncreseLevel` would |
| `processing_collect_sapcollector_yield` | The yield check, for the sap extractor |
| `processing_collect_sapcollector_whole_units` | The whole-unit check, for the sap extractor |
| `processing_collect_sapcollector_resumes_when_full` | At its maximum level with the chest full, a tick of the piece's own update keeps every unit; once the chest has room for one unit, the next tick stores that unit |
| `processing_collect_wisplure_yield` | Store puts the hand yield of one wisp into the chest and returns true |
| `processing_collect_wisplure_whole_unit` | With room for all but one item of a wisp's yield, Store puts nothing in and returns false, so the fountain lures the wisp as usual |
| `processing_collect_rate_restored` | The world's resource rate is back at its value before the run |

No check may leave a new item drop.

A precondition that is missing SKIPs the run under `processing_collect_setup`,
or the checks of the piece it concerns with the same message. Besides the `rpc`
preconditions, these count when the run could not set them: a piece's
`*_store_only_if_product_exists` is on, or its
`*_product_stacks_of_suppress_processing` is above 0 (that limit, not the chest,
would decide what is stored). A yield or whole-unit check SKIPs when one unit
still yields one item at the test rate, because the game does not scale that
item. A piece's checks are NOT-RUN when taking a unit out by hand dropped nothing
of its item, and a check is NOT-RUN when the chest cannot be left with the room
it needs.

```text
[Automatics Smoke] automatics_smoke_processing collect started: it places a chest, a beehive, a sap extractor and a wisp fountain ahead of you, sets the world's resource rate to 3, takes one unit out of each by hand, drives each one's Store entry into the chest, and removes them; the resource rate and the settings it needs for the run are put back afterwards
[Automatics Smoke] automatics_smoke_processing collect: the test chest is at (102.3, 31.0, -48.7), 4.0 m ahead of you, with the test stations 2.5 m from it
[Automatics Smoke] PASS processing_collect_beehive_yield: at a resource rate of 3, one unit of the beehive yields 3 Honey by hand, and Store put the 2 units it held into the chest as 6 Honey and emptied it
[Automatics Smoke] PASS processing_collect_beehive_whole_units: with room for 5 Honey in the chest and 3 new units, Store put one unit in as 3 Honey and left 2 units in the beehive
[Automatics Smoke] PASS processing_collect_beehive_clamped_to_maximum: with 7 new units and room in the chest, Store put the beehive's maximum of 4 units in as 12 Honey and kept none of the rest
[Automatics Smoke] PASS processing_collect_sapcollector_yield: at a resource rate of 3, one unit of the sap extractor yields 3 Sap by hand, and Store put the 2 units it held into the chest as 6 Sap and emptied it
[Automatics Smoke] PASS processing_collect_sapcollector_whole_units: with room for 5 Sap in the chest and 3 new units, Store put one unit in as 3 Sap and left 2 units in the sap extractor
[Automatics Smoke] PASS processing_collect_sapcollector_resumes_when_full: full at its maximum of 4 units with the chest full, the sap extractor kept them on a tick; with room for one unit, its next tick stored 3 Sap and left 3
[Automatics Smoke] PASS processing_collect_wisplure_yield: at a resource rate of 3, picking a lured wisp by hand yields 3 Wisp, and Store put 3 into the chest
[Automatics Smoke] PASS processing_collect_wisplure_whole_unit: with room for 2 Wisp in the chest, Store put none of a wisp's 3 in and returned false, so the fountain lures the wisp as usual
[Automatics Smoke] PASS processing_collect_rate_restored: the world's resource rate is back at 1
[Automatics Smoke] PASS processing_settings_restored: automatic_pickup/automatic_pickup_range was put back from 1 to 4; automatic_processing/container_search_range_by_piece_beehive was put back from 4 to 8; automatic_processing/container_search_range_by_piece_sapcollector was put back from 4 to 8; …
[Automatics Smoke] Summary: total=10 passed=10 failed=0 skipped=0 notRun=0 failedChecks=[] skippedChecks=[] notRunChecks=[]
```

The item names, the yields and the units a piece holds depend on your game build
and language.

Safety, on top of the `rpc` rules:

- The run does not touch your inventory. The filler is added to and taken from
  the check's own chest only.
- The resource rate is put back when the run ends or throws, and by the session
  if the smoke plugin stops mid-run. The run changes and restores it within one
  frame, so no other part of the game acts on the changed rate.
- The drops the hand extraction makes, and the wisp it picks, are the run's own
  and are removed in the same frame; they are the only objects the run removes
  besides its chest and pieces. A drop Store made would be reported, never
  removed.

#### `automatics_smoke_processing all`

`all` runs `rpc`, `toggle`, `config`, `skill`, `store` and `collect`, in that
order. The cases run one after another, never side by side, and each is a
smoke run of its own, exactly as when you type it: it has its own `started`
line, sets up, checks, restores and hands back what it changed as described
above, and ends with its own Summary line.

Between cases nothing is held. The next case starts the way a command you type
starts, and only when such a command would be accepted: once the case before
has put its settings back, removed its test objects and finished every
hand-back, the return to where it started included when it moved you. A
hand-back still under way, such as that return when it goes on after the
case's Summary line, or one that could not be done, makes the next case wait;
it is tried again once a second for up to 150 s. A failed check, or a case that
stopped on an exception, does not stop the cases after it.

`all` stops before the next case when that case is still refused after 150 s
and when the player it started with is no longer in the world (a logout or a
death). The case that was running then reports as it would alone, and a warning
names the reason and the cases that were not started. Since nothing is held
between cases, a stopped `all` leaves nothing behind that would refuse the next
command. Starting `all` while another smoke command runs is refused with `...
all was not started: <reason>`, as for any command.

The run ends with one more Summary line, over every case that ran: `cases`
counts them, the totals add up their checks, each failed, skipped and NOT-RUN
check is named after its case as `<case>:<check>`, and `casesNotStarted` lists
the cases a stop left out. A case that stopped before its own Summary line
counts as one FAIL, `<case>:case_reported`. A passing run ends like this:

```text
[Automatics Smoke] Summary (all): cases=6 total=39 passed=39 failed=0 skipped=0 notRun=0 failedChecks=[] skippedChecks=[] notRunChecks=[] casesNotStarted=[]
```

### Feature smoke command

`automatics_smoke_features all` runs every section below, and
`automatics_smoke_features <section>` runs one of them: `pickup`, `pin`,
`navhotkey`, `aimbar`, `mapfilter`, `pintooltip`, `mining`, `planting`, `farming`, `guide`,
`compass`, `supply`, `woodcutting`, `woodcuttingarea` or `repair`. Run it from the in-game console while
a single-player world is loaded. Each section drives the real game code path of
one feature against objects it spawns next to the player. The run ends with one
`[Automatics Smoke] Summary: ...` line over every section it ran. A section that
has no checks yet logs one `SKIP <section>_section: not implemented yet`, and an
exception inside a section is a `FAIL <section>_section` line, after which the
next section still runs. A section that moved you is followed by your return to
the spot the command started from, also when it stopped on an exception, so
every section starts from there.

Sections set up what their checks need instead of asking you to, with the
shared helpers and under the rules in *Safety of the console commands* above.
Most turn the module they test on for the run when it is off, and set the
settings their checks depend on; some also move you to clear ground or put
inventory items aside. A setting change is recorded before it is made and
refused when the record cannot be written, and every change is undone when the
section ends (or at the plugin's hand-back triggers); a setting you changed
meanwhile keeps your value. A section that changed a setting reports putting it
back in a check of its own. When a setup step fails, the checks that need it
SKIP and name what failed. What each section changes, and what it still leaves
to you, is given in its own rows and the paragraphs that follow them.
Test drops are marked non-persistent and are recorded with the session, so a run
that is cut short still removes them. Items a check picked up are removed from
your inventory only when the count of that item grew by exactly what the check
spawned; otherwise they stay and the log says so.

#### `pickup`

The Wood and Stone test drops start 0.3 m above the ground at their spot, not at
your height: one placed where the ground is higher than your feet would start
inside the terrain and fall through it.

| Check | Asserts |
|---|---|
| `pickup_key_default_is_left_shift_e` | The `pickup_all_nearby_key` entry's default value is Left Shift + E and BepInEx writes it as `E + LeftShift`. Runs without a world and with the module off; the note gives the current value |
| `pickup_setup` | The section's setup, as a note: whether Automatic Pickup had to be turned on, whether you were moved to a clear site, and what was put aside from your inventory. SKIP when no clear site was found within 45 m: the checks then run where you stand, and the reason lists the sites tried |
| `pickup_hover_text_shows_key` | `ItemDrop.GetHoverText()` of a spawned Wood drop contains the line the hover patch adds: the key as BepInEx prints it and the translated message with the item's name (SKIP when the key is not set and could not be bound for the run) |
| `pickup_key_pass_same_name_only` | Two Wood drops and one Stone drop lie 1.5 m to your right, a metre apart, with `m_autoPickup` cleared, so neither vanilla auto-pickup nor periodic pickup can take them. A key pass requested on the first Wood drop, through the call Automatic Pickup makes when the key goes down, takes both Wood drops, the Wood in your inventory grows by exactly 2, and the Stone drop is left. The Wood drops vanishing without the inventory growing is a FAIL, and so is the Stone drop being taken; more Wood arriving than the check spawned, or the Stone in your inventory changing while the Stone drop stays, is a SKIP, because Wood or Stone of your own was nearby (SKIP when `automatic_pickup_range` could not be set for the run and is below 2.5 m) |
| `pickup_key_and_interval_in_one_pass` | The same three drops, 0.6 m apart, placed to your left half a metre beyond vanilla auto-pickup's reach (SKIP, inconclusive, when a Wood drop has left `automatic_pickup_range` before the requests; a FAIL gives each drop's distance at the request). In one frame the first Wood drop is given `m_autoPickup` and both the interval's and the key's request are made, through the calls `FixedUpdate` and `Update` make. The one pass that follows takes the hovered Wood drop for the interval, still takes the other Wood drop by its name for the key, and leaves the Stone drop; the same inventory rules apply (SKIP when the range could not be set for the run and does not reach past vanilla auto-pickup) |
| `pickup_interval_pass_waits_for_pickable_drop` | With periodic pickup off (`automatic_pickup_interval` is 0 for the section, so only the check's own requests make passes), a Wood drop that allows auto-pickup lies to your left a metre beyond vanilla auto-pickup's reach, held so that it only falls and does not roll. In the frame it spawns, while vanilla still refuses it, the interval's request is made, through the call `FixedUpdate` makes. That pass has to leave the drop in the world, also for half a second after vanilla starts allowing it, since taking it then would mean the pass had started vanilla's deferred pickup; a second request made once vanilla allows it picks the drop up, and the Wood in your inventory grows by exactly 1. The drop vanishing before the second request is a FAIL, and so is the second pass leaving it, with the drop's distance and the range at the request in the reason; a first pass that ran only after vanilla allowed the drop is a SKIP, and so is a drop that has left `automatic_pickup_range` before the second request (inconclusive, with the distance, how far below you it is and the range) (SKIP when the interval could not be set to 0, or the range could not be set and does not reach past vanilla auto-pickup) |
| `pickup_key_takes_not_yet_pickable_drop` | With periodic pickup off, a Wood drop with `m_autoPickup` cleared lies 1.5 m to your right. In the frame it spawns, while vanilla still refuses it, a key pass is requested on it, through the call `Update` makes. The key pass collects the drop once vanilla allows it, and the Wood in your inventory grows by exactly 1 (SKIP when the interval could not be set to 0, or `automatic_pickup_range` could not be set and is below 2 m) |
| `pickup_key_leaves_fish_in_water` | Two `Fish1` drops lie a step apart, 1.5 m to your right, with `m_autoPickup` cleared; a water level above the fish is written into one of them, so vanilla's `Fish.IsOutOfWater` is false for it. For the length of the check a temporary patch on `Fish.SetLiquidLevel` drops the game's water-level writes into the two test fish, so a real water volume nearby cannot overwrite the levels (the other fish keeps none and reads as out of water; standing in real water it would get no buoyancy). The hover text of the fish in water lacks the Pickup All Nearby line and the other one has it. A key pass requested on the fish in water makes no pass: both fish stay. A key pass requested on the other fish takes it, the fish in your inventory grows by exactly 1, and the fish in water stays; when that fish is not taken, the FAIL says whether the request was taken and gives the fish's distance and height below you, the range, its water state and whether vanilla allows it (SKIP when `automatic_pickup_range` could not be set and is below 2.5 m, the prefab is missing, the patch could not be added, or the fish could not be put in and out of water; SKIP, inconclusive, when right before either request, or on any frame of a pass before a fish is taken, the fish in water no longer reads the level written into it, has a water volume attached or is out of water, or the other fish is in water. Where the Harmony build cannot remove patches at runtime, the check runs without the patch, and that SKIP asks you to run it away from water) |
| `pickup_interval_runs_with_key_bound` | With the key set and `automatic_pickup_interval` above 0, a Wood drop placed to your left a metre beyond vanilla auto-pickup's reach is picked up by Automatic Pickup's own timer (SKIP when the key or the interval could not be set for the run, or the range does not reach past vanilla auto-pickup) |
| `pickup_settings_restored` | Reported only when the section changed a setting: every setting it changed is back at your value |
| `pickup_fish_guard_removed` | Reported only as a FAIL, when the fish check's temporary patch could not be removed; it is tried again at the next hand-back |

The section sets up its world checks itself, under the rules in *Safety of the
console commands*:

- `automatic_pickup / module` is turned on for the section when it is off (the
  world checks SKIP only when it then does not start).
- When `pickup_all_nearby_key` is not set, it is bound to its default,
  `E + LeftShift`.
- `automatic_pickup_range` is set to 3.5 m for the two Wood pass checks, so
  every test drop lies inside it and as little else as possible does, and to
  6 m for the rest, whose drops lie up to 3 m away, so a drop that rolls
  when it lands stays inside it.
- When `automatic_pickup_interval` is above 0, it is set to 0 from the start, so
  that no pass of the timer's can take the drop a check watches. For the last
  check it is set back to your value, or, when it is 0, to its default, 0.5,
  because that check is about the timer.
- Before any drop is spawned, you are moved to a clear site when a pickable or
  a pickable item lies within 6 m of you, or an item drop within 8 m (a drop
  can roll into the range while the section runs): passes with an interval
  part pick those up too, and what they pick could make a check inconclusive or
  fill the inventory room the checks need. You are moved back when the section
  ends.
- When your inventory has fewer than two free slots, or too little carry weight
  for two Wood or two fish, items are put aside for the section and put back
  afterwards.

`pickup_settings_restored` then reports putting the settings back: PASS when
they are back, SKIP when you changed one while the section ran and your value
was kept, FAIL when one could not be written back (it is tried again at the
next hand-back, and new runs are refused until then). Stand still while it
runs: a player walking toward the drop could let vanilla auto-pickup take it. A
passing run looks like this:

```text
[Automatics Smoke] automatics_smoke_features pickup started: pickup
[Automatics Smoke] PASS pickup_key_default_is_left_shift_e: default 'E + LeftShift'; the current value is 'E + LeftShift'
[Automatics Smoke] PASS pickup_setup: Automatic Pickup already running; the spot you stand on is clear; nothing had to be put aside
[Automatics Smoke] PASS pickup_hover_text_shows_key: 'E + LeftShift' with 'Pick up nearby Wood'
[Automatics Smoke] PASS pickup_key_pass_same_name_only: both Wood drops were picked up and the Stone drop was left (woodDelta=2)
[Automatics Smoke] PASS pickup_key_and_interval_in_one_pass: one pass took the hovered Wood drop with the interval and the other Wood drop by its name, and left the Stone drop (woodDelta=2)
[Automatics Smoke] PASS pickup_interval_pass_waits_for_pickable_drop: a pass made while vanilla refused the drop left it in the world, and a later pass picked it up
[Automatics Smoke] PASS pickup_key_takes_not_yet_pickable_drop: a key pass made while vanilla refused the drop collected it once vanilla allowed it, with periodic pickup off
[Automatics Smoke] PASS pickup_key_leaves_fish_in_water: a fish in water got no key pass and no hover line; a key pass on a fish out of water took it and left the one in water
[Automatics Smoke] PASS pickup_interval_runs_with_key_bound: a Wood drop 3 m away was picked up at interval 0.5s with pickup_all_nearby_key 'E + LeftShift'
[Automatics Smoke] PASS pickup_settings_restored: automatic_pickup/automatic_pickup_interval is already back at 0.5; automatic_pickup/automatic_pickup_range is already back at 6
[Automatics Smoke] Summary: total=10 passed=10 failed=0 skipped=0 failedChecks=[] skippedChecks=[]
```

#### `pin`

Run `automatics_smoke_features pin` outdoors in a single-player world. This
section uses the current `pin_object_range` and does not require a bound key.
It sets up what its checks need itself and puts everything back at its end,
reported by `pin_settings_restored`: Automatic Mapping is turned on for the
section when it is off (`pin_setup` says so); `enable_automatic_mapping` is
turned on, `static_object_mapping_interval` set to 0.25 s when it is 0,
`static_object_mapping_range` raised to 16 m when it is below that (the test
spots stand up to 12 m out), `remove_pins_of_destroyed_object` turned on,
`flora_pins_merge_range` set to 4 m when it is below that,
`hide_unexplored_automatic_mapping_pins` turned off and
`map_filter_hidden_categories` cleared, so that no unsaved test pin is hidden;
copper and tin deposits are added to `allow_pinning_mineral`, and an
early-content spot and dungeon to `allow_pinning_spot` and
`allow_pinning_dungeon` when the lists hold none; before the location trip the
visited location itself is added to its list when it is not in it, and
`location_mapping_range` is raised to reach it from the arrival spot (its area
radius plus 13 m) when it is shorter. Single checks change more:
`hide_unexplored_automatic_mapping_pins` is turned on for `pin_not_hidden` and
off again right after, `navhotkey_location` binds `navigate_to_aimed_object_key`
to F8, `pin_static_loading_area_kept` raises `static_object_mapping_range` to
reach its spot across the zone border, and
`pin_runtime_flora_pins_loaded_plant` adds a test entry to `custom_flora` and
its identifier to `allow_pinning_flora`. A setting the session refuses to
change (its record could not be written, or you changed it meanwhile) is logged,
and the checks that need it SKIP naming it. The section's drops and its piece
are spawned at the first of several spots beside the camera's line of sight that
a camera ray reaches unobstructed and that no saved pin of yours is within 2 m
of. It calls the same ray pinning action as the shortcut, bypassing the physical
key and its input gate because the console is open.

| Check | Asserts |
|---|---|
| `pin_setup` | Reported only when Automatic Mapping was off: it was turned on for the section and is put back afterwards |
| `pin_object_saved` | A camera ray to a spawned non-persistent Wood drop adds exactly one saved, unauthored pin with the localized name and icon-pack type. Both automatic-pin classification and unexplored-pin hiding return false |
| `pin_duplicate` | A second ray to that drop, in the same frame, adds nothing: the duplicate check also sees a pin that has no minimap marker yet |
| `pin_not_hidden` | With `hide_unexplored_automatic_mapping_pins` on, the manual pin from `pin_object_saved` is not hidden as an automatic pin (SKIP when `enable_automatic_mapping` is off and could not be turned on for the section, or the setting could not be turned on) |
| `pin_piece_name` | A camera ray to a spawned non-persistent wood wall (a workbench when the wall prefab is missing) adds one saved pin named after the localized piece name, not the prefab name and without `$`, with the icon-pack type for the piece's name |
| `pin_location_excursion` | The section went to the nearest early-content location for the location checks and came back: PASS names the location, its distance and whether its interior was entered; SKIP names the step that failed (no such location in the world, the teleport, the location not loading, or the return) |
| `pin_location_wall` | A ray at an existing early-content location’s static structure (under its `Location`, not a piece) saves its recognized location name, icon and position once; the repeat adds nothing |
| `pin_location_detached_piece` | A separately spawned non-persistent piece whose creator is never set (creator 0), with no location parent and moved inside a loaded location’s area, saves the location name, icon and position |
| `pin_location_player_piece` | The `pin_piece_name` piece, whose player creator is set explicitly and verified, moved inside the same area, saves the piece’s own name, icon and position |
| `pin_location_ground` | A terrain ray at a point inside exactly one location’s exterior area saves the same location identity once |
| `pin_location_tree` | A spawned non-persistent tree inside a location’s area saves its own pin at the tree, not the location |
| `pin_location_deposit` | The same for a spawned non-persistent mineable deposit |
| `pin_location_creature` | The same for a spawned non-persistent creature |
| `pin_location_interior` | With the camera inside the dungeon's interior, which the section entered through the entrance, a ray at an interior surface saves the pin of the location in that zone at its entrance position (SKIP when the section could not get inside: the location has no interior, no entrance was found, the hop through it failed, or the camera did not follow; the reason names the step) |
| `pin_bare_ground` | A camera ray to bare terrain outside loaded location areas adds an unnamed saved `Icon3` pin at the hit point |
| `pin_no_hit` | A camera ray with nothing within range adds nothing |
| `pin_saved_automatic_recognized` | A saved pin with no owner and your platform user as its author, added through `Minimap.AddPin` as the game adds each saved pin back when it loads the map, is recognized as an automatic pin (SKIP when no local platform user is available) |
| `pin_saved_manual_not_recognized` | The same kind of pin without an author, as a pin you placed or saved by clicking is stored, is not recognized as automatic |
| `pin_shared_not_recognized` | The same kind of pin with your author but an owner, as a shared pin is stored, is not recognized as automatic |
| `pin_saved_automatic_uncached_duplicate` | A ray to a spawned non-persistent Stone drop that has a recognized saved automatic pin at its position, under its stored name, and no static cache entry adds nothing: the action reports the duplicate, and the object's pin lookup (which the navigation key shares) returns that saved pin |
| `pin_static_loading_area_kept` | A non-persistent copper deposit spawned past a border of your zone - the four borders nearest first, each 2 and 6 m past and slid up to 16 m along either way, at the first spot with no pin within 8 m; `static_object_mapping_range` is raised for the check when the spot is beyond it - gets its pin from a static pass that starts with a fresh scan of the area. A second static pass, run while the game reports that zone, and only that zone, as still loading, asks whether the zone has loaded and keeps the deposit's pin on the map and in the static cache (SKIP when `enable_automatic_mapping` is off or the static interval or range is 0 and could not be changed for the section, copper deposits are not in `allow_pinning_mineral` and could not be added, this Harmony build cannot remove patches at runtime, every spot past the borders has a pin within 8 m, the range could not be raised, the static scan is incomplete there, a pass does not run (the reason says so when the area around you had not loaded again after the location trip), or the first pass adds no pin, for example because your simulation distance does not reach the zones around the deposit) |
| `pin_key_mined_deposit_duplicate` | A non-persistent copper deposit spawned on the ground at the first spot 7 to 27 m around you (nearest first, in front of you first, within `static_object_mapping_range`) with no pin within 10 m of it or of the point 4 m to its right gets a saved automatic pin under its name and icon, added through `Minimap.AddPin`, where the centre of its hit areas is with the fewest of them mined that put the pin more than 1.5 m from the deposit's own position. More hit areas, nearest that position first, are then marked mined in the deposit's ZDO until its centre is more than 1.5 m on from the pin, and the deposit is unloaded and loaded again from its ZDO (`ZNetScene.CreateObject`), as the game loads a deposit mined in an earlier session, so its mined hit areas are off from the start. A camera ray to one of its standing hit areas adds nothing: the action reports the duplicate, and the object's pin lookup returns the saved pin, although the ray resolves the deposit at its own position |
| `pin_mined_deposit_one_pin` | A static pass over that deposit, run right after through the entry point the minimap update calls and starting with a fresh scan of the area, takes the saved pin as the deposit's own (it is then the deposit's pin in the static cache) and adds no pin within 1 m of the deposit's current centre (both checks SKIP when `enable_automatic_mapping` is off or the static interval or range is 0 and could not be changed for the section, copper deposits are not in `allow_pinning_mineral` and could not be added, no local platform user is available, a game method they use is missing, the static scan is incomplete there, no such spot is found, mining cannot move the deposit's centre that far, the reloaded deposit does not come back with its mined hit areas off, or the pass does not run (the reason says so when the area around you had not loaded again after the location trip); the key check also SKIPs when no unobstructed camera ray reaches the deposit) |
| `pin_adjacent_deposits_nearest_adopts` | Two non-persistent copper deposits spawned on the ground 4 m apart, at the first spot found as for the mined deposit and the point 4 m to its right, and a saved automatic pin under their name and icon 1.5 m from the centre of the first towards the second, so it lies within both deposits' areas. Both deposits go through the static pass's mineral mapping, called directly in each order (first then second, and second then first), each order starting with neither deposit mapped in the current pass or holding a pin. In both orders the first deposit takes the saved pin and the second does not |
| `pin_tied_deposits_adopt_none` | The same with the two deposits at one spot, so the saved pin, 0.5 m from their centre, is as close to one as to the other: in neither order does either deposit take it (both checks SKIP when copper deposits are not in `allow_pinning_mineral` and could not be added for the section, no local platform user is available, no such spot is found, or a deposit cannot be spawned, and `pin_adjacent_deposits_nearest_adopts` also when the two deposits' centres are less than 3.5 m apart; both FAIL when the static pass has no mineral mapping, with its record of the deposits mapped in the pass and of the wishbone, to call) |
| `pin_loaded_location_spot` | The static pass's mapping of loaded locations, which a multiplayer client uses because only the server keeps the world's location list, is called directly for one test location: a bare `Location` component on a new GameObject with no network view, named as the game names a spawned spot (the first early-content spot in `allow_pinning_spot`, with `(Clone)` appended), 6 m beside you, with a 1 m range so no real location is mapped. It adds exactly one spot pin with the spot's name and icon at the location's position, held in the static cache under that position; mapping it again adds nothing and keeps that pin. The test location is destroyed and its pin removed before the next check (SKIP when no early-content spot is in `allow_pinning_spot` and none could be added for the section, every test spot 6 m around you has a pin or a location within 2 m, or the map filters unsaved automatic pins there, for example because unexplored automatic pins are hidden) |
| `pin_dungeon_waits_for_location` | The static pass's dungeon mapping for a location instance, which a single-player world lists whether or not the game has spawned its `Location` object, is called directly for one test instance in no world list: the first early-content dungeon in `allow_pinning_dungeon`, 6 m beside you, with a 2 m radius. With no `Location` object at that position it adds no pin, caches none and logs no no-entrance warning (the entrance belongs to the `Location` object the game spawns later). With a bare `Location` component on a new GameObject there, which has no entrance, it adds exactly one pin with the dungeon's name and icon at the position, held in the static cache under it, and logs the no-entrance warning, which the check follows with a line saying the warning is its test dungeon's; the warning is checked only when the log settings write warnings. The test location is destroyed and its pin removed before the next check (SKIP when no early-content dungeon is in `allow_pinning_dungeon` and none could be added for the section, every test spot 6 m around you has a pin, a dungeon entrance or a location within 2 m, or the map filters unsaved automatic pins there, for example because unexplored automatic pins are hidden) |
| `pin_static_classification_follows_object` | The static scan's own entry point is called with its interval cleared, as a new pass of the static mapping starts, and one box collider on a new GameObject beside you is read from the static cache after each scan. Its object has a hover name that the check changes: a flora name (the first exact flora name in the registries) classifies it as that flora with the name as its source token; renamed to a mineral name it is classified as that mineral; renamed to a name no registry knows it is not classified; renamed to the flora name again it is a flora again. Moved under a second object named as the mineral it is classified as that mineral and its object is the second one; moved back it is the flora again. The objects have no network view and are not mapped, so no pin is added; they are destroyed and one more scan drops the collider from the cache (SKIP when `static_object_mapping_range` is 0, the static scan is incomplete or does not finish because its collider buffer is full, or the registries have no exact flora name and exact mineral name) |
| `pin_flora_patches_group_plants_in_range` | Non-persistent plants of two flora names (the first two of the common mushroom, dandelion, thistle, raspberry and blueberry pickables with different names) are spawned 60 m above you: a chain of one name along a diagonal, its plants 0.9 of `flora_pins_merge_range` apart and at least 33 m long on each axis, so that plants within the range of each other stand in different cells of the lookup a plant searches for its patch; a plant of that name 1.1 of the range past the chain's end; a plant of the other name half the range beside the chain; and two plants of the chain's name half the range apart, 1.1 of the range above its first plant. Two more stand for plants the game moves or renames right after they appear: one of the chain's name, set up 60 m away and moved at once to half the range from that pair, and one of the other name, set up half the range beside the plant past the chain's end and given the chain's name at once. Each plant gets its flora node, and its network is built at once, as the game builds it a second or two after the plant appears, the chain's two ends first and the moved and renamed plants last. The networks are exactly the groups that linking every two plants of one name within the range gives, by the names and positions the plants have then, chains included: the chain is one patch, the plant past its end with the renamed plant is one, the plant of the other name beside the chain is one, and the pair above with the moved plant is one. The plants are removed before the next check (SKIP when `flora_pins_merge_range` is below 4 m and could not be set to 4 m for the section, fewer than two of those prefabs are available, another flora plant is within the range of a test plant plus 1 m, or the plants did not stand where they were placed or take the name they were given) |
| `pin_runtime_flora_pins_loaded_plant` | A non-persistent pickable that is no flora (the first of the flint, stone and branch pickables that qualifies) is spawned on the ground at the first spot 4, 8 or 12 m around you with no pin within 8 m and gets no flora node. It is given a hover name of its own, so that the `custom_flora` entry the check then adds for that name, the way the config screen's object editor adds one, matches it and no plant of the world. The plant, already loaded, gets its flora node from the running module, with no reload and no module toggle, while every other plant keeps its one node. With the entry's identifier added to `allow_pinning_flora`, the plant's network is built at once, and a static pass that starts with a fresh scan of the area, run through the entry point the minimap update calls, adds the plant's pin and holds it in the static cache. The pin and the plant are removed before the next check, and the two settings go back with the section's others (SKIP when `enable_automatic_mapping` is off or the static interval or range is 0 and could not be changed for the section, the static scan is incomplete or the pass does not run (the reason says so when the area around you had not loaded again after the location trip), the test identifier or the test plant's name is already known to a registry, none of those pickables qualifies or the plant does not go by the name it was given, every spot 4 to 12 m around you has a pin within 8 m, the map filters unsaved automatic pins there, a setting could not be changed, or the scan did not classify the plant's collider) |
| `pin_destroyed_deposit_removed` | Three non-persistent tin deposits, spawned 4 m apart in a row on the ground 6 m (else 10 m) out in the first of eight headings from your facing where every spot is within the static range and has no pin within 3 m, get their pins from a static pass that starts with a fresh scan of the area. The first goes as a used-up deposit removes itself: its owner destroys it through `ZNetView.Destroy`, and the destruction the game sends on its next update is sent at once (`ZDOMan.SendDestroyed`), with the deposit's ZDO marked persistent for that moment, as a world object's is. Its pin leaves the map and the static cache. The deposit goes without any destroy callback of its own running, so only the destruction of its ZDO can remove the pin |
| `pin_destroyed_manual_and_shared_kept` | A pin you placed (no author) and a shared pin (with an owner), both added within 1 m of the first deposit's pin before it goes, are still on the map afterwards |
| `pin_destroyed_by_other_game_removed` | The second deposit's destruction arrives as another game's does, through the game's handler of the routed `DestroyZDO` call (`ZDOMan.RPC_DestroyZDO`): the game removes the deposit here, and the deposit's pin leaves the map and the static cache, although no callback of the game that destroyed it ran here |
| `pin_unloaded_deposit_kept` | The third deposit is unloaded with the steps the game takes when an object's zone leaves the active area (`ZNetScene.RemoveObjects`), and the destruction they lead to is sent at once: its instance goes, and its ZDO, which is not persistent, is destroyed by its owner, so the game raises its destruction as for any destroyed object. A world object's persistent ZDO would stay instead. The deposit's pin stays on the map and in the static cache: unloading is not destruction. A ZDO the steps leave is destroyed right after, before the game could spawn the deposit from it again |
| `pin_cleanup` | Every pin created by the section has been removed |
| `pin_settings_restored` | Reported when the section changed a setting, also when a check threw: every setting it changed is back at your value (SKIP when you changed one meanwhile and your value was kept; FAIL when one could not be written back), and the test flora of `pin_runtime_flora_pins_loaded_plant` is no longer defined |

The location checks run beside a location the section goes to itself. It takes
the nearest early-content location from the world's location list (a dungeon
before a spot, so that the interior check has an interior to enter; one with a
saved pin of yours at it is passed over), removes its own drop and piece first,
and teleports to a point 5 m outside the location's area, facing it, the way
the game moves a player between distant points, with god mode and ghost mode on
for the move and off again on arrival. It then waits for the location to load
(up to 10 s), for the camera to follow (up to 5 s) and for two static passes
(up to 10 s), so that `navhotkey_location` finds the pass's pin, and runs the
exterior checks with a fresh test piece. For `pin_location_interior` it goes
through the location's entrance the way entering a dungeon does - a short
teleport to the entrance's target point - and waits for the camera to follow
into the interior. It then returns to where the command started before the
next check, again with god mode and ghost mode on from before the return is
accepted (it waits out the teleport cooldown first) until it has ended, and
`pin_location_excursion` reports the trip; when a step fails the dependent
checks SKIP with that step's reason, and a failed return is retried by the
hand-back when the command ends. Back from the trip, the section waits up to
20 s for the zones around you to load again, until the game reports the area
ready (`ZNetScene.IsAreaReady`, as the static pass asks it) for every zone
within 38 m of you on either axis, because `pin_static_loading_area_kept`,
`pin_mined_deposit_one_pin` and `pin_runtime_flora_pins_loaded_plant` run a
static pass there; when the area has not loaded by then, a pass of theirs that
does not run SKIPs with that reason. These checks follow the ones before them in
the same frame, so each first hands on the destruction of the test objects
removed before it, as the game's next update would (`ZDOMan.SendDestroyed`): a
destroyed object's ZDO stays in its zone until then, and the game takes it for
an object still loading. Every setting the trip changes goes back with
the section's others. The timings are not confirmed
yet; the first in-game run shows whether they hold. Being teleported to a
location means the game may generate its zones on a first visit, and creatures
there act as usual once the player has arrived; the dedicated test world accepts
both. Location checks read loaded locations without modifying them and remove
only their own pins and objects; a location's automatic pin that the pin key
saved in place is put back unsaved and held by Automatic Mapping as before.
They SKIP with a reason when no suitable location ray without an existing
manual pin is available. No location prefab is spawned because its
initialization can generate terrain or entities. `pin_loaded_location_spot`
adds only a bare `Location` component on an empty GameObject, which generates
nothing, and destroys it in the same frame. Vanilla `Piece.SetCreator` writes only while the creator is 0, so the creator-0 piece is its own spawn and never gets a creator. Test pieces, trees, deposits and creatures are non-persistent, placed only where the camera ray's first hit is the object at a point that exactly one loaded location area contains (strictly inside, as `Location.IsInside` tests), and deactivated and removed before the next check; the `pin_piece_name` piece gets its position back. Checks SKIP with a reason when no such spot, unobstructed ray or duplicate-free position is available, and `pin_location_ground` SKIPs when every terrain point it finds lies inside more than one area. The action gives world-generated pieces, terrain and unnamed hits the nearest containing loaded location; when that location is unrecognized, the usual object or ground pin applies. Player-built pieces, creatures, deposits, trees, loose items and non-piece networked parts of a location such as destructibles or interactables keep their own pins; the game spawns those parts as scene roots, not under the location.

The four destroyed-object checks run first. Their deposits are spawned before
any test pin exists, and the section waits one frame for them, because a
deposit may still set itself up in `Start` (a `MineRock` reads its hit areas
there); everything after that frame runs in one.
The deposits' colliders stay off during that frame and are turned on right
before the section's own static pass, so the game's own scans cannot pin them
meanwhile, and no save can keep such a pin.
A deposit's ZDO is marked persistent only within that frame, for the call that
destroys it, so no save can keep it. The checks need the destruction
subscription Automatic Mapping makes for the world you are in: run the section
once in a world you entered with `automatic_mapping / module` already `Enabled`
when the game started, before turning the module off and on there, and once
more after going back to the main menu and entering a world again. They SKIP
when `enable_automatic_mapping` is off, the static interval or range is 0 or
`remove_pins_of_destroyed_object` is off and the setting could not be changed
for the section, tin deposits are not in `allow_pinning_mineral` and could not
be added, a game method they use is missing, the static scan is incomplete
there, every row of test spots around you has one outside the static range or
within 3 m of a pin, the static pass does not run, it adds no pin for a deposit
(for example because your simulation distance does not reach the zones around
it), or a deposit takes a saved deposit pin that was already on the map as its
own: that pin stays on the map as it was. A real
unloading, which needs you to move away, and a destruction by another player
are not produced; the checks reproduce the game's own steps for both.

Obstructed object rays at every spot tried, unavailable bare ground, existing
duplicate pins, and no empty direction within range cause the affected checks
to SKIP. Move to an open area and retry. Pin creation and removal finish in the same frame, before
another map save or player input. Every removal follows one rule: a check
removes only pins it added. A pin that was on the map when the check started is
never removed, also when the check fails or throws; if a test object took it as
its own, or the pin key saved an automatic pin in place, it is put back as it
was: saved or not, automatic, and held by Automatic Mapping as before. The
mineral checks choose their spots away from every pin, the section's own
included, rather than waiting for the section's cleanup. The saved-pin checks add their pins one at
a time through `Minimap.AddPin` next to you; they never load, replace or clear
the map data. `pin_static_loading_area_kept` runs two static passes itself
through the entry point the minimap update calls, so they also map the real
objects around you as any pass does. It reports its one test zone as still loading
through a temporary patch of `ZNetScene.IsAreaReady` that acts only during the
second pass and is removed right after it; it SKIPs when this Harmony build cannot
remove patches at runtime. If the removal fails, the check's detail says so, and
the patch, left inactive, answers as the game does until restart. The check removes
its deposit and the deposit's pin before the next check.
`pin_mined_deposit_one_pin` and `pin_runtime_flora_pins_loaded_plant` each run
one static pass the same way, which also maps the real objects around you. The
flora checks build a test plant's network by calling the plant's own network
construction, which the game runs a second or two after the plant appears, so
the section still runs in one frame. The mined-deposit and shared-pin checks remove
their deposits, their saved test pins and any pin the deposits got from them
before the next check; a saved pin that was already on the map and that a test
deposit took as its own is put back before the deposit goes, so the deposit's
destruction cannot take it off the map. The mined deposit is unloaded and loaded
again within one frame, and its ZDO, never persistent, is destroyed with it, so
no save can keep it. The shared-pin checks call the mineral mapping directly and
map nothing else; the record of the deposits mapped in the current pass and of
whether a wishbone is equipped is put back afterwards. The test drops, the test piece and the deposits use the
shared session cleanup, and the drops never enter the inventory. Cleanup failures stay registered with
the session for retry. Physical hotkey input, save/reload persistence, and a
module off/on with the saved pin remain in-game verification steps outside this
section; use `automatics_smoke_lifecycle mapping` for the module lifecycle checks.

#### `navhotkey`

The navigation hotkey section drives the aimed-object navigation key against a
spawned non-persistent boar and Wood drop, each at the first of several spots
beside the camera's line of sight that a camera ray reaches unobstructed. It
sets up what it needs itself and puts every setting back at its end, reported
by `navhotkey_settings_restored` (FAIL when a setting could not be put back or
was changed meanwhile): Automatic Mapping is turned on for the section when it
is off (`navhotkey_setup` says so), `navigate_to_aimed_object_key` is bound to
F8, `navigation_start_key` to its default, Left Shift, when it is unbound, for
the map-click checks (the click is simulated; the key is not pressed), and
`hide_unexplored_automatic_mapping_pins` is turned off and
`map_filter_hidden_categories` cleared, so that the test creature's automatic
pin is shown. `navhotkey_hidden_category_kept` turns `enable_map_enhancements`
on and hides the Other category for its own check, and puts both filters back
to the section's values right after. Run it in a single-player world with the
inventory closed. The `navhotkey_location` check runs in the `pin` section,
beside the location it visits.

| Check | Asserts |
|---|---|
| `navhotkey_setup` | Reported only when Automatic Mapping was off: it was turned on for the section and is put back afterwards |
| `navhotkey_exact_pin` | A ray at a creature with its own automatic pin selects that pin, not a saved pin beside it, and adds no marker |
| `navhotkey_replace_with_pin` | Switching from a temporary marker on the drop to the creature's pin removes the marker |
| `navhotkey_map_creature` | A map click on the creature's pin with `navigation_start_key` held stores the creature's ZDOID (SKIP when the key is unbound and could not be bound for the section) |
| `navhotkey_pin_follows` | The destination follows the pinned creature before the next validation tick |
| `navhotkey_removed_pin_kept` | Removing the tracked creature's dynamic pin leaves a temporary destination on it |
| `navhotkey_hidden_category_kept` | A non-creature whose automatic pin is hidden by category gets a temporary marker and stays navigating through three validation ticks |
| `navhotkey_temporary` | An unpinned object gets exactly one named, unsaved, non-automatic marker |
| `navhotkey_not_saved` | The temporary marker is absent from the serialized map data, while a saved control pin is in it |
| `navhotkey_distance_format` | The navigation label appends the bracketed horizontal distance |
| `navhotkey_repeat_clears` | Repeating the target removes its marker and clears navigation |
| `navhotkey_replacement` | Switching to the unpinned creature removes the old marker and keeps the creature's identity |
| `navhotkey_temporary_follows` | The destination and the temporary marker follow the creature |
| `navhotkey_marker_adopts_pin` | A new automatic pin on the followed creature replaces its marker, which is removed |
| `navhotkey_marker_returns` | Removing the adopted pin puts a marker back on the creature |
| `navhotkey_creature_marker_to_pin` | A map click on a pinned target from a creature marker leaves no extra unsaved pin (SKIP when `navigation_start_key` is unbound and could not be bound for the section) |
| `navhotkey_empty`, `navhotkey_unbound`, `navhotkey_stopped`, `navhotkey_blocked` | An empty ray, an unbound key, a stop request and an open inventory each leave the destination and its single marker unchanged |
| `navhotkey_dead_clears` | The creature's live death flag clears navigation before its ZDO is destroyed |
| `navhotkey_destroyed_clears` | The owner's destruction of the target ZDO clears navigation and removes every temporary marker |
| `navhotkey_settings_restored` | Every setting the section changed is back at your value |

#### `aimbar`

The looked-at object bar section aims at a spawned boar, a player-built wall piece
and a mushroom. Automatic Mapping is turned on for the section when it is off
(`aimbar_setup` says so) and put back with the section's other settings,
reported by `aimbar_settings_restored`. The game must be unpaused with the HUD
visible and the large map closed, which the section cannot arrange. These checks
cover the targets that get only a name and distance label, and the bar coming
back:

| Check | Asserts |
|---|---|
| `aimbar_hidden_creature` | A boar with its HUD hidden, aimed at, gets the looked-at label with its name and the rounded horizontal distance in brackets, and the label's Health group is not shown |
| `aimbar_boss` | The same for the boar marked as a boss |
| `aimbar_bar_after_label_only` | A mushroom aimed at right after those two, with the same label object, gets the label with its Health group shown again and a full bar |

#### `mapfilter`

Run `automatics_smoke_features mapfilter` in a loaded world without the no-map
world modifier (the section SKIPs and says so when it is on). The section
checks the large map's category filter and search field against test pins it
adds next to you: filter controls and their layout, what each display mode shows
while categories are hidden or shown, how saved pin names are classified, the
search field (matching, the Apply to minimap toggle, focus, typing with an input
method, and the pin and navigation keys blocked while it has focus), the
unexplored-pin rule combined with the filter, and turning map enhancements and
the module off and on. Results are named `mapfilter_<check>`, one per control,
category, display mode and test pin where that applies.

`automatic_mapping / module` is turned on for the section when it is off; a
`mapfilter_setup` PASS says so, and the section SKIPs only when the module then
does not start. The section holds `enable_map_enhancements`,
`map_filter_hidden_categories`, `hide_unexplored_automatic_mapping_pins` and the
module setting for its length, writes them as each check needs (it turns the
module off and on again itself near the end), and turns
`enable_automatic_mapping` on for the unexplored checks alone when it is off.
It opens the large map and changes its zoom and the search text; the map mode,
zoom, search text, Apply to minimap toggle and navigation target are put back
and its test pins removed when it ends. `mapfilter_settings_restored` and
`mapfilter_mode_restored` report the settings and the map mode back.

#### `pintooltip`

Run `automatics_smoke_features pintooltip` in a single-player world with the
inventory UI loaded. The section spawns a deer 15 m above you, two mushrooms
and a copper deposit, gives them test pins, and checks the pin tooltip's
content for each: name, horizontal distance, health, readiness and respawn time,
merged flora counts and the deposit's remaining sections, read from the loaded
object, from the server's data and from the client's cache. It then checks the
tooltip for saved pins that are no longer linked to an object, for manual and
location pins, the tooltip UI on the large map (shown, refreshed, closed,
clamped, and with map enhancements off and on), a real pointer hover on a test
marker, and a removed object's pin. The Pin Looked-at Object Key and the
Navigate to Looked-at Object Key are checked on the merged mushroom patch
(`pin_key_*` and `nav_key_*`). Results are named `pintooltip_<check>`.

`automatic_mapping / module` is turned on for the section when it is off; a
`pintooltip_setup` PASS says so, and the section SKIPs only when the module then
does not start. For its length the section sets `enable_map_enhancements` on,
clears `map_filter_hidden_categories`, turns
`hide_unexplored_automatic_mapping_pins` off, sets `flora_pins_merge_range` to
8 and binds `navigate_to_aimed_object_key` to F8; `pintooltip_settings_restored`
reports putting them back. Temporary patches make a test object read as not
loaded, make the tooltip read the data as a client would, and count sector
reads; they are removed when the section ends. The test objects and pins are removed, and the map mode, view, search
field, pointer and navigation target put back. The real pointer checks SKIP when
the cursor is locked or the game does not move the pointer onto the marker;
the hover is then left to a person.

#### `mining`

Run `automatics_smoke_features mining` in a single-player world, standing on the
ground, out of build mode and not attacking, swimming or attached. The section
sets up the rest itself before it spawns anything:

- it turns `enable_automatic_mining` off when Automatic Mining mines on an
  interval (see below), then turns the module's `module` setting on when the
  module is off, and `enable_area_mining` on when it is off;
- it takes the test deposit kinds in order (tin, obsidian, copper, then a
  mud pile; those the loaded world has, each with a pickaxe that reaches its
  tool tier), sets `allow_mining_mineral` to each kind alone in turn, and keeps
  the first kind for which area mining finds no deposit of your world within
  `area_mining_radius` + 5.8 m of you (8.8 m at the default radius of 3 m); the
  list stays at that kind for the section;
- it turns you to the first of eight headings, 45° apart and starting with the
  way you face, with no building piece or other destructible object within 4 m
  of the point 1.5 m ahead;
- when no kind or no heading will do where you stand, it moves you to clear
  ground nearby, as described in *Safety of the console commands*: a site with
  a clear heading, a test kind with no deposit of its kind whose colliders come
  within that distance, and no building piece or map pin within 5.5 m. You are
  taken back to where the command started at the end of the section;
- it puts unequipped items aside until two inventory slots are free, and puts
  them back at the end of the section.

The `mining_setup` check reports what was done. When a step fails, the section
SKIPs `mining_section` and names the step and why, for instance every site it
tried. It then spawns two non-persistent deposits of that kind about 1.8 m in
front of you, side by side, and a third to the other side, a little beyond
`area_mining_radius`, and gives every section a health no hit reaches, so
nothing breaks or drops. It adds a pickaxe whose tool tier reaches the deposit
and equips it, lowers your view a little, and starts a real attack with
`Humanoid.StartAttack`. An
observer patch, removed at the end of the section, records the melee call, every
rock damage call and every Pickaxes skill raise. While it is in place, it also
guards your world: a hit of yours on anything but the test deposits is dropped,
and so is the terrain operation a pickaxe hit on the ground would spawn, so a
swing that misses digs nothing.

| Check | Asserts |
|---|---|
| `mining_setup` | The section set itself up; the note says what it changed: the module or area mining turned on, the test kind written to `allow_mining_mineral`, the heading it turned you to, the move to clear ground and the items put aside |
| `mining_area_expands` | The swing hit a test deposit, and area mining added at least one extra hit, each seen as a damage call |
| `mining_area_cap_and_radius` | At most `area_mining_max_targets` extra hits, each within `area_mining_radius` of the swing's first mineral hit, none on a section or object the swing or an earlier extra hit already hit, and all on the test deposits |
| `mining_area_damage_applied` | Every extra target lost health |
| `mining_area_outside_radius_untouched` | The third deposit, which the physics engine's own sphere overlap at the swing's first mineral hit confirms lies beyond `area_mining_radius`, took no hit and kept its health (SKIP when it came within the radius) |
| `mining_area_durability_per_target` | The pickaxe lost the vanilla per-hit drain, scaled by the world durability rate, once for the swing and once per extra target |
| `mining_area_skill_once` | The Pickaxes skill was raised exactly once for the swing |
| `mining_area_not_from_automatic_mining` | A hit through Automatic Mining's own hit path on a test deposit adds no extra hit |
| `mining_area_not_from_other_weapons` | A real swing with an added club that hits a test deposit adds no extra hit |
| `mining_world_guarded` | The section ran to its end under the guard; the note counts the hits on other objects and the terrain operations it dropped |
| `mining_settings_restored` | Every setting the section changed is back at your value: `allow_mining_mineral`, and those of the module, area mining, `enable_automatic_mining` and Automatic Repair's `module` setting when it changed them |

When Automatic Mining would mine on its timer, `enable_automatic_mining` is
turned off for the section, so it cannot mine the test deposits with the test
pickaxe, and put back at its end. When Automatic Repair is running, its `module`
setting is turned off for the section too, and put back at its end: it puts every
worn item back to full durability once a second, which would undo the pickaxe's
wear between the frames a swing spans. The section SKIPs and names the setting
when either cannot be turned off. An attack that does not start within 1 second,
or triggers no melee hit within 3 seconds, SKIPs the swing's checks and names
the reason; so does a swing that hits none of the test deposits. At the end
the added pickaxe and club are removed from your inventory, the items you had
equipped are equipped again, the items put aside go back, and your view pitch
and Pickaxes skill level are put back; your view stays turned to the heading
unless you were moved. The swings still count toward your mining statistics and
use some stamina. Do not move or attack while the section runs.

#### `planting`

Requires a single-player world. It sets up the rest itself: the Farming module and
`enable_grid_planting` are turned on for the section when they are off (and put
back with the other settings). It walks the cultivator's own piece table in order
and uses the first piece with a `Plant` that needs no cultivated ground, has no
terrain modifier, takes one kind of seed, and grows in your current biome (all
read at runtime). It then puts aside the inventory items in the way, as described
under the safety rules above: every stack of that piece's seed you carry, so the
seed accounting counts only the section's own, and enough unequipped items for
three free slots. With the test cultivator equipped it teaches your character the
sapling piece when it is not known yet (kept by the character), and sets your
stamina to its maximum; the hand-back puts back the stamina you had. It tries grid
centres in eight directions around your facing at about 4, 8 and 12 m (farther
when the grid is wide) and uses the first one where every cell passes the rules
the mod applies before planting: build access, biome, cultivation, roof, grow
space (grass has no collider and never blocks) and, for crops that refuse
tilting ground, a slope limit, and where the cells the real-ghost checks plant at
beside the grid pass them too: the free lattice cell, the corner the ghost moves
on to beside the first grid, and the corners of the free cell's lattice square.
When none passes, it moves you to the nearest clear site (see the safety rules
above) around which one does, keeping your facing, and moves you back when the
section ends. When no such site is found, it uses a centre around you where the
grid's own cells pass, and the real-ghost checks whose cells fail SKIP with that
cell's reason and the failed move. A `planting_setup` PASS lists what was set
up. If only your own no-cost toggle (`nocost`) is on, the section turns it off for
its synchronous checks and turns it back on afterwards, also through the session
hand-back; stamina and durability are charged the same either way, as in vanilla.
God mode is lifted and restored the same way, because the Debug build of
Automatics skips stamina use while god mode is on. No terrain is modified.

It still SKIPs, naming what failed, when the module does not start, no piece is
usable in your biome (the SKIP names the biome and each plant piece's verdict
with its allowed biomes; pieces that grow only in biomes from the Mistlands update
onward are counted, not named), the items cannot be put aside, even full stamina
is not enough for eight saplings, or neither your spot nor a site within 45 m has
a plantable patch (the SKIP lists each centre tried at your spot with how many cells passed and the
first failing reason, and why the move failed). Left to you: the world's
free-build key for the piece must be unset (the SKIP names the key; world
modifiers are never changed), and you must not be dead, attacking or in the middle
of a grid placement when it starts.
The first grid's centre is 4 to 12 m from you (farther when the grid is wide), and
the snapping checks use ground up to about five cells from that centre, which is
about 25 m for a tree sapling's 5 m spacing. Rocks, trees and pieces within a
sapling's grow radius of a cell make that cell unusable, and the section uses
only ground where none is.

The session temporarily sets a 3×3 grid, spacing factor 2.5 and Left Alt, then
restores the settings. It adds its own cultivators and seeds, captures and
restores equipment, and drives recorded manual-placement requests synchronously.
Expected cell positions are computed by the check itself (the hand-placed crop's
position plus the rotated lattice offsets), not taken from the mod. It checks:

- a 4×4 grid keeps the hand-placed crop as a lattice cell and sits half a cell
  off it on both axes
- the released-modifier check calls the actual placement postfix and SKIPs while
  Left Alt is held; release the key before running it
- with the key temporarily unbound, the same postfix records a request (SKIPs
  while the alternative placement button is held)
- a failed placement and a piece without a `Plant` record nothing
- eight planted cells and each crop's seed, stamina and durability cost
- two cells with seeds for two, seven with a local collider blocking the first
  cell, two with stamina for two charges, and one with durability below one charge
- a request is rejected when the right-hand tool is swapped before it runs
- a second consume never replays a request
- lattice snapping, after planting a first 3×3 grid: a point aimed past its edge
  and off the lattice by a fraction of a cell, with a player rotation 100° from
  the grid's, snaps to the expected lattice cell with a yaw a quarter turn from
  the grid's; the same aim does not snap without the modifier, a point beyond the
  search radius (one grid width) does not snap, and after both grids are
  removed the same aim no longer snaps. The oracle is the check's own: a position
  is on the lattice when its offset from the first grid's centre, turned back by
  the first grid's rotation, is a whole number of cells (within 5 cm), and a yaw
  when it differs from the grid's by whole quarter turns
- the real `Player.UpdatePlacementGhost`: the section selects the test sapling in
  the cultivator table (SKIP when the table still does not offer it), points the camera
  straight down at an aimed point and widens the player's placement reach. With
  the unbound key it checks that the ghost lies on the free lattice cell on the
  ground with a lattice yaw, that vanilla's placement status is Valid (SKIP when
  that cell fails the planting rules above) and that the player's rotation step is
  back afterwards; aimed next to a cell of the first grid, whose crop leaves no
  room to grow at that nearest corner of the aimed lattice square, the ghost moves
  to the nearest corner without a crop, on the lattice with a lattice yaw, and the
  status is Valid (SKIP when that corner fails the planting rules above); aimed
  so that the lattice point lies just beyond the reach while the aimed point is
  within it, the ghost stays at the aimed point with the player's rotation. Then,
  with Left Alt bound again and released, the ghost stays at the aimed point with
  the player's rotation. The corners come from the check's own oracle: the four
  cells at the corners of the lattice square that holds the aimed point, nearest
  first on the ground plane
- grow space at the real ghost, aimed as at the free cell: the aim is the first
  lattice square near the first grid (the free cell's square first, then the
  squares in the columns two to four cells beyond either side of the first grid,
  rows -2 to 1, in a fixed order) whose four corners pass the planting rules
  above. With a test collider on the `piece` layer beside the nearest corner,
  outside the square, inside that corner's grow radius but clear of the ray down
  onto it (so vanilla alone would still call that corner Valid), the ghost lies
  on the second-nearest corner with status Valid; with such a collider beside
  every corner, the ghost stays on the nearest corner with status MoreSpace; with
  the colliders removed, the nearest corner is Valid again. SKIP when the free
  cell check did not pass, none of the squares tried (the SKIP names how many and
  the first failing reasons) has four plantable corners, or a collider does not
  take its corner's grow space. The colliders are removed before the section goes
  on
- the real `Player.TryPlacePiece` at the free cell, at the corner the ghost moved
  to next to the first grid and at the square with every corner blocked, with
  `Player.PlacePiece` intercepted so nothing is placed: the free cell and the
  moved ghost call it once at the ghost pose, the blocked square is refused. The
  player's no-cost toggle is on for each call, so vanilla counts the placement as
  cheated and grants no achievement progress; the build statistics it increments
  are put back and the grid request it records is dropped. The camera, the reach,
  the table's selected piece and the rotation step are put back before the frame
  ends, and the key is unbound again for the rest of the section
- a second grid planted at the snapped ghost pose (or at the snapped pose when
  the ghost could not be driven) puts every crop on the first grid's lattice
  (SKIP when no cell of the second grid is plantable)

The extra crops do not raise the Farming skill or the build statistics; only the
hand-placed crop does, by design, so the section does not check either.

Plants created during these calls are tracked, made non-persistent and removed
before the frame ends. Only the added inventory items are removed, and the items
put aside go back afterwards. Stamina, its regeneration delay and the test
cultivator's durability are restored within the same synchronous check, and the
stamina you had before the section when it ends. The
real cultivator key input and vanilla manual placement remain a separate in-game
check; this section drives the request path after a successful manual placement.

#### `farming`

Run `automatics_smoke_features farming` in a single-player world. The section
prepares its own plot:

- It sets a farming interval of 0 (no periodic passes) and turns the Farming
  module on for the section when it is off.
- It takes its candidate crops from the Flora registry, not from `Allow Farming
  Crop`: every crop that grows from one kind of seed on cultivated ground into a
  crop with a pickable yield. Crops from the Mistlands update onward are counted,
  not named.
- It needs a spot whose biome one of those crops grows in, with the same biome and
  ground within 1 m of the spot's height 4.5 m out, no plant or pickable within
  6 m and no container with a farming role within 13 m. When the spot you stand on
  is not one, it moves you to the nearest clear site that is (see the safety rules
  above), keeping your facing, and moves you back when the section ends.
- It uses the first candidate crop that grows in that biome and has no growing or
  ripe plant of its kind within 6 m, and makes it the only allowed crop.
- It cultivates the ground within 4.5 m of the spot as the cultivator does: the
  cultivator's own build piece whose terrain operation paints cultivated ground
  is placed on a grid of 1 to 1.5 m (no item, stamina or durability is used), and
  the section waits until the ground within 4 m reads as cultivated. This is a
  lasting change to the world's terrain; whether the operation also levels or
  smooths the ground is the game's, and its settings are logged and named in the
  setup note. Nothing is done when that ground is cultivated already.

A `farming_setup` PASS says what was set up. The section still SKIPs, naming what
failed, when the module does not start, no candidate crop exists, no site within
45 m suits the plot (the SKIP lists the sites tried and why each failed, such as
a biome where no candidate crop grows), the ground cannot be cultivated, or fewer
than three plantable cells lie within 3 m of you on the plot.

Before it looks for a crop, a plot or role containers, so that none of those
SKIPs stops it, the section runs one read-only check:

| Check | Pass condition |
| --- | --- |
| `farming_default_crops_have_saplings` | For every Flora identifier (custom flora objects included), the sapling lookup that farming replants with (`CropPlanting.TryResolveSapling`) finds a sapling exactly when the identifier is in the default value of `Allow Farming Crop`. A pass gives only the counts; a FAIL names each identifier from before the Mistlands update that differs and says which way, and counts the later ones, which it names in the BepInEx log file only |

For the length of the section it also sets `Player With Containers` mode, a
farming range of 4 m, a container search range of 8 m and a spacing factor of 2.5
(and, for the snapping check, grid planting on with a 3×3 grid), then restores
them with the interval, the module and the allowed crop. It spawns two chests beside you and gives them the Storage and
Cultivation roles through the real designation path, puts seeds into the
Cultivation chest, and runs one farming pass per check (the two sowing checks
share one pass):

| Check | Pass condition |
| --- | --- |
| `farming_flora_change_rebuilds_sapling_lookup` | With the sapling lookup built, a User-defined Flora entry added through the registry (`ValheimObject.Flora.RegisterCustom`, as the setting does; no config value is written, and the custom entries that were there are put back afterwards) drops the lookup, and the next `CropPlanting.TryResolveSapling` for the chosen crop builds it again and still finds its sapling |
| `farming_player_containers_harvest` | A ripe crop of that kind spawned on a free cell is picked, its yield goes into the Storage chest, it is replanted at the same spot with one seed from the Cultivation chest, and your inventory is unchanged |
| `farming_replant_below_reserve` | With `Seed Reserve` above the seeds held and sowing on, the harvested spot is still replanted and nothing is sown on the free cells left |
| `farming_replant_keeps_row_yaw` | A ripe crop standing at 194.5° (a planting yaw of 202.5° turned by 8° as growing does) is harvested and replanted at the same spot with a yaw of 202.5°, the planting yaw, not the grown crop's own |
| `farming_snap_follows_row_yaw` | Beside a growing crop of that kind planted at 22.5° and standing 7° off it (as a sapling replanted by an earlier version does), the grid-planting pose (`LatticeSnapping.TryLatticePose`) for a point aimed a cell and a bit past it, with a player rotation 100° from 22.5°, is the next cell of the rows turned 22.5° (within 2 cm) with a yaw of 112.5°, the nearest quarter turn |
| `farming_sow_rotated_lattice` | Beside a growing crop of that kind planted at 22.5° and standing 7° off it, and with a growing crop of another early kind half a metre from you (nearer than the turned one, so only a crop of the same kind may anchor the lattice; left out when no other early crop has a sapling), every crop sown lies on the lattice of the planting yaw (22.5°) and is turned with it (SKIP when no lattice cell beside it is plantable) |
| `farming_sow_exactly_plantable_cells` | In that same pass, the crops sown are exactly the cells worked out before it: the turned crop's lattice cells within Farming Range of you, walked as the sowing grid walks them, that are cultivated, within Container Search Range of one of the two chests, and pass build access, `CropPlanting.CanPlantAt` (biome, cultivation, temperature, grow space, roof) and, for a crop that refuses tilting ground, the slope limit. The Cultivation chest is given seeds for every such cell first, so running out never ends the pass early. A FAIL lists the cells not sown and the crops sown elsewhere, relative to you (SKIP when no such cell exists or the chest cannot take the seeds) |
| `farming_deposit_full_box_untouched` | With the Storage chest full of the crop's yield, the harvest deposit (`PickableHarvester.HarvestToContainers`, called directly on a ripe crop with the Storage chest as its only target) leaves the crop standing, and the chest gets nothing, no add call, no save and no warning |
| `farming_deposit_all_or_nothing` | With room for one in the Storage chest and the crop's yield set to two, the deposit still puts nothing in and leaves the crop standing, again without an add call, a save or a warning |
| `farming_deposit_competing_yields_untouched` | With one empty slot in the Storage chest and no room in the yield's stacks, the crop's yield set to one and one extra drop of another early item the chest holds none of given to it, two yields compete for the one slot that each would fit alone: the deposit puts nothing in and leaves the crop standing, without an add call, a save or a warning |
| `farming_settings_restored` | Every changed setting is put back, the module toggle too when the section turned it on |

The three deposit checks fill the Storage chest with the section's own yield
(never with an add that does not fit), and run with the container observer of
`automatics_smoke_processing store`: it counts the add calls on the chest's
inventory and its saves through Harmony prefixes applied around the call only,
and records every warning Unity logs meanwhile; its deliberate warning,
`AUTOMATICS_SMOKE_FARMING_DEPOSIT_PROBE`, must arrive or the check FAILs. They
SKIP when the chest cannot be filled, or left with room for exactly one or with
exactly one empty slot, when none of Stone, Wood, Resin and Flint is both
different from the yield and absent from the chest, or when the observer cannot
patch the game. The ripe crop they use is removed right after them.

Everything runs synchronously. Crops it plants are tracked and removed. The section takes out
only the seeds it added and the yield it harvested or used as filler; a chest
that still holds anything else is left in the world and the log says where. The
cultivated plot stays. A harvest may raise the pick skill, as a harvest by hand
does.

#### `guide`

Run `automatics_smoke_features guide` outdoors in a single-player world (the
section SKIPs indoors, where the swarm flies at your height, and while the game
is paused). When Automatic Mapping is off it is turned on for the section, and
the section waits until the module runs; `enable_navigation_guide` is turned on
when it is off, and an unbound `navigation_start_key` is bound to its default
(Left Shift). `guide_setup` PASSes and names what was changed; all of it is put
back before `guide_settings_restored`. Only when a change cannot be made does
the section SKIP (the module or the option) or the map click checks SKIP (the
key), naming why. It adds one
unsaved, unnamed test pin about 60 m behind the camera and starts navigation to
it through the same call a map click makes, so the start message appears. Stand
still while it runs: the section moves the test pin, not you, and the game's own
player update drives the navigation and the swarm over real frames.

The checks read the swarm's lights back and follow each light by its random
seed from frame to frame. A light's age is the time since it was born; travel
lights are stream lights older than `RadiateTime` + `GatherTime` + 0.3 s
(currently 1.2 s), the lights that have spread out and joined the stream. Only
a new swarm, at a navigation start or a target switch, lets out a burst of
lights that spread out from you; every other stream light leaves you along the
line toward the target. The checks tell burst lights by their start lifetime,
above the midpoint of `FlowLifetime` and `BurstLifetime` (currently 4.15 s). The
swarm is shown when both its particle renderers and both its point lights are
on and its particle systems are not paused, and hidden when all of them are off
and the systems are paused; the guide object stays active either way. The
bounds that follow the swarm's look are derived from the constants in
`NavigationGuide` and from `navigation_arrival_distance`; each note gives the
values it measured and the constants its bounds came from. The terrain height a
check measures is the terrain collider's (`ZoneSystem.GetGroundHeight`), the
surface the burst is kept above. The motion is random, so run the section three
times after a change to the swarm.

The section runs in this order and starts navigation again where a later check
needs one:

1. The setup above, then `navigation_arrival_distance` is set to its default,
   5 m.
2. `guide_map_click_toggle`.
3. Navigation starts on the test pin 60 m away: `guide_material`,
   `guide_radiates_from_body`, then `guide_swarm_alive`,
   `guide_height_above_ground` and `guide_flows_toward_target`.
4. `guide_destination_swarm` (the pin moves 3 m sideways and back).
5. `guide_column_sways` (the pin stays still for 7.5 s).
6. `guide_column_follows_moving_target` (the pin moves sideways at 8 m/s for
   2 s and back).
7. `guide_column_raised` (the pin goes under the ground, above it and under
   the water, then back).
8. `guide_burst_above_slope` (nothing moves; when no loaded slope is steep
   enough, a mound is raised 20 m from you first).
9. `guide_trail_ends_at_target` (the pin moves to 12 m and back to 60 m), then
   `guide_hidden_on_large_map`.
10. `guide_scatters_after_map_clear`; after each of its three cases the section
    waits for the scatter to go and starts navigation on the pin at 60 m again.
11. `guide_scatters_on_manual_clear`, then navigation on the pin at 60 m again.
12. `navigation_clears_on_arrival` and `guide_scatters_on_arrival` from the same
    arrival. Navigation is then started on the pin at 60 m again.
13. `navigation_kept_when_started_inside`. After its arrival the section waits up
    to `ScatterTime` + 0.5 s (currently 1.7 s) for the scattering swarm to
    remove itself, then starts navigation on the pin at 60 m again.
14. `navigation_arrival_distance_setting`; after its arrival at 10 m it waits for
    the scatter to go, and at the end it starts navigation on the pin at 60 m
    again.
15. `guide_distance_format`.
16. With `enable_navigation_guide` off: `guide_option_off_destroys` and
    `navigation_clears_with_swarm_off`; navigation is started on the pin at 60 m
    again before the option is turned back on for `guide_option_on_recreates`.
17. `guide_destroyed_on_clear`, then `guide_settings_restored` and
    `guide_navigation_restored`.

When a scattering swarm is still in the scene after any of these waits, the
checks that count guide objects (`guide_scatters_after_map_clear`,
`guide_scatters_on_manual_clear`, `guide_option_off_destroys` and
`guide_destroyed_on_clear`) SKIP and name the object that was left. Besides the
setup above, the section changes two settings: `navigation_arrival_distance` is
set to 5 m at its start (and to 10 m and 0 within
`navigation_arrival_distance_setting`), and `enable_navigation_guide` is turned
off within `guide_scatters_after_map_clear` and for the option-off checks, and
on again after each. Every setting the section changed is put back before
`guide_settings_restored`, or when the section ends early. When no loaded
terrain within 60 m is steep enough for `guide_burst_above_slope`, the section
raises a round mound 3 m high and 3 m in radius, its sides falling straight to
the ground (about 2 m over 2 m), 20 m from you on dry ground with no building
piece within 5 m: straight ahead (away from the target), or else 45 or 90
degrees to either side. The mound is a change to the world and stays, like a
cultivated plot; a later run that finds it uses it instead of raising another.

| Check | Asserts |
|---|---|
| `guide_setup` | Only when the setup changed something: PASS naming what was turned on or bound for the section |
| `guide_map_click_toggle` | With the large map open, clicking the test pin with `navigation_start_key` held starts navigation to it, and clicking it again clears navigation. The note gives what the simulated click saw (SKIP when `navigation_start_key` is unbound and could not be bound for the section) |
| `guide_material` | Both particle systems, the stream and the column, draw with a resolved particle material whose shader the GPU supports and which blends additively: a fixed additive shader, or blend factors that add onto the background. The note names the source (a built-in additive shader, or a copy of a campfire or torch effect material) and each renderer's shader and blend factors. The mod also logs the source once a session |
| `guide_radiates_from_body` | On the section's first swarm, the burst lights born in its first 0.1 s start within 0.3 m of your body point (`BodyHeight` above your feet). At age `RadiateTime` their median 3D distance from the body point is at least 0.8 m; they lie in at least 3 of the 4 horizontal quadrants around you taken against the direction to the target; at least 10 % of them are behind you (direction to the light · direction to the target below -0.3); at least 15 % are more than 0.4 m above the body point and at least 15 % more than 0.2 m below it; and none has gone below the ground under you by then. While younger than `RadiateTime` + `GatherTime` (currently 0.9 s), none is lower than the terrain under it plus `BurstFloor` - 0.05 m (currently 0.15 m). Once they are travel lights, each one's distance along the line to the target grows over the next 0.5 s. A cloud that stays at your body, spreads only forward or spreads in a flat ring fails it |
| `guide_swarm_alive` | The guide object is a shown root object with exactly two particle systems, two point lights in `navigation_guide_color` and no `ZNetView`; the stream and the column each hold at least 30 lights, at least half of each with alpha above 0.1 in a colour between `navigation_guide_color` and its mix with `MaxWhiteMix` white (currently 35 %); and the HUD label is hidden because the target is off-screen |
| `guide_height_above_ground` | The travel lights' mean height above the terrain (or the sea surface where that is higher) under them, measured by the check itself, is within `HoverHeight` ± (`StreamHalfHeight` + 0.4 m) (currently 0.8–2.2 m), and at least 80 % of them are within it; the note gives the mean, minimum, median and maximum |
| `guide_flows_toward_target` | Over 3 s with the target 60 m away, some travel lights reach at least 12 m along the straight line to the target, none is farther along than `TrailLength` + 1 m (currently 19 m), and none is farther to the side of the line than `StreamHalfWidth` + `WobbleAmplitude` + 0.5 m (currently 1.2 m). Over the same 3 s, every light born 1 s or more after the swarm started is, at every look, no more than 0.3 m behind you along the line and no farther to its side than that same bound. A stream that keeps spreading out around you fails it |
| `guide_destination_swarm` | Over 0.5 s every column light stays within `ColumnRadius` + `JitterAmplitude` + √2 × `SwayAmplitude` + 0.2 m (currently about 1.64 m) horizontally of the vertical axis through the target and within `ColumnCentreHeight` ± (`ColumnHalfHeight` + `JitterAmplitude` + 0.2 m) (currently 0.15–2.85 m) above it, and the same lights move a mean of more than 0.05 m. After the pin moves 3 m sideways, the column's mean position comes within 0.5 m of the new axis within 1 s, and 1.5 s after the move every light is within the bounds again. The pin is put back |
| `guide_column_sways` | After 1.5 s to settle, over 7.5 s (longer than `SwayPeriodX` and `SwayPeriodZ`, so both axes go through a cycle), at each look the column's mean horizontal offset from the axis is set against the mean sway its lights are prescribed (`SwayAt` at each light's height level in the column, 0 at its bottom and 1 at its top). The least-squares slope of the first on the second, both axes pooled with one intercept, is between 0.5 and 1.5; the prescribed mean spans at least half `SwayAmplitude` (currently 0.175 m) along each axis; and every column light stays within the bound of `guide_destination_swarm` from the axis. A still column gives a slope near 0, and the whole column shifted by the top's sway one near 2. Whether the column bends rather than drifts is judged by eye |
| `guide_column_follows_moving_target` | The pin moves sideways, across the line from you, at 8 m/s for 2 s. At every look after the first 0.3 s every column light is within the bound of `guide_destination_swarm` from the moving axis, and the column's mean offset along the motion, each light's prescribed sway taken out, averaged over the looks, is behind by less than half of 8 m/s / `ColumnFollowRate` (currently 0.33 m), what following alone would leave. The pin is put back and the column given 1.5 s to settle |
| `guide_column_raised` | Each case is watched for 0.5 s after 1.5 s to settle, and every column light is within `ColumnCentreHeight` ± (`ColumnHalfHeight` + `JitterAmplitude` + 0.2 m) (currently 0.15–2.85 m) of the base the column should stand on: with the pin 2 m under the terrain at a point whose terrain is above the water level, the terrain there; with the pin 3 m above that terrain, the pin; and with the pin 2 m under the water surface at a loaded point within 60 m whose terrain is at least 1 m below the water level, the water level. The points are taken from one look at the loaded terrain on a 2 m grid within 60 m, at least the arrival distance + 5 m from you. A case with no such point is named as skipped in the note (SKIP when none could be placed), and the raised column over water is then judged by eye. The pin is put back |
| `guide_burst_above_slope` | The steepest point of the loaded terrain within 60 m, on a 2 m grid, where the terrain rises at least 1.5 m within 2 m along a grid axis, is used as the place of your feet. The step the stream makes for a bursting light each frame (its place, the move toward it and the floor under both) is called there for 300 seeds × 10 ages within `RadiateTime` + `GatherTime`, toward a place in the stream `HoverHeight` over the terrain 2 m uphill, with each light one frame at 60 fps from the body point (`FollowRate` read from the mod). No place and no light is lower than `BurstFloor` - 0.05 m (currently 0.15 m) above the terrain under it, measured by the check itself. When no such point exists, the section raises the mound described above, looks at the terrain again and uses its slope; the note then says so and where the mound is. The note gives the slope found (SKIP, naming what failed, when no site for the mound is found, the terrain operation cannot be reached by reflection, the terrain collider does not show the mound within 30 frames, or the terrain is still not steep enough; the burst on a slope is then judged by eye) |
| `guide_trail_ends_at_target` | With the pin moved to 12 m and 1.5 s to settle, over the next 2 s no stream light is farther along the line than the target plus `ColumnRadius` + 0.5 m (currently 1.3 m), and some come within 2 m horizontally of the column's centre. The pin is put back at 60 m. A stream that runs on past the target fails it |
| `guide_hidden_on_large_map` | With the large map open the swarm is hidden: its particle renderers and point lights off and its particle systems paused. Once the map closes it is shown again; the column holds at least 30 lights and none of those it had before the map opened; and over the next 1 s no burst light appears, while the lights born in the first 0.5 s after the close are, at every look, no more than 0.3 m behind you along the line. The map mode is put back (SKIP in a world without a map) |
| `guide_scatters_after_map_clear` | Before each case the section waits up to 3 s for the swarm to be ready: shown, its burst over (no burst light younger than `RadiateTime` + `GatherTime`), and at least 30 stream and 30 column lights with alpha above 0.1. With the swarm ready, the stream's line and the column's axis are taken as in `guide_scatters_on_manual_clear`, the large map is opened and, once the swarm is hidden, the test pin is clicked with `navigation_start_key` held and the map left open. The click holds the swarm: for at least 0.5 s navigation stays cleared, the held swarm is the only guide object and stays hidden, and its lights keep the seeds and positions they had at the click (within 0.001 m; at least 30 stream lights). Within 2 frames of the map closing it is shown and scatters as in `guide_scatters_on_manual_clear`, and within `ScatterTime` + 0.5 s no guide object is left. Two more cases, each with a held swarm: a navigation started on the map before it closes drops the held swarm, and over 0.5 s after closing no scattering object appears, a new swarm is shown with burst lights, and it is then the only guide object; turning `enable_navigation_guide` off drops it within 2 frames with the map open, and over 0.5 s after closing no guide object appears. The option is turned back on. After each case, whatever happened, the section waits up to `ScatterTime` + 0.5 s for every guide object but the live swarm to go before it starts navigation again; one left is named for the later checks that count guide objects. The map's view and mode are put back after each click; a large map you had open is closed to the small map until the section ends (SKIP in a world without a map, when `navigation_start_key` is unbound and could not be bound for the section, when the option could not be turned off, or, naming the case, when the swarm was not ready in time) |
| `guide_scatters_on_manual_clear` | With the swarm ready as in `guide_scatters_after_map_clear` and the pin 60 m away, the check takes the stream's line itself - from you, horizontally toward the target, as far as the target or `TrailLength`, whichever is nearer - and the column's axis at the target. Then the routine both manual clears use (map click and looked-at-object key) clears navigation with the "Navigation cleared" message, and exactly one guide object is left, the swarm that was shown, shown and scattering. Following the stream and the column separately for 0.8 s, from the lights each held with alpha above 0.1 at the first look after the scatter began: no light with a new seed appears, every one of those lights is still there, at least 75 % of the stream's lights moved outward from their nearest point on the line the check took and at least 75 % of the column's outward from the axis it took (each light's outward direction taken at the first look), each system's mean outward move is at least the bound of `guide_scatters_on_arrival`, and their mean alpha falls below half its first value. The line the scatter was handed ends within 0.25 m of the ends of the check's line, and its axis lies within 0.25 m of the target, so a line of one point at the column fails. Within `ScatterTime` + 0.5 s no guide object is left, and the section waits up to that long for every guide object but the live swarm to go before it starts navigation again. A stream flying off from the column's axis instead moves only about half its lights outward (SKIP when the swarm was not ready in time) |
| `navigation_clears_on_arrival` | The test pin is moved in front of you and raised 3 m above your own height. At `navigation_arrival_distance` + 0.5 m (5.5 m at the section's 5 m) horizontally navigation stays on and the swarm is shown; at `navigation_arrival_distance` - 0.5 m (4.5 m, about 5.4 m in 3D, so a 3D measurement would not end it) navigation ends within 2 frames, the HUD label is gone, there is no live swarm, and the test pin is still on the map. The note gives both distances and the height above you |
| `guide_scatters_on_arrival` | At that arrival exactly one guide object is left, the swarm that was shown. Following the stream and the column separately for 0.8 s, from the lights each held with alpha above 0.1 at the first look after the arrival: no light with a new seed appears, every one of those lights is still there, their mean horizontal distance from the column's axis grows by at least half the outward distance the slowest light covers by then (from `ScatterDelay`, `MinScatterSpeed`, `ScatterDecay` and the smallest outward share of a direction, (1 - `ScatterSpread`) / (1 + `ScatterSpread` + `ScatterLift`); currently about 0.05 m at 0.8 s), and their mean alpha falls below half its first value. Both point lights are dimmer than at the arrival, and within `ScatterTime` + 0.5 s no guide object is left (SKIP when navigation did not end on arrival) |
| `navigation_kept_when_started_inside` | From a navigation on the test pin 60 m away, the pin is moved to 3 m and navigation is started on it in the same frame: a frame later the swarm is a new object, and navigation is still on after 1 s. With the pin at 8 m for two frames and then back at 3 m, navigation ends within 2 frames |
| `navigation_arrival_distance_setting` | With `navigation_arrival_distance` at 10 m, the steps of `navigation_clears_on_arrival` at 10.5 m and 9.5 m: navigation stays on at 10.5 m and ends within 2 frames at 9.5 m. With it at 0, after the pin at 10.5 m, the pin at 0.5 m keeps navigation on for 1 s. The setting goes back to 5 m |
| `guide_distance_format` | With the pin 20 m in front of the camera, the navigation label reads the pin's name followed by the rounded horizontal distance in brackets |
| `guide_option_off_destroys` | With `enable_navigation_guide` off, no guide object is left in the scene |
| `navigation_clears_with_swarm_off` | With `enable_navigation_guide` off, the steps of `navigation_clears_on_arrival` still end navigation: it stays on at 5.5 m and ends within 2 frames at 4.5 m |
| `guide_option_on_recreates` | With the option back on, the swarm is shown again for the navigation started at 60 m |
| `guide_destroyed_on_clear` | A frame after navigation is cleared without a scatter, the way it ends when the map's pins are cleared (`Navigation.ClearTarget`), no guide object is left |
| `guide_settings_restored` | Every setting the section changed (`navigation_arrival_distance`, `enable_navigation_guide`, and the module toggle and `navigation_start_key` when the setup changed them) is back at your value |
| `guide_navigation_restored` | The navigation you had before the section is set again, or none is left when you had none (FAIL when your target pin is no longer on the map; navigation is then restored through a temporary marker) |

The section removes only its own test pin and puts back the map mode and your
navigation target, also through the session hand-back when a run is cut short.
Whether the swarm looks like guiding insects, how the arrival feels, and the
frame cost remain an in-game judgement outside this section.

#### `compass`

Run `automatics_smoke_features compass` in a world with the game unpaused, the
HUD shown and the large map closed (the section SKIPs otherwise, and in a world
without a map). When Automatic Mapping is off it is turned on for the section,
and the section waits until the module runs (`compass_module_enabled`); it
SKIPs only when that fails. A pin search that applies to the minimap is cleared
for the section and set again at its end (`compass_search_cleared`); the
section SKIPs only when it stays applied. Stand still while it runs. After that
setup everything runs in one frame: the section adds unsaved test
pins at known bearings and distances from you, turns the camera to known
headings and runs the compass update itself, so no scan moves or removes a pin
in between. For the section it turns `enable_compass`, `enable_compass_labels`
and `enable_map_enhancements` on, sets `compass_position` to `Top` and
`compass_pin_range` to 80 m, clears `map_filter_hidden_categories` and turns
`hide_unexplored_automatic_mapping_pins` off, and it changes them further for
single checks as described below; `compass_unexplored_filter` turns
`enable_automatic_mapping` on when it is off. It clears your navigation target before it
counts the pins on the map and sets it again at the end; the target checks
navigate to test pins without the start message. The boss bar check spawns a
non-persistent boar, marks it as a boss and shows its bar through the enemy
HUD, so it needs a single-player world (it SKIPs otherwise); the creature, its
flag and its bar are removed in the same frame.

| Check | Asserts |
|---|---|
| `compass_module_enabled` | Reported when the section turned Automatic Mapping on: it started within 600 frames |
| `compass_search_cleared` | Reported when a pin search applied to the minimap: its text was cleared for the section |
| `compass_strip_shown` | The strip exists, is shown and sits under the HUD's root object |
| `compass_letters_facing_north` | Facing north, N is at the centre and E and W at the strip's ends, each within 1 canvas unit, and S is hidden |
| `compass_letters_follow_turning` | At headings 30 and 225 each letter is where its angle from the heading puts it, or hidden beyond the half view |
| `compass_letters_localized` | The letters read N, E, S and W, in Japanese as in English |
| `compass_marker_positions` | Pins at -60, 0, 30 and 75 degrees from the heading, 20 to 70 m away, are shown within 1 canvas unit of `angle / 90 × 300` |
| `compass_marker_icons` | Each of those markers shows its own pin's icon; the four pins have four different icon types (SKIP when those share a sprite) |
| `compass_range_cut` | A pin 90 m away in view is not shown with the range at 80 m and is shown with it at 100 m |
| `compass_view_cut` | Pins at 180 and -100 degrees are not shown; turned to face each without collecting the pins again, each is shown at the centre |
| `compass_icon_type_toggle` | With its icon type turned off in the minimap's icon filter, a pin is hidden while a pin of another type stays; turned back on, it is shown again |
| `compass_category_filter` | An automatic pin registered with a live Flora category on explored ground is hidden while `map_filter_hidden_categories` holds `Flora`, and shown again once it is empty |
| `compass_manual_pin_kept` | A manual pin of the same type stays shown while Flora is hidden, and a manual pin on unexplored ground stays shown with `hide_unexplored_automatic_mapping_pins` on |
| `compass_unexplored_filter` | An automatic pin 110 to 290 m away on unexplored ground (with the range at 300 m and the camera facing it) is shown until `hide_unexplored_automatic_mapping_pins` is turned on and hidden after, while an automatic pin on explored ground stays (SKIP when `enable_automatic_mapping` could not be turned on or everything in that distance is explored) |
| `compass_cap_nearest_in_view` | With 24 pins 5 to 28 m behind you and 30 pins 32 to 61 m ahead, the 24 shown are the nearest in view by the map's own rule, and none of the pins behind is among them |
| `compass_cap_after_turn` | Turned around before the next collection of pins, the 24 shown are again the nearest in view, now the pins behind, and none of the pins ahead |
| `compass_nearer_on_top` | The shown pins run nearest first, and each marker is drawn over the next |
| `compass_hidden_on_large_map` | With the large map open the strip is hidden, and it is shown again with the small map |
| `compass_hidden_without_minimap` | With the map mode the game uses while you are dead and in a world without a map, the strip is hidden, and it is shown again with the small map |
| `compass_hidden_with_hud_hidden` | With the HUD hidden the strip is hidden, and it is shown again once the HUD is shown |
| `compass_strip_in_top_band` | The strip's screen rectangle is within the screen, horizontally centred within 2 pixels and inside the top fifth, under the HUD's root object |
| `compass_strip_in_bottom_band` | With `compass_position` at `Bottom`, the strip's screen rectangle is within the screen, horizontally centred within 2 pixels and inside the bottom fifth, under the HUD's root object |
| `compass_position_switch` | Switching `compass_position` from `Top` to `Bottom` and back while the game runs puts a new strip in the top, bottom and top band in turn |
| `compass_distance_style` | Pins 10 m, half the range (40 m) and just inside the range (79.5 m) away are drawn at the size, grey tint (red, green and blue alike) and opacity the eased curve gives at their share t of the range, with f = 1 - (1 - t)^2: from 52 canvas units, white and fully opaque at you to 12 units, a 0.55 grey and 0.6 opaque at the range, falling fastest near you (about 22 units, a 0.66 grey and 0.7 opaque at half the range, where a linear fall would give 32, 0.78 and 0.8) |
| `compass_target_beyond_range` | A navigation target 120 m away, beyond the 80 m range, is shown on its own marker at its bearing with its own icon, in `navigation_guide_color` at full opacity and at the full 52 units, without the grey tint and unaffected by the distance curve |
| `compass_target_not_duplicated` | A navigation target 10 m away in view is on its own marker and not also an ordinary marker, and with 24 other test pins in view the 24 ordinary markers show the nearest in view by the map's own rule, the target left out |
| `compass_target_behind_hidden` | A navigation target 30 m behind you is not shown; with the navigation left on it, it is shown at the centre once the camera turns to face it and hidden again once the camera turns back |
| `compass_target_follows_live_position` | A creature target whose pin lies 40 m away at 30 degrees, last seen 25 m away at -20 degrees and then 50 m away at 10 degrees with the same pin, has its marker at the bearing and its label at the distance of where it was last seen each time, and its pin is not an ordinary marker |
| `compass_target_cleared` | A temporary marker navigated to shows on the target's marker; once navigation is cleared and the marker removed from the map, the target's marker is hidden and the removed marker is not shown as an ordinary marker in the same frame, before the next collection of pins (SKIP when nearer pins of yours would push it out of the 24 markers) |
| `compass_label_faced` | A named pin 4 degrees from the centre is labelled `name [Nm]` at its marker's x, with the name localized and the horizontal distance rounded to the metre, and no target label shows without navigation |
| `compass_label_not_rewritten` | With the pin, its name and the metre unchanged, the next update leaves text written into the label in place |
| `compass_label_rename` | Renamed at the same distance, the pin's label reads the new name |
| `compass_label_outside_window` | Turned 20 degrees to the left, with the test pins 24 and 25 degrees off the centre, no pin is labelled |
| `compass_label_target` | A navigation target 45 degrees to the left is labelled at its marker's x while the faced pin keeps its own label |
| `compass_labels_after_position_change` | After `compass_position` changes to `Bottom` and back to `Top`, both labels read as before, above the strip and the 8 units its nearest icons overflow it at the bottom and below them at the top |
| `compass_labels_after_recreation` | After `enable_compass` is turned off, which removes the strip, and on again, both labels read as before |
| `compass_label_faced_target_once` | When the faced pin is the navigation target, only the target label shows, at its marker's x |
| `compass_label_target_in_window` | With a pin at 0 degrees and the navigation target at 20 degrees, outside the 10-degree window, both are labelled; with the target at 4 degrees, inside the window, only the target is labelled, though the pin is nearer the centre |
| `compass_labels_option_off` | With `enable_compass_labels` off neither label shows; turned back on, both show again |
| `compass_avoids_event_bar` | At the top, the event bar shown and moved over the band of the strip, its overflowing icons and its labels (8 units above and below the strip, then the label below that) moves the strip down so that its top is 8 units plus the 8 the icons overflow it below the bar, which leaves the icons 8 units below the bar, and neither the icons nor the labels overlap it; hidden again, the strip goes back. The section first checks on its own that the moved bar overlaps the band (FAIL when it does not) |
| `compass_avoids_boss_bar` | The same for the bar of the test creature marked as a boss (SKIP outside a single-player world) |
| `compass_bottom_ignores_bars` | At the bottom, each of those bars moved over the strip, its overflowing icons and its labels leaves the strip where it is |
| `compass_pins_removed` | None of the section's test pins is left on the map, and every pin there before the section still is |
| `compass_navigation_restored` | Your navigation target is set again, compared by its target state, position, name and creature rather than by pin, since a temporary marker comes back as a new pin; with none before, none is left |
| `compass_icon_filter_restored` | After your navigation is set again, which can add a temporary marker and so turn its icon type back on, every icon type in the minimap's filter is shown or hidden as before the section |
| `compass_settings_restored` | Every setting the section changed is back at your value |

"The map's own rule" is worked out by the check over every pin on the map: the
pins the minimap would draw (icon filter, shared-map fade, automatic-pin
filters), within range and the half view, nearest first. A check whose test pins
that rule would push out of the 24 markers in favour of nearer pins of yours
SKIPs as inconclusive; run it away from crowded pins. So do the label checks
when a pin of yours lies at least as near the centre as the test pin (or, for
`compass_label_outside_window`, within 10 degrees of it), and the three bar
checks while a boss's bar is already shown. The section removes only
its own test pins, and puts back the camera, the map mode, the HUD, the pin
search, the event
bar's place and visibility, your navigation target and, after it, the
minimap's icon filter (vanilla turns a hidden icon type back on when a pin of
that type is added, which a temporary marker set again with your navigation
is), and removes the test creature with its boss flag and bar, also through
the session hand-back when a run is cut short. How the strip looks, the
legibility of its labels and the fade, its legibility at other GUI scales and
screen shapes, its collisions with the bars at the bottom of the screen, and its
avoidance of a real boss fight's bar and a real event's bar remain an in-game
judgement outside this section.

#### `supply`

Run `automatics_smoke_features supply` in a single-player world with the
inventory closed, outside build mode and not at a crafting station. The section:

- turns `automatic_supply / module` on when it is off and waits for it to start,
  and sets `supply_range` to 4 m; both are recorded and put back through the
  session, like every setting a section changes;
- lifts your own no-cost toggle (`nocost`) for the section and turns it back on
  afterwards, because vanilla skips crafting and building costs while it is on;
- when a container stands within 5 m of you, moves you with a short teleport
  to the first clear, dry and fairly level spot on rings 15 to 45 m away (see
  *Safety of the console commands*; `supply_relocated`), and moves you back at the end
  of the section. It SKIPs when no such spot is found or another usable
  container is still within 4 m;
- puts aside every early material you carry and everything the smelter and the
  cooking station take, then more items until four inventory slots are free,
  and one more slot than the chosen recipe's materials when the craft checks
  need it (`supply_items_put_aside`); the same items go back at the end of the
  section. It SKIPs when four slots cannot be freed;
- when you know no recipe whose result the made-up recipes can show, teaches
  you one (`supply_recipe_learned`); your character keeps it;
- spawns a wooden chest 1.5 m to your right, a workbench, a fireplace, a smelter,
  a cooking station over a fire pit and, for one check, a ward, all
  non-persistent.

Every test item is an early material you carry none of, chosen at runtime (a
workbench recipe and a hammer piece of such materials, the fireplace's fuel, the
smelter's and the cooking station's accepted items). The log names none of
them. A check whose station or material cannot be found SKIPs and says why.

| Check | Asserts |
|---|---|
| `supply_module_enabled` | Reported when the section turned the module on: it started within 600 frames |
| `supply_no_cost_lifted` | Reported when your no-cost toggle was on and was lifted for the section |
| `supply_relocated` | Reported when a container stood within 5 m: you were moved to a clear spot |
| `supply_items_put_aside` | Reported when items had to be put aside; the note names them |
| `supply_recipe_learned` | Reported when you knew no recipe for the made-up recipes to show and one was learned |
| `supply_container_panel_hidden` | With the container panel left active as the last chest leaves it, `InventoryGui.Show(null)` hides it in the same frame; `InventoryGui.Hide()` alone leaves it as vanilla does (still active, for the closing fade); the inventory is closed and the panel hidden again afterwards |
| `supply_containers_eligible` | The test chest is the only usable container within Supply Range |
| `supply_scoped_count` | With the recipe's materials only in the chest and the workbench as the current station, the real requirement check passes, and every per-quality count it makes is the inventory plus the chest for that quality; the same count outside the check is the inventory alone |
| `supply_panel_matches_check` | The build panel (`Hud.SetupPieceInfo`) and the build check (`HaveRequirements(piece, CanBuild)`) count the inventory plus the chest for a material the chest holds (SKIP when the piece's crafting station is not in range) |
| `supply_partial_insertion` | With room for two, a take of five moves two, leaves the rest in the chest and reports a full inventory; closing the action returns both |
| `supply_ledger_refund` | Two taken, one removed from the inventory the way vanilla consumes: the ledger reads pulled 2, debited 1, unconsumed 1, and one goes back to the chest |
| `supply_insertion_shortfall_returned` | The last free slot is filled from the chest's own change callback, between the removal from the chest and the insertion: nothing arrives, the full inventory is reported, and every unit is back in the chest |
| `supply_craft_complete` | `InventoryGui.DoCrafting` with the materials only in the chest crafts the item, and the chest pays exactly the recipe cost |
| `supply_craft_incomplete` | With room for one material only, `DoCrafting` crafts nothing, the chest ends unchanged and no material is left in the inventory |
| `supply_craft_rebuilds_list_once` | With the inventory open at the workbench and twice the materials in the chest, `DoCrafting` crafts the item and rebuilds the recipe list (`InventoryGui.UpdateRecipeList`) once, with the chest counted (not while the containers are hidden from the craft's own requirement check): a recipe made up from the same materials shows craftable from what the chest still holds, with none of them left in the inventory. Three crafting panel updates (`InventoryGui.UpdateRecipe`) after it rebuild nothing, and neither do three more once the stock refresh is due again (the stock cache is dropped, as when the refresh interval has passed) |
| `supply_choose_one_recipe_vanilla_count` | With a made-up recipe where the player chooses one ingredient listed and selected, the inventory open, no station, and its ingredient only in the chest, every count the panel update makes of that ingredient is the inventory alone, as vanilla counts |
| `supply_recipe_list_follows_stock` | With that recipe selected, a made-up ordinary recipe that needs one of the ingredient listed, and no craft, the ingredient added to the chest (Supply's stock cache left as it was read) rebuilds nothing and leaves the ordinary recipe uncraftable in the three panel updates of the frame the stock was read; in the three updates after the stock refresh is due (the cache is dropped, as when the refresh interval has passed), the list is rebuilt once with the chest counted, the ordinary recipe shows craftable, and the selection stays on the choose-one recipe |
| `supply_place_incomplete` | With a full inventory and the placement status held at Valid (vanilla's ghost update is skipped and a stand-in ghost is set, and `Player.PlacePiece` is intercepted and recorded instead of run), `Player.TryPlacePiece` inside the module's placement bracket returns false, the module's own rejection counter moves by one, `PlacePiece` is never reached, and nothing moves |
| `supply_place_failed_refund` | With room, the placement takes every material, vanilla refuses the spot (the placement status is held at Invalid, so vanilla can never place the piece here), and closing the bracket returns everything to the chest |
| `supply_fireplace_intake` | `Fireplace.Interact` takes one fuel from the chest; none is left in the inventory |
| `supply_exception_no_refund` | The player inventory's change callback throws once, right after the fuel arrived: `Fireplace.Interact` throws, chest plus inventory is unchanged (the fuel stays in the inventory, not returned), and no action, counting scope or suppression is left open |
| `supply_smelter_ore_intake` | The smelter's add-ore switch takes one accepted item from the chest (SKIP when you still carry an item the smelter takes) |
| `supply_smelter_fuel_intake` | The smelter's add-fuel switch takes one fuel from the chest |
| `supply_cooking_intake` | With the test fire lit, the cooking station takes one accepted item from the chest into a free slot (SKIP when the fire does not light the station within 240 frames) |
| `supply_in_use_chest_untouched` | With the chest marked in use, the fireplace takes nothing from it |
| `supply_warded_chest_untouched` | Inside a switched-on ward no player has access to, the fireplace takes nothing from the chest |
| `supply_other_games_chest_untouched` | With the chest owned by another player's game (a made-up session id), Supply does not list or count it, the fireplace takes nothing from it, and the chest stays that game's; it is this game's again afterwards |
| `supply_take_leaves_chest_without_item_unclaimed` | With the chest empty and without an owner (which the server claims before it writes a chest), a take of the fireplace's fuel moves nothing and leaves the chest without an owner: a chest that lacks the item is passed over before the container gate; it is this game's again afterwards |
| `supply_settings_restored` | Every setting the section changed is back at your value |

The crafting checks run in one frame, because the player drops a station it is
not using at its next update. The three crafting panel checks open the inventory
and close it again within that frame, and drive the panel update directly instead
of waiting for frames. They SKIP when the inventory is open as they start (close
it, then run the section again), because opening it over an open chest would
leave that chest in use. The recipes they make up show the result of a recipe
you already know, so they teach you nothing; they are in the game's recipe list
only within that frame. Your selected recipe and crafting tab are restored. The
crafting skill and the cooking skill are put back afterwards. The
section counts every unit of a test item it gives, puts in the chest or crafts,
and never removes more than that. After each frame boundary
it records any test item already in your inventory or the chest as yours (a
pickup while it waited) and stops with a SKIP; those units are never removed.
When a count cannot be accounted for, nothing of that item is removed and
`supply_test_items_removed` SKIPs. The chest is removed only when taking out the
section's own units leaves it empty; otherwise it is kept, saved with your world,
and its position and contents are logged (`supply_chest_removed` SKIP). A test
object whose removal fails is kept for the next hand-back trigger. The real UI
paths (a craft through the crafting panel, a placement with the hammer, a grid
planting with seeds only in a chest) and multiplayer container use remain
in-game checks outside this section.

#### `woodcutting`

Run `automatics_smoke_features woodcutting` in a single-player world, standing on
the ground, out of build mode and not attacking, swimming or attached. The
section:

- sets `woodcutting_interval` to 0 before it equips or spawns anything, so no
  periodic pass runs while it holds a test axe. The write is recorded through the
  session like every setting a section changes, and put back at the end. Some
  checks set it to 1 s and back to 0 around a single call, in the same frame;
- turns Automatic Woodcutting's `module` setting on when the module is off;
- checks the tree data against the prefabs of the loaded world and the trees the
  world generator places. The species each log and stump prefab should have is
  worked out by the section itself, from the prefabs alone, and the module's
  resolver is compared with it;
- sets `woodcutting_range` to 3, the default, when the range a pass reaches
  (`woodcutting_range`, or the axe's attack range at 0) does not reach the test
  tree 2 m in front of you;
- turns you to the first of eight headings, 45° apart and starting with the way
  you face, with no building piece, tree, log, creature or other destructible
  object within 2.5 m of the point 1 m ahead. When no heading will do, or a tree,
  log or stump of your world stands within 3.5 m of you, where a pass could take
  it before the test objects, it moves you to clear ground nearby, as described
  in *Safety of the console commands*: a site with a clear heading, no tree, log
  or stump within 3.5 m and no building piece or map pin within 3.5 m. You are
  taken back to where the command started at the end of the section;
- puts unequipped items aside until five inventory slots are free, and puts them
  back at the end of the section;
- writes, where yours differ, the settings the checks assume:
  `enable_automatic_mining` off when Automatic Mining mines on an interval, so it
  cannot wear a pickaxe of yours or raise your Pickaxes skill while the section
  runs across frames; `enable_automatic_woodcutting` on, `need_to_equip_axe` on,
  and the beech's species in `allow_woodcutting_tree`. A
  check that needs another value writes it and writes this one back straight
  after;
- spawns a non-persistent beech 2 m in front of you and gives it a health no hit
  reaches, and nothing to drop, so it falls only when the section fells it at the
  end;
- adds the first earlier-biome axe whose tool tier reaches the tree, and a club,
  and equips the axe; the inventory check adds one axe of each other tier;
- registers a hand-back with the session, run at the end and whenever the run is
  cut short, that removes the added items, equips your items again, and puts back
  the skills a check can raise (the axes' skill and special-hit skill, and the
  club's) and the durability of your own axes;
- adds an observer patch, removed at the end of the section, that records every
  damage call on the test objects, whoever makes it, and every skill raise of
  yours, and drops every damage call on any other tree, small tree, log or stump,
  whoever makes it; other destructibles are left alone. When a test tree is
  felled, the log and the stump it spawns become test objects in the same frame:
  recorded with the session, non-persistent, at a health no hit reaches, and the
  log held where it spawned instead of falling. The observer's removal is
  registered with the session before its patches go on, so a run cut short still
  removes them.

Everything up to the felling runs in one frame, with one exception: when no ray
from your eyes meets anything at all on its way to the test tree just spawned,
the section waits for the next physics step and looks once more (see the end of
this section). The hit checks call the module's
per-target entry, `AutomaticWoodcutting.Chop`, which every woodcutting pass uses
for each candidate; the pass checks call the hooks the interval and the key use.
The felling checks span frames: a stump gets the component in its `Start`, and a
log takes damage 0.2 seconds after it spawns.

After the felling, the section removes the test tree, its log and its stump, and
spawns small trees where the tree stood, one at a time: non-persistent
Destructibles the tree data names by their own prefab name, `Beech_small1` and,
when the world has it, `Beech_small2`. Once its `Start` has run, each is given a
health no hit reaches, and its damage is set to spawn nothing and to reach no
ward, so a hit leaves nothing behind. Then a Destructible prefab that is neither
a stump nor named by the tree data is spawned where the tree stood, and
`Beech_Sapling` 1.2 m to the right of it. That prefab is picked the same way
every run: the first by ordinal name among those of type `Tree`, else among all
of them, never one that grows or spawns creatures; the log file names it.

| Check | Asserts |
|---|---|
| `woodcutting_setup` | The section set itself up; the note says what it changed: the module turned on, `woodcutting_range` written, the heading it turned you to, the move to clear ground and the items put aside |
| `woodcutting_species_identifiers_match` | Every built-in tree identifier matches at least one standing tree (`TreeBase`) prefab of the loaded world. A FAIL names the identifiers of the earlier biomes and counts the others; the log file names them all |
| `woodcutting_species_coverage` | The section walks every standing tree prefab's log, that log's smaller logs in turn, and its stump, and gives each of them the species of every tree that leads to it. The module resolves each of those prefabs to exactly that set, each standing tree prefab to its own species, and a prefab that leads to no species to none. A prefab that leads to no species fails the check only when the world generator places it, or when it is a log or stump that a tree the generator places leads to. The generator places the prefabs of the enabled entries of its vegetation list (`ZoneSystem.m_vegetation`). The other prefabs that lead to no species, such as legacy prefabs, trees of an area the generator does not place and `TreeLog`s that are not wood, do not fail: the log file names each with the reason, and the note counts them. The log file also lists, in one line, the prefabs that lead to no species and are placed or reached from a placed tree. SKIP when the vegetation list is missing or has no enabled entry with a prefab, unless a prefab resolves differently. A FAIL counts the prefabs that fail for leading to no species and those resolved differently; the log file names them |
| `woodcutting_species_shared_allowed` | For every log or stump prefab that trees of more than one species lead to, by the same walk, `allow_woodcutting_tree` holding any one of those species alone allows it, and holding none of them does not; the list is written back straight after. The note counts them; the log file names them with their species |
| `woodcutting_small_tree_coverage` | Every prefab of the loaded world whose own name a built-in tree identifier matches, and that is neither a standing tree (`TreeBase`) nor a log (`TreeLog`), is a small tree and carries a `Destructible` and a `ZNetView` on its root, so it can be a target. It FAILs when the tree data names no such prefab. The screen gives counts; the log file names them |
| `woodcutting_tree_registered` | The tree spawned while the module runs carries an `AutomaticWoodcutting` component of the running generation |
| `woodcutting_hit_damage` | One call of the entry hits the test tree once and takes health; the note gives the hit's distance from your eyes and its height against them |
| `woodcutting_hit_data` | The hit's attacker is you, its hit type is `PlayerHit`, its push force is 0, its direction is level and points from you toward the tree, and its skill is the one a real swing of the axe uses on a tree: the attack's special-hit skill when trees are its special-hit type, otherwise the axe's own skill, and its item world level is the axe's |
| `woodcutting_hit_durability` | The axe lost its per-hit drain, scaled by the world durability rate |
| `woodcutting_hit_skill_once` | The skill raise follows the rule of a swing as the attack's fields define it: when the tree is of the attack's special-hit or skill-hit type, the hit's skill is raised exactly once, by the raise amount of the axe's attack; otherwise nothing is raised. The comparison with what a real swing raises is in `woodcuttingarea` |
| `woodcutting_refused_no_weapon` | With nothing in your right hand, the entry hits nothing and spends nothing: no damage call, and no change of the tree's health, the axe's durability, your stamina or any skill |
| `woodcutting_refused_other_weapon` | The same with the club in your right hand |
| `woodcutting_refused_no_durability` | The same with the axe's durability at 0 (put back afterwards; SKIP for an axe that does not use durability) |
| `woodcutting_refused_tool_tier` | The same with the test tree's minimum tool tier raised above the axe's (put back afterwards) |
| `woodcutting_refused_blocked` | The same with a non-persistent wood wall spawned across the line from your eyes to where the hit landed, and removed again in the same frame (SKIP when the wall does not end up on that line) |
| `woodcutting_interval_zero_no_pass` | With `woodcutting_interval` at 0, the module's interval hook, given a 10-second step, hits nothing |
| `woodcutting_interval_pass` | With `woodcutting_interval` set to 1 s, the same hook given a 2-second step runs one pass that hits the test tree once and nothing else, and the interval is written back to 0 straight after the call (SKIP under the same condition as `woodcutting_key_pass`) |
| `woodcutting_key_pass` | At interval 0, one pass run through the call the key makes when it goes down hits the test tree once and nothing else (SKIP when a tree, log or stump of your world is not farther from you than the test object, since the pass could go to it; the same holds for every later check that runs a pass) |
| `woodcutting_refused_out_of_range` | With `woodcutting_range` set below the distance from your eyes to where the hit landed, the entry hits nothing and spends nothing; the range is written back straight after |
| `woodcutting_species_tree` | The test tree resolves to its species, which `allow_woodcutting_tree` allows |
| `woodcutting_species_removed_tree` | With that species taken out of `allow_woodcutting_tree`, the tree is not allowed and a key pass leaves it alone; the list is written back straight after |
| `woodcutting_switch_off_no_pass` | With `enable_automatic_woodcutting` off and `woodcutting_interval` at 1 s, the interval hook given a 2-second step hits nothing and takes up no timer; both are written back straight after. The key's path has the same guard but cannot be pressed from the console |
| `woodcutting_inventory_axe_used` | With `need_to_equip_axe` off and nothing in your right hand, a key pass hits the test tree with the lowest-tier axe of your inventory whose tool tier reaches it (the first such one on a tie): the hit carries that axe's tool tier and world level, and only that axe loses durability. The tree's tier is raised, for this check only, to the second of the available tiers, so the lowest test axe does not reach it. Your own axes count too, and their durability is put back |
| `woodcutting_inventory_axe_needs_equip` | With `need_to_equip_axe` on and nothing in your right hand, the same pass hits nothing and wears no axe |
| `woodcutting_felling_spawned` | With the tree at 1 health, one hit of the entry fells it into a log and a stump, both taken as test objects |
| `woodcutting_fresh_log_refused` | In that frame the log carries a component of the running generation, the entry refuses it without wearing the axe, and a key pass leaves it alone |
| `woodcutting_stump_attached` | Within 10 frames, the stump carries a component of the running generation, attached by the module's own `Destructible.Start` postfix |
| `woodcutting_species_log_stump` | Once the log can take damage, the log and the stump resolve to the felled tree's species and are allowed |
| `woodcutting_species_removed_log_stump` | With that species taken out of `allow_woodcutting_tree`, the log and the stump are not allowed, unless another allowed species also leads to them, and a key pass leaves the ones that are not alone; the list is written back straight after |
| `woodcutting_log_hit_after_delay` | The entry hits the log once (SKIP when something stands between your eyes and the log, or no part of it is found at your eye height, also after one physics step; see below) |
| `woodcutting_log_skill` | The skill raise on the log follows the same rule as on the tree |
| `woodcutting_stump_pass_hit` | With the log removed, a key pass hits the stump once (SKIP when something stands between your eyes and the stump, or no part of it is found at your eye height, also after one physics step) |
| `woodcutting_stump_skill` | The skill raise on the stump follows the same rule |
| `woodcutting_other_destructible_not_attached` | A non-persistent destructible of the earlier biomes that is not the stump of a tree, spawned 2 m behind you, has run its `Start` and carries no component |
| `woodcutting_small_tree_registered` | Each small tree, once its `Start` has run, carries an `AutomaticWoodcutting` component of the running generation on its `Destructible` (SKIP when neither small tree prefab is available) |
| `woodcutting_small_tree_species` | Each small tree resolves to the beech's species alone, and is allowed while that species is in `allow_woodcutting_tree` and not while it is taken out; the list is written back straight after |
| `woodcutting_small_tree_pass_hit` | One key pass hits each small tree once, takes health and hits nothing else (SKIP when a tree, log or stump of your world is not farther from you than the small tree, when something stands between your eyes and it, when no part of it is found at your eye height, also after one physics step, or when its tool tier is above the test axe's) |
| `woodcutting_small_tree_others_not_attached` | Once their `Start` has run, the picked Destructible and `Beech_Sapling` carry no component, and the module's rule does not take them as targets. It runs only after a small tree got the component from the same hook (SKIP otherwise); the screen does not name the picked prefab |
| `woodcutting_world_guarded` | The section ran to its end under the guard; the note counts the damage calls on other trees, logs and stumps it dropped |
| `woodcutting_settings_restored` | Reported when the section changed a setting: every setting it changed is back at your value |

When the section cannot start it SKIPs `woodcutting_section` and names the
reason: the module could not be turned on, the interval or another setting could
not be written, no clear spot was found where you stand or nearby (the reason
names the sites tried), something stands between your eyes and the test tree, no
part of the test tree is found at your eye height, or the inventory slots could
not be freed. The checks that run a pass still SKIP when a tree, log or stump of
your world has come nearer than the test object since. The section looks along rays from your eyes toward each of the
tree's colliders, at eye height, aiming both at the vertical line through the
tree's position and at the collider's own vertical centre line. No part is found
when none of those rays meets anything at all. The section then waits for the next physics
step, syncs the physics engine with the transforms and looks once more, and it
SKIPs only when that look meets nothing either. The log, stump and small tree hit
checks do the same before they SKIP for that reason, and a hit the module refused
still FAILs when the second look reaches its target. Such a SKIP says that the
details are in the log file: the position of your eyes, of you and of the ground
under you, the target's position and the ground there, and for each collider
under the target, inactive, disabled and trigger ones included, its path and
layer, whether it is enabled, a trigger and active, its type (and for a mesh
collider whether it is convex) and its bounds. For each active, enabled, solid
collider it adds each point the module's entry aims at on it (the point nearest
your eyes on a box, sphere, capsule or convex mesh collider; on any other, the
vertical line through the target's position and the collider's own vertical
centre line, both at eye height kept within its bounds), whether a ray toward
that point meets the collider itself and at what distance, and the first solid
collider of any layer that ray meets. At the end the added axes and club
are removed from your inventory, the items you had equipped are equipped again,
the items put aside go back, the skills are put back, and the test objects,
including the log, the stump, the small trees and the other Destructibles the
section spawned, are removed. Your view stays turned to the heading unless you
were moved. The
hits still count toward your tree statistics. The fall direction of a real tree,
real stumps of your world and the feel of the interval remain in-game checks
outside this section.

#### `woodcuttingarea`

Run `automatics_smoke_features woodcuttingarea` in a single-player world, standing
on the ground, out of build mode and not attacking, swimming or attached. Before
it spawns anything, the section writes the settings below, then sets up the rest
itself:

- it turns Automatic Woodcutting's `module` setting on when the module is off,
  and `enable_area_woodcutting` on when it is off;
- it turns you to the first of eight headings, 45° apart and starting with the
  way you face, with no building piece or other destructible object at any of
  the spots the test trees of the section go to, the same-kind checks' included,
  or within a swing's reach, and no allowed tree, log or stump of your world
  within the raised area radius plus 2 m of the spot in front of you. While it
  looks, `allow_woodcutting_tree` holds every species the section allows at some
  point (the beech's, the second species' and the birch's), and it is set to the
  beech's species alone again afterwards;
- when no heading will do where you stand, it moves you to clear ground nearby,
  as described in *Safety of the console commands*: a site with such a heading
  and no building piece or map pin within the raised area radius. You are taken
  back to where the command started at the end of the section;
- it puts unequipped items aside until two inventory slots are free, and puts
  them back at the end of the section.

The `woodcutting_area_setup` check reports what was done. When a step fails, the
section SKIPs `woodcuttingarea_section` and names the step and why, for instance
every site it tried. It spawns non-persistent trees and a stump around the spot
in front of you, each with a health no hit reaches and nothing to drop:

- a beech 1.6 m in front of you, the first swing's target, at 1 health so that
  swing fells it;
- a beech a little beyond the reach of a swing, and another beside it that is felled
  at once into a log and a stump. These three are the targets that qualify;
- a beech whose minimum tool tier is raised above the axe's;
- two trees of a second early species, one felled at once into a log and a stump;
- a beech stump to the side, moved once spawned so that the nearest point of its
  colliders a swing can hit lies midway between the area radius and the raised
  one (1.5 m from each) from where the first swing is expected to hit: the
  middle of its target's trunk at eye height.

It turns Automatic Mining's passes off when they run on an interval, as in
`woodcutting`, and Automatic Repair's `module` setting off while that module is
running, because it puts every worn item back to full durability once a second and
would undo the worn-down axe of the second swing, or its wear, between frames (the
section SKIPs and names the setting when it cannot be turned off).
It turns `enable_automatic_woodcutting` off for the section, so no pass
hits the test trees and the swings show area woodcutting working with the passes
off, sets `area_woodcutting_radius` to fit the trees,
`area_woodcutting_max_targets` to 2, below the three targets that qualify,
`area_woodcutting_same_kind_first` to false, so the axe swings before the
same-kind checks take the nearest targets whatever their kind, and
`allow_woodcutting_tree` to the beech's species alone. The same-kind checks at the end write these four settings again
for themselves. It adds an axe whose tool tier reaches both species and equips it,
keeps you facing the trees, lowers your view a little, refills your stamina before
each swing, and starts real attacks with `Humanoid.StartAttack`, only while the
swing's weapon is in your right hand. The game unequips an axe whose durability
runs out at its next physics step and equips nothing during an attack, so after
the second swing wears the axe out, the section puts its durability back, waits
for the attack to end and equips it again before the third swing and the hit of
the module's entry. An observer patch, removed at the end of the section, records
the melee call, every damage call on the test objects, the point of each swing's first direct hit on an allowed test object,
and every skill raise and stamina use of yours. Every distance below is measured
from that observed point. The observer's removal is registered with the session
before its patches go on, so a run cut short still removes them. While it is in place,
it also guards your world: a hit of yours on anything but the test objects is
dropped, and so is the terrain operation a hit on the ground would spawn. The log
and the stump a felling of a test tree spawns become test objects in the same frame,
as in `woodcutting`.

Last, the section removes every test object so far and checks that area
woodcutting takes the targets of the same kind as the one it hit first while
`area_woodcutting_same_kind_first` is on, and the nearest whatever their kind
while it is off. It looks
again for building pieces and destructible objects at the new spots, and for
allowed trees, logs and stumps of your world within the new radius plus 2 m of
the spot in front of you (the checks SKIP and name it). It sets
`allow_woodcutting_tree` to the beech's species and the birch's,
`area_woodcutting_radius` to fit the layout (at most 10 m) and
`area_woodcutting_max_targets` to 1 and `area_woodcutting_same_kind_first` to true, and spawns, non-persistent and at a health
no hit reaches: a beech 1.6 m in front of you, the swing's target; a birch
straight beyond it and a beech stump to the side, both just beyond the reach of a
swing; and a second beech straight ahead, 2.5 m farther out. The birch's and the
stump's tool tier is lowered to the axe's where it is above it, and the stump's
damage spawns nothing. A swing counts only when it hit the beech in front of you
and nothing else itself, and the distances from the hit to each target, measured
with area woodcutting's own measure (the nearest point it finds on the enabled,
solid colliders on the layers a swing hits), put the birch and the stump nearer than the second beech and all
three within the radius; otherwise the check SKIPs and gives the distances. A
check FAILs when area woodcutting centred its expansion away from that hit. A
third swing at the same layout, with `area_woodcutting_same_kind_first` written
to false and the cap at 1, is judged by the same rules.

| Check | Asserts |
|---|---|
| `woodcutting_area_setup` | The section set itself up; the note says what it changed: the module or area woodcutting turned on, the heading it turned you to, the move to clear ground and the items put aside |
| `woodcutting_area_expands` | The first swing hit a test object, and area woodcutting added at least one extra hit, each seen as a damage call on a test object |
| `woodcutting_area_cap_and_radius` | That swing added exactly `area_woodcutting_max_targets` extra hits while more targets qualified, each within `area_woodcutting_radius` of the swing's first direct hit on an allowed target as the observer saw it, none on an object the swing or an earlier extra hit already hit, and all on the targets that qualify (SKIP when the swing itself hit so many of them that the cap could not be reached, or no direct hit on an allowed test object was seen) |
| `woodcutting_area_damage_applied` | Every extra target lost health |
| `woodcutting_area_durability_per_target` | The axe lost the vanilla per-hit drain, scaled by the world durability rate, once for the swing and once per extra target. A FAIL also gives the axe's durability as the swing's melee call began |
| `woodcutting_area_skill_once` | No skill was raised more than once for the swing (SKIP when none was raised) |
| `woodcutting_area_stamina_once` | Stamina was used exactly once for the swing |
| `woodcutting_area_with_passes_off` | `enable_automatic_woodcutting` was off and the swing still expanded |
| `woodcutting_area_fresh_log_skipped` | The log the swing's own felling of its target spawned took no hit and kept its health |
| `woodcutting_area_tool_tier_skipped` | The tree above the axe's tool tier took no hit |
| `woodcutting_area_species_skipped` | The tree, log and stump of the species left out of `allow_woodcutting_tree` took no hit |
| `woodcutting_area_outside_radius_untouched` | A radius swing right after the first, with the first swing's radius and exclusions, `area_woodcutting_max_targets` raised to 32 and the axe at full durability, added fewer extra hits than the cap, so the cap could not have stopped it. Measured from its first direct hit on an allowed test object, usually the log or stump the first swing's felling left in front of you, it reached every target that qualifies within the radius as area woodcutting measures it (one within 0.01 m of the radius is not counted), apart from what it hit itself; no extra hit landed beyond the radius; and the stump that the physics engine's own sphere overlap confirms lies beyond the radius took no hit and kept its health. Only the stump's enabled, solid colliders on the layers a swing hits count, the ones area woodcutting measures its reach on. SKIP when one of them came within the radius, when the swing reached the cap, when no target that qualifies lay within the radius, or when the axe could not be equipped or the cap raised |
| `woodcutting_area_stops_when_axe_breaks` | A second swing, with the cap raised and the axe worn down to one and a half hits, added exactly one extra hit while at least two targets qualified, and left the axe with no durability. A FAIL also gives the axe's durability as the swing's melee call began, which shows whether the swing started from the worn-down value the check wrote. The axe's durability is put back afterwards |
| `woodcutting_area_excluded_hittable` | With the tool tier put back, the second species allowed, the radius raised and the cap lifted, a third swing hit each target the first swing left out: those the cap left out, the tree above the tool tier, the second species' tree, log and stump, and the stump beyond the first radius when the physics engine confirms that one of those same colliders lies within the raised radius. The fresh log took a hit of the second or third swing. SKIP when the axe the second swing wore out could not be equipped again |
| `woodcutting_area_trigger_point` | In each axe swing that hit an allowed test object itself, area woodcutting centred its expansion on the point of the first such hit the observer saw, and no swing expanded without one |
| `woodcutting_area_trees_logs_stumps` | Across the axe swings, extra hits landed on at least one standing tree, one log and one stump |
| `woodcutting_area_not_from_automatic_woodcutting` | A hit of the module's own entry on a test object in front of you adds no extra hit. When the entry hits none of them, it is tried once more after the next physics step (a refused entry spends nothing); SKIP when it still hits none, with the details of each object in the log file, as in `woodcutting`, and when the test axe is not in your right hand and cannot be equipped again |
| `woodcutting_area_not_from_other_weapons` | A real swing with an added club that hits a test object adds no extra hit |
| `woodcutting_area_skill_rule_log` | With its stump removed, the log the first swing's felling spawned stands alone in front of you. A real axe swing that hits only that log raises the same skills, by the same amounts, as a hit of the module's entry on it (SKIP, naming the reason, only when the swing hits something else as well, or the entry cannot reach the log from your eyes, also when tried once more after the next physics step; the log file then gives the details, as in `woodcutting`) |
| `woodcutting_area_skill_rule_tree` | The same with a test tree spawned alone at that spot |
| `woodcutting_area_skill_rule_stump` | The same with a stump spawned alone at that spot from the tree's stump prefab, once it has the component; you look further down for this swing |
| `woodcutting_area_same_kind_first` | With `area_woodcutting_same_kind_first` on and `area_woodcutting_max_targets` at 1, a swing at the beech in front of you adds exactly one extra hit, on the second beech, while the birch (a tree of another species) and the beech stump (another form), both nearer the hit, take no hit and keep their health |
| `woodcutting_area_same_kind_then_others` | With the option on and the cap at 3, a second swing adds exactly three extra hits: the second beech first, then the birch and the stump, nearest first (their order is not checked when their distances differ by 0.01 m or less) |
| `woodcutting_area_same_kind_off` | With `area_woodcutting_same_kind_first` off and the cap at 1, a swing at the same layout adds exactly one extra hit, on the nearest of the three targets by the measured distance, the birch or the stump (both count when their distances differ by 0.01 m or less), and none on the second beech, the farthest |
| `woodcutting_area_world_guarded` | The section ran to its end under the guard; the note counts the hits on other objects and the terrain operations it dropped |
| `woodcutting_area_settings_restored` | Every setting the section changed is back at your value |

An attack that does not start within 1 second, or triggers no melee hit within 3
seconds, SKIPs the swing's checks and names the reason; so does a swing whose
weapon is not in your right hand, which is not started, and a swing that hits
none of the test objects. At the end the added axe and club are removed from your
inventory, the items you had equipped are equipped again, the items put aside go
back, and your view pitch, skills, stamina and settings are put back; your view
stays turned to the heading unless you were moved. The swings and fellings still
count toward your statistics. `woodcutting_area_stops_when_axe_breaks` SKIPs in a
world whose durability rate is 0, a world setting the section leaves to you. Do not move or attack while the section runs. Area woodcutting in a
real forest, with real stumps and falling trees, remains an in-game check outside
this section.

#### `repair`

Run `automatics_smoke_features repair` in a single-player world, alive and not in
a teleport (the section SKIPs otherwise). The section calls the module's own
entries, `PieceRepair.Repair` for the periodic piece pass and
`ItemRepair.Repair` and `ItemRepair.CraftingStationInteractHook` for the two item
paths, against non-persistent objects it spawns 1.5 to 2.2 m from you. Every check
sets up and calls in one frame, so the module's own timers cannot act on the test
objects in between. The section:

- turns Automatic Repair on when it is off and waits for it to start, and sets
  `piece_search_range` and `crafting_station_search_range` to 4, through the
  session like every setting a section changes; it SKIPs only when one of these
  fails;
- when a crafting station stands within 5 m of you, moves you with a short
  teleport to the first clear, dry and fairly level spot on rings 15 to 45 m
  away before it spawns anything (see *Safety of the console commands*;
  `repair_relocated`), and moves you back at the end of the section;
- lifts your own no-cost toggle (`nocost`) for the section and turns it back on
  afterwards (`repair_no_cost_lifted`), also through the session hand-back when
  the run is cut short;
- puts items aside until two inventory slots are free for the test items, and
  puts the same items back at the end (SKIP when two slots cannot be freed);
  `repair_setup` names what was set up and put aside;
- adds an observer patch, removed at the end, that records only while a check has
  it armed: the repair calls on test pieces (and refuses them on any other piece),
  the ward flashes of the test ward (the flash of any other ward is dropped, so no
  ward of yours flashes), the ward access checks, the skill raises, the usability
  checks of crafting stations, the recipe lookups and the build station lookups.
  While an item check is armed, the worn-item collection returns only that check's
  test items, so no item of yours is repaired, charged or counted. For the piece
  pass it also hands
  the module a hammer item data in place of the one in your hand, so your equipment
  and build selection are not touched. Its removal is registered with the session
  before its patches go on;
- spawns, per check, damaged test pieces (their stored health set to half),
  undamaged ones (a stationless piece and a wood wall), a test ward made small and enabled, and wood walls (which need a
  workbench in range); the stationless piece is the first piece of the hammer's
  table that needs no build station and has nothing on it that does more than stand
  there. Each piece is made undamaged and removed when its check is done;
- spawns two test workbenches 1.5 m from you with their roof requirement off, and
  waits one frame so that the game lists them; the item checks then add one worn
  earlier-biome item that a workbench repairs and one that only another station
  repairs, at 37 percent of their durability, and remove them afterwards;
- turns `repair_items_when_accessing_the_crafting_station` on for the interaction
  checks, through the session, and puts it back;
- puts back your skills (the Crafting skill the checks raise) and your stamina, and
  runs the same hand-back when the run is cut short.

| Check | Asserts |
|---|---|
| `repair_no_cost_lifted` | Reported when your no-cost toggle was on and was lifted for the section |
| `repair_relocated` | Reported when a crafting station stood within 5 m: you were moved to a clear spot |
| `repair_setup` | The module, the two ranges and the free slots are set up; the note says whether the module was turned on and what was put aside |
| `repair_piece_damaged_only` | A pass repairs two damaged test pieces. An undamaged stationless piece and an undamaged wood wall cause no repair call, no ward access check and no station lookup (SKIP when the observer sees no access check at the damaged pieces) |
| `repair_piece_ward_no_flash` | A control check with flashing on is denied and seen by the observer; then a pass flashes the test ward no time, leaves the damaged piece inside the ward alone and repairs the damaged piece outside it (SKIP when the ward grants you access or the control flash is not seen) |
| `repair_piece_station_lookup_once` | Three damaged wood walls cost one build station lookup, and all are repaired when a build station is in range or the no-workbench rule is on, and none otherwise |
| `repair_piece_in_range_station_repairs` | With a test workbench in range, two damaged wood walls are repaired after one build station lookup |
| `repair_piece_stops_when_tool_used_up` | With a tool that has one repair of durability left, a pass over three damaged pieces repairs one and leaves the tool at zero or below; a pass with the used-up tool repairs nothing and changes nothing (SKIP for a tool that does not use durability) |
| `repair_item_none_worn_touches_no_station` | With nothing worn, a pass asks no station whether it is usable and looks up no recipe, whatever you carry |
| `repair_item_periodic_raises_crafting` | The periodic pass repairs the worn test item and raises the Crafting skill by the wear it had, as a hand repair does |
| `repair_item_station_checked_once` | In the same pass, each of the two test workbenches is asked once whether it is usable |
| `repair_item_recipe_resolved_once` | An item that only another station repairs costs one recipe lookup with two stations in range, and stays worn |
| `repair_item_unusable_station_excluded` | With both test workbenches made unusable (a fire requirement they do not meet), the periodic pass repairs nothing, raises nothing, and asks each once |
| `repair_item_interaction_raises_crafting` | The interaction repairs the worn test item at the station, raises the Crafting skill by the wear it had, and asks the station once |
| `repair_item_interaction_unusable_station_excluded` | At an unusable station the interaction repairs and raises nothing |
| `repair_settings_restored` | Every setting the section changed (the two ranges, and the module toggle and the interaction setting when it changed them) is back at your value |

The periodic item checks SKIP when a crafting station of yours is still within
4 m of you (`crafting_station_search_range` as the section sets it) because the
move to clear ground failed, and name why, because the pass would let it repair
the test items before the test workbenches do. The exclusion of
station-requiring pieces is only seen where no build station is in range of you:
when one is, `repair_piece_station_lookup_once` expects the walls to be repaired.
Repairing a piece sends the module's usual animation trigger, so your character may
swing once. A repair with the real hammer, the interaction through the real
crafting panel and multiplayer remain in-game checks outside this section.

## Remove

To uninstall the smoke plugin, remove the dedicated deploy directory:

```bash
rm -r "$VALHEIM_DIR/BepInEx/plugins/AutomaticsSmoke"
```

The smoke project is optional developer tooling. The data checks under
`scripts/` are unaffected by this plugin.
