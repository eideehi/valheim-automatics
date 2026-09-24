#!/usr/bin/env bash
#
# Source-shape invariants for the module lifecycle framework.
#
# These are regression tripwires, not proofs. Each one is tied to a defect shape that
# review actually found, and none of them interprets C#: "the deferred return is guarded"
# means the guard is written near the return, not that it dominates it. The properties
# that can genuinely be established offline are executed by tests/ModuleLifecycleHarness
# instead; the rest stay open until the game can be run.
#
# What a tripwire may never do is fail to run and call that a pass. Every search below
# distinguishes "no match" from "could not search", and a missing or unreadable file stops
# the script rather than quietly satisfying every reject_source_pattern in it.
#
# The Automatic Processing, Automatic Mapping and Automatic Storage checks read code with comments and literals
# blanked over a whitespace-collapsed stream, yet they still only see what is spelled out, so a
# state read, a change or a gated call made through a delegate, a method group, a stored ZDO or
# inventory reference, or a helper their patterns do not name still passes them.
#
# Two halves: the framework itself, and the modules that have been migrated onto it. The
# migrated list below is explicit and grows by one line per migration, because "check every
# module" would fail for the ones that still follow the start-up-only path on purpose (R5.1a).
#
set -euo pipefail

failures=0
registry_file="Automatics/ModuleRegistry.cs"
scope_file="Automatics/ModuleScope.cs"
lifecycle_file="Automatics/ModuleLifecycle.cs"
machine_file="Automatics/ModuleTransitionMachine.cs"
plugin_file="Automatics/Automatics.cs"
root_patches_file="Automatics/Patches.cs"

# Modules migrated to the runtime lifecycle. One line per migration; everything else keeps
# the start-up-only path and its early return, and checking it here would be wrong.
migrated_module_dirs=(
  "Automatics/AutomaticPickup"
  "Automatics/AutomaticProcessing"
  "Automatics/AutomaticMapping"
  "Automatics/AutomaticStorage"
  "Automatics/AutomaticDoor"
  "Automatics/AutomaticMining"
  "Automatics/AutomaticFeeding"
  "Automatics/AutomaticFarming"
  "Automatics/AutomaticRepair"
)

# Modules that have not been migrated. Their early return is part of passing, not a defect:
# removing it before the module is migrated hands non-activation guards to code this slice
# does not verify (R5.1a).
unmigrated_module_configs=(
)

pickup_config="Automatics/AutomaticPickup/Config.cs"
pickup_module="Automatics/AutomaticPickup/Module.cs"
pickup_patches="Automatics/AutomaticPickup/Patches.cs"
pickup_component="Automatics/AutomaticPickup/AutomaticPickup.cs"
pickup_globals="Automatics/AutomaticPickup/Globals.cs"

processing_dir="Automatics/AutomaticProcessing"
processing_config="$processing_dir/Config.cs"
processing_module="$processing_dir/Module.cs"
processing_patches="$processing_dir/Patches.cs"
processing_globals="$processing_dir/Globals.cs"

# Every Automatic Processing method with a side effect on the game, one per line:
# qualified name | file | declaration pattern | the exact statement it has to open with.
# Each moves items between a container and a station, so where it checks the stop request
# decides whether an item can be lost; see require_processing_entries_open_with_the_gate. The
# statement fixes the pass-through value too, because a wrong one is not harmless: a Store
# prefix whose gate returned true would suppress the game's own output and lose the product.
processing_entries=(
  "BeehiveProcess.Store|$processing_dir/BeehiveProcess.cs|public static bool Store\\(Beehive |if (!Module.Running) return false;"
  "SapCollectorProcess.Store|$processing_dir/SapCollectorProcess.cs|public static bool Store\\(SapCollector |if (!Module.Running) return false;"
  "WispSpawnerProcess.Store|$processing_dir/WispSpawnerProcess.cs|public static bool Store\\(WispSpawner |if (!Module.Running) return false;"
  "FireplaceProcess.Refuel|$processing_dir/FireplaceProcess.cs|public static void Refuel\\(Fireplace |if (!Module.Running) return;"
  "SmelterProcess.QuickCraft|$processing_dir/SmelterProcess.cs|public static string QuickCraft\\(|if (!Module.Running) return ore;"
  "SmelterProcess.Craft|$processing_dir/SmelterProcess.cs|public static void Craft\\(Smelter |if (!Module.Running) return;"
  "SmelterProcess.QuickRefuel|$processing_dir/SmelterProcess.cs|public static float QuickRefuel\\(|if (!Module.Running) return fuel;"
  "SmelterProcess.Refuel|$processing_dir/SmelterProcess.cs|public static void Refuel\\(Smelter |if (!Module.Running) return;"
  "SmelterProcess.Store|$processing_dir/SmelterProcess.cs|public static bool Store\\(Smelter |if (!Module.Running) return false;"
  "CookingStationProcess.Craft|$processing_dir/CookingStationProcess.cs|public static void Craft\\(CookingStation |if (!Module.Running) return;"
  "CookingStationProcess.Refuel|$processing_dir/CookingStationProcess.cs|public static float Refuel\\(CookingStation |if (!Module.Running) return fuel;"
  "CookingStationProcess.Store|$processing_dir/CookingStationProcess.cs|public static bool Store\\(CookingStation |if (!Module.Running) return false;"
  "FermenterProcess.Craft|$processing_dir/FermenterProcess.cs|public static void Craft\\(Fermenter |if (!Module.Running) return;"
  "FermenterProcess.Store|$processing_dir/FermenterProcess.cs|public static void Store\\(Fermenter |if (!Module.Running) return;"
  "TurretProcess.Charge|$processing_dir/TurretProcess.cs|public static void Charge\\(Turret |if (!Module.Running) return;"
)

mapping_dir="Automatics/AutomaticMapping"
mapping_config="$mapping_dir/Config.cs"
mapping_module="$mapping_dir/Module.cs"
mapping_patches="$mapping_dir/Patches.cs"
mapping_flora="$mapping_dir/FloraNetwork.cs"
mapping_icon_pack="$mapping_dir/IconPack.cs"
printobjects_file="Automatics/ConsoleCommands/PrintObjects.cs"

# Every Automatic Mapping method the game reaches with an effect on the map, or with a new object
# to take into the activation's ownership, one per line:
# qualified name | file | declaration pattern | the exact statement it has to open with.
# The statement fixes the pass-through value too, because it is what the game's own method goes
# on with: ResizeIcon has to hand back the size it was given, and the click handlers false, so
# that the game handles the click. See require_mapping_entries_open_with_the_gate.
mapping_entries=(
  "AutomaticMapping.Mapping|$mapping_dir/AutomaticMapping.cs|public static void Mapping\\(Player |if (!Module.Running) return;"
  "AutomaticMapping.DynamicMapping|$mapping_dir/AutomaticMapping.cs|public static void DynamicMapping\\(|if (!Module.Running) return;"
  "AutomaticMapping.AnimatePins|$mapping_dir/AutomaticMapping.cs|public static void AnimatePins\\(\\)|if (!Module.Running) return;"
  "AutomaticMapping.SetSaveFlag|$mapping_dir/AutomaticMapping.cs|public static bool SetSaveFlag\\(|if (!Module.Running) return false;"
  "Navigation.TryHandleMapClick|$mapping_dir/Navigation.cs|public static bool TryHandleMapClick\\(|if (!Module.Running) return false;"
  "Navigation.Update|$mapping_dir/Navigation.cs|public static void Update\\(Player |if (!Module.Running) {"
  "Map.ShouldHideAutomaticPin|$mapping_dir/Map.cs|public static bool ShouldHideAutomaticPin\\(|if (!Module.Running) return false;"
  "Map.DestroyHiddenAutomaticPinMarkers|$mapping_dir/Map.cs|public static void DestroyHiddenAutomaticPinMarkers\\(|if (!Module.Running) return;"
  "IconPack.ResizeIcon|$mapping_icon_pack|public static float ResizeIcon\\(|if (!Module.Running) return originalSize;"
  "IconPack.IsNameTagHidden|$mapping_icon_pack|public static bool IsNameTagHidden\\(|if (!Module.Running) return false;"
  "DynamicObjectMapping.FlushVehiclePins|$mapping_dir/DynamicObjectMapping.cs|public static void FlushVehiclePins\\(|if (!Module.Running) return;"
  "DynamicObjectMapping.OnObjectDestroy|$mapping_dir/DynamicObjectMapping.cs|public static void OnObjectDestroy\\(|if (!Module.Running) return;"
  "Config.OnHideUnexploredPinsChanged|$mapping_config|private static void OnHideUnexploredPinsChanged\\(|if (!Module.Running) return;"
  "Patches.Minimap_Explore_Postfix|$mapping_patches|private static void Minimap_Explore_Postfix\\(|if (!Module.Running) return;"
  "Patches.Minimap_AddSharedMapData_Postfix|$mapping_patches|private static void Minimap_AddSharedMapData_Postfix\\(|if (!Module.Running) return;"
  "Patches.Destructible_Awake_Postfix|$mapping_patches|private static void Destructible_Awake_Postfix\\(|if (!Module.Discovering) return;"
  "Patches.TeleportWorld_Awake_Postfix|$mapping_patches|private static void TeleportWorld_Awake_Postfix\\(|if (!Module.Discovering) return;"
  "Patches.Vagon_Awake_Postfix|$mapping_patches|private static void Vagon_Awake_Postfix\\(|if (!Module.Discovering) return;"
)

storage_dir="Automatics/AutomaticStorage"
storage_config="$storage_dir/Config.cs"
storage_module="$storage_dir/Module.cs"

# Every Automatic Storage method that moves items, in the form of processing_entries. There is one:
# Store moves every item a key press selected, from the player's inventory into containers. Its
# first statement is the only place it may look at the stop request; see
# require_item_entries_open_with_the_gate.
storage_entries=(
  "AutomaticStorage.Store|$storage_dir/AutomaticStorage.cs|public static Result Store\\(Player |if (!Module.Running) return new Result();"
)

door_dir="Automatics/AutomaticDoor"
door_config="$door_dir/Config.cs"
door_module="$door_dir/Module.cs"
door_patches="$door_dir/Patches.cs"
door_component="$door_dir/AutomaticDoor.cs"
door_smoke="smoke/Automatics.SmokeTest/LifecycleSmokeModules.cs"
door_world_smoke="smoke/Automatics.SmokeTest/WorldSmokeCommand.cs"
door_startup_smoke="smoke/Automatics.SmokeTest/SmokePlugin.cs"

# Every Door entry that can be reached after a stop request, in the same table shape as the
# Processing and Mapping entries. OnPlayerAwake is intentionally absent: it only clears session
# bookkeeping and is checked among the gate-free callbacks below.
door_entries=(
  "Module.OnPlayerUpdate|$door_module|private static void OnPlayerUpdate\\(|if (!Running) return;"
  "Module.OnPlayerFixedUpdate|$door_module|private static void OnPlayerFixedUpdate\\(|if (!Running) return;"
  "Module.OnDedicatedServerFixedUpdate|$door_module|private static void OnDedicatedServerFixedUpdate\\(|if (!Running) return;"
  "AutomaticDoor.TryOpenNearby|$door_component|public static void TryOpenNearby\\(|if (!Module.Running) return;"
  "AutomaticDoor.TryCloseDoors|$door_component|public static void TryCloseDoors\\(|if (!Module.Running) return;"
  "Patches.Door_Awake_Postfix|$door_patches|private static void Door_Awake_Postfix\\(|if (!Module.Discovering) return;"
  "Module.AttachAutomaticDoor|$door_module|internal static void AttachAutomaticDoor\\(|if (!Discovering) return;"
)

door_callbacks=(
  "Module.OnPlayerAwake|$door_module|private static void OnPlayerAwake\\(|ResetState();"
  "AutomaticDoor.MarkAllowAutomaticDoorDirty|$door_component|public static void MarkAllowAutomaticDoorDirty\\(|if (AllInstance.Count == 0) return;"
  "AutomaticDoor.Awake|$door_component|private void Awake\\(|_door = GetComponent<Door>();"
  "AutomaticDoor.OnScopeDetaching|$door_component|public void OnScopeDetaching\\(|Unregister();"
  "AutomaticDoor.OnDestroy|$door_component|private void OnDestroy\\(|Unregister();"
)

mining_dir="Automatics/AutomaticMining"
mining_config="$mining_dir/Config.cs"
mining_module="$mining_dir/Module.cs"
mining_patches="$mining_dir/Patches.cs"
mining_component="$mining_dir/AutomaticMining.cs"
mining_area="$mining_dir/AreaMining.cs"
mining_smoke="smoke/Automatics.SmokeTest/LifecycleSmokeModules.cs"

# The two player hooks and the swing prefix are the only boundaries at which mining may observe
# a stop. OnPlayerAwake only clears timers, AttachMineral is discovery rather than a world effect,
# and the area-mining damage recorders and expansion act only inside a swing the prefix opened.
mining_entries=(
  "Module.OnPlayerUpdate|$mining_module|private static void OnPlayerUpdate\\(|if (!Module.Running) return;"
  "Module.OnPlayerFixedUpdate|$mining_module|private static void OnPlayerFixedUpdate\\(|if (!Module.Running) return;"
  "Patches.Attack_DoMeleeAttack_Prefix|$mining_patches|private static void Attack_DoMeleeAttack_Prefix\\(|if (!Module.Running) return;"
)

# Every bind in Automatic Mining, in its persisted order. The patterns include the defaults so
# an edit cannot silently change what a new install gets while leaving the key in place.
mining_config_entries=(
  'module|config *\. *Bind *\( *"module" *, *AutomaticsModule *\. *Disabled'
  'enable_automatic_mining|config *\. *Bind *\( *"enable_automatic_mining" *, *true *\)'
  'mining_interval|config *\. *Bind *\( *"mining_interval" *, *1\.5f *, *\( *0\.1f *, *4f *\) *\)'
  'mining_range|config *\. *Bind *\( *"mining_range" *, *3 *, *\( *0 *, *32 *\) *\)'
  'allow_mining_mineral|config *\. *BindValheimObjectList *\( *"allow_mining_mineral" *, *ValheimObject *\. *Mineral *\)'
  'need_to_equip_pickaxe|config *\. *Bind *\( *"need_to_equip_pickaxe" *, *true *\)'
  'allow_mining_underground_minerals|config *\. *Bind *\( *"allow_mining_underground_minerals" *, *true *\)'
  'need_to_equip_wishbone_for_mining_underground_minerals|config *\. *Bind *\( *"need_to_equip_wishbone_for_mining_underground_minerals" *, *true *\)'
  'mining_key|config *\. *Bind *\( *"mining_key" *, *new KeyboardShortcut *\( *\) *\)'
  'enable_area_mining|config *\. *Bind *\( *"enable_area_mining" *, *true *\)'
  'area_mining_radius|config *\. *Bind *\( *"area_mining_radius" *, *3f *, *\( *1f *, *10f *\) *\)'
  'area_mining_max_targets|config *\. *Bind *\( *"area_mining_max_targets" *, *8 *, *\( *1 *, *32 *\) *\)'
)

feeding_dir="Automatics/AutomaticFeeding"
feeding_config="$feeding_dir/Config.cs"
feeding_module="$feeding_dir/Module.cs"
feeding_patches="$feeding_dir/Patches.cs"
feeding_component="$feeding_dir/AutomaticFeeding.cs"
feeding_globals="$feeding_dir/Globals.cs"
feeding_animal_type="$feeding_dir/AnimalType.cs"
feeding_smoke="smoke/Automatics.SmokeTest/LifecycleSmokeModules.cs"

# The two static entry points of Automatic Feeding, in the form of processing_entries. Feeding
# takes one item out of a feed box or the local player's inventory and feeds it within the same
# call; CancelAttackOnFeedBox only reads. Each looks at the stop request in its first statement
# and nowhere else. AttachFeeding is discovery rather than an effect, and is checked with the
# attach paths.
feeding_entries=(
  "AutomaticFeeding.Feeding|$feeding_component|public static bool Feeding\\(MonsterAI |if (!Module.Running) return false;"
  "AutomaticFeeding.CancelAttackOnFeedBox|$feeding_component|public static bool CancelAttackOnFeedBox\\(BaseAI |if (!Module.Running) return false;"
)

# The operations with which the instance Feeding commits an item: the feed box claim, the removal
# and the animal's consume callback. Each has to be found on its own, so one renamed out of this
# list stops the check instead of silently moving the first change later.
feeding_change_anchors=(
  'claim (TryPrepareContainerMutationServerAware)|(^|[^A-Za-z0-9_])TryPrepareContainerMutationServerAware *\('
  'removal (RemoveOneItem)|(^|[^A-Za-z0-9_])RemoveOneItem *\('
  'consume callback (m_onConsumedItem)|(^|[^A-Za-z0-9_])m_onConsumedItem([^A-Za-z0-9_]|$)'
)
feeding_changes='(^|[^A-Za-z0-9_])(TryPrepareContainerMutationServerAware|RemoveOneItem|ClaimOwnership) *\(|(^|[^A-Za-z0-9_])m_onConsumedItem([^A-Za-z0-9_]|$)'

# Every bind in Automatic Feeding, in its persisted order, defaults included.
feeding_config_entries=(
  'module|config *\. *Bind *\( *"module" *, *AutomaticsModule *\. *Disabled'
  'enable_automatic_feeding|config *\. *Bind *\( *"enable_automatic_feeding" *, *true *\)'
  'feed_search_range|config *\. *Bind *\( *"feed_search_range" *, *0 *, *\( *0 *, *64 *\) *\)'
  'need_get_close_to_eat_the_feed|config *\. *Bind *\( *"need_get_close_to_eat_the_feed" *, *false *\)'
  'allow_to_feed_from_container|config *\. *Bind *\( *"allow_to_feed_from_container" *, *AnimalType *\. *Tamed *\)'
  'allow_to_feed_from_player|config *\. *Bind *\( *"allow_to_feed_from_player" *, *AnimalType *\. *None *\)'
)

farming_dir="Automatics/AutomaticFarming"
farming_config="$farming_dir/Config.cs"
farming_module="$farming_dir/Module.cs"
farming_patches="$farming_dir/Patches.cs"
farming_core="$farming_dir/AutomaticFarming.cs"
farming_planting="$farming_dir/CropPlanting.cs"
farming_seed_pool="$farming_dir/SeedPool.cs"
farming_containers="$farming_dir/FarmingContainers.cs"
farming_roles="$farming_dir/ContainerRole.cs"
farming_smoke="smoke/Automatics.SmokeTest/LifecycleSmokeModules.cs"

# The Automatic Farming entry points outside Module.cs, in the form of processing_entries: the
# container hover postfix, which only appends a line to the game's text, and the crop claim
# Automatic Pickup asks before it collects a crop, whose false is what Pickup does without Farming.
# The other two gates are the player hook handlers in Module.cs, the only places a farming pass is
# accepted. They open with the bare Running and are checked by
# require_hook_handlers_open_with_the_gate, as the Storage handler is.
farming_entries=(
  "Patches.Container_GetHoverText_Postfix|$farming_patches|private static void Container_GetHoverText_Postfix\\(|if (!Module.Running) return;"
  "AutomaticFarming.ClaimsCrop|$farming_core|public static bool ClaimsCrop\\(Player player, Pickable pickable\\)|if (!Module.Running) return false;"
)

# Every bind in Automatic Farming, in its persisted order, defaults included.
farming_config_entries=(
  'module|config *\. *Bind *\( *"module" *, *AutomaticsModule *\. *Disabled'
  'enable_automatic_farming|config *\. *Bind *\( *"enable_automatic_farming" *, *true *\)'
  'farming_interval|config *\. *Bind *\( *"farming_interval" *, *1\.5f *, *\( *0\.1f *, *4f *\) *\)'
  'farming_range|config *\. *Bind *\( *"farming_range" *, *8 *, *\( *1 *, *64 *\) *\)'
  'allow_farming_crop|config *\. *BindValheimObjectList *\( *"allow_farming_crop" *, *ValheimObject *\. *Flora *\)'
  'seed_reserve|config *\. *Bind *\( *"seed_reserve" *, *10 *, *\( *0 *, *999 *\) *\)'
  'seed_source|config *\. *Bind *\( *"seed_source" *, *SeedSource *\. *Inventory *\)'
  'enable_proactive_sowing|config *\. *Bind *\( *"enable_proactive_sowing" *, *true *\)'
  'sowing_spacing_factor|config *\. *Bind *\( *"sowing_spacing_factor" *, *1f *, *\( *0\.5f *, *4f *\) *\)'
  'farming_key|config *\. *Bind *\( *"farming_key" *, *new KeyboardShortcut *\( *\) *\)'
  'designate_container_key|config *\. *Bind *\( *"designate_container_key" *, *new KeyboardShortcut *\( *\) *\)'
)

# The Automatic Processing patterns are POSIX EREs matched against a stream in which every run
# of whitespace, line breaks included, is a single space (see strip_code and find_all below).
#
# The operations that change a container, a station or the world. The first of them in an entry
# point is where an item is committed to the transfer. A reflective call counts as one because
# the name it invokes is a string the checks do not read. A change made through a helper this
# list does not name stays invisible until it is added here.
processing_changes='(^|[^A-Za-z0-9_])(TryTakeMaterial|TryClaimContainer|TryRemoveItem|AddItemAndGetCountDelta|InvokeRPC|Instantiate|ClaimOwnership) *\(|GetZDO *\( *\) *\. *Set *\(|Reflections *\. *InvokeMethod *(<[^>]*>)? *\(|\. *(Add|Remove)Item *\('

# What a patch method may not do itself: the changes above, and any use of the helpers that make
# them.
processing_patch_changes='(^|[^A-Za-z0-9_])(InvokeRPC|QueueOre|Instantiate|ClaimOwnership|TryTakeMaterial|TryClaimContainer|TryRemoveItem|AddItemAndGetCountDelta) *\(|(^|[^A-Za-z0-9_])(Logics|Reflections|Inventories|ContainerAccess) *\.|GetZDO *\( *\) *\. *Set *\(|\. *(Add|Remove)Item *\('

# The operations with which Automatic Storage moves an item: the transfer helpers Store calls and
# any inventory add or remove. The first of them in Store is where an item is committed.
storage_changes='(^|[^A-Za-z0-9_])(TransferExactStacks|TransferAny|TransferAmount|RollbackTransfer) *\(|\. *(Add|Remove)Item *\('

# Reads of the module state, in any migrated module: the gate predicate, the two flags behind it,
# and any member at all.
module_running_read='(^|[^A-Za-z0-9_])Module *\. *Running([^A-Za-z0-9_]|$)'
module_raw_state_read='(^|[^A-Za-z0-9_])Module *\. *(Active|StopRequested)([^A-Za-z0-9_]|$)'
module_read='(^|[^A-Za-z0-9_])Module *\.'

# The module brought in under another name, which would let its state be read without spelling
# Module. at all.
module_import='(^|[^A-Za-z0-9_])using +static +([A-Za-z0-9_.:]*[.:])?Module *;|(^|[^A-Za-z0-9_])using +[A-Za-z_][A-Za-z0-9_]* *= *([A-Za-z0-9_.:]*[.:])?Module *;'

# Identifiers that are followed by an argument list but are not calls.
keywords="if|for|foreach|while|switch|catch|using|lock|return|new|do|fixed|sizeof|typeof|nameof"

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  failures=$((failures + 1))
}

# rg exits 0 on a match, 1 on no match, and 2 or more on an error. Collapsing the last two
# is how a check silently stops checking, so each helper below spells the three out.
require_source_pattern() {
  local file="$1"
  local pattern="$2"
  local message="$3"
  local status=0

  rg -q "$pattern" "$file" || status=$?
  case "$status" in
    0) ;;
    1) fail "$message" ;;
    *) fail "could not search $file (rg exit $status); the check behind this was not run: $message" ;;
  esac
}

reject_source_pattern() {
  local file="$1"
  local pattern="$2"
  local message="$3"
  local status=0

  rg -q "$pattern" "$file" || status=$?
  case "$status" in
    0) fail "$message" ;;
    1) ;;
    *) fail "could not search $file (rg exit $status); the check behind this was not run: $message" ;;
  esac
}

# Prints the number of matching lines, or the word ERROR, which no caller accepts.
count_matches() {
  local file="$1"
  local pattern="$2"
  local status=0
  local count=""

  count="$(rg -c "$pattern" "$file")" || status=$?
  case "$status" in
    0) printf '%s\n' "$count" ;;
    1) printf '0\n' ;;
    *) printf 'ERROR\n' ;;
  esac
}

count_stdin_matches() {
  local pattern="$1"
  local status=0
  local count=""

  count="$(rg -c "$pattern")" || status=$?
  case "$status" in
    0) printf '%s\n' "$count" ;;
    1) printf '0\n' ;;
    *) printf 'ERROR\n' ;;
  esac
}

# Prints the 1-based line number of the first match in the text on stdin, nothing when
# there is no match, and ERROR when the search could not run. Written this way because
# `rg -n ... | head -1` inside a command substitution aborts the whole script under
# `set -euo pipefail` when there is no match: the check would then neither report nor let
# the checks after it run.
# True when any argument is the ERROR marker from first_match_line. ERROR is not a number,
# and bash arithmetic would read it as 0, so a search that could not run has to be caught
# before any comparison rather than after it.
lookup_failed() {
  local value

  for value in "$@"; do
    if [[ "$value" == "ERROR" ]]; then
      return 0
    fi
  done

  return 1
}

first_match_line() {
  local pattern="$1"
  local status=0
  local out=""

  out="$(rg -n "$pattern")" || status=$?
  case "$status" in
    0) printf '%s\n' "$out" | head -1 | cut -d: -f1 ;;
    1) ;;
    *) printf 'ERROR\n' ;;
  esac
}

# Prints the block that starts at the first line matching the pattern and ends at the
# line where the braces opened inside it balance again. Pass - to read standard input.
extract_block() {
  local file="$1"
  local start_pattern="$2"

  # The pattern travels through the environment: awk -v would eat its backslashes.
  EXTRACT_BLOCK_START="$start_pattern" awk '
    BEGIN { start = ENVIRON["EXTRACT_BLOCK_START"] }
    !started && $0 ~ start { started = 1 }
    !started { next }
    {
      line = $0
      opens = gsub(/{/, "{")
      closes = gsub(/}/, "}")
      depth += opens - closes
      if (!opened && opens > 0) opened = 1
      print line
      if (opened && depth <= 0) exit
    }
  ' "$file"
}

# Every identifier that is applied to an argument list, comments stripped. A search that
# could not run emits a token no allowlist matches, so the caller fails instead of seeing
# an empty and therefore innocent-looking result.
calls_in() {
  local status=0

  sed -e 's://.*::' | rg -o '[A-Za-z_][A-Za-z0-9_]*\s*\(' || status=$?
  if ((status > 1)); then
    printf '<<the search for calls could not be run>>\n'
  fi
}

# Source with its line comments removed. Prose that names a forbidden call must not trip a
# check, and prose that names a required one must not satisfy it.
without_comments() {
  sed -e 's://.*::' "$1"
}

# The number of lines matching a pattern once comments are gone, or ERROR.
count_stripped_matches() {
  local file="$1"
  local pattern="$2"
  local body

  body="$(without_comments "$file")"
  printf '%s\n' "$body" | count_stdin_matches "$pattern"
}

# An expression-bodied member: from its declaration to the line that ends the expression.
# extract_block only knows braces, and would run to the end of the file here.
extract_expression_body() {
  local file="$1"
  local start_pattern="$2"

  EXTRACT_EXPR_START="$start_pattern" awk '
    BEGIN { start = ENVIRON["EXTRACT_EXPR_START"] }
    !started && $0 ~ start { started = 1 }
    !started { next }
    { print; if ($0 ~ /;[ \t]*$/) exit }
  ' "$file"
}

# A reject_source_pattern against a file that is not there passes every time, so the files
# are checked before anything else and a missing one ends the run.
for source_file in \
  "$registry_file" "$scope_file" "$lifecycle_file" \
  "$machine_file" "$plugin_file" "$root_patches_file" \
  "$pickup_config" "$pickup_module" "$pickup_patches" \
  "$pickup_component" "$pickup_globals" \
  "$processing_config" "$processing_module" "$processing_patches" "$processing_globals" \
  "$mapping_config" "$mapping_module" "$mapping_patches" "$mapping_flora" "$mapping_icon_pack" \
  "$printobjects_file" \
  "$storage_config" "$storage_module" \
  "$door_config" "$door_module" "$door_patches" "$door_component" \
  "$door_smoke" "$door_world_smoke" "$door_startup_smoke" \
  "$mining_config" "$mining_module" "$mining_patches" "$mining_component" "$mining_area" "$mining_smoke" \
  "$feeding_config" "$feeding_module" "$feeding_patches" "$feeding_component" \
  "$feeding_globals" "$feeding_animal_type" "$feeding_smoke" \
  "${unmigrated_module_configs[@]}"; do
  if [[ ! -r "$source_file" ]]; then
    fail "cannot read $source_file; none of the checks below would mean anything"
  fi
done

for module_dir in "${migrated_module_dirs[@]}"; do
  if [[ ! -d "$module_dir" ]]; then
    fail "cannot read $module_dir; none of the checks below would mean anything"
  fi
done

for processing_entry in "${processing_entries[@]}"; do
  IFS='|' read -r _ processing_entry_file _ _ <<<"$processing_entry"
  if [[ ! -r "$processing_entry_file" ]]; then
    fail "cannot read $processing_entry_file; none of the checks below would mean anything"
  fi
done

for mapping_entry in "${mapping_entries[@]}"; do
  IFS='|' read -r _ mapping_entry_file _ _ <<<"$mapping_entry"
  if [[ ! -r "$mapping_entry_file" ]]; then
    fail "cannot read $mapping_entry_file; none of the checks below would mean anything"
  fi
done

for storage_entry in "${storage_entries[@]}"; do
  IFS='|' read -r _ storage_entry_file _ _ <<<"$storage_entry"
  if [[ ! -r "$storage_entry_file" ]]; then
    fail "cannot read $storage_entry_file; none of the checks below would mean anything"
  fi
done

for door_row in "${door_entries[@]}" "${door_callbacks[@]}"; do
  IFS='|' read -r _ door_row_file _ _ <<<"$door_row"
  if [[ ! -r "$door_row_file" ]]; then
    fail "cannot read $door_row_file; none of the Door checks below would mean anything"
  fi
done

for mining_entry in "${mining_entries[@]}"; do
  IFS='|' read -r _ mining_entry_file _ _ <<<"$mining_entry"
  if [[ ! -r "$mining_entry_file" ]]; then
    fail "cannot read $mining_entry_file; none of the Mining checks below would mean anything"
  fi
done

for feeding_entry in "${feeding_entries[@]}"; do
  IFS='|' read -r _ feeding_entry_file _ _ <<<"$feeding_entry"
  if [[ ! -r "$feeding_entry_file" ]]; then
    fail "cannot read $feeding_entry_file; none of the Feeding checks below would mean anything"
  fi
done

if ((failures > 0)); then
  exit 1
fi

# I8 (R1.3b): the registry reads the toggle and never writes it. The UI and the applied
# state are reconciled by reporting the state, not by rewriting the user's setting.
reject_source_pattern \
  "$registry_file" \
  "\\.Value\\s*=[^=]" \
  "the module registry must never assign a config value (R1.3b: the toggle is read only)"

# I8 (R1.4): a setting-changed handler is raised from the ConfigurationManager OnGUI pass
# and from a config file reload, so it may only record intent - and it is a game call
# stack, so it must also contain whatever it provokes.
require_setting_changed_handler_only_requests() {
  local body
  local offending

  body="$(extract_block "$registry_file" "SettingChanged \\+=" | tail -n +2)"
  if [[ -z "$body" ]]; then
    fail "the module registry must subscribe to the module toggle's SettingChanged event"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "RequestTransition\\("; then
    fail "the SettingChanged handler must hand the change to RequestTransition"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "catch"; then
    fail "the SettingChanged handler must contain its own failures; it runs on a game call stack"
  fi

  offending="$(
    printf '%s\n' "$body" | calls_in |
      rg -v "^($keywords|RequestTransition|Contain)\\s*\\(" || true
  )"
  if [[ -n "$offending" ]]; then
    fail "the SettingChanged handler must request the transition and contain it, nothing else (R1.4), found: ${offending//$'\n'/ }"
  fi
}

# The pump is called from UnityPlugin.Update. The transition callbacks are contained by the
# machine, but nothing else in the loop is, and an escape here reaches the update loop.
require_pump_contains_its_failures() {
  local body

  body="$(extract_block "$registry_file" "public static void Pump\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the module registry must define Pump()"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "catch"; then
    fail "Pump must contain its own failures; it runs from the update loop"
  fi
}

# I15 (R1.5a / R1.4): raising the stop request is the one structural thing that happens at
# request time, because the observable stop boundary has to be the frame the user asked
# for. Contain is allowed alongside it: containment is not structural work.
require_request_transition_shape() {
  local body
  local offending

  body="$(extract_block "$registry_file" "private static void RequestTransition" | tail -n +2)"
  if [[ -z "$body" ]]; then
    fail "the module registry must define RequestTransition"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "SetStopRequested\\(!enable\\)"; then
    fail "RequestTransition must raise and lower the stop request as SetStopRequested(!enable) (R1.5a)"
  fi

  if ! printf '%s\n' "$body" | rg -q "\\.Request\\(enable\\)"; then
    fail "RequestTransition must leave the structural transition to the pump via Request(enable)"
  fi

  offending="$(
    printf '%s\n' "$body" | calls_in |
      rg -v "^($keywords|SetStopRequested|Request|Contain)\\s*\\(" || true
  )"
  if [[ -n "$offending" ]]; then
    fail "RequestTransition must do nothing but the stop request, the transition request and containment (R1.4), found: ${offending//$'\n'/ }"
  fi
}

# I9 (R1.6 / R3.1): removing patches is the scope's business alone, and the always-on root
# patches have no removal path at all.
require_unpatch_calls_only_in_the_scope() {
  local files
  local status=0

  # The call syntax, not the name: the capability probe resolves the same API by
  # reflection, so the string "UnpatchSelf" legitimately appears in $lifecycle_file and
  # that file is excluded here rather than the check being weakened for everyone.
  files="$(
    rg -l --glob "!Automatics/Libraries/**" --glob "!$lifecycle_file" \
      "\\.UnpatchSelf\\(" Automatics
  )" || status=$?

  if ((status > 1)); then
    fail "could not search for UnpatchSelf call sites (rg exit $status); the check was not run"
    return
  fi

  if [[ "$files" != "$scope_file" ]]; then
    fail "UnpatchSelf() must be called from $scope_file and nowhere else (R3.1), found: ${files//$'\n'/ }"
  fi
}

reject_source_pattern \
  "$root_patches_file" \
  "Unpatch[A-Za-z]*\\s*\\(" \
  "the always-on root patches must have no removal path (R3.1)"
reject_source_pattern \
  "$plugin_file" \
  "Unpatch[A-Za-z]*\\s*\\(" \
  "the plugin entry point must not remove the always-on root patches (R3.1)"

# I9 (AC-S10): the transition layer stays offline-executable. Nothing that needs the engine,
# the patch library or the config framework may leak into it.
reject_source_pattern \
  "$machine_file" \
  "UnityEngine|HarmonyLib|BepInEx|ConfigEntry" \
  "the transition layer must not depend on the engine, Harmony, BepInEx or config entries (AC-S10)"

# I12 (MAJOR-1): Deferred asserts that this attempt left nothing behind. A rollback that
# failed must be reported as a failure, or the machine calls a module disabled while its
# patches and subscriptions are still live.
require_source_pattern \
  "$registry_file" \
  "return TransitionOutcome\\.Deferred" \
  "the module registry must be able to defer an activation (R4.5)"

require_deferred_returns_are_guarded() {
  local unguarded

  unguarded="$(
    awk '
      { lines[NR] = $0 }
      /return TransitionOutcome\.Deferred/ {
        start = NR - 4
        if (start < 1) start = 1
        guarded = 0
        for (i = start; i <= NR; i++)
          if (lines[i] ~ /TeardownIsClean/) guarded = 1
        if (!guarded) print NR
      }
    ' "$registry_file"
  )"

  if [[ -n "$unguarded" ]]; then
    fail "a deferred activation must be returned only after TeardownIsClean confirmed the rollback (MAJOR-1), unguarded at line(s): ${unguarded//$'\n'/ }"
  fi
}

# I13 (MAJOR-2): a scope whose teardown did not complete is the only handle on what is
# still installed. Both the failed-activation paths and the failed-teardown path keep it.
require_failed_scopes_are_kept() {
  local kept

  kept="$(count_matches "$registry_file" "Residual\\s*=\\s*scope")"
  if [[ "$kept" == "ERROR" ]]; then
    fail "could not count the scopes kept for recovery; the check was not run"
    return
  fi

  if ((kept < 3)); then
    fail "every failure path must keep its scope for recovery (MAJOR-2): expected at least 3 assignments of Residual, found $kept"
  fi
}

# I13 (MAJOR-2): and it is only let go where the teardown really finished.
require_residual_is_cleared_only_after_a_clean_teardown() {
  local total
  local in_recover
  local in_disable

  total="$(count_matches "$registry_file" "Residual\\s*=\\s*null")"
  in_recover="$(
    extract_block "$registry_file" "private static TransitionOutcome RecoverTransition" |
      count_stdin_matches "Residual\\s*=\\s*null"
  )"
  in_disable="$(
    extract_block "$registry_file" "private static TransitionOutcome DisableTransition" |
      count_stdin_matches "Residual\\s*=\\s*null"
  )"

  if [[ "$total" == "ERROR" || "$in_recover" == "ERROR" || "$in_disable" == "ERROR" ]]; then
    fail "could not count where the residual scope is released; the check was not run"
    return
  fi

  if ((in_recover < 1)); then
    fail "recovery must release the residual scope once its teardown completed (MAJOR-2)"
  fi

  if ((in_disable < 1)); then
    fail "a clean teardown must leave no residual scope behind (MAJOR-2)"
  fi

  if ((total != in_recover + in_disable)); then
    fail "the residual scope must be released only by recovery and by a clean teardown (MAJOR-2): $total assignments of null, $in_recover in recovery and $in_disable in the teardown"
  fi
}

# Recovery is a new attempt at the same teardown, so it has to repeat the work whose report
# it drops. Clearing without redoing turns an unfinished cleanup into a clean one.
require_recovery_retries_before_it_clears() {
  local body
  local disable_line
  local clear_line

  body="$(extract_block "$registry_file" "private static TransitionOutcome RecoverTransition")"
  if [[ -z "$body" ]]; then
    fail "the module registry must define RecoverTransition"
    return
  fi

  disable_line="$(printf '%s\n' "$body" | first_match_line "Module\\.Disable\\(")"
  clear_line="$(printf '%s\n' "$body" | first_match_line "ClearRecordedFailures\\(")"

  if lookup_failed "$disable_line" "$clear_line"; then
    fail "could not search for recovery ordering; the check was not run"
    return
  fi

  if [[ -z "$disable_line" || -z "$clear_line" ]]; then
    fail "recovery must redo the module's own teardown and only then drop the previous report"
    return
  fi

  if ((disable_line >= clear_line)); then
    fail "recovery must repeat the module teardown before it clears the report of the attempt that failed"
  fi
}

# I14 (MAJOR-7): the subscription token removes first and only marks itself spent once the
# removal returned. Clearing first makes a failed removal look done and loses the only
# handle on a subscription that is still live.
require_token_removes_before_it_clears() {
  local body
  local invoke_line
  local clear_line

  body="$(
    extract_block "$machine_file" "class SubscriptionToken<T>" |
      extract_block - "public void Dispose\\(\\)"
  )"
  if [[ -z "$body" ]]; then
    fail "SubscriptionToken<T> in $machine_file must define Dispose()"
    return
  fi

  invoke_line="$(printf '%s\n' "$body" | first_match_line "remove\\(")"
  clear_line="$(printf '%s\n' "$body" | first_match_line "_remove = null")"

  if lookup_failed "$invoke_line" "$clear_line"; then
    fail "could not search for the subscription token teardown order; the check was not run"
    return
  fi

  if [[ -z "$invoke_line" || -z "$clear_line" ]]; then
    fail "SubscriptionToken<T>.Dispose must both run the removal and mark the token spent"
    return
  fi

  if ((invoke_line >= clear_line)); then
    fail "SubscriptionToken<T>.Dispose must run the removal before it marks the token spent (MAJOR-7)"
  fi
}

# The scope acquires before it registers in only one place left - the component stamp - and
# everywhere else registers the undo first, so an acquisition that throws part way through
# still has a removal behind it.
require_scope_registers_before_it_applies() {
  local body
  local register_line
  local apply_line

  body="$(extract_block "$scope_file" "public void PatchAll\\(Type patchType\\)")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define PatchAll(Type)"
    return
  fi

  register_line="$(printf '%s\n' "$body" | first_match_line "RegisterOrRelease\\(")"
  apply_line="$(printf '%s\n' "$body" | first_match_line "harmony\\.PatchAll\\(")"

  if lookup_failed "$register_line" "$apply_line"; then
    fail "could not search for the acquisition order; the check was not run"
    return
  fi

  if [[ -z "$register_line" || -z "$apply_line" ]]; then
    fail "PatchAll must both apply the patches and register their removal"
    return
  fi

  if ((register_line >= apply_line)); then
    fail "PatchAll must register the removal before it applies anything: a partial application must not be left without one"
  fi

  body="$(extract_block "$scope_file" "public IDisposable Subscribe<T>")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define Subscribe<T>"
    return
  fi

  register_line="$(printf '%s\n' "$body" | first_match_line "RegisterOrRelease\\(")"
  apply_line="$(printf '%s\n' "$body" | first_match_line "^\\s*add\\(handler\\)")"

  if lookup_failed "$register_line" "$apply_line"; then
    fail "could not search for the acquisition order; the check was not run"
    return
  fi

  if [[ -z "$register_line" || -z "$apply_line" ]]; then
    fail "Subscribe must both add the handler and register its removal"
    return
  fi

  if ((register_line >= apply_line)); then
    fail "Subscribe must register the removal before it adds the handler: an add that subscribes and then throws must not be left without one"
  fi
}

# The pump's containment has to start at the list read, not after it, or the boundary the
# method advertises is not the boundary it has.
require_pump_containment_starts_at_the_top() {
  local body
  local try_line
  local read_line

  body="$(extract_block "$registry_file" "public static void Pump\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the module registry must define Pump()"
    return
  fi

  try_line="$(printf '%s\n' "$body" | first_match_line "^\\s*try$")"
  read_line="$(printf '%s\n' "$body" | first_match_line "Entries\\[i\\]")"

  if lookup_failed "$try_line" "$read_line"; then
    fail "could not search for Pump's containment boundary; the check was not run"
    return
  fi

  if [[ -z "$try_line" || -z "$read_line" ]]; then
    fail "Pump must read each entry inside its containment"
    return
  fi

  if ((try_line >= read_line)); then
    fail "Pump's containment must start before it reads an entry, not after"
  fi
}

# One scope owns one Harmony id, and removing an id's patches removes all of them. A second
# registration would take the first call's patches off too, and at the wrong point in the
# reverse walk - before the subscriptions and components acquired after them.
require_patch_removal_registered_once() {
  local body
  local registrations
  local guard_line
  local register_line

  body="$(extract_block "$scope_file" "public void PatchAll\\(Type patchType\\)")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define PatchAll(Type)"
    return
  fi

  registrations="$(printf '%s\n' "$body" | count_stdin_matches "RegisterOrRelease\\(")"
  if [[ "$registrations" == "ERROR" ]]; then
    fail "could not count the patch removal registrations; the check was not run"
    return
  fi

  if ((registrations != 1)); then
    fail "PatchAll must register exactly one removal for the whole scope (found $registrations): one Harmony id means one removal, at the position of the first call"
  fi

  guard_line="$(printf '%s\n' "$body" | first_match_line "_harmony == null")"
  register_line="$(printf '%s\n' "$body" | first_match_line "RegisterOrRelease\\(")"

  if lookup_failed "$guard_line" "$register_line"; then
    fail "could not search for the patch removal registration; the check was not run"
    return
  fi

  if [[ -z "$guard_line" || -z "$register_line" ]]; then
    fail "PatchAll must register the scope's single patch removal behind a first-call guard"
    return
  fi

  if ((guard_line >= register_line)); then
    fail "PatchAll must guard the removal registration so that only the first call makes it"
  fi
}

# The refusal path has to give the same isolation the two ledger entries give: a hook that
# throws must not stop the destruction.
require_refusal_release_isolates_the_halves() {
  local body
  local detach_line
  local destroy_line
  local catch_line

  body="$(extract_block "$scope_file" "private void DetachThenDestroy")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define the refusal-time release for a component"
    return
  fi

  detach_line="$(printf '%s\n' "$body" | first_match_line "DetachTracked\\(")"
  destroy_line="$(printf '%s\n' "$body" | first_match_line "DestroyTracked\\(")"
  catch_line="$(printf '%s\n' "$body" | first_match_line "catch")"

  if lookup_failed "$detach_line" "$destroy_line" "$catch_line"; then
    fail "could not search for the refusal-time release; the check was not run"
    return
  fi

  if [[ -z "$detach_line" || -z "$destroy_line" ]]; then
    fail "the refusal-time release must run both the detach hook and the destruction"
    return
  fi

  if ((detach_line >= destroy_line)); then
    fail "the refusal-time release must run the detach hook before the destruction"
  fi

  if [[ -z "$catch_line" ]] || ((catch_line >= destroy_line)); then
    fail "the refusal-time release must isolate a throwing detach hook from the destruction"
  fi
}

# An undo is arbitrary code and may add an obligation, so a list position taken before it
# means nothing afterwards. This is the same hazard that was removed from the component
# bookkeeping; the orphan walk must not reintroduce it.
require_orphan_walk_is_identity_based() {
  local body

  body="$(extract_block "$scope_file" "private void DisposeOrphans")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define the refused-obligation walk"
    return
  fi

  if printf '%s\n' "$body" | rg -q "RemoveAt\\(i\\)"; then
    fail "the refused-obligation walk must not hold a list position across an undo: resolve the completed entry by identity"
  fi

  if ! printf '%s\n' "$body" | rg -q "RemoveOrphan\\("; then
    fail "the refused-obligation walk must remove a completed obligation by identity"
  fi
}

# ---------------------------------------------------------------------------------------
# Pilot module: Automatic Pickup. Everything below checks the shape of a migrated module.
# ---------------------------------------------------------------------------------------

# I10 (BLOCKER): the highest-value check in this slice. A coroutine resumes at the yield it
# suspended on, so a guard at the head of its loop is never reached by an iterator that was
# suspended half way through a pass: it resumes straight into the next pickup. Every yield
# therefore has to be followed by its own guard, before the next side effect.
#
# The split is deliberate and this check enforces both halves. `if (!Alive) yield break;`
# handles the irreversible conditions - the module is off, the component is detached, the
# generation moved on - and folds the iterator, because a re-activation builds a new
# component and this one must not linger. `if (!Live) continue;` handles the reversible ones
# - a stop request that can be lowered again, ownership that can come back - and goes back
# to idling, because folding on those would kill pickup on a component that survives them.
#
# What this does not check is that Alive and Live look at the right things: `Alive => true`
# passes. That half is AC-R-P2, and it needs the game.
require_every_yield_is_guarded() {
  local body
  local unguarded
  local yields
  local breaks
  local continues

  body="$(extract_block "$pickup_component" "private IEnumerator Pickup\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the pickup component must define the Pickup() coroutine"
    return
  fi

  unguarded="$(
    printf '%s\n' "$body" | awk '
      { lines[NR] = $0 }
      END {
        for (i = 1; i <= NR; i++) {
          if (lines[i] !~ /yield return/) continue

          guard = "<the coroutine body ends here>"
          for (j = i + 1; j <= NR; j++) {
            candidate = lines[j]
            gsub(/^[ \t]+|[ \t]+$/, "", candidate)
            if (candidate == "") continue
            if (candidate ~ /^\/\//) continue
            guard = candidate
            break
          }

          if (guard != "if (!Alive) yield break;") print i ": " guard
        }
      }
    '
  )"

  if [[ -n "$unguarded" ]]; then
    fail "every yield in Pickup() must be followed by \"if (!Alive) yield break;\" before the next side effect (BLOCKER): a coroutine resumes where it suspended, not at the head of its loop; offending yield(s): ${unguarded//$'\n'/ | }"
  fi

  yields="$(printf '%s\n' "$body" | count_stdin_matches "yield return")"
  breaks="$(printf '%s\n' "$body" | count_stdin_matches "if \\(!Alive\\) yield break;")"
  continues="$(printf '%s\n' "$body" | count_stdin_matches "if \\(!Live\\) continue;")"

  if lookup_failed "$yields" "$breaks" "$continues"; then
    fail "could not count the guards in Pickup(); the check was not run"
    return
  fi

  # 3: the idle wait and the two between the periodic pass's kinds. The key pass resolves its
  # kind and name when the key goes down, so it no longer suspends between kinds.
  if ((yields != 3)); then
    fail "Pickup() must suspend at exactly the 3 known points (BLOCKER): found $yields; a new one needs its own guard and this count updated deliberately"
  fi

  if ((breaks != yields)); then
    fail "Pickup() must fold the iterator after every yield (BLOCKER): $yields yields but $breaks occurrences of \"if (!Alive) yield break;\""
  fi

  if ((continues != 2)); then
    fail "Pickup() must idle rather than fold on the reversible conditions after each of the 2 mid-pass yields (R1.5a / MAJOR-6): found $continues occurrences of \"if (!Live) continue;\""
  fi

  # The idle wait is the fifth yield and the one the loop head owns. Its guard is Alive only,
  # so the loop condition is what keeps a stale _active - raised up to a tenth of a second
  # before this loop reads it - from driving a pass that starts after a stop request.
  if ! printf '%s\n' "$body" | rg -q "while \\(!_active \\|\\| !Live\\)"; then
    fail "the idle loop of Pickup() must wait on Live as well as _active (R1.5a): _active is raised frames earlier, and entering the body on a stale one produces a pickup after the stop was requested"
  fi
}

# I10 sibling (P1): Alive and Live answer "is the module still on", not "is the object I
# captured before the suspension still there". The hover target is read once and then
# dereferenced after two frame boundaries; if it is picked or destroyed in between, the
# dereference raises MissingReferenceException outside the per-object handlers, the iterator
# dies, and pickup stops silently for that player until the module is toggled. So the rule is
# positional: every dereference of the captured target must have a revalidation after the
# most recent suspension.
require_the_hover_target_is_revalidated() {
  local body
  local stale

  body="$(extract_block "$pickup_component" "private IEnumerator Pickup\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the pickup component must define the Pickup() coroutine"
    return
  fi

  stale="$(
    printf '%s\n' "$body" | awk '
      { line = $0; gsub(/^[ \t]+|[ \t]+$/, "", line) }
      line ~ /^var hovering =/ { validated = 0; next }
      line ~ /yield return/ { validated = 0; next }
      line ~ /^if \(!hovering\) continue;$/ { validated = 1; next }
      line ~ /hovering\./ {
        if (!validated) print NR ": " line
      }
    '
  )"

  if [[ -n "$stale" ]]; then
    fail "the hovered object must be revalidated after every suspension before it is dereferenced (P1): a target destroyed while the iterator was parked raises MissingReferenceException outside the per-object handlers and kills the coroutine; unguarded use at: ${stale//$'\n'/ | }"
  fi
}

# BLOCKER(c): the reason the guards above exist, written where the next person to tidy them
# away will read it.
require_the_resume_point_is_documented() {
  local preamble

  preamble="$(
    awk '
      /private IEnumerator Pickup\(\)/ { exit }
      { print }
    ' "$pickup_component" | tail -20
  )"

  if ! printf '%s\n' "$preamble" | rg -q "resumes at the yield"; then
    fail "Pickup() must be preceded by the note that a coroutine resumes at the yield it suspended on, not at the head of its loop (BLOCKER): without it the per-yield guards read as redundant"
  fi
}

# BLOCKER(a): destruction is deferred to the end of the frame, and OnDestroy with it, so the
# coroutine has to be stopped synchronously from the teardown instead. Neither shutdown may
# be conditioned on ownership: the coroutine now runs on non-owners too, and the previous
# ownership guard on OnDestroy was already leaving coroutines running on Players whose
# ownership had moved.
require_synchronous_coroutine_shutdown() {
  local hook
  local destroy

  hook="$(extract_block "$pickup_component" "public void OnScopeDetaching\\(\\)")"
  destroy="$(extract_block "$pickup_component" "private void OnDestroy\\(\\)")"

  if [[ -z "$hook" ]]; then
    fail "the pickup component must implement OnScopeDetaching() so the scope can stop its coroutine before it destroys it (BLOCKER)"
  else
    if ! printf '%s\n' "$hook" | rg -q "StopCoroutine\\("; then
      fail "OnScopeDetaching() must stop the pickup coroutine synchronously (BLOCKER): engine destruction is deferred to the end of the frame"
    fi
    if ! printf '%s\n' "$hook" | rg -q "_cancelled = true"; then
      fail "OnScopeDetaching() must mark the component cancelled so an iterator already resumed this frame folds at its next guard (BLOCKER)"
    fi
    if printf '%s\n' "$hook" | rg -q "IsOwner\\("; then
      fail "OnScopeDetaching() must not condition the shutdown on ownership (BLOCKER): the coroutine runs on non-owners, so stopping it cannot depend on ownership"
    fi
  fi

  if [[ -z "$destroy" ]]; then
    fail "the pickup component must define OnDestroy()"
    return
  fi

  if ! printf '%s\n' "$destroy" | rg -q "StopCoroutine\\("; then
    fail "OnDestroy() must stop the pickup coroutine for the destructions the scope does not drive"
  fi

  if printf '%s\n' "$destroy" | rg -q "IsOwner\\("; then
    fail "OnDestroy() must not condition the shutdown on ownership (BLOCKER): a Player whose ownership moved would keep its coroutine running"
  fi
}

# FIX 3: Unity stops a component's coroutines when its GameObject is deactivated and does not
# call Awake again on the way back, so an iterator started in Awake never returns: Update goes
# on raising _active with nothing to consume it. The activation scan includes inactive objects
# by design, so this is a path the pilot really has.
require_the_coroutine_starts_on_enable() {
  local awake
  local enable
  local disable

  awake="$(extract_block "$pickup_component" "private void Awake\\(\\)")"
  enable="$(extract_block "$pickup_component" "private void OnEnable\\(\\)")"
  disable="$(extract_block "$pickup_component" "private void OnDisable\\(\\)")"

  if [[ -z "$awake" ]]; then
    fail "the pickup component must define Awake()"
    return
  fi

  if printf '%s\n' "$awake" | rg -q "StartCoroutine\\("; then
    fail "Awake() must not start the pickup coroutine: a component whose GameObject is deactivated loses its coroutine and never gets another Awake, so it would come back with Update raising a flag nothing consumes"
  fi

  if [[ -z "$enable" ]]; then
    fail "the pickup component must define OnEnable() so a reactivated component starts a coroutine again"
    return
  fi

  if ! printf '%s\n' "$enable" | rg -q "StartCoroutine\\("; then
    fail "OnEnable() must start the pickup coroutine"
  fi

  if ! printf '%s\n' "$enable" | rg -q "if \\(_running\\) return;"; then
    fail "OnEnable() must refuse to start a second iterator beside a running one: disabling the component rather than the GameObject raises OnEnable again without the engine having stopped anything"
  fi

  if ! printf '%s\n' "$enable" | rg -q "if \\(_cancelled\\) return;"; then
    fail "OnEnable() must not restart a component whose activation is over"
  fi

  if [[ -z "$disable" ]]; then
    fail "the pickup component must define OnDisable()"
    return
  fi

  if ! printf '%s\n' "$disable" | rg -q "StopCoroutine\\("; then
    fail "OnDisable() must stop the coroutine itself: deactivating the GameObject stops it but disabling the component alone does not, and OnEnable can only restart exactly one iterator if both leave the same state"
  fi
}

# MAJOR-6: ownership decided once in Awake froze whatever was true at attach time, which
# runtime activation makes worse - the reconciliation pass attaches to every Player at one
# instant. It moved to the per-frame predicate instead.
require_ownership_is_a_per_frame_question() {
  local awake
  local enable

  awake="$(extract_block "$pickup_component" "private void Awake\\(\\)")"
  enable="$(extract_block "$pickup_component" "private void OnEnable\\(\\)")"
  if [[ -z "$awake" || -z "$enable" ]]; then
    fail "the pickup component must define Awake() and OnEnable()"
    return
  fi

  if printf '%s\n' "$awake$enable" | rg -q "IsOwner\\("; then
    fail "the coroutine must be started whatever the ownership is (MAJOR-6): deciding once at attach time leaves a Player that gains ownership later with no coroutine at all"
  fi

  require_source_pattern \
    "$pickup_component" \
    "private bool Live =>" \
    "the pickup component must have a per-frame Live predicate (MAJOR-6 / R1.5a)"
  require_source_pattern \
    "$pickup_component" \
    "_player\\.IsOwner\\(\\)" \
    "Live must be the place ownership is checked (MAJOR-6)"
  require_source_pattern \
    "$pickup_component" \
    "Module\\.StopRequested" \
    "Live must carry the stop request so the observable stop boundary is the request (R1.5a)"

  local live

  live="$(extract_expression_body "$pickup_component" "private bool Live =>")"
  if [[ -z "$live" ]]; then
    fail "the pickup component must have a Live predicate"
    return
  fi

  if ! printf '%s\n' "$live" | rg -q "IsPaused\\(\\)"; then
    fail "Live must refuse a paused game: Update and FixedUpdate already decline to raise the flag while paused, but a pass suspended at a yield resumes and picks unless the check is in the per-frame predicate too"
  fi

  # The reversible half belongs to Live alone. In Alive it would fold the iterator of a
  # component that survives a disable-then-re-enable inside one frame, killing pickup on it
  # permanently.
  local alive
  local misplaced

  alive="$(extract_expression_body "$pickup_component" "private bool Alive =>")"
  if [[ -z "$alive" ]]; then
    fail "the pickup component must have an Alive predicate for the irreversible conditions"
    return
  fi

  misplaced="$(printf '%s\n' "$alive" | count_stdin_matches "StopRequested")"
  if lookup_failed "$misplaced"; then
    fail "could not search the Alive predicate; the check was not run"
    return
  fi

  if ((misplaced != 0)); then
    fail "the stop request belongs in Live, not Alive (R1.5a): it is reversible, and folding the iterator on it kills pickup on a component that survives the request"
  fi
}

# Both per-frame entry points raise _active, so both have to answer the same question the
# coroutine does before they do.
require_the_frame_hooks_check_live() {
  local hook
  local body

  for hook in "private void Update\\(\\)" "private void FixedUpdate\\(\\)"; do
    body="$(extract_block "$pickup_component" "$hook")"
    if [[ -z "$body" ]]; then
      fail "the pickup component must define ${hook//\\/}"
      continue
    fi

    if ! printf '%s\n' "$body" | tail -n +2 | head -3 | rg -q "if \\(!Live\\) return;"; then
      fail "${hook//\\/} must open with \"if (!Live) return;\": it raises the flag the coroutine acts on, so a check anywhere later lets a stopped module queue one more pass"
    fi
  done
}

# The static buffers outlive every component that filled them, so a disable that leaves them
# populated keeps destroyed objects reachable. EmptyItemDrops is exempt by contract.
require_shared_buffers_are_cleared_on_disable() {
  local body

  require_source_pattern \
    "$pickup_component" \
    "internal static void ClearSharedBuffers\\(\\)" \
    "the pickup component must expose ClearSharedBuffers() for its module to call on disable"

  body="$(extract_block "$pickup_module" "public void Disable\\(ModuleScope scope\\)")"
  if [[ -z "$body" ]]; then
    fail "the pickup module must define Disable(ModuleScope)"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "ClearSharedBuffers\\(\\)"; then
    fail "Disable must clear the shared pickup buffers: they are static and would keep destroyed objects reachable across a disable"
  fi

  local sentinel

  sentinel="$(
    extract_block "$pickup_component" "internal static void ClearSharedBuffers\\(\\)" |
      sed -e 's://.*::' | count_stdin_matches "EmptyItemDrops"
  )"
  if lookup_failed "$sentinel"; then
    fail "could not search ClearSharedBuffers; the check was not run"
    return
  fi

  if ((sentinel != 0)); then
    fail "ClearSharedBuffers must leave EmptyItemDrops alone: it is the empty sentinel GetAllItemDrop falls back to"
  fi
}

# I1 (AC-S4, slice scoped): a migrated module binds its settings whether it starts on or
# off, because it can be turned on later in the same session.
require_migrated_modules_bind_unconditionally() {
  local module_dir
  local config

  for module_dir in "${migrated_module_dirs[@]}"; do
    config="$module_dir/Config.cs"
    if [[ ! -r "$config" ]]; then
      fail "cannot read $config; the unconditional-bind check was not run"
      continue
    fi

    reject_source_pattern \
      "$config" \
      "AutomaticsModule\\.Disabled\\) return" \
      "$config must bind its settings unconditionally (R5.1): a module that can be turned on at runtime cannot leave its entries null"
  done
}

# The same removal must not spread. Until a module is migrated its early return is what
# keeps this slice from handing inactive-guard duty to code it never verifies (R5.1a).
require_unmigrated_modules_keep_their_early_return() {
  local config

  for config in "${unmigrated_module_configs[@]}"; do
    require_source_pattern \
      "$config" \
      "AutomaticsModule\\.Disabled\\) return" \
      "$config must keep its early return until that module is migrated (R5.1a): removing it here binds and wires a module this slice does not verify"
  done
}

# R1.5: a migrated module follows its applied state, never the setting read as a flag.
# ModuleDisabled was that flag - the start-up-only path read it once and wired nothing when it was
# set - and a read of it left in a migrated module acts on the setting instead of the state and
# ignores the stop request. Once a module is migrated nothing needs the flag, and it must not come
# back.
require_migrated_modules_read_no_start_up_flag() {
  local module_dir
  local file

  for module_dir in "${migrated_module_dirs[@]}"; do
    while IFS= read -r file; do
      reject_source_pattern \
        "$file" \
        "ModuleDisabled" \
        "$file must not declare or read ModuleDisabled (R1.5): a migrated module follows its applied state, Module.Running, which carries the stop request, not its setting read as a flag"
    done < <(find "$module_dir" -name '*.cs' -type f | sort)
  done
}

# I2 / I3 (AC-S5, R2.4): a migrated module owns no subscription directly. Every add has to be
# half of a scope.Subscribe pair, or the teardown has nothing to remove. The lambda form is
# what makes the pair; a bare `Hooks.X += Handler;` statement is the defect shape. The event may
# be named through its owner, as in `h => PickableCache.OnCacheAdded += h`.
require_event_wiring_goes_through_the_scope() {
  local module_dir
  local pattern="$1"
  local what="$2"
  local total
  local scoped
  local file

  for module_dir in "${migrated_module_dirs[@]}"; do
    while IFS= read -r file; do
      total="$(count_matches "$file" "$pattern\\s*(\\+|-)=")"
      scoped="$(count_matches "$file" "h => ([A-Za-z_][A-Za-z0-9_]*\\.)*$pattern\\s*(\\+|-)= h")"

      if lookup_failed "$total" "$scoped"; then
        fail "could not search $file for $what wiring; the check was not run"
        continue
      fi

      if ((total != scoped)); then
        fail "$file must wire $what through scope.Subscribe only (AC-S5): $total add/remove sites, $scoped of them half of a Subscribe pair"
      fi
    done < <(find "$module_dir" -name '*.cs' -type f | sort)
  done
}

# I4 (R3.1): patches are applied through the scope, which is what registers their removal.
# A direct CreateAndPatchAll leaves patches nothing can take off.
require_patching_goes_through_the_scope() {
  local module_dir
  local file

  for module_dir in "${migrated_module_dirs[@]}"; do
    while IFS= read -r file; do
      reject_source_pattern \
        "$file" \
        "Harmony\\.CreateAndPatchAll" \
        "$file must apply its patches through scope.PatchAll (R3.1): a direct CreateAndPatchAll registers no removal"
    done < <(find "$module_dir" -name '*.cs' -type f | sort)
  done
}

# I11 (MAJOR-4): three paths create components - the activation scan, the Player.Awake hook
# and the PickableItem.Awake postfix. A component created outside the scope has no owner, so
# the disable leaves it in the scene and the counts never come back to baseline.
require_components_are_created_through_the_scope() {
  local module_dir
  local file
  local count

  for module_dir in "${migrated_module_dirs[@]}"; do
    while IFS= read -r file; do
      reject_source_pattern \
        "$file" \
        "AddComponent<" \
        "$file must create components through scope.Attach or scope.TryAttach (MAJOR-4): AddComponent leaves the component without an owner to destroy it"
    done < <(find "$module_dir" -name '*.cs' -type f | sort)
  done

  for count in \
    "$pickup_module|TryAttach<AutomaticPickup>|1|the Player.Awake hook must attach through the scope" \
    "$pickup_module|TryAttach<PickableItemCache>|1|the cache attach path must go through the scope" \
    "$pickup_patches|Module\\.AttachCache|1|the PickableItem.Awake postfix must attach through the module's single entry point"; do
    IFS='|' read -r file pattern expected message <<<"$count"
    local found
    found="$(count_matches "$file" "$pattern")"
    if lookup_failed "$found"; then
      fail "could not count $pattern in $file; the check was not run"
      continue
    fi
    if ((found != expected)); then
      fail "$message (MAJOR-4): expected $expected occurrence(s) of $pattern in $file, found $found"
    fi
  done
}

# MAJOR-5: both attach paths run on a game call stack and can still be reached after the
# module stopped - a removal that failed, or an invocation list a caller already fetched. They
# gate on Discovering rather than Running on purpose (FIX 2): a stop request must not suppress
# discovery, because an object dropped in the frame between the request and the pump is never
# recovered if the user withdraws the request before the pump runs - no transition means no
# reconciliation, ever. Attaching under a stop request is harmless; the component's own Live
# gate stops it acting, and a disable that goes ahead destroys it with the rest of the scope.
# Arguments: the module file, then the declaration of each of its attach paths.
require_attach_paths_recheck_the_effective_state() {
  local module="$1"
  local entry
  local body
  local predicate

  shift
  for entry in "$@"; do
    body="$(extract_block "$module" "$entry")"
    if [[ -z "$body" ]]; then
      fail "$module must define ${entry##* }"
      continue
    fi

    if ! printf '%s\n' "$body" | rg -q "if \\(!Discovering\\) return;"; then
      fail "${entry##* } in $module must open with \"if (!Discovering) return;\" (MAJOR-5 / FIX 2): it is reachable after the module stopped, and gating it on Running instead would drop objects that nothing ever comes back for"
    fi
  done

  predicate="$(extract_expression_body "$module" "internal static bool Discovering =>")"
  if [[ -z "$predicate" ]]; then
    fail "$module must define the Discovering predicate (FIX 2)"
    return
  fi

  if ! printf '%s\n' "$predicate" | rg -q "Active && CurrentScope != null"; then
    fail "Discovering in $module must be exactly the applied state and the scope (FIX 2): Active && CurrentScope != null"
  fi

  if printf '%s\n' "$predicate" | rg -q "StopRequested|Running"; then
    fail "Discovering in $module must not carry the stop request (FIX 2): discovery and side effects are different questions, and sharing Running's predicate answers one of them wrongly"
  fi
}

# I5 (R1.5 / R1.5a): every patch is gated, and gated on Running - which carries the stop
# request - rather than on the applied flag alone. Counting is not enough on its own, so the
# gate is also required to appear once between one patch attribute and the next.
require_every_patch_is_gated() {
  local targets
  local acting
  local discovering
  local bypass
  local ungated
  local misplaced

  targets="$(count_matches "$pickup_patches" "\\[HarmonyPatch\\(typeof\\(")"
  acting="$(count_matches "$pickup_patches" "Module\\.Running")"
  discovering="$(count_matches "$pickup_patches" "Module\\.Discovering")"
  bypass="$(count_matches "$pickup_patches" "Module\\.Active")"

  if lookup_failed "$targets" "$acting" "$discovering" "$bypass"; then
    fail "could not count the patch gates; the check was not run"
    return
  fi

  if ((targets != 5)); then
    fail "the pickup module must patch exactly the 5 audited targets: found $targets; adding or removing one needs the liveness audit of specification 1.4 re-run"
  fi

  if ((acting + discovering != targets)); then
    fail "every patch must open with the module's effective-state gate (R1.5): $targets patches, $acting occurrences of Module.Running and $discovering of Module.Discovering"
  fi

  if ((discovering != 1)); then
    fail "exactly one patch is a discovery path and may use the discovery gate (FIX 2): found $discovering occurrences of Module.Discovering"
  fi

  if ((bypass != 0)); then
    fail "a patch that produces side effects must read Module.Running, never Module.Active (R1.5a): Active alone lets it keep acting between the disable request and the pump that removes it"
  fi

  ungated="$(
    awk '
      /\[HarmonyPatch\(typeof\(/ {
        if (open && seen != 1) print target ": " seen " gate(s)"
        open = 1; seen = 0; target = $0
        gsub(/^[ \t]+|[ \t]+$/, "", target)
        next
      }
      /Module\.(Running|Discovering)/ { if (open) seen++ }
      END { if (open && seen != 1) print target ": " seen " gate(s)" }
    ' "$pickup_patches"
  )"

  if [[ -n "$ungated" ]]; then
    fail "each patch must carry exactly one module gate of its own (R1.5): ${ungated//$'\n'/ | }"
  fi

  # And the discovery gate belongs to the discovery patch, not to one that picks things up.
  misplaced="$(
    awk '
      /\[HarmonyPatch\(typeof\(/ {
        target = $0
        gsub(/^[ \t]+|[ \t]+$/, "", target)
        next
      }
      /Module\.Discovering/ {
        if (target != "[HarmonyPatch(typeof(PickableItem), \"Awake\")]") print target
      }
    ' "$pickup_patches"
  )"

  if [[ -n "$misplaced" ]]; then
    fail "only the PickableItem.Awake postfix may use the discovery gate (FIX 2): every other patch produces side effects and must stop at the stop request; found it on ${misplaced//$'\n'/ | }"
  fi
}

# I6 (R4.3 / R1.6): the scope registers its single patch removal at the first PatchAll and
# undoes registrations in reverse, so acquiring patches first is what puts their removal
# last. Subscribing or attaching before the first PatchAll silently moves the patch removal
# ahead of them. The framework cannot detect it; this is where it is caught.
#
# Arguments: the module file, then the steps its Enable takes: scope (publishing CurrentScope),
# patch, subscribe and reconcile. With none listed, all four are required. A step that is not listed
# must not be taken either, so a module cannot grow an acquisition this check does not order. The
# list has to keep the order above, each step at most once: the order is the framework contract's,
# and a caller cannot make the check accept another one.
require_enable_acquires_in_order() {
  local module="$1"
  local -a canonical=(scope patch subscribe reconcile)
  local -a steps
  local -A pattern=(
    [scope]="CurrentScope = scope;"
    [patch]="scope\\.PatchAll\\("
    [subscribe]="scope\\.Subscribe<"
    [reconcile]="Reconcile[A-Za-z]*\\("
  )
  local -A doing=(
    [scope]="publish CurrentScope"
    [patch]="apply its patches"
    [subscribe]="subscribe"
    [reconcile]="reconcile the scene"
  )
  local -A listed=()
  local -A found=()
  local step
  local index
  local previous=-1
  local i
  local body
  local wanted=""
  local missing=""
  local prior=""
  local prior_line
  local step_line
  local last

  shift
  if (($# == 0)); then
    steps=("${canonical[@]}")
  else
    steps=("$@")
  fi

  for step in "${steps[@]}"; do
    index=-1
    for i in "${!canonical[@]}"; do
      if [[ "${canonical[$i]}" == "$step" ]]; then
        index=$i
      fi
    done

    if ((index <= previous)); then
      fail "invalid step sequence \"${steps[*]}\" for the Enable check on $module: the steps have to be taken from \"${canonical[*]}\" in that order, each at most once, because that is the acquisition order the framework contract fixes (R4.3); the check was not run"
      return
    fi

    previous=$index
    listed[$step]=1
    wanted+="${wanted:+, then }${doing[$step]}"
  done

  # Comments are removed first: prose that names a step must neither satisfy nor trip a check.
  body="$(extract_block "$module" "public void Enable\\(ModuleScope scope\\)" | sed -e 's://.*::')"
  if [[ -z "$body" ]]; then
    fail "$module must define Enable(ModuleScope)"
    return
  fi

  for step in "${canonical[@]}"; do
    found[$step]="$(printf '%s\n' "$body" | first_match_line "${pattern[$step]}")"
    if lookup_failed "${found[$step]}"; then
      fail "could not search the acquisition order of Enable in $module; the check was not run"
      return
    fi
  done

  for step in "${steps[@]}"; do
    if [[ -z "${found[$step]}" ]]; then
      missing+="${missing:+; }${doing[$step]}"
    fi
  done

  if [[ -n "$missing" ]]; then
    fail "Enable in $module must $wanted, and it does not: missing $missing"
    return
  fi

  for step in "${canonical[@]}"; do
    if [[ -z "${listed[$step]:-}" && -n "${found[$step]}" ]]; then
      fail "Enable in $module must not ${doing[$step]}: the steps it was declared to take are \"${steps[*]}\", and a step this check was not told about escapes the order it enforces (R4.3)"
    fi
  done

  for step in "${steps[@]}"; do
    if [[ -n "$prior" ]]; then
      prior_line="${found[$prior]}"
      step_line="${found[$step]}"
      if ((prior_line >= step_line)); then
        case "$prior:$step" in
          scope:*)
            fail "Enable in $module must publish CurrentScope before it acquires anything (MAJOR-4): a component created while the activation runs needs an owner already"
            ;;
          patch:subscribe)
            fail "Enable in $module must apply its patches before it subscribes (R4.3): the scope registers one patch removal at the first PatchAll, and a subscription taken first has its removal run after the patches come off"
            ;;
          patch:reconcile)
            fail "Enable in $module must apply its patches before it reconciles the scene (R4.3): the scope registers one patch removal at the first PatchAll, and a component attached first has its removal run after the patches come off"
            ;;
          *)
            fail "Enable in $module must subscribe before it reconciles the scene (R4.3 / R4.2): a producer installed after the scan misses everything that appears in between"
            ;;
        esac
      fi
    fi
    prior="$step"
  done

  last="$(
    printf '%s\n' "$body" | awk '
      { line = $0; gsub(/^[ \t]+|[ \t]+$/, "", line) }
      line == "" { next }
      line ~ /^\/\// { next }
      line ~ /^\/\/\// { next }
      line == "}" { next }
      { keep = line }
      END { print keep }
    '
  )"

  if [[ "$last" != "Active = true;" ]]; then
    fail "the last statement of Enable in $module must be \"Active = true;\" (R1.3 / R1.5): anything after it runs with the module already advertising itself as running, and a throw before it must leave the module inactive; found \"$last\""
  fi
}

# Arguments: the module file, then --no-scope for a module that attaches no component and so has no
# attach scope to close and no generation to reset.
require_disable_lowers_the_flag_first() {
  local module="$1"
  local option="${2:-}"
  local body
  local first

  if [[ -n "$option" && "$option" != "--no-scope" ]]; then
    fail "unknown option \"$option\" for the Disable check on $module; the check was not run"
    return
  fi

  body="$(extract_block "$module" "public void Disable\\(ModuleScope scope\\)")"
  if [[ -z "$body" ]]; then
    fail "$module must define Disable(ModuleScope)"
    return
  fi

  first="$(
    printf '%s\n' "$body" | tail -n +2 | awk '
      { line = $0; gsub(/^[ \t]+|[ \t]+$/, "", line) }
      line == "" { next }
      line ~ /^\/\// { next }
      line == "{" { next }
      { print line; exit }
    '
  )"

  if [[ "$first" != "Active = false;" ]]; then
    fail "the first statement of Disable in $module must be \"Active = false;\" (R1.5): every hot path has to be shut before anything is dismantled; found \"$first\""
  fi

  if [[ "$option" == "--no-scope" ]]; then
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "CurrentScope = null;"; then
    fail "Disable in $module must close the attach paths by clearing CurrentScope (MAJOR-4)"
  fi

  if ! printf '%s\n' "$body" | rg -q "Generation = 0;"; then
    fail "Disable in $module must reset the generation to the reserved zero (R4.5): a component left over from this activation has to stop matching"
  fi
}

# I7 (R4.2): the cache this module reconciles is filled by its own patch, so it is empty for
# everything that spawned while the module was off. Reading it back would reconcile against
# nothing; the scan has to enumerate the scene itself, inactive objects included.
require_reconciliation_enumerates_the_scene() {
  require_source_pattern \
    "$pickup_module" \
    "FindObjectsByType<PickableItem>\\(\\s*$" \
    "the activation scan must enumerate PickableItem itself (R4.2)"
  require_source_pattern \
    "$pickup_module" \
    "FindObjectsByType<Player>\\(\\s*$" \
    "the activation scan must enumerate Player itself (R4.2)"

  local including
  local deprecated

  including="$(count_stripped_matches "$pickup_module" "FindObjectsInactive\\.Include")"
  deprecated="$(count_stripped_matches "$pickup_module" "FindObjectsOfType")"

  if lookup_failed "$including" "$deprecated"; then
    fail "could not search the enumeration calls; the check was not run"
    return
  fi

  if ((including != 2)); then
    fail "both activation scans must ask for inactive objects (R4.2): FindObjectsInactive.Include appears $including time(s), expected 2; without it a re-enable misses everything parked under a deactivated root"
  fi

  if ((deprecated != 0)); then
    fail "the activation scan must use FindObjectsByType, not the deprecated FindObjectsOfType (build quality): the newer API exists in the shipped Unity and needs no warning suppression"
  fi

  local refill
  local roster

  refill="$(count_stripped_matches "$pickup_module" "PickableItemCache\\.Fill")"
  roster="$(count_stripped_matches "$pickup_module" "GetAllPlayers\\(")"

  if lookup_failed "$refill" "$roster"; then
    fail "could not search the activation scan; the check was not run"
    return
  fi

  if ((refill != 0)); then
    fail "the activation scan must not read the cache back (R4.2): this module's own patch fills it, so it holds nothing that appeared while the module was off"
  fi

  if ((roster != 0)); then
    fail "the activation scan must not use Player.GetAllPlayers (R4.2): it lists the players the game considers alive, not the objects that ran Player.Awake"
  fi
}

# Build quality: the build carries zero warnings, and it has to do so by not producing them.
# A suppression buys the same green result while leaving the thing the compiler objected to in
# place, so a migrated module is not allowed to hold one.
require_no_warning_suppressions() {
  local module_dir
  local file

  for module_dir in "${migrated_module_dirs[@]}"; do
    while IFS= read -r file; do
      reject_source_pattern \
        "$file" \
        "#pragma warning disable" \
        "$file must not suppress a compiler warning: the build is warning-free because the code is, not because the objection was silenced"
    done < <(find "$module_dir" -name '*.cs' -type f | sort)
  done
}

migration_file="Automatics/ConfigMigration.cs"
plugin_config_file="Automatics/Config.cs"

# The Config.cs of every module, found on disk rather than listed, so that a module added later
# is checked too. The callers insist on nine.
module_config_files() {
  find Automatics -mindepth 2 -maxdepth 2 -path 'Automatics/Automatic*/Config.cs' -type f | sort
}

# A new install starts with every module off: each module binds its `module` entry with the
# default Disabled. An existing config keeps a module on through the value the migrator writes
# into the file, never through the default.
require_every_module_starts_disabled() {
  local -a configs
  local config

  mapfile -t configs < <(module_config_files)
  if ((${#configs[@]} != 9)); then
    fail "expected the Config.cs of nine modules under Automatics/Automatic*/, found ${#configs[@]}: a module added or removed also changes ConfigMigration.ModuleSections and this count"
  fi

  for config in "${configs[@]}"; do
    require_source_pattern \
      "$config" \
      "config\\.Bind\\(\"module\", AutomaticsModule\\.Disabled," \
      "$config must bind its module entry with the default Disabled: a new install starts with every module off"
    reject_source_pattern \
      "$config" \
      "Bind\\(\"module\", AutomaticsModule\\.Enabled" \
      "$config must not bind its module entry with the default Enabled: an existing config keeps a module on through the value the migrator writes, not through the default"
  done
}

# A config file written before that default keeps each module as it was: the migrator writes
# Enabled for every module entry such a file lacks. A module missing from the migrator's list
# would be turned off by the update for those users, so the list has to name every section.
require_the_migrator_keeps_every_module_running() {
  local -a configs
  local config
  local section
  local list

  list="$(extract_block "$migration_file" 'ModuleSections =')"
  if [[ -z "$list" ]]; then
    fail "could not find ModuleSections in $migration_file; the check that the migrator keeps every module running was not run"
    return
  fi

  mapfile -t configs < <(module_config_files)
  if ((${#configs[@]} != 9)); then
    fail "expected the Config.cs of nine modules under Automatics/Automatic*/, found ${#configs[@]}; the migrator's list was checked against those only"
  fi

  for config in "${configs[@]}"; do
    section="$(sed -n -E 's/^[[:space:]]*private const string Section = "([a-z_]+)";.*$/\1/p' "$config" || true)"
    if [[ -z "$section" ]]; then
      fail "could not read the section name of $config; the check that the migrator keeps that module running was not run"
      continue
    fi

    if ! grep -Fq "\"$section\"" <<<"$list"; then
      fail "$migration_file must list \"$section\" in ModuleSections ($config): without it, updating turns that module off for a config written before the Disabled default"
    fi
  done
}

# The marker that tells the migrator a file already has the Disabled defaults is bound, so it
# is written with a description, and hidden, because it is bookkeeping and not a setting. The
# migrator has to run before anything binds: the first bind writes the defaults and the marker
# into an existing file, and the migrator would then find the marker and keep no module running.
require_the_defaults_marker_is_hidden_and_read_first() {
  local status=0
  local migrate_line
  local first_config_line
  local first_runtime_line

  # A string preserves unreadable markers; "0" also retries a failed migration write.
  # New installs get their marker from ConfigMigration.CreateForNewInstall.
  rg -U -q "Bind\\(ConfigMigration\\.DefaultsRevisionSection, ConfigMigration\\.DefaultsRevisionKey,\\s*\"0\", initializer: x =>\\s*\\{[^}]*x\\.Browsable = false;" "$plugin_config_file" || status=$?
  case "$status" in
    0) ;;
    1) fail "$plugin_config_file must bind the defaults marker (ConfigMigration.DefaultsRevisionKey) with string default \"0\" and Browsable = false: unreadable markers must survive and failed file writes must retry; new installs get the marker from ConfigMigration.CreateForNewInstall, and ConfigurationManager must not show it" ;;
    *) fail "could not search $plugin_config_file (rg exit $status); the check that the defaults marker is hidden was not run" ;;
  esac

  migrate_line="$(first_match_line 'ConfigMigration\.Migration\(Plugin\.Config\)' <"$plugin_file")"
  first_config_line="$(first_match_line 'Plugin\.Config\b' <"$plugin_file")"
  first_runtime_line="$(first_match_line '^\s*InitializeRuntime\(' <"$plugin_file")"
  if lookup_failed "$migrate_line" "$first_config_line" "$first_runtime_line" ||
    [[ -z "$migrate_line" || -z "$first_config_line" || -z "$first_runtime_line" ]]; then
    fail "could not find the migration call, the first use of Plugin.Config or the InitializeRuntime call in $plugin_file; the check that the migrator runs before the first bind was not run"
  elif ((migrate_line != first_config_line || migrate_line > first_runtime_line)); then
    fail "$plugin_file must call ConfigMigration.Migration(Plugin.Config) before anything else uses Plugin.Config and before InitializeRuntime binds the entries: the first bind writes the defaults and the marker into the file, and the migrator then keeps no module running"
  fi
}

# The migrator writes the config only through a temporary file that File.Replace swaps in, so an
# interrupted write leaves the old file or the new one. A direct write to the config path could
# leave the marker without the module entries it vouches for, or lose settings.
require_the_config_is_replaced_atomically() {
  require_source_pattern \
    "$migration_file" \
    "File\\.Replace\\(temp, path, null\\)" \
    "$migration_file must swap the config in with File.Replace(temp, path, null): an interrupted direct write can leave a part of the file"
  reject_source_pattern \
    "$migration_file" \
    "(File\\.(WriteAllText|WriteAllLines|WriteAllBytes|AppendAllText|AppendAllLines|OpenWrite|Create)|new (StreamWriter|FileStream))\\(\\s*path\\b" \
    "$migration_file must not write the config path directly: write a temporary file and swap it in with File.Replace"
}

# R9.1 / R9.2: every migrated module uses its own toggle strings, and none of the shared restart
# ones. Those still say "Reboot Required" and are still correct for the modules that have not
# moved, and wrong for one that has.
require_the_runtime_toggle_strings_exist() {
  local language_file
  local key
  local module_dir
  local config

  for module_dir in "${migrated_module_dirs[@]}"; do
    config="$module_dir/Config.cs"
    if [[ ! -r "$config" ]]; then
      fail "cannot read $config; the toggle string check was not run"
      continue
    fi

    require_source_pattern \
      "$config" \
      "@config_common_disable_module_runtime_name" \
      "$config must use the runtime toggle name, not the one that says a restart is required (R9.1)"
    require_source_pattern \
      "$config" \
      "@config_common_disable_module_runtime_description" \
      "$config must use the runtime toggle description (R9.1)"
    reject_source_pattern \
      "$config" \
      "\"@config_common_disable_module_name\"" \
      "$config must not use the toggle name that says a restart is required (R9.1): the module applies the change while the game runs"
    reject_source_pattern \
      "$config" \
      "\"@config_common_disable_module_description\"" \
      "$config must not use the toggle description that says a restart is required (R9.1): the module applies the change while the game runs"
  done

  for language_file in Automatics/Languages/en_us.json Automatics/Languages/ja_jp.json; do
    if [[ ! -r "$language_file" ]]; then
      fail "cannot read $language_file; the translation check was not run"
      continue
    fi

    for key in \
      "@config_common_disable_module_runtime_name" \
      "@config_common_disable_module_runtime_description" \
      "@config_common_disable_module_name" \
      "@config_common_disable_module_description"; do
      require_source_pattern \
        "$language_file" \
        "\"$key\"" \
        "$language_file must define $key (R9.2): the two key sets have to stay in step, and the unmigrated modules still use the restart wording"
    done
  done
}

# ---------------------------------------------------------------------------------------
# Automatic Processing: a migrated module that moves items between containers and stations.
#
# Every check below reads the source through strip_code - comments removed, the contents of
# string, verbatim string and char literals blanked, every line kept - and matches POSIX EREs
# over a stream in which every run of whitespace, line breaks included, is a single space: ` *`
# stands for any whitespace, and a leading `(^|[^A-Za-z0-9_])` for a word boundary. A line break
# inside `Module.Running`, inside a call or before its argument list therefore moves nothing
# past a check, prose never trips or satisfies one, and hits are still reported against the
# lines of the real file.
# ---------------------------------------------------------------------------------------

# The file with its // and /* */ comments removed and the contents of its string, verbatim
# string and char literals blanked, every line kept so that line numbers still point into the
# real file. Pass a second argument to keep the literal contents, for the one check that has to
# read a method name written as a string.
strip_code() {
  STRIP_KEEP_STRINGS="${2:-}" awk '
    BEGIN { sq = sprintf("%c", 39); dq = "\""; keep = (ENVIRON["STRIP_KEEP_STRINGS"] != "") }
    {
      line = $0
      out = ""
      n = length(line)
      i = 1
      while (i <= n) {
        c = substr(line, i, 1)
        two = substr(line, i, 2)
        if (state == "block") {
          if (two == "*/") { state = ""; i += 2 } else { i++ }
          continue
        }
        if (state == "verbatim") {
          if (two == dq dq) { if (keep) out = out two; i += 2; continue }
          if (c == dq) { out = out dq; state = ""; i++; continue }
          if (keep) out = out c
          i++
          continue
        }
        if (state == "string" || state == "char") {
          if (c == "\\") { if (keep) out = out two; i += 2; continue }
          if ((state == "string" && c == dq) || (state == "char" && c == sq)) { out = out c; state = ""; i++; continue }
          if (keep) out = out c
          i++
          continue
        }
        if (two == "//") break
        if (two == "/*") { state = "block"; i += 2; continue }
        if (two == "@" dq) { out = out dq; state = "verbatim"; i += 2; continue }
        if (substr(line, i, 3) == "$@" dq || substr(line, i, 3) == "@$" dq) { out = out dq; state = "verbatim"; i += 3; continue }
        if (c == dq) { out = out dq; state = "string"; i++; continue }
        if (c == sq) { out = out sq; state = "char"; i++; continue }
        out = out c
        i++
      }
      if (state == "string" || state == "char") state = ""
      print out
    }
  ' "$1"
}

# Prints "LINE<TAB>TEXT" for every match of the ERE in the text on stdin, searched as one stream
# in which every run of whitespace, line breaks included, is a single space. LINE is the line
# of the text where the match starts. Prints ERROR when the search could not run.
find_all() {
  local pattern="$1"
  local status=0
  local out=""

  out="$(FIND_PATTERN="$pattern" awk '
    {
      line = $0
      gsub(/[ \t\r]+/, " ", line)
      start[NR] = length(stream) + 1
      stream = stream line " "
    }
    END {
      pattern = ENVIRON["FIND_PATTERN"]
      offset = 0
      rest = stream
      while (rest != "" && match(rest, pattern)) {
        at = offset + RSTART
        text = substr(rest, RSTART, RLENGTH)
        if (text ~ /^[^A-Za-z0-9_.]/) { text = substr(text, 2); at++ }
        found = 1
        for (k = 1; k <= NR; k++) {
          if (start[k] > at) break
          found = k
        }
        gsub(/ +/, " ", text)
        printf "%d\t%s\n", found, text
        step = RSTART + RLENGTH - 1
        if (step < 1) step = 1
        offset += step
        rest = substr(rest, step + 1)
      }
    }
  ')" || status=$?

  if ((status != 0)); then
    printf 'ERROR\n'
  elif [[ -n "$out" ]]; then
    printf '%s\n' "$out"
  fi
}

# Exit status 0 when the ERE matches the text on stdin (as find_all sees it), 1 when it does
# not, 2 when the search could not run.
has_match() {
  local hits

  hits="$(find_all "$1")"
  if [[ "$hits" == "ERROR" ]]; then
    return 2
  elif [[ -n "$hits" ]]; then
    return 0
  fi
  return 1
}

# The line of the first match in the text on stdin, nothing when there is none, ERROR when the
# search could not run.
first_hit() {
  local hits
  local first

  hits="$(find_all "$1")"
  if [[ "$hits" == "ERROR" ]]; then
    printf 'ERROR\n'
    return
  fi
  if [[ -z "$hits" ]]; then
    return
  fi

  first="${hits%%$'\n'*}"
  printf '%s\n' "${first%%$'\t'*}"
}

# The number of matches in the text on stdin whose line is greater than the first argument, or
# ERROR. Pass 0 to count every match.
count_hits_after() {
  local after="$1"
  local hits
  local line
  local text
  local count=0

  hits="$(find_all "$2")"
  if [[ "$hits" == "ERROR" ]]; then
    printf 'ERROR\n'
    return
  fi

  if [[ -n "$hits" ]]; then
    while IFS=$'\t' read -r line text; do
      if ((line > after)); then
        count=$((count + 1))
      fi
    done <<<"$hits"
  fi

  printf '%s\n' "$count"
}

count_hits() {
  count_hits_after 0 "$1"
}

# Exactly one match of the pattern in the file's code, read through strip_code so a comment or a
# literal holding the expected text cannot stand in for the code itself. Do not use it for a
# pattern that contains a string literal: strip_code blanks those.
require_single_code_match() {
  local file="$1"
  local pattern="$2"
  local message="$3"
  local stripped
  local found

  stripped="$(strip_code "$file")"
  found="$(printf '%s\n' "$stripped" | count_hits "$pattern")"
  if lookup_failed "$found"; then
    fail "could not search the code of $file; the check behind this was not run: $message"
  elif ((found != 1)); then
    fail "$message (found $found occurrence(s) in code)"
  fi
}

# The text of every match in the text on stdin with its whitespace removed, one per line, or
# ERROR, which no expected set contains.
hit_texts() {
  local hits
  local line
  local text

  hits="$(find_all "$1")"
  if [[ "$hits" == "ERROR" ]]; then
    printf 'ERROR\n'
    return
  fi

  if [[ -n "$hits" ]]; then
    while IFS=$'\t' read -r line text; do
      text="${text//[[:space:]]/}"
      printf '%s\n' "${text#.}"
    done <<<"$hits"
  fi
}

# One method, read through strip_code: from its declaration to the brace that closes it. The
# stripped text is captured before it is piped: extract_block stops reading at the closing
# brace, and a strip_code still writing into that closed pipe would take the whole script down
# under pipefail with no message at all.
method_body() {
  local stripped

  stripped="$(strip_code "$1")"
  printf '%s\n' "$stripped" | extract_block - "$2"
}

# A method whose whole gate-free body is safety-relevant, compared after comments, literals and
# whitespace have been removed. The caller supplies the complete audited implementation.
require_exact_method_body() {
  local file="$1"
  local declaration="$2"
  local expected="$3"
  local message="$4"
  local body
  local compact

  body="$(method_body "$file" "$declaration")"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != "$expected" ]]; then
    fail "$message"
  fi
}

# Component methods named like Unity messages are engine entry points even when they are not in
# the six-name set older checks knew about. Awake is deliberately outside the pattern; the only
# On* messages these scoped components may declare are their two audited teardown callbacks.
require_component_declares_only_allowed_unity_messages() {
  local file="$1"
  local label="$2"
  local stripped
  local found
  local allowed

  stripped="$(strip_code "$file")"
  found="$(printf '%s\n' "$stripped" | count_hits '(void|IEnumerator) *(On[A-Z][A-Za-z0-9_]*|Update|FixedUpdate|LateUpdate|Start|Reset|OnValidate) *\(')"
  allowed="$(printf '%s\n' "$stripped" | count_hits '(void|IEnumerator) *(OnDestroy|OnScopeDetaching) *\(')"
  if lookup_failed "$found" "$allowed"; then
    fail "could not search $file; the Unity message allowlist check was not run"
  elif ((found != allowed)); then
    fail "$label may declare only Awake, OnDestroy and OnScopeDetaching as Unity message-shaped methods; found $found candidate(s), of which $allowed are allowed"
  fi
}

# The first statement of a block with all whitespace removed: what follows the opening brace up
# to and including the first ';', or up to the first '{' when a block statement comes first.
# Without whitespace, a statement may be wrapped over lines but not changed.
first_statement() {
  awk '
    { stream = stream $0 "\n" }
    END {
      gsub(/[ \t\r\n]+/, "", stream)
      i = index(stream, "{")
      if (i == 0) exit
      rest = substr(stream, i + 1)
      semi = index(rest, ";")
      brace = index(rest, "{")
      if (semi == 0 && brace == 0) exit
      if (brace > 0 && (semi == 0 || brace < semi)) print substr(rest, 1, brace)
      else print substr(rest, 1, semi)
    }
  '
}

# The last statement of a block with all whitespace removed, or a marker no expected statement
# matches when the block does not end in a simple statement.
last_statement() {
  awk '
    { stream = stream $0 "\n" }
    END {
      gsub(/[ \t\r\n]+/, "", stream)
      sub(/}$/, "", stream)
      if (substr(stream, length(stream), 1) != ";") { print "<not a simple statement>"; exit }
      body = substr(stream, 1, length(stream) - 1)
      cut = 0
      for (k = length(body); k > 0; k--) {
        ch = substr(body, k, 1)
        if (ch == ";" || ch == "{" || ch == "}") { cut = k; break }
      }
      print substr(body, cut + 1) ";"
    }
  '
}

# The method names of a table of gated entry points, as an alternation. Argument: the name of the
# table.
entry_method_names() {
  local -n _names_table="$1"
  local entry

  for entry in "${_names_table[@]}"; do
    entry="${entry%%|*}"
    printf '%s\n' "${entry#*.}"
  done | sort -u | paste -sd '|' -
}

# The static state a file declares, read from strip_code output on stdin: one "collection NAME"
# per static collection (readonly or not) and one "timer NAME" per static float field that is
# neither const nor readonly. ERROR when a search could not run.
processing_static_state() {
  local source
  local hits
  local line
  local text

  source="$(cat)"

  hits="$(printf '%s\n' "$source" | find_all 'static +(readonly +)?(HashSet|Dictionary|List|Queue|Stack|SortedSet|SortedDictionary) *<[^;={}]*> *[A-Za-z_][A-Za-z0-9_]* *[;=]')"
  if [[ "$hits" == "ERROR" ]]; then
    printf 'ERROR\n'
    return
  fi
  if [[ -n "$hits" ]]; then
    while IFS=$'\t' read -r line text; do
      text="${text%[;=]}"
      text="${text%"${text##*[![:space:]]}"}"
      printf 'collection %s\n' "${text##* }"
    done <<<"$hits"
  fi

  hits="$(printf '%s\n' "$source" | find_all 'static +float +[A-Za-z_][A-Za-z0-9_]* *[;=]')"
  if [[ "$hits" == "ERROR" ]]; then
    printf 'ERROR\n'
    return
  fi
  if [[ -n "$hits" ]]; then
    while IFS=$'\t' read -r line text; do
      text="${text%[;=]}"
      text="${text%"${text##*[![:space:]]}"}"
      printf 'timer %s\n' "${text##* }"
    done <<<"$hits"
  fi
}

# The owners of the module's session state, and what each one's Cleanup has to do beyond the
# collections and timers its file declares (fields separated by @@: owner, pattern, duty).
processing_cleanup_owners=(
  "Logics|$processing_globals"
  "SmelterProcess|$processing_dir/SmelterProcess.cs"
  "TurretProcess|$processing_dir/TurretProcess.cs"
  "ConnectionEffects|$processing_dir/ConnectionEffects.cs"
)
processing_cleanup_extras=(
  'ConnectionEffects@@(^|[^A-Za-z0-9_])RemoveHelper *\(@@destroy every helper object it created'
  'ConnectionEffects@@(^|[^A-Za-z0-9_.])_currentProcessorId *= *0 *;@@forget the processor it was showing'
  'ConnectionEffects@@(^|[^A-Za-z0-9_.])_lastHovering *= *null *;@@drop the object it resolved last'
  'ConnectionEffects@@(^|[^A-Za-z0-9_.])_lastContextValid *= *false *;@@invalidate its memoized context'
  'ConnectionEffects@@(^|[^A-Za-z0-9_.])_lastContext *= *default *;@@drop its memoized context'
)

# R4.3 / R1.6: a module that attaches no component owns its patches, if it has any, and exactly
# the listed hook subscriptions, and nothing else. The scope registers its one patch removal at the
# first PatchAll and tears down in reverse, so patching first is what puts the patch removal last.
# Active is raised last and lowered first, once each, so no entry point sees the module as running
# while it is half built or half dismantled (R1.5).
# Arguments: the module file, the name the messages give the module, --patches when the module
# applies patches, then the hooks it subscribes (OnPlayerAwake, OnPlayerUpdate, ...).
require_module_enable_and_disable_shape() {
  local module="$1"
  local label="$2"
  local patches=0
  local -a hooks
  local names=""
  local body
  local patch_line
  local subscribe_line
  local subscriptions
  local hook
  local hooked
  local last
  local first
  local stripped
  local raised
  local lowered

  shift 2
  if [[ "${1:-}" == "--patches" ]]; then
    patches=1
    shift
  fi
  hooks=("$@")

  if ((${#hooks[@]} == 0)); then
    fail "no hook was named for the $label Enable; the check on its subscriptions was not run"
    return
  fi

  for hook in "${hooks[@]}"; do
    names+="${names:+, }Hooks.$hook"
  done

  body="$(method_body "$module" "public void Enable\\(ModuleScope scope\\)")"
  if [[ -z "$body" ]]; then
    fail "the $label module must define Enable(ModuleScope)"
    return
  fi

  patch_line="$(printf '%s\n' "$body" | first_hit 'scope *\. *PatchAll *\(')"
  subscribe_line="$(printf '%s\n' "$body" | first_hit 'scope *\. *Subscribe *<')"
  subscriptions="$(printf '%s\n' "$body" | count_hits 'scope *\. *Subscribe *<')"

  if lookup_failed "$patch_line" "$subscribe_line" "$subscriptions"; then
    fail "could not search the acquisition order of the $label Enable; the check was not run"
    return
  fi

  if ((patches)); then
    if [[ -z "$patch_line" || -z "$subscribe_line" ]]; then
      fail "the $label Enable must apply its patches and subscribe through the scope"
    elif ((patch_line >= subscribe_line)); then
      fail "the $label Enable must apply its patches before it subscribes (R4.3): the scope registers one patch removal at the first PatchAll, and a subscription taken first has its removal run after the patches come off"
    fi
  fi

  if ((subscriptions != ${#hooks[@]})); then
    fail "the $label Enable must subscribe exactly $names through the scope: found $subscriptions scope.Subscribe call(s), expected ${#hooks[@]}"
  fi

  for hook in "${hooks[@]}"; do
    hooked="$(printf '%s\n' "$body" | count_hits "h *=> *Hooks *\\. *$hook *\\+= *h([^A-Za-z0-9_]|\$)")"
    if lookup_failed "$hooked"; then
      fail "could not search the $label Enable for its $hook subscription; the check was not run"
      continue
    fi

    if ((hooked != 1)); then
      fail "the $label Enable must subscribe Hooks.$hook exactly once through the scope: found $hooked; a hook subscribed twice runs twice per call, and a missing one never runs"
    fi
  done

  last="$(printf '%s\n' "$body" | last_statement)"
  if [[ "$last" != "Active=true;" ]]; then
    fail "the last statement of the $label Enable must be \"Active = true;\" (R1.3 / R1.5): anything after it runs with the module already advertising itself as running; found \"$last\""
  fi

  stripped="$(strip_code "$module")"
  raised="$(printf '%s\n' "$stripped" | count_hits '(^|[^A-Za-z0-9_])Active *= *true *;')"
  lowered="$(printf '%s\n' "$stripped" | count_hits '(^|[^A-Za-z0-9_])Active *= *false *;')"
  if lookup_failed "$raised" "$lowered"; then
    fail "could not count the assignments of Active in the $label module; the check was not run"
  elif ((raised != 1 || lowered != 1)); then
    fail "the $label module must raise Active exactly once, as the last statement of Enable, and lower it exactly once, as the first statement of Disable (R1.5): found $raised raise(s) and $lowered lowering(s)"
  fi

  body="$(method_body "$module" "public void Disable\\(ModuleScope scope\\)")"
  if [[ -z "$body" ]]; then
    fail "the $label module must define Disable(ModuleScope)"
    return
  fi

  first="$(printf '%s\n' "$body" | first_statement)"
  if [[ "$first" != "Active=false;" ]]; then
    fail "the first statement of the $label Disable must be \"Active = false;\" (R1.5): every entry point has to be shut before anything is dismantled; found \"$first\""
  fi
}

# Automatic Processing owns patches and two hook subscriptions and nothing else.
require_processing_enable_and_disable_shape() {
  require_module_enable_and_disable_shape "$processing_module" processing --patches \
    OnPlayerAwake OnPlayerUpdate
}

# R6.1 / R6.2: everything the module keeps between calls - the container cache, the smelter
# throttles and their reset times, the turret timers and the connection-effect objects - is
# cleared in one place, and that place runs both on a disable and on a new world session. That a
# Cleanup is called proves nothing if its body was emptied, so each owner's Cleanup must itself
# clear every static collection and reset every static float timer its file declares, plus the
# connection-effect state that is neither.
require_processing_session_is_reset() {
  local body
  local cleanup
  local caller
  local status
  local row
  local owner
  local file
  local fields
  local kind
  local name
  local pattern
  local duty
  local rest

  body="$(method_body "$processing_module" "internal static void ResetSession\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the processing module must define ResetSession() (R6.1)"
    return
  fi

  for cleanup in "Logics" "SmelterProcess" "TurretProcess" "ConnectionEffects"; do
    status=0
    printf '%s\n' "$body" | has_match "(^|[^A-Za-z0-9_])$cleanup *\\. *Cleanup *\\( *\\) *;" || status=$?
    case "$status" in
      0) ;;
      1) fail "ResetSession must clear $cleanup (R6.1): state left there outlives the disable and the next activation starts from it" ;;
      *) fail "could not search ResetSession for $cleanup.Cleanup(); the check was not run" ;;
    esac
  done

  for caller in "private static void OnPlayerAwake\\(" "public void Disable\\(ModuleScope scope\\)"; do
    body="$(method_body "$processing_module" "$caller")"
    if [[ -z "$body" ]]; then
      fail "the processing module must define ${caller//\\/}"
      continue
    fi

    status=0
    printf '%s\n' "$body" | has_match '(^|[^A-Za-z0-9_])ResetSession *\( *\) *;' || status=$?
    case "$status" in
      0) ;;
      1) fail "${caller//\\/} must call ResetSession() (R6.1 / R6.2): a disable and a new world session both have to start from empty state" ;;
      *) fail "could not search ${caller//\\/} for ResetSession(); the check was not run" ;;
    esac
  done

  for row in "${processing_cleanup_owners[@]}"; do
    IFS='|' read -r owner file <<<"$row"
    body="$(method_body "$file" "public static void Cleanup\\(\\)")"
    if [[ -z "$body" ]]; then
      fail "$owner must define Cleanup() in $file (R6.1)"
      continue
    fi

    fields="$(strip_code "$file" | processing_static_state)"
    if [[ "$fields" == *ERROR* ]]; then
      fail "could not search $file for its static state; the check on $owner.Cleanup was not run"
      continue
    fi
    if [[ -z "$fields" ]]; then
      fail "found no static collection or timer in $file, so the check on $owner.Cleanup would check nothing"
      continue
    fi

    while read -r kind name; do
      if [[ "$kind" == "collection" ]]; then
        pattern="(^|[^A-Za-z0-9_.])$name *\\. *Clear *\\( *\\) *;"
        duty="clear $name"
      else
        pattern="(^|[^A-Za-z0-9_.])$name *= *0(\\.0)?f? *;"
        duty="reset $name"
      fi

      status=0
      printf '%s\n' "$body" | has_match "$pattern" || status=$?
      case "$status" in
        0) ;;
        1) fail "$owner.Cleanup must $duty (R6.1): a Cleanup that is still called but no longer does this lets the state outlive the disable and the session" ;;
        *) fail "could not search $owner.Cleanup; the check on $name was not run" ;;
      esac
    done <<<"$fields"
  done

  for row in "${processing_cleanup_extras[@]}"; do
    owner="${row%%@@*}"
    rest="${row#*@@}"
    pattern="${rest%%@@*}"
    duty="${rest#*@@}"
    file=""
    for cleanup in "${processing_cleanup_owners[@]}"; do
      if [[ "${cleanup%%|*}" == "$owner" ]]; then
        file="${cleanup#*|}"
      fi
    done

    body="$(method_body "$file" "public static void Cleanup\\(\\)")"
    status=0
    printf '%s\n' "$body" | has_match "$pattern" || status=$?
    case "$status" in
      0) ;;
      1) fail "$owner.Cleanup must $duty (R6.1): a Cleanup that is still called but no longer does this lets the state outlive the disable and the session" ;;
      *) fail "could not search $owner.Cleanup; the check that it must $duty was not run" ;;
    esac
  done
}

# R1.5a: a hook handler can still be reached after the module stopped - a removal that failed, or
# an invocation list fetched before the removal - so it opens with its own gate, and nothing it
# does runs before that. Arguments: the module file, then the declaration of each handler.
require_hook_handlers_open_with_the_gate() {
  local module="$1"
  local declaration
  local body
  local first

  shift
  if (($# == 0)); then
    fail "no hook handler was named for $module; the check that the handlers open with the gate was not run"
    return
  fi

  for declaration in "$@"; do
    body="$(method_body "$module" "$declaration")"
    if [[ -z "$body" ]]; then
      fail "$module must define ${declaration//\\/}"
      continue
    fi

    first="$(printf '%s\n' "$body" | first_statement)"
    if [[ "$first" != "if(!Running)return;" ]]; then
      fail "${declaration//\\/} in $module must open with \"if (!Running) return;\" (R1.5a): it is reachable after the module stopped, and whatever comes before the gate still acts; found \"$first\""
    fi
  done
}

# The processing per-frame hook draws the connection effect.
require_processing_player_update_is_gated() {
  require_hook_handlers_open_with_the_gate "$processing_module" "private static void OnPlayerUpdate\\("
}

# A module that attaches no component has no generation, no attach scope, no discovery gate and no
# activation scan. Growing one of them means it started creating things the scope has to own, and
# that needs the component checks written for the pickup module applied to it first. Argument: the
# module file.
require_module_has_no_component_machinery() {
  local module="$1"
  local found

  found="$(strip_code "$module" | count_hits '(^|[^A-Za-z0-9_])(Generation|CurrentScope|Discovering|FindObjectsByType)([^A-Za-z0-9_]|$)')"
  if lookup_failed "$found"; then
    fail "could not search $module; the check was not run"
    return
  fi

  if ((found != 0)); then
    fail "$module must not carry component machinery (a generation, an attach scope, a discovery gate or a scene scan): it attaches nothing, and a module that starts to needs the component checks extended to it first"
  fi
}

require_processing_has_no_component_machinery() {
  require_module_has_no_component_machinery "$processing_module"
}

# R1.5a: the gates live in the entry points, not in the patch methods, because the transpilers
# inject calls that run outside any patch method. That only holds if every patch reaches nothing
# but a gated entry point and makes no change of its own. TurretProcess.ClearTimer is the one
# exception: it forgets a timer and has no effect on the game. Every way of naming a callee
# counts - a direct call from a prefix or postfix, the typeof/name pair and the "Type:Name" string
# a transpiler resolves, and nameof - and none may be satisfied by a comment.
require_processing_patches_reach_only_gated_entries() {
  local stripped
  local literal
  local targets
  local found
  local expected
  local entry
  local hits
  local line
  local text

  stripped="$(strip_code "$processing_patches")"
  literal="$(strip_code "$processing_patches" keep-strings)"

  targets="$(printf '%s\n' "$stripped" | count_hits '\[ *HarmonyPatch *\( *typeof *\(')"
  if lookup_failed "$targets"; then
    fail "could not count the processing patch targets; the check was not run"
    return
  fi

  if ((targets != 13)); then
    fail "the processing module must patch exactly the 13 audited targets: found $targets; adding or removing one needs the liveness audit of specification 1.4 re-run and the gated entry points reviewed"
  fi

  found="$(
    {
      printf '%s\n' "$stripped" |
        hit_texts '(^|[^A-Za-z0-9_])[A-Z][A-Za-z0-9_]*Process *\. *[A-Za-z_][A-Za-z0-9_]* *\(' |
        sed -e 's/($//'
      printf '%s\n' "$stripped" |
        hit_texts 'nameof *\( *[A-Z][A-Za-z0-9_]*Process *\. *[A-Za-z_][A-Za-z0-9_]* *\)' |
        sed -e 's/^nameof(//' -e 's/)$//'
      printf '%s\n' "$literal" |
        hit_texts 'typeof *\( *[A-Z][A-Za-z0-9_]*Process *\) *, *"[A-Za-z_][A-Za-z0-9_]*"' |
        sed -E 's/^typeof\(([A-Za-z0-9_]+)\),"([A-Za-z0-9_]+)"$/\1.\2/'
      printf '%s\n' "$literal" |
        hit_texts '"[A-Z][A-Za-z0-9_]*Process *: *[A-Za-z_][A-Za-z0-9_]*"' |
        sed -E 's/^"([A-Za-z0-9_]+):([A-Za-z0-9_]+)"$/\1.\2/'
    } | sort -u
  )"
  expected="$(
    {
      for entry in "${processing_entries[@]}"; do
        printf '%s\n' "${entry%%|*}"
      done
      printf 'TurretProcess.ClearTimer\n'
    } | sort -u
  )"

  if [[ "$found" != "$expected" ]]; then
    fail "the processing patches must reach exactly the gated entry points and TurretProcess.ClearTimer (R1.5a): anything else acts without checking the stop request; expected: ${expected//$'\n'/ }; found: ${found//$'\n'/ }"
  fi

  hits="$(printf '%s\n' "$stripped" | find_all "$processing_patch_changes")"
  if [[ "$hits" == "ERROR" ]]; then
    fail "could not search the processing patches for changes of their own; the check was not run"
    return
  fi

  if [[ -n "$hits" ]]; then
    while IFS=$'\t' read -r line text; do
      fail "the processing patches must not change a container, a station or the world themselves (R1.5a): only the gated entry points may, and a change made in a patch method skips the stop request ($processing_patches:$line: $text)"
    done <<<"$hits"
  fi
}

# R1.5a / R8.11: each entry point in a table moves items, and what it calls to do so - a claim, a
# removal, a store, an RPC, an inventory change - is code that is not ours: a synchronous callback
# or another mod's patch there can change the setting, and so raise the stop request, before the
# outer call returns. So the stop request is checked exactly once, as the first statement, and
# never again: a check after an item left where it was would strand it, while a transfer that was
# accepted is delivered once and the next call sees the stop. Every read of the module state
# outside Module.cs therefore has to be one of these gates, and the state may not be imported under
# another name that these patterns would not see.
#
# What this does not check is that Running means the right thing - `Running => true` passes - or
# that an accepted transfer really completes. Both need the game.
#
# Arguments: the name of the entry table (fields separated by |: qualified name, file, declaration
# pattern, the exact statement it has to open with), the module's directory, its Module.cs, and the
# number of entries the table has to hold. An empty table fails: it would check nothing.
require_item_entries_open_with_the_gate() {
  local -n _gate_table="$1"
  local table="$1"
  local module_dir="$2"
  local module_file="$3"
  local expected_count="$4"
  local entry
  local qualified
  local file
  local declaration
  local gate
  local body
  local first
  local gates
  local listed
  local direct
  local imports
  local stripped
  local entry_file

  if ((${#_gate_table[@]} == 0)); then
    fail "found no entry point in $table, so the stop-boundary checks on $module_dir could not be run"
    return
  fi

  if ((${#_gate_table[@]} != expected_count)); then
    fail "$module_dir must list exactly its $expected_count audited side-effect entry points in $table (R1.5a): found ${#_gate_table[@]}; adding or removing one needs the list and this count changed deliberately"
  fi

  for entry in "${_gate_table[@]}"; do
    IFS='|' read -r qualified file declaration gate <<<"$entry"
    body="$(method_body "$file" "$declaration")"
    if [[ -z "$body" ]]; then
      fail "cannot find $qualified in $file; the stop-boundary checks on it were not run"
      continue
    fi

    first="$(printf '%s\n' "$body" | first_statement)"
    if [[ "$first" != "${gate//[[:space:]]/}" ]]; then
      fail "$qualified must open with \"$gate\" (R1.5a): its first statement is where the stop request is checked, anything before it still runs after a stop, and the returned value is what the game falls back to; found \"$first\""
    fi

    gates="$(printf '%s\n' "$body" | count_hits "$module_running_read")"
    if lookup_failed "$gates"; then
      fail "could not count the gates in $qualified; the check was not run"
      continue
    fi

    if ((gates != 1)); then
      fail "$qualified must check Module.Running exactly once (R8.11): a second check - even one at the head of a loop - can run after an item already left where it was and strand it; found $gates"
    fi
  done

  # Every file of the module but the one that defines the state. A read in a helper, in a patch
  # method or in any other file is invisible to the per-entry checks, and would let a call made
  # between a removal and its delivery look at the stop request after all.
  for file in "$module_dir"/*.cs; do
    if [[ "$file" == "$module_file" ]]; then
      continue
    fi

    stripped="$(strip_code "$file")"
    gates="$(printf '%s\n' "$stripped" | count_hits "$module_running_read")"
    direct="$(printf '%s\n' "$stripped" | count_hits "$module_raw_state_read")"
    imports="$(printf '%s\n' "$stripped" | count_hits "$module_import")"
    listed=0
    for entry in "${_gate_table[@]}"; do
      IFS='|' read -r _ entry_file _ _ <<<"$entry"
      if [[ "$entry_file" == "$file" ]]; then
        listed=$((listed + 1))
      fi
    done

    if lookup_failed "$gates" "$direct" "$imports"; then
      fail "could not search $file for module state reads; the check was not run"
      continue
    fi

    if ((direct != 0)); then
      fail "$file must read Module.Running, never Module.Active or Module.StopRequested (R1.5a): Active alone keeps acting between the stop request and the teardown"
    fi

    if ((imports != 0)); then
      fail "$file must not import the module under another name (R1.5a): with a using static or an alias, a read of its state no longer spells Module. and every check on where the state is read would miss it"
    fi

    if ((gates != listed)); then
      fail "$file must read Module.Running only in the gates of its listed entry points (R8.11): $listed listed, $gates reads; a read in a helper, a patch method or an unlisted method escapes the checks on where the stop request may be looked at"
    fi
  done
}

# Automatic Processing lists 15 entry points, each moving items between a container and a station.
require_processing_entries_open_with_the_gate() {
  require_item_entries_open_with_the_gate processing_entries "$processing_dir" "$processing_module" 15
}

# R8.11: the rest of the same rule. Nothing after the first change an entry point makes may look
# at the module state, and no entry point calls another gated one anywhere in its body - a call
# after the first change, or at the head of a loop whose previous pass already moved an item,
# would check the stop request between a removal and its delivery.
#
# Arguments: the name of the entry table, the ERE of the changes its entry points make, then every
# helper file whose code runs between an entry point's gate and its delivery and so may not read
# the module state at all. An empty table fails: it would check nothing.
require_item_entries_never_recheck_after_a_change() {
  local -n _recheck_table="$1"
  local table="$1"
  local changes="$2"
  local entry
  local qualified
  local file
  local declaration
  local body
  local gate_line
  local change_line
  local late_reads
  local nested_calls
  local names
  local helper
  local helper_reads

  shift 2
  if ((${#_recheck_table[@]} == 0)); then
    fail "found no entry point in $table, so the check on what follows each first change could not be run"
    return
  fi

  names="$(entry_method_names "$table")"

  for entry in "${_recheck_table[@]}"; do
    IFS='|' read -r qualified file declaration _ <<<"$entry"
    body="$(method_body "$file" "$declaration")"
    if [[ -z "$body" ]]; then
      fail "cannot find $qualified in $file; the check on what follows its first change was not run"
      continue
    fi

    gate_line="$(printf '%s\n' "$body" | first_hit 'if *\( *! *Module *\. *Running *\) *return')"
    change_line="$(printf '%s\n' "$body" | first_hit "$changes")"
    # Line 1 is the declaration, which names the entry point itself.
    nested_calls="$(printf '%s\n' "$body" | count_hits_after 1 "(^|[^A-Za-z0-9_])($names) *\\(")"

    if lookup_failed "$gate_line" "$change_line" "$nested_calls"; then
      fail "could not search $qualified; the check on what follows its first change was not run"
      continue
    fi

    if [[ -z "$change_line" ]]; then
      fail "$qualified makes no change this check recognises: either it is not a side-effect entry point, or its change goes through a helper the list of changes does not name yet"
      continue
    fi

    if [[ -z "$gate_line" ]] || ((gate_line >= change_line)); then
      fail "$qualified must check the stop request before its first change (R1.5a): the first change is where an item is committed to the transfer"
    fi

    late_reads="$(printf '%s\n' "$body" | count_hits_after "$change_line" "$module_read")"
    if lookup_failed "$late_reads"; then
      fail "could not search $qualified after its first change; the check was not run"
      continue
    fi

    if ((late_reads != 0)); then
      fail "$qualified must not read the module state after its first change (R8.11): a stop seen there strands an item that already left where it was"
    fi

    if ((nested_calls != 0)); then
      fail "$qualified must not call a gated entry point (R8.11): that call checks the stop request again, and after a removal it can strand the item between the removal and its delivery"
    fi
  done

  for helper in "$@"; do
    if [[ ! -r "$helper" ]]; then
      fail "cannot read $helper; the check that it does not read the module state was not run"
      continue
    fi

    helper_reads="$(strip_code "$helper" | count_hits "$module_read")"
    if lookup_failed "$helper_reads"; then
      fail "could not search $helper; the check was not run"
      continue
    fi

    if ((helper_reads != 0)); then
      fail "$helper must not read the module state (R8.11): what it does runs between an entry point's gate and its delivery"
    fi
  done
}

# The claim, removal and store helpers of Automatic Processing live in Globals.cs.
require_processing_entries_never_recheck_after_a_change() {
  require_item_entries_never_recheck_after_a_change processing_entries "$processing_changes" \
    "$processing_globals"
}

# AC-S11 (ownership): an entry point changes a station only while this peer owns it, so two peers
# never process the same station at once. The ownership check has to come before the first
# change, and in its guard form - a check whose result is thrown away confirms nothing. Ownership
# moving in the middle of a call is the game's ownership model, and not something this looks at.
require_processing_entries_check_ownership_first() {
  local entry
  local qualified
  local file
  local declaration
  local body
  local owner_line
  local change_line

  for entry in "${processing_entries[@]}"; do
    IFS='|' read -r qualified file declaration _ <<<"$entry"
    body="$(method_body "$file" "$declaration")"
    if [[ -z "$body" ]]; then
      fail "cannot find $qualified in $file; the ownership check on it was not run"
      continue
    fi

    owner_line="$(printf '%s\n' "$body" | first_hit 'if *\( *! *Objects *\. *HasValidOwnership *\(')"
    change_line="$(printf '%s\n' "$body" | first_hit "$processing_changes")"

    if lookup_failed "$owner_line" "$change_line"; then
      fail "could not search $qualified for its ownership check; the check was not run"
      continue
    fi

    if [[ -z "$owner_line" || -z "$change_line" ]] || ((owner_line >= change_line)); then
      fail "$qualified must confirm that this peer owns the station before its first change (AC-S11): without it two peers can process the same station at once"
    fi
  done
}

# AC-S11 (containers): a container is changed only after it was claimed through the access-checked
# path. The claim takes ownership, so two peers do not change it at once, and with a local player
# it enforces wards, private and group chests, and containers in use; the containers offered to
# an entry point are filtered by the same rules. An entry point either claims the container itself
# before it removes or stores, or removes through TryTakeMaterial, which claims first. Changing an
# inventory directly would skip both, so an entry point file never does.
require_processing_containers_are_claimed_first() {
  local entry
  local qualified
  local file
  local declaration
  local body
  local claim_line
  local take_line
  local change_line
  local direct
  local status

  for entry in "${processing_entries[@]}"; do
    IFS='|' read -r qualified file declaration _ <<<"$entry"
    body="$(method_body "$file" "$declaration")"
    if [[ -z "$body" ]]; then
      fail "cannot find $qualified in $file; the container check on it was not run"
      continue
    fi

    claim_line="$(printf '%s\n' "$body" | first_hit '(^|[^A-Za-z0-9_])TryClaimContainer *\(')"
    take_line="$(printf '%s\n' "$body" | first_hit '(^|[^A-Za-z0-9_])TryTakeMaterial *\(')"
    change_line="$(printf '%s\n' "$body" | first_hit '(^|[^A-Za-z0-9_])(TryRemoveItem|AddItemAndGetCountDelta) *\(')"

    if lookup_failed "$claim_line" "$take_line" "$change_line"; then
      fail "could not search $qualified for its container claim; the check was not run"
      continue
    fi

    if [[ -z "$claim_line" && -z "$take_line" ]]; then
      fail "$qualified must reach its containers through TryClaimContainer or TryTakeMaterial (AC-S11): those are the paths that claim a container and apply its access rules"
      continue
    fi

    if [[ -n "$change_line" ]] && { [[ -z "$claim_line" ]] || ((claim_line >= change_line)); }; then
      fail "$qualified must claim a container before it removes from or stores into it (AC-S11): the claim is what takes ownership and applies the ward and private-chest rules"
    fi
  done

  body="$(method_body "$processing_globals" "public static bool TryTakeMaterial\\(")"
  claim_line="$(printf '%s\n' "$body" | first_hit '(^|[^A-Za-z0-9_])TryClaimContainer *\(')"
  change_line="$(printf '%s\n' "$body" | first_hit '(^|[^A-Za-z0-9_])TryRemoveItem *\(')"
  if lookup_failed "$claim_line" "$change_line"; then
    fail "could not search Logics.TryTakeMaterial; the check was not run"
  elif [[ -z "$claim_line" || -z "$change_line" ]] || ((claim_line >= change_line)); then
    fail "Logics.TryTakeMaterial must claim a container before it removes from it (AC-S11): every Craft entry point relies on it for the claim"
  fi

  body="$(method_body "$processing_globals" "public static bool TryClaimContainer\\(")"
  status=0
  printf '%s\n' "$body" | has_match 'ContainerAccess *\. *TryPrepareContainerMutationServerAware *\(' || status=$?
  case "$status" in
    0) ;;
    1) fail "Logics.TryClaimContainer must claim through ContainerAccess.TryPrepareContainerMutationServerAware (AC-S11): that is what applies the ward, private-chest and in-use rules when there is a local player" ;;
    *) fail "could not search Logics.TryClaimContainer; the check was not run" ;;
  esac

  body="$(method_body "$processing_globals" "> GetNearbyContainers\\(")"
  status=0
  printf '%s\n' "$body" | has_match 'ContainerAccess *\. *CanUseContainer *\( *player *, *x *\)' || status=$?
  case "$status" in
    0) ;;
    1) fail "Logics.GetNearbyContainers must offer only containers the local player may use, through ContainerAccess.CanUseContainer(player, x) (AC-S11)" ;;
    *) fail "could not search Logics.GetNearbyContainers; the check was not run" ;;
  esac

  for file in "$processing_dir"/*Process.cs; do
    direct="$(strip_code "$file" | count_hits '\. *(RemoveItem|AddItem) *\(')"
    if lookup_failed "$direct"; then
      fail "could not search $file for direct inventory changes; the check was not run"
      continue
    fi

    if ((direct != 0)); then
      fail "$file must change inventories only through the Logics helpers (AC-S11): a direct RemoveItem or AddItem skips the claim and its access rules"
    fi
  done
}

# ---------------------------------------------------------------------------------------
# Automatic Mapping: a migrated module whose effect is on the local minimap. It adds, moves and
# removes pins, and moves no item. Its checks read code through strip_code, as the Automatic
# Processing ones do.
# ---------------------------------------------------------------------------------------

# method_body for a file of any size. The stripped text reaches extract_block as a here-string,
# written in full before extract_block starts, so extract_block stopping at the closing brace
# cannot break a pipe that is still being written.
mapping_method_body() {
  local stripped

  stripped="$(strip_code "$1")"
  extract_block - "$2" <<<"$stripped"
}

# R1.5a: every Automatic Mapping entry point with an effect on the map opens with the module gate,
# and every postfix that takes a new object into the activation's ownership with the discovery
# gate. The game reaches them through the patches, the transpilers and the destroy callbacks left
# on objects, and on a Harmony build that cannot remove patches it does so for the rest of the
# session, so the first statement is where a stop has to take hold.
require_mapping_entries_open_with_the_gate() {
  local entry
  local qualified
  local file
  local declaration
  local gate
  local body
  local first
  local after
  local try_first

  for entry in "${mapping_entries[@]}"; do
    IFS='|' read -r qualified file declaration gate <<<"$entry"
    body="$(mapping_method_body "$file" "$declaration")"
    if [[ -z "$body" ]]; then
      fail "cannot find $qualified in $file; the check that it opens with the module gate was not run"
      continue
    fi

    first="$(printf '%s\n' "$body" | first_statement)"
    if [[ "$first" != "${gate//[[:space:]]/}" ]]; then
      fail "$qualified must open with \"$gate\" (R1.5a): the game reaches it after a stop request, and for the rest of the session where patches cannot be removed, so anything before the gate still acts on the map, and the value the gate returns is what the game goes on with; found \"$first\""
    fi
  done

  # The one entry whose gate sits inside a try: its finally releases a destroyed rock's snapshot
  # whether the module runs or not, so nothing may come before the try.
  file="$mapping_dir/StaticObjectMapping.cs"
  body="$(mapping_method_body "$file" "public static void OnObjectDestroy\\(Component ")"
  if [[ -z "$body" ]]; then
    fail "cannot find StaticObjectMapping.OnObjectDestroy in $file; the check that it opens with the module gate was not run"
    return
  fi

  first="$(printf '%s\n' "$body" | first_statement)"
  after="$(tail -n +2 <<<"$body")"
  try_first="$(extract_block - '(^|[^A-Za-z0-9_])try([^A-Za-z0-9_]|$)' <<<"$after" | first_statement)"
  if [[ "$first" != "try{" ]]; then
    fail "StaticObjectMapping.OnObjectDestroy must open with its try (R1.5a): its gate is the first statement inside the try, and a statement before the try still runs after a stop; found \"$first\""
  elif [[ "$try_first" != "if(!Module.Running)return;" ]]; then
    fail "StaticObjectMapping.OnObjectDestroy must check \"if (!Module.Running) return;\" as the first statement of its try (R1.5a): destroy callbacks stay on objects while the module is off and still reach it; found \"$try_first\""
  fi
}

# R7.3 / R1.5: the registry and allowlist subscriptions belong to the scope, and the module state is
# read as the applied state. A bind-once flag, or the lazy subscription it guarded, subscribes for
# the life of the game and leaves a disable nothing to remove.
require_mapping_leaves_no_start_up_only_state() {
  local file

  for file in "$mapping_dir"/*.cs; do
    reject_source_pattern \
      "$file" \
      "_registrySubscriptionsBound" \
      "$file must not keep a bind-once subscription flag (R7.3): the scope owns the registry and allowlist subscriptions, and a flag that outlives it keeps the next activation from subscribing again"
    reject_source_pattern \
      "$file" \
      "EnsureRegistrySubscriptions" \
      "$file must not subscribe lazily from a scan (R7.3): a subscription made there is owned by no scope, so a disable cannot remove it"
    reject_source_pattern \
      "$file" \
      "ModuleDisabled" \
      "$file must not read the module setting as a flag (R1.5): the entry points read Module.Running, which carries the stop request"
  done
}

# A destroy callback sits in a game object's Action field, where the scope cannot reach it, so each
# object gets at most one: only Module.HookDestroyOnce adds one, behind the mark that makes it
# once, and routes it to a gated entry point. The flora node's own callback is the one other, and
# it is taken off again when the node is destroyed.
require_mapping_destroy_callbacks_are_added_once() {
  local file
  local hits
  local total
  local inside
  local first
  local adds
  local removes
  local flora_total

  for file in "$mapping_dir"/*.cs; do
    if [[ "$file" == "$mapping_module" || "$file" == "$mapping_flora" ]]; then
      continue
    fi

    hits="$(strip_code "$file" | count_hits 'm_onDestroyed *\+=')"
    if lookup_failed "$hits"; then
      fail "could not search $file for destroy callbacks; the check was not run"
    elif ((hits != 0)); then
      fail "$file adds to a game object's destroy callback directly ($hits found): only Module.HookDestroyOnce may, because the scope cannot take such a callback back and only its mark keeps it to one per object"
    fi
  done

  total="$(strip_code "$mapping_module" | count_hits 'm_onDestroyed *\+=')"
  inside="$(mapping_method_body "$mapping_module" "private static void HookDestroyOnce\\(" | count_hits 'm_onDestroyed *\+=')"
  first="$(mapping_method_body "$mapping_module" "private static void HookDestroyOnce\\(" | first_statement)"
  if lookup_failed "$total" "$inside"; then
    fail "could not search $mapping_module for destroy callbacks; the check was not run"
  elif ((inside == 0 || total != inside)); then
    fail "$mapping_module must add destroy callbacks in HookDestroyOnce and nowhere else: $total add(s) in the file, $inside of them in HookDestroyOnce"
  fi

  if [[ "$first" != "if(!owner||DestroyHooked.TryGetValue(owner,out_))return;" ]]; then
    fail "HookDestroyOnce must open with \"if (!owner || DestroyHooked.TryGetValue(owner, out _)) return;\": without the mark, turning the module on again adds a second callback to every object; found \"$first\""
  fi

  adds="$(strip_code "$mapping_flora" | count_hits '\+= *OnDestroyed([^A-Za-z0-9_]|$)')"
  removes="$(strip_code "$mapping_flora" | count_hits '-= *OnDestroyed([^A-Za-z0-9_]|$)')"
  flora_total="$(strip_code "$mapping_flora" | count_hits 'm_onDestroyed *\+=')"
  if lookup_failed "$adds" "$removes" "$flora_total"; then
    fail "could not search $mapping_flora for its destroy callback; the check was not run"
    return
  fi

  if ((adds == 0 || adds != removes)); then
    fail "the flora node must take its destroy callback off again as often as it adds it ($adds added, $removes removed): turning the module off destroys the node and keeps the pickable, so every activation would leave one more callback behind"
  fi

  if ((flora_total != adds)); then
    fail "the flora node may add only its own OnDestroyed to a destroy callback: $flora_total add(s), $adds of them OnDestroyed"
  fi
}

# R7.4: the custom icons extend the minimap's icon tables, so they are registered once per
# minimap. Only IconPack.EnsureAttached registers them, and its first statement is the check that
# the minimap does not have them yet; a registration anywhere else would extend the tables again
# on every enable.
require_mapping_icons_are_registered_once_per_minimap() {
  local file
  local hits
  local declarations
  local inside
  local first

  for file in "$mapping_dir"/*.cs; do
    if [[ "$file" == "$mapping_icon_pack" ]]; then
      continue
    fi

    hits="$(strip_code "$file" | count_hits '(^|[^A-Za-z0-9_])RegisterIcons *\(')"
    if lookup_failed "$hits"; then
      fail "could not search $file for RegisterIcons; the check was not run"
    elif ((hits != 0)); then
      fail "$file registers the custom icons ($hits call(s) of RegisterIcons) (R7.4): only IconPack.EnsureAttached may, behind its check that the minimap does not have them yet"
    fi
  done

  hits="$(strip_code "$mapping_icon_pack" | count_hits '(^|[^A-Za-z0-9_])RegisterIcons *\(')"
  declarations="$(strip_code "$mapping_icon_pack" | count_hits 'void +RegisterIcons *\(')"
  inside="$(mapping_method_body "$mapping_icon_pack" "public static void EnsureAttached\\(" | count_hits '(^|[^A-Za-z0-9_])RegisterIcons *\(')"
  first="$(mapping_method_body "$mapping_icon_pack" "public static void EnsureAttached\\(" | first_statement)"
  if lookup_failed "$hits" "$declarations" "$inside"; then
    fail "could not search $mapping_icon_pack for RegisterIcons; the check was not run"
  elif ((declarations != 1 || inside != 1 || hits - declarations != 1)); then
    fail "IconPack must register the custom icons from EnsureAttached and nowhere else (R7.4): $((hits - declarations)) call(s) of RegisterIcons, $inside of them in EnsureAttached"
  fi

  if [[ "$first" != "if(!map||map.GetInstanceID()==_attachedMinimapId)return;" ]]; then
    fail "IconPack.EnsureAttached must open with \"if (!map || map.GetInstanceID() == _attachedMinimapId) return;\" (R7.4): turning the module on again for the same minimap would otherwise extend its icon tables a second time; found \"$first\""
  fi
}

# The pin rule. A disable moves the vehicle pins to their vehicles, then removes the unsaved pins
# the module holds, and only then drops the caches that say which pins it holds. Nothing in the
# module clears a pin's saved flag or writes its owner: only the user's click saves a pin, and
# the owner is what makes a pin shared.
require_mapping_disable_keeps_the_pin_rule() {
  local body
  local flush
  local dynamic
  local static
  local reset
  local file
  local cleared
  local owned

  body="$(mapping_method_body "$mapping_module" "public void Disable\\(ModuleScope scope\\)")"
  if [[ -z "$body" ]]; then
    fail "$mapping_module must define Disable(ModuleScope)"
    return
  fi

  flush="$(printf '%s\n' "$body" | first_hit 'DynamicObjectMapping *\. *MoveVehiclePinsToTargets *\(')"
  dynamic="$(printf '%s\n' "$body" | first_hit 'DynamicObjectMapping *\. *RemoveCachedPins *\(')"
  static="$(printf '%s\n' "$body" | first_hit 'StaticObjectMapping *\. *RemoveCachedPins *\(')"
  reset="$(printf '%s\n' "$body" | first_hit '(^|[^A-Za-z0-9_.])ResetSession *\(')"

  if lookup_failed "$flush" "$dynamic" "$static" "$reset"; then
    fail "could not search the mapping Disable for its pin handling; the check was not run"
  elif [[ -z "$flush" || -z "$dynamic" || -z "$static" || -z "$reset" ]]; then
    fail "the mapping Disable must move the vehicle pins, remove the unsaved pins both scans hold and reset the session"
  else
    if ((flush >= dynamic || flush >= static)); then
      fail "the mapping Disable must move the vehicle pins to their vehicles before it removes the cached pins: the removal lets go of the vehicle pins, and a pin let go of first stays saved at a point on its vehicle's way"
    fi

    if ((dynamic >= reset || static >= reset)); then
      fail "the mapping Disable must remove the cached pins before it resets the session: the reset forgets which pins the module holds, and an unsaved pin forgotten first stays on the map"
    fi
  fi

  for file in "$mapping_dir"/*.cs; do
    cleared="$(strip_code "$file" | count_hits 'm_save *= *false')"
    owned="$(strip_code "$file" | count_hits 'm_ownerID *=([^=]|$)')"
    if lookup_failed "$cleared" "$owned"; then
      fail "could not search $file for pin flag writes; the check was not run"
      continue
    fi

    if ((cleared != 0)); then
      fail "$file clears a pin's saved flag ($cleared found): only the user's click decides that a pin is kept, and this module removes unsaved pins"
    fi

    if ((owned != 0)); then
      fail "$file writes a pin's owner ($owned found): the owner is what makes a pin shared, and this module leaves shared pins alone"
    fi
  done
}

# R7.2: the console commands stay available whatever the modules are set to, and none of them
# writes a setting. printobjects reads Automatic Mapping's applied state, says when it is not
# running, and still lists; each language defines that line.
require_console_commands_never_write_a_setting() {
  local file
  local writes
  local language_file

  for file in Automatics/ConsoleCommands/*.cs; do
    writes="$(strip_code "$file" | count_hits '\. *Value *=([^=>]|$)')"
    if lookup_failed "$writes"; then
      fail "could not search $file for config writes; the check was not run"
    elif ((writes != 0)); then
      fail "$file assigns a config value ($writes found) (R7.2 / R1.3b): a console command may read a module's state but never turns a module on or off for the user"
    fi
  done

  require_source_pattern \
    "$printobjects_file" \
    "\"@command_printobjects_message_mapping_not_running\"" \
    "printobjects must print @command_printobjects_message_mapping_not_running when Automatic Mapping is not running (R7.2)"

  for language_file in Automatics/Languages/en_us.json Automatics/Languages/ja_jp.json; do
    require_source_pattern \
      "$language_file" \
      "\"@command_printobjects_message_mapping_not_running\"" \
      "$language_file must define @command_printobjects_message_mapping_not_running (R9.2)"
  done
}

require_setting_changed_handler_only_requests
require_pump_contains_its_failures
require_pump_containment_starts_at_the_top
require_request_transition_shape
require_unpatch_calls_only_in_the_scope
require_deferred_returns_are_guarded
require_failed_scopes_are_kept
require_residual_is_cleared_only_after_a_clean_teardown
require_recovery_retries_before_it_clears
require_token_removes_before_it_clears
require_scope_registers_before_it_applies
require_patch_removal_registered_once
require_refusal_release_isolates_the_halves
require_orphan_walk_is_identity_based

require_every_yield_is_guarded
require_the_hover_target_is_revalidated
require_the_resume_point_is_documented
require_synchronous_coroutine_shutdown
require_the_coroutine_starts_on_enable
require_ownership_is_a_per_frame_question
require_the_frame_hooks_check_live
require_shared_buffers_are_cleared_on_disable
require_migrated_modules_bind_unconditionally
require_migrated_modules_read_no_start_up_flag
require_unmigrated_modules_keep_their_early_return
require_event_wiring_goes_through_the_scope "Hooks\\.[A-Za-z]+" "hook subscriptions"
require_event_wiring_goes_through_the_scope "OnCacheAdded" "cache-added subscriptions"
require_event_wiring_goes_through_the_scope "OnCacheRemoved" "cache-removed subscriptions"
require_event_wiring_goes_through_the_scope "RegistryChanged" "registry-change subscriptions"
require_event_wiring_goes_through_the_scope "StaticAllowlistChanged" "allowlist-change subscriptions"
require_patching_goes_through_the_scope
require_components_are_created_through_the_scope
require_attach_paths_recheck_the_effective_state "$pickup_module" \
  "private static void OnPlayerAwake" "internal static void AttachCache"
require_attach_paths_recheck_the_effective_state "$mapping_module" \
  "internal static void AttachCache" "internal static void AttachMineRock5" \
  "private static void AttachFlora"
require_every_patch_is_gated
require_enable_acquires_in_order "$pickup_module"
require_enable_acquires_in_order "$mapping_module"
require_disable_lowers_the_flag_first "$pickup_module"
require_disable_lowers_the_flag_first "$mapping_module"
require_reconciliation_enumerates_the_scene
require_no_warning_suppressions
require_every_module_starts_disabled
require_the_migrator_keeps_every_module_running
require_the_defaults_marker_is_hidden_and_read_first
require_the_config_is_replaced_atomically
require_the_runtime_toggle_strings_exist

require_processing_enable_and_disable_shape
require_processing_session_is_reset
require_processing_player_update_is_gated
require_processing_has_no_component_machinery
require_processing_patches_reach_only_gated_entries
require_processing_entries_open_with_the_gate
require_processing_entries_never_recheck_after_a_change
require_processing_entries_check_ownership_first
require_processing_containers_are_claimed_first

require_mapping_entries_open_with_the_gate
require_mapping_leaves_no_start_up_only_state
require_mapping_destroy_callbacks_are_added_once
require_mapping_icons_are_registered_once_per_minimap
require_mapping_disable_keeps_the_pin_rule
require_console_commands_never_write_a_setting

# Automatic Door: one patch, four subscriptions, one scoped component and seven gated entries.
# Its player-awake hook and four component/config callbacks only maintain bookkeeping, so they
# deliberately do not use the side-effect gate.
require_door_entries_and_callbacks() {
  local row
  local name
  local file
  local declaration
  local expected
  local body
  local first

  if ((${#door_entries[@]} != 7)); then
    fail "the Door entry table must name exactly the seven audited gated entries: found ${#door_entries[@]}"
  fi
  if ((${#door_callbacks[@]} != 5)); then
    fail "the Door callback table must name exactly the five audited gate-free bookkeeping entries: found ${#door_callbacks[@]}"
  fi

  for row in "${door_entries[@]}"; do
    IFS='|' read -r name file declaration expected <<<"$row"
    body="$(method_body "$file" "$declaration")"
    if [[ -z "$body" ]]; then
      fail "$name must remain an audited Door entry in $file; its first-statement check could not run"
      continue
    fi
    first="$(printf '%s\n' "$body" | first_statement)"
    expected="${expected// /}"
    if [[ "$first" != "$expected" ]]; then
      fail "$name must open with the audited module gate; expected \"$expected\", found \"$first\""
    fi
  done

  for row in "${door_callbacks[@]}"; do
    IFS='|' read -r name file declaration expected <<<"$row"
    body="$(method_body "$file" "$declaration")"
    if [[ -z "$body" ]]; then
      fail "$name must remain an audited gate-free Door bookkeeping entry in $file; its first-statement check could not run"
      continue
    fi
    first="$(printf '%s\n' "$body" | first_statement)"
    expected="${expected// /}"
    if [[ "$first" != "$expected" ]]; then
      fail "$name must open with its audited bookkeeping statement and no module gate; expected \"$expected\", found \"$first\""
    fi
  done
}

require_door_acquisition_and_component_ownership() {
  local body
  local found

  body="$(method_body "$door_module" "private static void ReconcileDoors\\(")"
  if [[ -z "$body" ]]; then
    fail "the Door module must define ReconcileDoors; its activation scan could not be checked"
    return
  fi

  for pattern in \
    'FindObjectsByType *< *Door *> *\( *FindObjectsInactive *\. *Include *, *FindObjectsSortMode *\. *None *\)' \
    'Objects *\. *GetZNetView *\(' \
    'GetZDO *\( *\) *== *null' \
    'scope *\. *Attach *< *AutomaticDoor *> *\('; do
    found="$(printf '%s\n' "$body" | count_hits "$pattern")"
    if lookup_failed "$found"; then
      fail "could not inspect the Door activation scan; the component ownership check was not run"
      return
    fi
    if ((found != 1)); then
      fail "ReconcileDoors must enumerate inactive Doors, require a ZDO and attach AutomaticDoor through the supplied scope exactly once; pattern '$pattern' occurred $found time(s)"
    fi
  done

  found="$(printf '%s\n' "$body" | count_hits 'AttachAutomaticDoor *\(')"
  if lookup_failed "$found"; then
    fail "could not inspect ReconcileDoors for the runtime attach helper; the check was not run"
  elif ((found != 0)); then
    fail "ReconcileDoors must attach through its supplied scope, not AttachAutomaticDoor: Active is raised only after reconciliation, so the runtime helper would reject every scanned Door"
  fi

  body="$(method_body "$door_module" "internal static void AttachAutomaticDoor\\(")"
  found="$(printf '%s\n' "$body" | count_hits 'CurrentScope *\. *TryAttach *< *AutomaticDoor *> *\(')"
  if lookup_failed "$found"; then
    fail "could not inspect the Door runtime attach path; the check was not run"
  elif ((found != 1)); then
    fail "AttachAutomaticDoor must attach through CurrentScope.TryAttach exactly once: found $found"
  fi

  body="$(method_body "$door_patches" "private static void Door_Awake_Postfix\\(")"
  found="$(printf '%s\n' "$body" | count_hits 'Module *\. *AttachAutomaticDoor *\(')"
  if lookup_failed "$found"; then
    fail "could not inspect the Door Awake postfix; the runtime attach routing check was not run"
  elif ((found != 1)); then
    fail "the Door Awake postfix must route exactly once through Module.AttachAutomaticDoor: found $found"
  fi
  found="$(printf '%s\n' "$body" | count_hits '(CurrentScope *\. *(Try)?Attach|scope *\. *(Try)?Attach) *<')"
  if lookup_failed "$found"; then
    fail "could not inspect the Door Awake postfix for a direct scope attach; the check was not run"
  elif ((found != 0)); then
    fail "the Door Awake postfix must not attach directly; it has to go through the gated Module.AttachAutomaticDoor path"
  fi

  require_source_pattern "$door_component" 'class AutomaticDoor *: *MonoBehaviour *, *IScopedComponent' \
    "AutomaticDoor must implement IScopedComponent so its activation scope can stamp and detach it"
  require_source_pattern "$door_component" 'public int ScopeGeneration *\{ *get *; *set *; *\}' \
    "AutomaticDoor must expose its scope generation stamp"
}

require_door_cleanup_is_non_destructive() {
  local body
  local compact

  body="$(method_body "$door_module" "public void Disable\\(ModuleScope scope\\)")"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'publicvoidDisable(ModuleScopescope){Active=false;Generation=0;CurrentScope=null;ResetState();}' ]]; then
    fail "Door Disable must only lower Active, clear generation and scope, then ResetState; it must never operate a Door"
  fi

  body="$(method_body "$door_module" "private static void ResetState\\(\\)")"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'privatestaticvoidResetState(){_openTimer=0f;_closeTimer=0f;PlayerMotionStates.Clear();ActivePlayerStateKeys.Clear();PlayerStateKeysBuffer.Clear();}' ]]; then
    fail "Door ResetState must reset both timers and clear all three player-motion collections, and do nothing else"
  fi

  require_exact_method_body "$door_component" 'private void Awake\(\)' \
    'privatevoidAwake(){_door=GetComponent<Door>();_transform=transform;Objects.GetZNetView(_door,out_zNetView);_allowAutomaticDoorDirty=true;AllInstance.Add(this);}' \
    "AutomaticDoor.Awake must only acquire its borrowed references, mark its allow-state dirty and register the component"

  body="$(method_body "$door_component" "private void Unregister\\(\\)")"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'privatevoidUnregister(){AllInstance.Remove(this);_zNetView=null;_transform=null;_door=null;}' ]]; then
    fail "AutomaticDoor.Unregister must only remove its static registration and borrowed references; it must never change Door state"
  fi

  # The dispatch table only checks each callback's first statement, which a later statement
  # in the same body can undermine: e.g. an open/close call added right after Unregister();
  # would still pass a first-statement check while quietly touching a door on teardown or
  # bookkeeping. Every gate-free bookkeeping callback in door_callbacks is pinned to its
  # full, whitespace-collapsed body instead, the same way Disable/ResetState/Unregister are
  # pinned above.
  body="$(method_body "$door_component" "public void OnScopeDetaching\\(\\)")"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'publicvoidOnScopeDetaching(){Unregister();}' ]]; then
    fail "AutomaticDoor.OnScopeDetaching must do nothing but Unregister; it must never change a door on teardown"
  fi

  body="$(method_body "$door_component" "private void OnDestroy\\(\\)")"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'privatevoidOnDestroy(){Unregister();}' ]]; then
    fail "AutomaticDoor.OnDestroy must do nothing but Unregister; it must never change a door on teardown"
  fi

  body="$(method_body "$door_module" "private static void OnPlayerAwake\\(")"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'privatestaticvoidOnPlayerAwake(Playerplayer,ZNetViewnview){ResetState();}' ]]; then
    fail "Door OnPlayerAwake must do nothing but ResetState; it must never touch a door"
  fi

  body="$(method_body "$door_module" "public void SetStopRequested\\(bool value\\)")"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'publicvoidSetStopRequested(boolvalue){StopRequested=value;}' ]]; then
    fail "Door SetStopRequested must do nothing but set the flag; it must never touch a door"
  fi

  body="$(method_body "$door_component" "public static void MarkAllowAutomaticDoorDirty\\(\\)")"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'publicstaticvoidMarkAllowAutomaticDoorDirty(){if(AllInstance.Count==0)return;foreach(varautomaticDoorinAllInstance)if(automaticDoor)automaticDoor._allowAutomaticDoorDirty=true;}' ]]; then
    fail "AutomaticDoor.MarkAllowAutomaticDoorDirty must do nothing but mark every registered instance dirty; it must never touch a door"
  fi

  require_component_declares_only_allowed_unity_messages "$door_component" AutomaticDoor
}

require_door_access_checks_precede_opening() {
  local body
  local valid
  local interact
  local open

  body="$(method_body "$door_component" "private void TryOpen\\(Player ")"
  valid="$(printf '%s\n' "$body" | first_match_line 'IsValid *\( *\)')"
  interact="$(printf '%s\n' "$body" | first_match_line 'CanInteract *\( *player *\)')"
  open="$(printf '%s\n' "$body" | first_match_line 'OpenDoor *\(')"
  if lookup_failed "$valid" "$interact" "$open" || [[ -z "$valid" || -z "$interact" || -z "$open" ]]; then
    fail "could not locate IsValid, CanInteract and OpenDoor in Door.TryOpen; the ownership and ward ordering check was not run"
  elif ((valid >= interact || interact >= open)); then
    fail "Door.TryOpen must pass ownership and interaction/ward checks before OpenDoor"
  fi
  if ! printf '%s\n' "$body" | has_match 'if *\( *!CanInteract *\( *player *\) *\) *return *;'; then
    fail "Door.TryOpen must return when CanInteract rejects the player before it opens the door"
  fi

  body="$(method_body "$door_component" "private void TryClose\\(")"
  valid="$(printf '%s\n' "$body" | first_match_line 'IsValid *\( *\)')"
  interact="$(printf '%s\n' "$body" | first_match_line 'CanInteract *\( *player *\)')"
  open="$(printf '%s\n' "$body" | first_match_line 'OpenDoor *\(')"
  if lookup_failed "$valid" "$interact" "$open" || [[ -z "$valid" || -z "$interact" || -z "$open" ]]; then
    fail "could not locate IsValid, CanInteract and OpenDoor in AutomaticDoor.TryClose; the ownership and ward ordering check was not run"
  elif ((valid >= interact || interact >= open)); then
    fail "AutomaticDoor.TryClose must pass ownership and interaction/ward checks before OpenDoor"
  fi
  if ! printf '%s\n' "$body" | has_match 'if *\( *!canInteract *\) *continue *;'; then
    fail "AutomaticDoor.TryClose must skip players that CanInteract rejects"
  fi
  if ! printf '%s\n' "$body" | has_match 'if *\( *!closestInteractablePlayer *\) *return *;'; then
    fail "AutomaticDoor.TryClose must return without closing when no player can interact"
  fi

  body="$(method_body "$door_component" "private bool CanInteract\\(")"
  if ! printf '%s\n' "$body" | has_match 'if *\( *_door *\. *m_checkGuardStone *&& *!CheckWardAccess *\( *player *, *_transform *\. *position *\) *\) *return false *;'; then
    fail "AutomaticDoor.CanInteract must reject a player denied by the ward check"
  fi

  require_source_pattern "$door_component" 'return Objects *\. *HasValidOwnership *\( *_zNetView *\) *;' \
    "AutomaticDoor.IsValid must keep ownership validation through Objects.HasValidOwnership"
}

require_door_registration_contract() {
  local body

  require_source_pattern "$door_module" 'public string Name *=> *"automatic-door" *;' \
    "the Door module registry name must remain automatic-door"
  require_source_pattern "$door_module" 'public string ConfigSection *=> *"automatic_door" *;' \
    "the Door module config section must remain automatic_door"
  require_source_pattern "$door_module" 'public bool SupportsRuntimeToggle *=> *true *;' \
    "the Door module must declare runtime toggle support"
  require_source_pattern "$door_module" '\[AutomaticsInitializer\(1\)\]' \
    "the Door module initializer order must remain 1"

  body="$(method_body "$door_module" "private static void Initialize\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the Door module must define its initializer; the registry check could not run"
  elif ! printf '%s\n' "$body" | has_match 'ModuleRegistry *\. *Register *\( *Instance *\) *;'; then
    fail "the Door initializer must register Module.Instance instead of directly initializing config and patches"
  fi
}

require_door_smoke_descriptor() {
  local body
  local found
  local stripped

  stripped="$(strip_code "$door_smoke" keep)"
  body="$(printf '%s\n' "$stripped" | extract_block - "private static ModuleDescriptor DescribeDoor\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the lifecycle smoke must define a Door descriptor; its probes could not be checked"
    return
  fi

  for row in \
    'Module *= *DoorModule *\. *Instance@@the Door descriptor must use the registered Door module' \
    'PatchTypes *= *new\[\] *\{ *typeof *\( *DoorPatches *\) *\}@@the Door descriptor must declare its one patch type' \
    'Probe *\. *Handlers *\( *"Hooks.OnPlayerAwake"[^;]*, *1 *\)@@the Door descriptor must probe its OnPlayerAwake handler' \
    'Probe *\. *Handlers *\( *"Hooks.OnPlayerUpdate"[^;]*, *1 *\)@@the Door descriptor must probe its OnPlayerUpdate handler' \
    'Probe *\. *Handlers *\( *"Hooks.OnPlayerFixedUpdate"[^;]*, *1 *\)@@the Door descriptor must probe its OnPlayerFixedUpdate handler' \
    'Probe *\. *Handlers *\( *"Hooks.OnDedicatedServerFixedUpdate"[^;]*, *1 *\)@@the Door descriptor must probe its dedicated-server handler' \
    'Probe *\. *Counter *\( *"door.OnPlayerAwake.handler" *, *\(\) *=> *CountHandlersOf *\( *Hooks *\. *OnPlayerAwake *, *typeof *\( *DoorModule *\) *, *"OnPlayerAwake" *\) *, *OffExpectation *\. *Zero *, *0 *, *\(\) *=> *1 *\)@@the Door descriptor must probe its own OnPlayerAwake handler occurrence, separately from the hook total, and require it back at exactly 1' \
    'Probe *\. *Counter *\( *"door.OnPlayerUpdate.handler" *, *\(\) *=> *CountHandlersOf *\( *Hooks *\. *OnPlayerUpdate *, *typeof *\( *DoorModule *\) *, *"OnPlayerUpdate" *\) *, *OffExpectation *\. *Zero *, *0 *, *\(\) *=> *1 *\)@@the Door descriptor must probe its own OnPlayerUpdate handler occurrence, separately from the hook total, and require it back at exactly 1' \
    'Probe *\. *Counter *\( *"door.OnPlayerFixedUpdate.handler" *, *\(\) *=> *CountHandlersOf *\( *Hooks *\. *OnPlayerFixedUpdate *, *typeof *\( *DoorModule *\) *, *"OnPlayerFixedUpdate" *\) *, *OffExpectation *\. *Zero *, *0 *, *\(\) *=> *1 *\)@@the Door descriptor must probe its own OnPlayerFixedUpdate handler occurrence, separately from the hook total, and require it back at exactly 1' \
    'Probe *\. *Counter *\( *"door.OnDedicatedServerFixedUpdate.handler" *, *\(\) *=> *CountHandlersOf *\( *Hooks *\. *OnDedicatedServerFixedUpdate *, *typeof *\( *DoorModule *\) *, *"OnDedicatedServerFixedUpdate" *\) *, *OffExpectation *\. *Zero *, *0 *, *\(\) *=> *1 *\)@@the Door descriptor must probe its own dedicated-server handler occurrence, separately from the hook total, and require it back at exactly 1' \
    'Probe *\. *Components *< *DoorComponent *> *\( *CountEligibleDoors *\)@@the Door descriptor must compare every component with the current eligible Door count' \
    'Probe *\. *Counter *\( *"RegisteredDoorCount"[^;]*CountActiveDoorComponents *\)@@the Door descriptor must compare its registration count with active components, with no allow-zero override'; do
    local pattern="${row%%@@*}"
    local message="${row#*@@}"
    found="$(printf '%s\n' "$body" | count_hits "$pattern")"
    if lookup_failed "$found"; then
      fail "could not inspect the Door lifecycle descriptor; the check behind '$message' was not run"
    elif ((found != 1)); then
      fail "$message exactly once: found $found"
    fi
  done

  for extra in state_snapshot_captured door_id_set_unchanged door_state_unchanged components_current_generation; do
    found="$(printf '%s\n' "$body" | count_hits "new ExtraCheck *\\( *\"$extra\"")"
    if lookup_failed "$found"; then
      fail "could not inspect the Door lifecycle descriptor for $extra; the check was not run"
    elif ((found != 1)); then
      fail "the Door descriptor must include the $extra read-only check exactly once: found $found"
    fi
  done

  require_source_pattern "$door_smoke" 'Dictionary *< *ZDOID *, *int *> *DoorStates *\(' \
    "the Door lifecycle smoke must normalize its state fingerprint by ZDOID"
  require_source_pattern "$door_smoke" 'zdo *\. *GetInt *\( *"state" *\)' \
    "the Door lifecycle smoke fingerprint must read each Door ZDO state"
}

require_door_existing_smokes_follow_runtime_state() {
  local body
  local stripped

  stripped="$(strip_code "$door_world_smoke" keep)"
  body="$(printf '%s\n' "$stripped" | extract_block - "private static CheckResult CheckDoorOpenSetsZdoState\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the world smoke Door check could not be extracted; its runtime-state guard was not checked"
  else
    if ! printf '%s\n' "$body" | has_match 'ModuleRegistry *\. *GetState *\( *"automatic-door" *\) *!= *ModuleState *\. *Enabled'; then
      fail "the world smoke Door check must skip unless the registry reports automatic-door Enabled"
    fi
    if ! printf '%s\n' "$body" | has_match 'AutomaticDoor *\. *Module *\. *Running'; then
      fail "the world smoke Door check must also require the Door module Running predicate"
    fi
    if printf '%s\n' "$body" | has_match 'Config *\. *ModuleDisabled'; then
      fail "the world smoke Door check must not read the removed start-up-only ModuleDisabled flag"
    fi
  fi

  stripped="$(strip_code "$door_startup_smoke" keep)"
  body="$(printf '%s\n' "$stripped" | extract_block - "private CheckResult CheckDoorInternalNameResolution\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the startup Door registry check could not be extracted; its unconditional-bind contract was not checked"
  elif printf '%s\n' "$body" | has_match 'CheckResult *\. *Skip|ModuleDisabled'; then
    fail "the startup Door registry check must not SKIP because the module is disabled; its config binds unconditionally"
  fi
}

require_mining_state_and_registration() {
  local body
  local compact

  require_source_pattern "$mining_module" 'internal sealed class Module *: *IAutomaticsModule' \
    "the Mining module must implement IAutomaticsModule"
  require_source_pattern "$mining_module" 'public string Name *=> *"automatic-mining" *;' \
    "the Mining module registry name must remain automatic-mining"
  require_source_pattern "$mining_module" 'public string ConfigSection *=> *"automatic_mining" *;' \
    "the Mining module config section must remain automatic_mining"
  require_source_pattern "$mining_module" 'public bool SupportsRuntimeToggle *=> *true *;' \
    "the Mining module must declare runtime toggle support"
  require_source_pattern "$mining_module" '\[AutomaticsInitializer\(6\)\]' \
    "the Mining module initializer order must remain 6"

  require_single_code_match "$mining_module" \
    'internal static bool Running *=> *Active *&& *!StopRequested *;' \
    "Mining Running must be exactly Active && !StopRequested"
  require_single_code_match "$mining_module" \
    'internal static bool Discovering *=> *Active *&& *CurrentScope *!= *null *;' \
    "Mining Discovering must be exactly Active && CurrentScope != null"

  body="$(method_body "$mining_module" 'public void SetStopRequested\(bool value\)')"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'publicvoidSetStopRequested(boolvalue){StopRequested=value;}' ]]; then
    fail "Mining SetStopRequested must do nothing but assign the requested value"
  fi

  body="$(method_body "$mining_module" 'private static void Initialize\(\)')"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'privatestaticvoidInitialize(){ModuleRegistry.Register(Instance);}' ]]; then
    fail "the Mining initializer must only register Module.Instance"
  fi
}

require_mining_entries_and_attach_paths() {
  local body
  local stripped
  local found
  local file
  local total=0
  local declarations=0
  local inside=0

  require_item_entries_open_with_the_gate mining_entries "$mining_dir" "$mining_module" 3
  require_item_entries_never_recheck_after_a_change mining_entries \
    'AutomaticMining *\. *TryMining *\(|AreaMining *\. *BeginSwing *\(' \
    "$mining_component" "$mining_config" "$mining_area"

  stripped="$(strip_code "$mining_module")"
  found="$(printf '%s\n' "$stripped" | count_hits "$module_running_read")"
  if lookup_failed "$found"; then
    fail "could not search the Mining module for its Running reads; the check was not run"
  elif ((found != 2)); then
    fail "Mining must read Module.Running only at its two player-hook entry gates: found $found read(s)"
  fi

  for method in OnPlayerUpdate OnPlayerFixedUpdate; do
    found="$(printf '%s\n' "$stripped" | count_hits "(^|[^A-Za-z0-9_])$method *\\(")"
    if lookup_failed "$found"; then
      fail "could not search the Mining module for calls to $method; the nested-entry check was not run"
    elif ((found != 1)); then
      fail "Mining.$method must only occur as its declaration and must not be called as a nested gated entry: found $found occurrence(s)"
    fi
  done

  found="$(printf '%s\n' "$stripped" | count_hits '(^|[^A-Za-z0-9_])Discovering([^A-Za-z0-9_]|$)')"
  if lookup_failed "$found"; then
    fail "could not search the Mining module for Discovering reads; the check was not run"
  elif ((found != 2)); then
    fail "Mining Discovering must occur only in its predicate and AttachMineral gate: found $found occurrence(s)"
  fi

  # The file list is taken with its exit status: rg reports an unreadable directory on stderr,
  # still prints the files it could reach and exits 2, and a partial list read through a
  # process substitution would count every reachable call and pass.
  local files
  local list_status=0
  files="$(rg --files Automatics smoke -g '*.cs')" || list_status=$?
  if ((list_status != 0)) || [[ -z "$files" ]]; then
    fail "could not list the C# files under Automatics and smoke (rg exit $list_status); the Mining call-site check was not run"
    return
  fi

  while IFS= read -r file; do
    [[ "$file" == Automatics/Libraries/* ]] && continue
    stripped="$(strip_code "$file")"
    found="$(printf '%s\n' "$stripped" | count_hits '(^|[^A-Za-z0-9_])TryMining *\(')"
    if lookup_failed "$found"; then
      fail "could not search $file for TryMining; the Mining call-site check was not run"
      return
    fi
    total=$((total + found))

    found="$(printf '%s\n' "$stripped" | count_hits 'public *static *void *TryMining *\(')"
    if lookup_failed "$found"; then
      fail "could not distinguish the TryMining declaration in $file; the Mining call-site check was not run"
      return
    fi
    declarations=$((declarations + found))
  done <<<"$files"

  for method in OnPlayerUpdate OnPlayerFixedUpdate; do
    body="$(method_body "$mining_module" "private static void $method\\(")"
    found="$(printf '%s\n' "$body" | count_hits 'AutomaticMining *\. *TryMining *\(')"
    if lookup_failed "$found"; then
      fail "could not inspect Mining.$method for its TryMining call; the call-site check was not run"
      return
    fi
    inside=$((inside + found))
  done

  if ((declarations != 1 || total - declarations != 2 || inside != 2)); then
    fail "TryMining must have exactly two calls across Automatics (excluding Libraries) and smoke, both qualified calls inside Module.OnPlayerUpdate and Module.OnPlayerFixedUpdate; found $((total - declarations)) call(s), $declarations declaration(s), and $inside call(s) in those two bodies"
  fi
}

require_mining_component_ownership() {
  local body
  local found
  local type

  body="$(method_body "$mining_module" 'private static void ReconcileMinerals\(ModuleScope scope\)')"
  if [[ -z "$body" ]]; then
    fail "the Mining module must define ReconcileMinerals; its activation scan could not be checked"
    return
  fi

  for type in Destructible MineRock MineRock5; do
    found="$(printf '%s\n' "$body" | count_hits "FindObjectsByType *< *$type *> *\\( *FindObjectsInactive *\\. *Include *, *FindObjectsSortMode *\\. *None *\\)")"
    if lookup_failed "$found"; then
      fail "could not inspect the Mining activation scan for $type; the check was not run"
    elif ((found != 1)); then
      fail "ReconcileMinerals must enumerate inactive $type objects exactly once: found $found"
    fi
  done

  for pattern in 'Objects *\. *GetZNetView *\(' 'GetZDO *\( *\) *== *null' \
    'scope *\. *Attach *< *AutomaticMining *> *\('; do
    found="$(printf '%s\n' "$body" | count_hits "$pattern")"
    if lookup_failed "$found"; then
      fail "could not inspect the Mining activation scan; the ZDO and scope ownership check was not run"
    elif ((found != 3)); then
      fail "ReconcileMinerals must validate and attach each of its three target types through the supplied scope; pattern '$pattern' occurred $found time(s)"
    fi
  done

  body="$(method_body "$mining_module" 'internal static void AttachMineral\(GameObject target\)')"
  found="$(printf '%s\n' "$body" | count_hits 'CurrentScope *\. *TryAttach *< *AutomaticMining *> *\(')"
  if lookup_failed "$found"; then
    fail "could not inspect the Mining runtime attach path; the check was not run"
  elif ((found != 1)); then
    fail "AttachMineral must attach through CurrentScope.TryAttach exactly once: found $found"
  fi

  found="$(strip_code "$mining_patches" | count_hits 'Module *\. *AttachMineral *\(')"
  if lookup_failed "$found"; then
    fail "could not inspect the Mining postfix routing; the check was not run"
  elif ((found != 3)); then
    fail "the three Mining postfixes must each route through Module.AttachMineral: found $found route(s)"
  fi

  require_source_pattern "$mining_component" \
    'class AutomaticMining *: *MonoBehaviour *, *IScopedComponent' \
    "AutomaticMining must implement IScopedComponent so its activation scope can stamp and detach it"
  require_source_pattern "$mining_component" 'public int ScopeGeneration *\{ *get *; *set *; *\}' \
    "AutomaticMining must expose its scope generation stamp"
}

require_mining_gate_free_bodies_are_fixed() {
  require_exact_method_body "$mining_component" 'private void Awake\(\)' \
    'privatevoidAwake(){_component=GetDestructibleComponent();if(!_component)thrownewException("");if(!Objects.GetZNetView(_component,out_zNetView))thrownewException("");AllInstance.Add(this);}' \
    "AutomaticMining.Awake must only acquire its destructible and ZNetView references and register the component"

  require_exact_method_body "$mining_patches" 'private static void Destructible_Awake_Postfix\(' \
    'privatestaticvoidDestructible_Awake_Postfix(Destructible__instance,ZNetView___m_nview){if(___m_nview&&___m_nview.GetZDO()!=null)Module.AttachMineral(__instance.gameObject);}' \
    "the Destructible Mining postfix must only attach a networked mineral through Module.AttachMineral"
  require_exact_method_body "$mining_patches" 'private static void MineRock_Start_Postfix\(' \
    'privatestaticvoidMineRock_Start_Postfix(MineRock__instance,ZNetView___m_nview){if(___m_nview&&___m_nview.GetZDO()!=null)Module.AttachMineral(__instance.gameObject);}' \
    "the MineRock Mining postfix must only attach a networked mineral through Module.AttachMineral"
  require_exact_method_body "$mining_patches" 'private static void MineRock5_Awake_Postfix\(' \
    'privatestaticvoidMineRock5_Awake_Postfix(MineRock5__instance,ZNetView___m_nview){if(___m_nview&&___m_nview.GetZDO()!=null)Module.AttachMineral(__instance.gameObject);}' \
    "the MineRock5 Mining postfix must only attach a networked mineral through Module.AttachMineral"

  require_exact_method_body "$mining_module" 'internal static void AttachMineral\(GameObject target\)' \
    'internalstaticvoidAttachMineral(GameObjecttarget){if(!Discovering)return;CurrentScope.TryAttach<AutomaticMining>(target,out_);}' \
    "Mining AttachMineral must only recheck discovery state and attach through the current scope"
  require_exact_method_body "$mining_module" 'private static void ReconcileMinerals\(ModuleScope scope\)' \
    'privatestaticvoidReconcileMinerals(ModuleScopescope){foreach(vardestructibleinUnityEngine.Object.FindObjectsByType<Destructible>(FindObjectsInactive.Include,FindObjectsSortMode.None)){if(!Objects.GetZNetView(destructible,outvarzNetView))continue;if(zNetView.GetZDO()==null)continue;scope.Attach<AutomaticMining>(destructible.gameObject);}foreach(varmineRockinUnityEngine.Object.FindObjectsByType<MineRock>(FindObjectsInactive.Include,FindObjectsSortMode.None)){if(!Objects.GetZNetView(mineRock,outvarzNetView))continue;if(zNetView.GetZDO()==null)continue;scope.Attach<AutomaticMining>(mineRock.gameObject);}foreach(varmineRock5inUnityEngine.Object.FindObjectsByType<MineRock5>(FindObjectsInactive.Include,FindObjectsSortMode.None)){if(!Objects.GetZNetView(mineRock5,outvarzNetView))continue;if(zNetView.GetZDO()==null)continue;scope.Attach<AutomaticMining>(mineRock5.gameObject);}}' \
    "Mining ReconcileMinerals must remain the audited networked-object scan and must not start mining"
}

require_mining_cleanup_contract() {
  local body
  local compact

  body="$(method_body "$mining_module" 'public void Disable\(ModuleScope scope\)')"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'publicvoidDisable(ModuleScopescope){Active=false;Generation=0;CurrentScope=null;Cleanup();}' ]]; then
    fail "Mining Disable must only lower Active, clear generation and scope, then clear its timers"
  fi

  body="$(method_body "$mining_module" 'private static void Cleanup\(\)')"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'privatestaticvoidCleanup(){MiningTimers.Clear();AreaMining.Reset();}' ]]; then
    fail "Mining Cleanup must only clear MiningTimers and the area-mining swing context"
  fi

  body="$(method_body "$mining_module" 'private static void OnPlayerAwake\(')"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'privatestaticvoidOnPlayerAwake(Playerplayer,ZNetViewnview){Cleanup();}' ]]; then
    fail "Mining OnPlayerAwake must do nothing but clear session timers"
  fi

  body="$(method_body "$mining_component" 'private void Unregister\(\)')"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'privatevoidUnregister(){AllInstance.Remove(this);_component=null;_zNetView=null;}' ]]; then
    fail "AutomaticMining.Unregister must only remove its registration and borrowed references"
  fi

  body="$(method_body "$mining_component" 'public void OnScopeDetaching\(\)')"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'publicvoidOnScopeDetaching(){Unregister();}' ]]; then
    fail "AutomaticMining.OnScopeDetaching must do nothing but unregister"
  fi

  body="$(method_body "$mining_component" 'private void OnDestroy\(\)')"
  compact="$(printf '%s\n' "$body" | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$compact" != 'privatevoidOnDestroy(){Unregister();}' ]]; then
    fail "AutomaticMining.OnDestroy must do nothing but unregister"
  fi

  require_source_pattern "$mining_module" 'internal static int MiningTimerCount *=> *MiningTimers *\. *Count *;' \
    "the Mining timer smoke probe must expose only MiningTimers.Count"
  require_source_pattern "$mining_component" 'internal static int AllInstanceCount *=> *AllInstance *\. *Count *;' \
    "the Mining component smoke probe must expose only AllInstance.Count"

  require_component_declares_only_allowed_unity_messages "$mining_component" AutomaticMining
}

require_mining_config_contract() {
  local body
  local stripped
  local row
  local key
  local pattern
  local line
  local previous=0

  if ((${#mining_config_entries[@]} != 12)); then
    fail "the Mining config table must name exactly its twelve persisted entries: found ${#mining_config_entries[@]}"
  fi

  stripped="$(strip_code "$mining_config" keep)"
  body="$(printf '%s\n' "$stripped" | extract_block - 'public static void Initialize\(\)')"
  if [[ -z "$body" ]]; then
    fail "the Mining config initializer could not be extracted; its bind contract was not checked"
    return
  fi

  for row in "${mining_config_entries[@]}"; do
    IFS='|' read -r key pattern <<<"$row"
    line="$(printf '%s\n' "$body" | first_match_line "$pattern")"
    if lookup_failed "$line"; then
      fail "could not search the Mining config for $key; its bind check was not run"
    elif [[ -z "$line" ]]; then
      fail "Mining config must bind $key with its audited default and constraints"
    elif ((line <= previous)); then
      fail "Mining config must keep its twelve entries in persisted order; $key is out of order"
    else
      previous=$line
    fi
  done

  require_source_pattern "$mining_config" \
    'public static ConfigEntry<AutomaticsModule> ModuleEntry *=> *_module *;' \
    "Mining Config.ModuleEntry must publicly expose the module toggle"
}

require_mining_smoke_descriptor() {
  local body
  local found
  local stripped

  stripped="$(strip_code "$mining_smoke" keep)"
  body="$(printf '%s\n' "$stripped" | extract_block - 'private static ModuleDescriptor DescribeMining\(\)')"
  if [[ -z "$body" ]]; then
    fail "the lifecycle smoke must define a Mining descriptor; its probes could not be checked"
    return
  fi

  for row in \
    'Module *= *MiningModule *\. *Instance@@the Mining descriptor must use the registered Mining module' \
    'PatchTypes *= *new\[\] *\{ *typeof *\( *MiningPatches *\) *\}@@the Mining descriptor must declare its one patch type' \
    'Probe *\. *Handlers *\( *"Hooks.OnPlayerAwake"[^;]*, *1 *\)@@the Mining descriptor must probe its OnPlayerAwake handler total' \
    'Probe *\. *Handlers *\( *"Hooks.OnPlayerUpdate"[^;]*, *1 *\)@@the Mining descriptor must probe its OnPlayerUpdate handler total' \
    'Probe *\. *Handlers *\( *"Hooks.OnPlayerFixedUpdate"[^;]*, *1 *\)@@the Mining descriptor must probe its OnPlayerFixedUpdate handler total' \
    'Probe *\. *Counter *\( *"mining.OnPlayerAwake.handler" *, *\(\) *=> *CountHandlersOf *\( *Hooks *\. *OnPlayerAwake *, *typeof *\( *MiningModule *\) *, *"OnPlayerAwake" *\) *, *OffExpectation *\. *Zero *, *0 *, *\(\) *=> *1 *\)@@the Mining descriptor must pin its own OnPlayerAwake handler to exactly one' \
    'Probe *\. *Counter *\( *"mining.OnPlayerUpdate.handler" *, *\(\) *=> *CountHandlersOf *\( *Hooks *\. *OnPlayerUpdate *, *typeof *\( *MiningModule *\) *, *"OnPlayerUpdate" *\) *, *OffExpectation *\. *Zero *, *0 *, *\(\) *=> *1 *\)@@the Mining descriptor must pin its own OnPlayerUpdate handler to exactly one' \
    'Probe *\. *Counter *\( *"mining.OnPlayerFixedUpdate.handler" *, *\(\) *=> *CountHandlersOf *\( *Hooks *\. *OnPlayerFixedUpdate *, *typeof *\( *MiningModule *\) *, *"OnPlayerFixedUpdate" *\) *, *OffExpectation *\. *Zero *, *0 *, *\(\) *=> *1 *\)@@the Mining descriptor must pin its own OnPlayerFixedUpdate handler to exactly one' \
    'Probe *\. *Components *< *MiningComponent *> *\( *CountEligibleMinerals *\)@@the Mining descriptor must compare components with the current eligible target count' \
    'Probe *\. *Counter *\( *"AllInstance.Count"[^;]*CountActiveMiningComponents *\)@@the Mining descriptor must compare AllInstance with active components' \
    'Precondition *= *MiningPrecondition@@the Mining descriptor must use its non-mutating safety precondition' \
    'new ExtraCheck *\( *"timers_cleared" *, *Phase *\. *WhileOff@@the Mining descriptor must check timer cleanup only while off' \
    'new ExtraCheck *\( *"components_current_generation" *, *Phase *\. *AfterOn@@the Mining descriptor must verify component generations after every enable' \
    'new ExtraCheck *\( *"interval_timer_resumed" *, *Phase *\. *AfterOn *, *MiningIntervalTimerResumed *\)@@the Mining descriptor must see the interval hook take up its timer after every enable'; do
    local pattern="${row%%@@*}"
    local message="${row#*@@}"
    found="$(printf '%s\n' "$body" | count_hits "$pattern")"
    if lookup_failed "$found"; then
      fail "could not inspect the Mining lifecycle descriptor; the check behind '$message' was not run"
    elif ((found != 1)); then
      fail "$message exactly once: found $found"
    fi
  done

  require_source_pattern "$mining_smoke" \
    'if *\( *!HasUsablePickaxe *\( *player *\) *\|\| *!HasNearbyAllowedMineral *\( *player *\) *\) *return null *;' \
    "the Mining smoke precondition must allow the run when there is no usable pickaxe or no nearby allowed mineral"
  require_source_pattern "$mining_smoke" \
    'var range *= *\( *MiningConfig *\. *MiningRange *> *0 *\? *MiningConfig *\. *MiningRange *: *32 *\) *\+ *24f? *\+ *2f? *;' \
    "the Mining smoke safety radius must use the product candidate fallback and include its 24 metre root-pivot margin"

  require_exact_method_body "$mining_smoke" 'private static string MiningPrecondition\(\)' \
    'privatestaticstringMiningPrecondition(){varplayer=Player.m_localPlayer;if(!player)return"";if(!HasUsablePickaxe(player)||!HasNearbyAllowedMineral(player))returnnull;return"";}' \
    "MiningPrecondition must remain the audited non-mutating safety decision"
  require_exact_method_body "$mining_smoke" 'private static bool HasUsablePickaxe\(Player player\)' \
    'privatestaticboolHasUsablePickaxe(Playerplayer){varitems=MiningConfig.NeedToEquipPickaxe?player.GetInventory().GetEquippedItems():player.GetInventory().GetAllItems();returnitems.Any(x=>x!=null&&x.m_durability>0f&&x.m_shared.m_skillType==Skills.SkillType.Pickaxes);}' \
    "HasUsablePickaxe must honor NeedToEquipPickaxe and require a durable pickaxe"
  require_exact_method_body "$mining_smoke" 'private static bool HasNearbyAllowedMineral\(Player player\)' \
    'privatestaticboolHasNearbyAllowedMineral(Playerplayer){varrange=(MiningConfig.MiningRange>0?MiningConfig.MiningRange:32)+24f+2f;varorigin=player.transform.position;foreach(varmineralinEligibleMinerals()){if(!mineral||!mineral.activeInHierarchy)continue;vardestructible=mineral.GetComponent<IDestructible>()asComponent;if(!destructible)continue;if(!ValheimObject.Mineral.GetIdentify(Objects.GetName(destructible),outvaridentifier))continue;if(!MiningConfig.AllowMiningMinerals.Contains(identifier))continue;if(Vector3.Distance(origin,mineral.transform.position)<=range)returntrue;}returnfalse;}' \
    "HasNearbyAllowedMineral must remain the audited allowed-mineral scan over the full product candidate radius"
  reject_source_pattern "$mining_smoke" \
    'MiningConfig *\. *(EnableAutomaticMining|ModuleEntry) *=' \
    "the Mining lifecycle smoke must not change the user's Mining settings"
}

# ---------------------------------------------------------------------------------------
# Automatic Feeding: one patch class, no hook subscription, and one scoped component on every
# tameable animal with a ZDO. The component's two static entry points are the only places the
# module state is read; one of them takes an item out and feeds it within the same call.
# ---------------------------------------------------------------------------------------

require_feeding_state_and_registration() {
  local stripped
  local raised
  local lowered
  local file
  local found

  require_source_pattern "$feeding_module" 'internal sealed class Module *: *IAutomaticsModule' \
    "the Feeding module must implement IAutomaticsModule"
  require_source_pattern "$feeding_module" 'public string Name *=> *"automatic-feeding" *;' \
    "the Feeding module registry name must remain automatic-feeding"
  require_source_pattern "$feeding_module" 'public string ConfigSection *=> *"automatic_feeding" *;' \
    "the Feeding module config section must remain automatic_feeding"
  require_source_pattern "$feeding_module" 'public bool SupportsRuntimeToggle *=> *true *;' \
    "the Feeding module must declare runtime toggle support"
  require_source_pattern "$feeding_module" '\[AutomaticsInitializer\(4\)\]' \
    "the Feeding module initializer order must remain 4"
  require_single_code_match "$feeding_module" \
    'internal static bool Running *=> *Active *&& *!StopRequested *;' \
    "Feeding Running must be exactly Active && !StopRequested"
  require_single_code_match "$feeding_module" \
    'internal static bool Discovering *=> *Active *&& *CurrentScope *!= *null *;' \
    "Feeding Discovering must be exactly Active && CurrentScope != null"

  require_exact_method_body "$feeding_module" 'public void SetStopRequested\(bool value\)' \
    'publicvoidSetStopRequested(boolvalue){StopRequested=value;}' \
    "Feeding SetStopRequested must do nothing but assign the requested value"
  require_exact_method_body "$feeding_module" 'private static void Initialize\(\)' \
    'privatestaticvoidInitialize(){ModuleRegistry.Register(Instance);}' \
    "the Feeding initializer must only register Module.Instance"

  stripped="$(strip_code "$feeding_module")"
  raised="$(printf '%s\n' "$stripped" | count_hits '(^|[^A-Za-z0-9_])Active *= *true *;')"
  lowered="$(printf '%s\n' "$stripped" | count_hits '(^|[^A-Za-z0-9_])Active *= *false *;')"
  if lookup_failed "$raised" "$lowered"; then
    fail "could not count the assignments of Active in the Feeding module; the check was not run"
  elif ((raised != 1 || lowered != 1)); then
    fail "the Feeding module must raise Active exactly once, as the last statement of Enable, and lower it exactly once, as the first statement of Disable (R1.5): found $raised raise(s) and $lowered lowering(s)"
  fi

  # Automatic Feeding subscribes to nothing. The acquisition-order check only sees Enable; a
  # subscription made anywhere else in the module would be one the teardown never removes, and the
  # hook counts the lifecycle smoke holds at their baseline would contradict it.
  for file in "$feeding_dir"/*.cs; do
    found="$(strip_code "$file" | count_hits '(^|[^A-Za-z0-9_])(Subscribe *<|Hooks *\.)')"
    if lookup_failed "$found"; then
      fail "could not search $file for hook subscriptions; the check was not run"
    elif ((found != 0)); then
      fail "$file must not subscribe to a hook or name Hooks: Automatic Feeding owns no subscription; found $found"
    fi
  done
}

# Both entry points and who may call them. Each instance method behind an entry shares its name, so
# a name alone cannot tell the two apart: the only calls allowed are the qualified one from the
# MonsterAI or BaseAI postfix, and the dispatch from the static entry to its instance method after
# the gate. A call from anywhere else - the teardown, Awake, Enable, an attach path, the smoke -
# raises the count. The file list is taken with its exit status, for the reason given at the Mining
# call-site count.
require_feeding_entries_and_call_sites() {
  local files
  local list_status=0
  local file
  local stripped
  local found
  local name
  local body
  local qualified
  local dispatched
  local -A total=([Feeding]=0 [CancelAttackOnFeedBox]=0)
  local -A declared=([Feeding]=0 [CancelAttackOnFeedBox]=0)
  local -A postfix=(
    [Feeding]='private static void MonsterAI_UpdateConsumeItem_Postfix\('
    [CancelAttackOnFeedBox]='private static void BaseAI_CanSeeTarget_Postfix\('
  )
  local -A entry=(
    [Feeding]='public static bool Feeding\(MonsterAI '
    [CancelAttackOnFeedBox]='public static bool CancelAttackOnFeedBox\(BaseAI '
  )

  require_item_entries_open_with_the_gate feeding_entries "$feeding_dir" "$feeding_module" 2

  files="$(rg --files Automatics smoke -g '*.cs')" || list_status=$?
  if ((list_status != 0)) || [[ -z "$files" ]]; then
    fail "could not list the C# files under Automatics and smoke (rg exit $list_status); the Feeding call-site check was not run"
    return
  fi

  while IFS= read -r file; do
    [[ "$file" == Automatics/Libraries/* ]] && continue
    stripped="$(strip_code "$file")"
    for name in Feeding CancelAttackOnFeedBox; do
      found="$(printf '%s\n' "$stripped" | count_hits "(^|[^A-Za-z0-9_])$name *\\(")"
      if lookup_failed "$found"; then
        fail "could not search $file for $name; the Feeding call-site check was not run"
        return
      fi
      total[$name]=$((total[$name] + found))

      found="$(printf '%s\n' "$stripped" | count_hits "(^|[^A-Za-z0-9_])(public|private|internal|protected)( +static)? +bool +$name *\\(")"
      if lookup_failed "$found"; then
        fail "could not distinguish the $name declarations in $file; the Feeding call-site check was not run"
        return
      fi
      declared[$name]=$((declared[$name] + found))
    done
  done <<<"$files"

  for name in Feeding CancelAttackOnFeedBox; do
    body="$(method_body "$feeding_patches" "${postfix[$name]}")"
    qualified="$(printf '%s\n' "$body" | count_hits "AutomaticFeeding *\\. *$name *\\(")"
    body="$(method_body "$feeding_component" "${entry[$name]}")"
    dispatched="$(printf '%s\n' "$body" | count_hits "(^|[^A-Za-z0-9_])feeding *\\. *$name *\\(")"
    if lookup_failed "$qualified" "$dispatched"; then
      fail "could not inspect the callers of $name; the Feeding call-site check was not run"
      continue
    fi

    if ((declared[$name] != 2 || total[$name] - declared[$name] != 2 || qualified != 1 || dispatched != 1)); then
      fail "$name must be declared exactly twice (the static entry and the instance method behind it) and called exactly twice across Automatics (excluding Libraries) and smoke: once qualified from ${postfix[$name]//\\/}, and once from the static entry to its instance method after the gate; found ${declared[$name]} declaration(s), $((total[$name] - declared[$name])) call(s), $qualified in the postfix and $dispatched in the entry"
    fi
  done
}

# R8.11 for the instance Feeding, where an item leaves its inventory. Its first change is the
# earliest of the claim, the removal and the consume callback; from that line on it may neither
# read the module state nor call an entry point, qualified or not - the instance method shares its
# name with the static entry, which needs no qualifier inside the class. The file's only reads of
# the module state are the two entry gates, and the files whose code runs inside a feeding call
# read none.
require_feeding_never_rechecks_after_a_change() {
  local stripped
  local found
  local gates
  local file
  local body
  local row
  local what
  local pattern
  local line
  local missing=0
  local change_line
  local late
  local nested

  stripped="$(strip_code "$feeding_component")"
  found="$(printf '%s\n' "$stripped" | count_hits "$module_read")"
  gates="$(printf '%s\n' "$stripped" | count_hits 'if *\( *! *Module *\. *Running *\) *return *false *;')"
  if lookup_failed "$found" "$gates"; then
    fail "could not search $feeding_component for module state reads; the check was not run"
  elif ((found != 2 || gates != 2)); then
    fail "$feeding_component must read the module state only in the gates of its two entry points (R8.11): found $found read(s) of Module., $gates of them the gate \"if (!Module.Running) return false;\""
  fi

  for file in "$feeding_globals" "$feeding_animal_type" "$feeding_config"; do
    found="$(strip_code "$file" | count_hits "$module_read")"
    if lookup_failed "$found"; then
      fail "could not search $file for module state reads; the check was not run"
    elif ((found != 0)); then
      fail "$file must not read the module state (R8.11): its code runs inside a feeding call, between the entry gate and the item's delivery; found $found"
    fi
  done

  body="$(method_body "$feeding_component" 'private bool Feeding\(Humanoid ')"
  if [[ -z "$body" ]]; then
    fail "cannot find the instance AutomaticFeeding.Feeding(Humanoid, float); the check on what follows its first change was not run"
    return
  fi

  for row in "${feeding_change_anchors[@]}"; do
    IFS='|' read -r what pattern <<<"$row"
    line="$(printf '%s\n' "$body" | first_hit "$pattern")"
    if lookup_failed "$line"; then
      fail "could not search the instance AutomaticFeeding.Feeding for its $what; the check on what follows its first change was not run"
      return
    fi
    if [[ -z "$line" ]]; then
      fail "could not find the $what in the instance AutomaticFeeding.Feeding: the claim, the removal and the consume callback each have to be found, or the first change this check measures from may not be the one the code makes"
      missing=1
    fi
  done
  if ((missing)); then
    return
  fi

  change_line="$(printf '%s\n' "$body" | first_hit "$feeding_changes")"
  if lookup_failed "$change_line" || [[ -z "$change_line" ]]; then
    fail "could not locate the first change in the instance AutomaticFeeding.Feeding; the check on what follows it was not run"
    return
  fi

  # From the line of the first change on, that line included.
  late="$(printf '%s\n' "$body" | count_hits_after "$((change_line - 1))" "$module_read")"
  nested="$(printf '%s\n' "$body" | count_hits_after "$((change_line - 1))" '(^|[^A-Za-z0-9_])(Feeding|CancelAttackOnFeedBox) *\(')"
  if lookup_failed "$late" "$nested"; then
    fail "could not search the instance AutomaticFeeding.Feeding after its first change; the check was not run"
    return
  fi

  if ((late != 0)); then
    fail "the instance AutomaticFeeding.Feeding must not read the module state after its first change (R8.11): a stop seen there loses an item that already left its inventory; found $late"
  fi
  if ((nested != 0)); then
    fail "the instance AutomaticFeeding.Feeding must not call Feeding or CancelAttackOnFeedBox, qualified or not, after its first change (R8.11): the static entry checks the stop request again, and after the removal that can lose the item; found $nested"
  fi
}

# AC-S11: the instance methods act only while this peer owns the animal, and a feed box is claimed -
# in its guard form, so a failed claim returns before anything moves - before an item is removed
# from it.
require_feeding_checks_ownership_first() {
  local declaration
  local body
  local first
  local claim
  local removal

  for declaration in 'private bool Feeding\(Humanoid ' 'private bool CancelAttackOnFeedBox\(StaticTarget '; do
    body="$(method_body "$feeding_component" "$declaration")"
    if [[ -z "$body" ]]; then
      fail "cannot find ${declaration//\\/} in $feeding_component; its ownership check was not run"
      continue
    fi
    first="$(printf '%s\n' "$body" | first_statement)"
    if [[ "$first" != "if(!HasNetworkOwnership())returnfalse;" ]]; then
      fail "${declaration//\\/} in $feeding_component must open with \"if (!HasNetworkOwnership()) return false;\" (AC-S11): nothing may be read or changed for an animal this peer does not own; found \"$first\""
    fi
  done

  require_exact_method_body "$feeding_component" 'private bool HasNetworkOwnership\(\)' \
    'privateboolHasNetworkOwnership(){returnObjects.HasValidOwnership(_character);}' \
    "AutomaticFeeding.HasNetworkOwnership must remain the Objects.HasValidOwnership check on the animal (AC-S11)"

  body="$(method_body "$feeding_component" 'private bool Feeding\(Humanoid ')"
  claim="$(printf '%s\n' "$body" | first_hit 'TryPrepareContainerMutationServerAware *\(')"
  removal="$(printf '%s\n' "$body" | first_hit 'RemoveOneItem *\(')"
  if lookup_failed "$claim" "$removal" || [[ -z "$claim" || -z "$removal" ]]; then
    fail "could not locate the claim and the removal in the instance AutomaticFeeding.Feeding; the claim-before-removal check was not run"
  elif ((claim >= removal)); then
    fail "the instance AutomaticFeeding.Feeding must claim the feed box (TryPrepareContainerMutationServerAware) before it removes an item (RemoveOneItem) (AC-S11): an item removed from a box this peer has not claimed desyncs the container"
  fi
  if ! printf '%s\n' "$body" | has_match 'if *\( *! *ContainerAccess *\. *TryPrepareContainerMutationServerAware *\( *Player *\. *m_localPlayer *, *_closestFeedBox *\) *\) *return true *;'; then
    fail "the instance AutomaticFeeding.Feeding must return when the feed box claim fails, before anything is removed (AC-S11): a claim whose result is thrown away confirms nothing"
  fi
}

# The patch class, the two attach paths and the component's scope contract. The patches carry no
# gate of their own: the gates sit in the entry points they call (ADR-0004) and in AttachFeeding.
require_feeding_component_ownership() {
  local stripped
  local found
  local row
  local pattern
  local rest
  local expected
  local message

  stripped="$(strip_code "$feeding_patches")"
  found="$(printf '%s\n' "$stripped" | count_hits '\[ *HarmonyPatch *\( *typeof *\(')"
  if lookup_failed "$found"; then
    fail "could not count the Feeding patch targets; the check was not run"
  elif ((found != 3)); then
    fail "the Feeding patch class must patch exactly its three audited targets (Tameable.Awake, MonsterAI.UpdateConsumeItem and BaseAI.CanSeeTarget(StaticTarget)): found $found"
  fi

  found="$(printf '%s\n' "$stripped" | count_hits '(^|[^A-Za-z0-9_])Module *\. *(Running|Active|StopRequested|Discovering)([^A-Za-z0-9_]|$)')"
  if lookup_failed "$found"; then
    fail "could not search the Feeding patches for module state reads; the check was not run"
  elif ((found != 0)); then
    fail "the Feeding patches must not read the module state (ADR-0004): the gates sit in the entry points they call and in AttachFeeding, and a gate in a patch is a second, unlisted place to look at the stop request; found $found"
  fi

  found="$(printf '%s\n' "$stripped" | count_hits '(^|[^A-Za-z0-9_])Module *\. *AttachFeeding *\(')"
  if lookup_failed "$found"; then
    fail "could not search the Feeding patches for the runtime attach path; the check was not run"
  elif ((found != 1)); then
    fail "the Tameable.Awake postfix must route through Module.AttachFeeding exactly once, and no other patch may attach: found $found"
  fi

  stripped="$(strip_code "$feeding_module")"
  for row in \
    'CurrentScope *\. *TryAttach *< *AutomaticFeeding *> *\(@@1@@the Feeding runtime attach path must attach through CurrentScope.TryAttach exactly once' \
    '(^|[^A-Za-z0-9_])scope *\. *Attach *< *AutomaticFeeding *> *\(@@1@@ReconcileTameables must attach through the supplied scope exactly once' \
    'FindObjectsByType *< *Tameable *> *\(@@1@@the Feeding activation scan must enumerate Tameable itself exactly once (R4.2)' \
    'FindObjectsInactive *\. *Include@@1@@the Feeding activation scan must ask for inactive animals (R4.2): without it a re-enable misses every animal under a deactivated root' \
    'FindObjectsOfType@@0@@the Feeding activation scan must use FindObjectsByType, not the deprecated FindObjectsOfType' \
    'GetAllCharacters@@0@@the Feeding activation scan must not use Character.GetAllCharacters (R4.2): it lists the characters alive now, not the objects that ran Tameable.Awake'; do
    pattern="${row%%@@*}"
    rest="${row#*@@}"
    expected="${rest%%@@*}"
    message="${rest#*@@}"
    found="$(printf '%s\n' "$stripped" | count_hits "$pattern")"
    if lookup_failed "$found"; then
      fail "could not search the Feeding module for '$pattern'; the component ownership check was not run"
    elif ((found != expected)); then
      fail "$message: found $found"
    fi
  done

  require_source_pattern "$feeding_component" \
    'class AutomaticFeeding *: *MonoBehaviour *, *IScopedComponent' \
    "AutomaticFeeding must implement IScopedComponent so its activation scope can stamp and detach it"
  require_source_pattern "$feeding_component" 'public int ScopeGeneration *\{ *get *; *set *; *\}' \
    "AutomaticFeeding must expose its scope generation stamp"

  require_component_declares_only_allowed_unity_messages "$feeding_component" AutomaticFeeding
}

# Every Feeding method that runs without the entry gate, pinned to its whole body: the teardown, the
# component's engine callbacks, the three postfixes and the two attach paths. The call-site count
# above catches an entry point called from one of them; these catch any other statement, such as a
# claim or a removal made directly.
require_feeding_gate_free_bodies_are_fixed() {
  require_exact_method_body "$feeding_module" 'public void Disable\(ModuleScope scope\)' \
    'publicvoidDisable(ModuleScopescope){Active=false;Generation=0;CurrentScope=null;}' \
    "Feeding Disable must only lower Active and clear the generation and the scope; the scope removes the patch and the components"
  require_exact_method_body "$feeding_component" 'public void OnScopeDetaching\(\)' \
    'publicvoidOnScopeDetaching(){_closestFeedBox=null;_closestFeeder=null;_consumeTargetItem=null;_consumeSearchTimer=0f;_feedBoxBuffer.Clear();}' \
    "AutomaticFeeding.OnScopeDetaching must only drop its feed targets, reset its search timer and clear its buffer; no item is held to put back"
  require_exact_method_body "$feeding_component" 'private void OnDestroy\(\)' \
    'privatevoidOnDestroy(){_baseAI=null;_monsterAI=null;_tamable=null;_character=null;_closestFeeder=null;_closestFeedBox=null;_consumeTargetItem=null;}' \
    "AutomaticFeeding.OnDestroy must only drop its references"
  require_exact_method_body "$feeding_component" 'private void Awake\(\)' \
    'privatevoidAwake(){_tamable=GetComponent<Tameable>();_character=GetComponent<Character>();_monsterAI=GetComponent<MonsterAI>();_baseAI=GetComponent<BaseAI>();}' \
    "AutomaticFeeding.Awake must only acquire the animal's components"
  require_exact_method_body "$feeding_patches" 'private static void Tameable_Awake_Postfix\(' \
    'privatestaticvoidTameable_Awake_Postfix(Tameable__instance,ZNetView___m_nview){if(___m_nview&&___m_nview.GetZDO()!=null)Module.AttachFeeding(__instance.gameObject);}' \
    "the Tameable.Awake postfix must only attach to a networked animal through Module.AttachFeeding"
  require_exact_method_body "$feeding_patches" 'private static void MonsterAI_UpdateConsumeItem_Postfix\(' \
    'privatestaticvoidMonsterAI_UpdateConsumeItem_Postfix(MonsterAI__instance,refbool__result,Humanoidhumanoid,floatdt){if(!Config.EnableAutomaticFeeding)return;if(!__result&&AutomaticFeeding.Feeding(__instance,humanoid,dt))__result=true;}' \
    "the MonsterAI.UpdateConsumeItem postfix must only call the gated Feeding entry, once, when the game found nothing to eat"
  require_exact_method_body "$feeding_patches" 'private static void BaseAI_CanSeeTarget_Postfix\(' \
    'privatestaticvoidBaseAI_CanSeeTarget_Postfix(BaseAI__instance,refbool__result,StaticTargettarget){if(!__result)return;if(!Config.EnableAutomaticFeeding)return;if(AutomaticFeeding.CancelAttackOnFeedBox(__instance,target))__result=false;}' \
    "the BaseAI.CanSeeTarget postfix must only call the gated CancelAttackOnFeedBox entry, once"
  require_exact_method_body "$feeding_module" 'internal static void AttachFeeding\(GameObject target\)' \
    'internalstaticvoidAttachFeeding(GameObjecttarget){if(!Discovering)return;CurrentScope.TryAttach<AutomaticFeeding>(target,out_);}' \
    "Feeding AttachFeeding must only recheck the discovery state and attach through the current scope"
  require_exact_method_body "$feeding_module" 'private static void ReconcileTameables\(ModuleScope scope\)' \
    'privatestaticvoidReconcileTameables(ModuleScopescope){foreach(vartameableinUnityEngine.Object.FindObjectsByType<Tameable>(FindObjectsInactive.Include,FindObjectsSortMode.None)){if(!Objects.GetZNetView(tameable,outvarzNetView))continue;if(zNetView.GetZDO()==null)continue;scope.Attach<AutomaticFeeding>(tameable.gameObject);}}' \
    "Feeding ReconcileTameables must remain the audited scan of networked tameables and must not feed"
}

# R5.1 / R5.2 and R1.5: every entry is bound, in its persisted order and with its default, and
# nothing in the module reads the setting as a flag.
require_feeding_config_contract() {
  local body
  local stripped
  local row
  local key
  local pattern
  local line
  local previous=0
  local file

  if ((${#feeding_config_entries[@]} != 6)); then
    fail "the Feeding config table must name exactly its six persisted entries: found ${#feeding_config_entries[@]}"
  fi

  stripped="$(strip_code "$feeding_config" keep)"
  body="$(printf '%s\n' "$stripped" | extract_block - 'public static void Initialize\(\)')"
  if [[ -z "$body" ]]; then
    fail "the Feeding config initializer could not be extracted; its bind contract was not checked"
    return
  fi

  for row in "${feeding_config_entries[@]}"; do
    IFS='|' read -r key pattern <<<"$row"
    line="$(printf '%s\n' "$body" | first_match_line "$pattern")"
    if lookup_failed "$line"; then
      fail "could not search the Feeding config for $key; its bind check was not run"
    elif [[ -z "$line" ]]; then
      fail "Feeding config must bind $key with its audited default and constraints"
    elif ((line <= previous)); then
      fail "Feeding config must keep its six entries in persisted order; $key is out of order"
    else
      previous=$line
    fi
  done

  require_source_pattern "$feeding_config" \
    'public static ConfigEntry<AutomaticsModule> ModuleEntry *=> *_module *;' \
    "Feeding Config.ModuleEntry must publicly expose the module toggle"

  for file in "$feeding_dir"/*.cs; do
    reject_source_pattern "$file" "ModuleDisabled" \
      "$file must not declare or read ModuleDisabled (R1.5): the entry points read Module.Running, which carries the stop request"
    reject_source_pattern "$file" '_module\s*\.\s*Value' \
      "$file must not read the module setting's value (R1.5): the registry follows the setting, and the entry points read Module.Running"
    reject_source_pattern "$file" 'ModuleEntry\s*\.\s*Value' \
      "$file must not read the module setting's value (R1.5): the registry follows the setting, and the entry points read Module.Running"
  done
}

require_feeding_smoke_descriptor() {
  local body
  local found
  local stripped
  local row
  local pattern
  local message

  stripped="$(strip_code "$feeding_smoke" keep)"
  body="$(printf '%s\n' "$stripped" | extract_block - 'private static ModuleDescriptor DescribeFeeding\(\)')"
  if [[ -z "$body" ]]; then
    fail "the lifecycle smoke must define a Feeding descriptor; its probes could not be checked"
    return
  fi

  for row in \
    'Argument *= *"feeding"@@the Feeding descriptor must be reached by the argument feeding' \
    'Module *= *FeedingModule *\. *Instance@@the Feeding descriptor must use the registered Feeding module' \
    'Active *= *\(\) *=> *FeedingModule *\. *Active *,@@the Feeding descriptor must read the Feeding applied state' \
    'Running *= *\(\) *=> *FeedingModule *\. *Running *,@@the Feeding descriptor must read the Feeding Running predicate' \
    'PatchTypes *= *new\[\] *\{ *typeof *\( *FeedingPatches *\) *\}@@the Feeding descriptor must declare its one patch type' \
    'Probe *\. *Handlers *\( *"Hooks.OnPlayerAwake" *, *\(\) *=> *Hooks *\. *OnPlayerAwake *, *0 *\)@@the Feeding descriptor must hold Hooks.OnPlayerAwake at its baseline' \
    'Probe *\. *Handlers *\( *"Hooks.OnPlayerUpdate" *, *\(\) *=> *Hooks *\. *OnPlayerUpdate *, *0 *\)@@the Feeding descriptor must hold Hooks.OnPlayerUpdate at its baseline' \
    'Probe *\. *Handlers *\( *"Hooks.OnPlayerFixedUpdate" *, *\(\) *=> *Hooks *\. *OnPlayerFixedUpdate *, *0 *\)@@the Feeding descriptor must hold Hooks.OnPlayerFixedUpdate at its baseline' \
    'Probe *\. *Handlers *\( *"Hooks.OnDedicatedServerFixedUpdate" *, *\(\) *=> *Hooks *\. *OnDedicatedServerFixedUpdate *, *0 *\)@@the Feeding descriptor must hold Hooks.OnDedicatedServerFixedUpdate at its baseline' \
    'Probe *\. *Components *< *FeedingComponent *> *\( *CountEligibleTameables *\)@@the Feeding descriptor must compare the components with the tameables loaded at that moment' \
    'Precondition *= *null@@the Feeding descriptor must declare no precondition' \
    'new ExtraCheck *\( *"components_current_generation" *, *Phase *\. *AfterOn@@the Feeding descriptor must verify component generations after every enable'; do
    pattern="${row%%@@*}"
    message="${row#*@@}"
    found="$(printf '%s\n' "$body" | count_hits "$pattern")"
    if lookup_failed "$found"; then
      fail "could not inspect the Feeding lifecycle descriptor; the check behind '$message' was not run"
    elif ((found != 1)); then
      fail "$message exactly once: found $found"
    fi
  done

  require_exact_method_body "$feeding_smoke" 'private static Tameable\[\] EligibleTameables\(\)' \
    'privatestaticTameable[]EligibleTameables(){returnObject.FindObjectsByType<Tameable>(FindObjectsInactive.Include,FindObjectsSortMode.None).Where(x=>Objects.GetZNetView(x,outvarzNetView)&&zNetView.GetZDO()!=null).ToArray();}' \
    "EligibleTameables must count every loaded Tameable with a ZDO, inactive ones included, by the rule the module attaches by"
  require_exact_method_body "$feeding_smoke" 'private static int CountEligibleTameables\(\)' \
    'privatestaticintCountEligibleTameables(){returnEligibleTameables().Length;}' \
    "CountEligibleTameables must count the eligible tameables and nothing else"
  reject_source_pattern "$feeding_smoke" 'AutomaticFeeding\.Config|FeedingConfig' \
    "the lifecycle smoke must not read or change the user's Feeding settings"
}

# ---------------------------------------------------------------------------------------
# Automatic Farming: one patch class, three named hook subscriptions and no component. A farming
# pass harvests crops into the inventory or designated containers, spends seeds and places
# saplings; it is accepted only at the two player-hook gates and then finishes. Automatic Pickup's
# crop claim reads the same predicate, so the two modules hand the crops over at the stop request.
# ---------------------------------------------------------------------------------------

# The registration contract, the three state fields and the activation, each spelled out once in
# code, and no subscription anywhere but the three in Enable.
require_farming_state_and_registration() {
  local file
  local found

  require_source_pattern "$farming_module" 'internal sealed class Module *: *IAutomaticsModule' \
    "the Farming module must implement IAutomaticsModule"
  require_source_pattern "$farming_module" 'public string Name *=> *"automatic-farming" *;' \
    "the Farming module registry name must remain automatic-farming, the Harmony id its patch has always been applied under"
  require_source_pattern "$farming_module" 'public string ConfigSection *=> *"automatic_farming" *;' \
    "the Farming module config section must remain automatic_farming"
  require_source_pattern "$farming_module" 'public bool SupportsRuntimeToggle *=> *true *;' \
    "the Farming module must declare runtime toggle support"
  require_source_pattern "$farming_module" '\[AutomaticsInitializer\(9\)\]' \
    "the Farming module initializer order must remain 9"
  require_single_code_match "$farming_module" 'internal static bool Active *;' \
    "Farming Active must be declared exactly once, as a plain static field"
  require_single_code_match "$farming_module" 'internal static bool StopRequested *;' \
    "Farming StopRequested must be declared exactly once, as a plain static field"
  require_single_code_match "$farming_module" \
    'internal static bool Running *=> *Active *&& *!StopRequested *;' \
    "Farming Running must be exactly Active && !StopRequested"

  require_exact_method_body "$farming_module" 'public void SetStopRequested\(bool value\)' \
    'publicvoidSetStopRequested(boolvalue){StopRequested=value;}' \
    "Farming SetStopRequested must do nothing but assign the requested value"
  require_exact_method_body "$farming_module" 'public void BindConfig\(\)' \
    'publicvoidBindConfig(){Config.Initialize();}' \
    "Farming BindConfig must do nothing but bind the settings"
  require_exact_method_body "$farming_module" 'private static void Initialize\(\)' \
    'privatestaticvoidInitialize(){ModuleRegistry.Register(Instance);}' \
    "the Farming initializer must only register Module.Instance"
  require_exact_method_body "$farming_module" 'public void Enable\(ModuleScope scope\)' \
    'publicvoidEnable(ModuleScopescope){scope.PatchAll(typeof(Patches));scope.Subscribe<Action<Player,ZNetView>>(h=>Hooks.OnPlayerAwake+=h,h=>Hooks.OnPlayerAwake-=h,OnPlayerAwake);scope.Subscribe<Action<Player,bool>>(h=>Hooks.OnPlayerUpdate+=h,h=>Hooks.OnPlayerUpdate-=h,OnPlayerUpdate);scope.Subscribe<Action<Player,float>>(h=>Hooks.OnPlayerFixedUpdate+=h,h=>Hooks.OnPlayerFixedUpdate-=h,OnPlayerFixedUpdate);Active=true;}' \
    "Farming Enable must only apply the patch class, subscribe the three named player hooks and raise Active last (R4.3): it runs without a gate, so anything else it did - a farming pass, a designation - would act before the module advertises itself as running"

  # The three subscriptions are the only ones: each is a Subscribe call with an add and a remove
  # of the hook, all inside the Enable pinned above. One made anywhere else is never removed.
  for file in "$farming_dir"/*.cs; do
    found="$(strip_code "$file" | count_hits '(^|[^A-Za-z0-9_])(Subscribe *<|Hooks *\.)')"
    if lookup_failed "$found"; then
      fail "could not search $file for hook subscriptions; the check was not run"
    elif [[ "$file" == "$farming_module" ]] && ((found != 9)); then
      fail "$file must subscribe only in Enable: three Subscribe calls, each with an add and a remove of its hook, are 9 occurrences of Subscribe< or Hooks.; found $found"
    elif [[ "$file" != "$farming_module" ]] && ((found != 0)); then
      fail "$file must not subscribe to a hook or name Hooks: Automatic Farming owns only the three subscriptions in Module.Enable, and one made here is never removed by the teardown; found $found"
    fi
  done
}

# R8.11: a farming pass checks the stop request once, at its hook gate, and never again. Module.cs
# names each state field only where the lifecycle needs it; the files a pass runs through read no
# module state apart from the two listed gates in AutomaticFarming.cs and Patches.cs; and no file
# of the module reaches a gated entry point from inside a pass - neither a hook handler nor the crop
# claim, whose gate is a stop check of its own. Calls and method groups count alike. The shared
# harvest and container helpers a pass runs through know nothing of the module.
require_farming_never_rechecks_after_a_change() {
  local stripped
  local found
  local row
  local rest
  local word
  local expected
  local where
  local method
  local file
  local reads
  local claims
  local note

  stripped="$(strip_code "$farming_module")"
  for row in \
    'Running@@3@@its declaration and the gates of the two hook handlers' \
    'Active@@4@@its declaration, the Running predicate, the raise in Enable and the lowering in Disable' \
    'StopRequested@@3@@its declaration, the Running predicate and SetStopRequested'; do
    word="${row%%@@*}"
    rest="${row#*@@}"
    expected="${rest%%@@*}"
    where="${rest#*@@}"
    found="$(printf '%s\n' "$stripped" | count_hits "(^|[^A-Za-z0-9_])$word([^A-Za-z0-9_]|\$)")"
    if lookup_failed "$found"; then
      fail "could not search $farming_module for $word; the check on where the Farming state is read was not run"
    elif ((found != expected)); then
      fail "$farming_module may name $word only in $where (R8.11): a read anywhere else is a stop check no gate list knows about, and one after a hook gate can leave a harvested spot unplanted; found $found occurrence(s), expected $expected"
    fi
  done

  for method in OnPlayerUpdate OnPlayerFixedUpdate; do
    found="$(printf '%s\n' "$stripped" | count_hits "(^|[^A-Za-z0-9_])$method *\\(")"
    if lookup_failed "$found"; then
      fail "could not search $farming_module for calls to $method; the nested-entry check was not run"
    elif ((found != 1)); then
      fail "Farming.$method must only occur as its declaration and never be called (R8.11): a call checks the stop request again from inside a pass; found $found occurrence(s)"
    fi
  done

  # file@@reads of Module.@@occurrences of ClaimsCrop
  for row in \
    "$farming_module@@0@@0" \
    "$farming_patches@@1@@0" \
    "$farming_core@@1@@3" \
    "$farming_planting@@0@@0" \
    "$farming_seed_pool@@0@@0" \
    "$farming_containers@@0@@0" \
    "$farming_roles@@0@@0" \
    "$farming_config@@0@@0"; do
    file="${row%%@@*}"
    rest="${row#*@@}"
    expected="${rest%%@@*}"
    stripped="$(strip_code "$file")"
    reads="$(printf '%s\n' "$stripped" | count_hits "$module_read")"
    claims="$(printf '%s\n' "$stripped" | count_hits '(^|[^A-Za-z0-9_])ClaimsCrop([^A-Za-z0-9_]|$)')"
    if lookup_failed "$reads" "$claims"; then
      fail "could not search $file for module state reads and crop claims; the check was not run"
      continue
    fi

    if ((reads != expected)); then
      note="no read of Module."
      if ((expected == 1)); then
        note="exactly one read of Module., the gate of its listed entry point"
      fi
      fail "$file must hold $note (R8.11): its code runs inside a farming pass, between the hook gate and the pass's last change, or reads state no gate list knows about; found $reads"
    fi

    expected="${rest#*@@}"
    if ((claims != expected)); then
      note="must not name ClaimsCrop"
      if ((expected == 3)); then
        note="must name ClaimsCrop exactly three times, its two declarations and the forwarding call of ClaimsCrop(Pickable),"
      fi
      fail "$file $note (R8.11): the claim opens with its own stop check, so calling it, or passing it on as a method group, from inside a farming pass checks the stop request again after a harvest that still has to be replanted; found $claims occurrence(s)"
    fi
  done

  found="$(strip_code "$farming_core" | count_hits '(^|[^A-Za-z0-9_])public static bool ClaimsCrop *\(')"
  if lookup_failed "$found"; then
    fail "could not search $farming_core for the ClaimsCrop declarations; the check was not run"
  elif ((found != 2)); then
    fail "$farming_core must declare exactly the two ClaimsCrop overloads, so that its occurrence count leaves no room for a call: found $found declaration(s)"
  fi

  for file in Automatics/Valheim/PickableHarvester.cs Automatics/Valheim/ContainerAccess.cs; do
    found="$(strip_code "$file" | count_hits '(^|[^A-Za-z0-9_])(AutomaticFarming|Module *\.)')"
    if lookup_failed "$found"; then
      fail "could not search $file; the check that it knows nothing of the module state was not run"
    elif ((found != 0)); then
      fail "$file must not name AutomaticFarming or read a module state (R8.11): Automatic Pickup, Processing and Storage share it, and a farming pass runs through it between its hook gate and its last change; found $found"
    fi
  done
}

# Who may start what. A farming pass (TryFarming) is started only behind the two hook gates, a
# container designation (TryDesignateContainer, then TryCycleRole) only behind the update gate, and
# nothing else in the mod or the smoke names them, as a call or as a method group: a call from the
# bookkeeping, Enable or the smoke harvests, plants or rewrites a role without the gate. The crop
# claim is asked by Automatic Pickup alone outside the Farming files (the smoke may read it). The
# file list is taken with its exit status, for the reason given at the Mining call-site count.
require_farming_call_sites() {
  local files
  local list_status=0
  local file
  local stripped
  local found
  local name
  local body
  local row
  local rest
  local claims=0
  local -A total=([TryFarming]=0 [TryDesignateContainer]=0 [TryCycleRole]=0)
  local -A declared=([TryFarming]=0 [TryDesignateContainer]=0 [TryCycleRole]=0)
  local -A declaration=(
    [TryFarming]='public static void TryFarming *\('
    [TryDesignateContainer]='private static void TryDesignateContainer *\('
    [TryCycleRole]='public static bool TryCycleRole *\('
  )
  local -A inside=([TryFarming]=0 [TryDesignateContainer]=0 [TryCycleRole]=0)
  local -A calls=([TryFarming]=2 [TryDesignateContainer]=1 [TryCycleRole]=1)
  local -A where=(
    [TryFarming]='one qualified call in each of Module.OnPlayerUpdate and Module.OnPlayerFixedUpdate'
    [TryDesignateContainer]='one call in Module.OnPlayerUpdate'
    [TryCycleRole]='one qualified call in Module.TryDesignateContainer'
  )

  files="$(rg --files Automatics smoke -g '*.cs')" || list_status=$?
  if ((list_status != 0)) || [[ -z "$files" ]]; then
    fail "could not list the C# files under Automatics and smoke (rg exit $list_status); the Farming call-site check was not run"
    return
  fi

  while IFS= read -r file; do
    [[ "$file" == Automatics/Libraries/* ]] && continue
    stripped="$(strip_code "$file")"
    for name in TryFarming TryDesignateContainer TryCycleRole; do
      found="$(printf '%s\n' "$stripped" | count_hits "(^|[^A-Za-z0-9_])$name([^A-Za-z0-9_]|\$)")"
      if lookup_failed "$found"; then
        fail "could not search $file for $name; the Farming call-site check was not run"
        return
      fi
      total[$name]=$((total[$name] + found))

      found="$(printf '%s\n' "$stripped" | count_hits "(^|[^A-Za-z0-9_])${declaration[$name]}")"
      if lookup_failed "$found"; then
        fail "could not distinguish the $name declaration in $file; the Farming call-site check was not run"
        return
      fi
      declared[$name]=$((declared[$name] + found))
    done

    if [[ "$file" == Automatics/* && "$file" != "$farming_dir"/* ]]; then
      found="$(printf '%s\n' "$stripped" | count_hits '(^|[^A-Za-z0-9_])ClaimsCrop([^A-Za-z0-9_]|$)')"
      if lookup_failed "$found"; then
        fail "could not search $file for ClaimsCrop; the Farming call-site check was not run"
        return
      fi
      claims=$((claims + found))
    fi
  done <<<"$files"

  for row in \
    'TryFarming@@private static void OnPlayerUpdate\(@@AutomaticFarming *\. *TryFarming *\(' \
    'TryFarming@@private static void OnPlayerFixedUpdate\(@@AutomaticFarming *\. *TryFarming *\(' \
    'TryDesignateContainer@@private static void OnPlayerUpdate\(@@(^|[^A-Za-z0-9_.])TryDesignateContainer *\(' \
    'TryCycleRole@@private static void TryDesignateContainer\(@@ContainerRoles *\. *TryCycleRole *\('; do
    name="${row%%@@*}"
    rest="${row#*@@}"
    body="$(method_body "$farming_module" "${rest%%@@*}")"
    found="$(printf '%s\n' "$body" | count_hits "${rest#*@@}")"
    if lookup_failed "$found"; then
      fail "could not inspect the callers of $name; the Farming call-site check was not run"
      return
    fi
    # Each expected body holds exactly one call: a total of two TryFarming calls could otherwise
    # sit both in the key handler and leave the interval handler without one.
    if ((found != 1)); then
      fail "$name must be called exactly once in ${rest%%@@*}, not $found time(s) (R8.11): each hook handler takes its own farming pass, and the total alone cannot tell two calls in one handler from one in each"
    fi
    inside[$name]=$((inside[$name] + found))
  done

  for name in TryFarming TryDesignateContainer TryCycleRole; do
    if ((declared[$name] != 1 || total[$name] - declared[$name] != calls[$name] || inside[$name] != calls[$name])); then
      fail "$name must be declared once and otherwise named only by ${where[$name]}, across Automatics (excluding Libraries) and smoke (R8.11): a call or method group anywhere else acts without the hook gate; found ${declared[$name]} declaration(s), $((total[$name] - declared[$name])) other occurrence(s), ${inside[$name]} in the expected bodies"
    fi
  done

  if ((claims != 1)); then
    fail "ClaimsCrop must be named exactly once in Automatics outside the Farming module and its libraries, by Automatic Pickup's periodic pickup (R7.5): any other caller would act on a crop Farming claims, or leave one Farming no longer harvests; found $claims"
  fi
}

# R6.1 / R6.2: what a disable and a new world session drop, pinned to whole bodies because each of
# these runs without a gate: the interval timers, the sapling lookup and the colliders the last
# grow-space check left behind. Module.cs itself writes no ZDO and claims nothing: a container's
# role changes only in ContainerRoles.TryCycleRole, behind the update gate, and a disable keeps it.
require_farming_session_is_reset() {
  local found

  require_exact_method_body "$farming_module" 'public void Disable\(ModuleScope scope\)' \
    'publicvoidDisable(ModuleScopescope){Active=false;ResetSession();CropPlanting.ClearCaches();}' \
    "Farming Disable must only lower Active, reset the session and drop the planting caches (R6.1): the scope removes the patch and the subscriptions, no item is held to put back, and the container roles are the user's"
  require_exact_method_body "$farming_module" 'private static void ResetSession\(\)' \
    'privatestaticvoidResetSession(){FarmingTimers.Clear();}' \
    "Farming ResetSession must clear FarmingTimers and do nothing else (R6.1 / R6.2): a timer left there carries into the next activation and runs its first interval pass early"
  require_exact_method_body "$farming_module" 'private static void OnPlayerAwake\(' \
    'privatestaticvoidOnPlayerAwake(Playerplayer,ZNetViewzNetView){ResetSession();}' \
    "Farming OnPlayerAwake must do nothing but reset the session (R6.2): it runs without a gate, so anything else it did would act after a stop"
  require_exact_method_body "$farming_planting" 'internal static void ClearCaches\(\)' \
    'internalstaticvoidClearCaches(){_saplingByCrop=null;Array.Clear(ColliderBuffer,0,ColliderBuffer.Length);}' \
    "CropPlanting.ClearCaches must drop the sapling lookup and clear the collider buffer, and do nothing else (R6.1)"

  found="$(strip_code "$farming_module" | count_hits '(^|[^A-Za-z0-9_])ClaimOwnership *\(|\. *Set *\(')"
  if lookup_failed "$found"; then
    fail "could not search $farming_module for ZDO writes; the check was not run"
  elif ((found != 0)); then
    fail "$farming_module must not write a ZDO or claim ownership itself: a container role is changed only by ContainerRoles.TryCycleRole after its access checks, and a disable leaves every role as the user set it; found $found"
  fi

  require_source_pattern "$farming_module" 'internal static int FarmingTimerCount *=> *FarmingTimers *\. *Count *;' \
    "the Farming timer smoke probe must expose only FarmingTimers.Count"
  require_source_pattern "$farming_planting" 'internal static bool SaplingMapBuilt *=> *_saplingByCrop *!= *null *;' \
    "the Farming sapling-lookup smoke probe must expose only whether the lookup is built"
}

# R7.5: Automatic Pickup leaves a crop to Farming only while Farming is running. The claim it asks
# opens with the Running gate (see farming_entries); the overload without a player only forwards to
# it; no Farming file reads Active, which keeps acting between the stop request and the teardown;
# and Pickup asks the claim once, from its periodic crop pickup (who else may ask it is counted
# with the call sites).
require_farming_crop_claim_contract() {
  local file
  local found

  require_exact_method_body "$farming_core" 'public static bool ClaimsCrop\(Pickable pickable\)' \
    'publicstaticboolClaimsCrop(Pickablepickable){returnClaimsCrop(Player.m_localPlayer,pickable);}' \
    "AutomaticFarming.ClaimsCrop(Pickable) must only forward to ClaimsCrop(Player, Pickable) for the local player (R7.5): the gate is there, and this overload carries none of its own"
  # The claim decides which crops periodic pickup leaves to Farming. Pinned whole, so a body that
  # kept the gate but no longer claimed (or claimed everything) cannot pass: the static checks and
  # the smoke's claim checks would both stay quiet, since the smoke skips when no crop is claimed.
  # strip_code blanks the "seed" tag literal to "".
  require_exact_method_body "$farming_core" 'public static bool ClaimsCrop\(Player player, Pickable pickable\)' \
    'publicstaticboolClaimsCrop(Playerplayer,Pickablepickable){if(!Module.Running)returnfalse;if(!pickable)returnfalse;if(!Config.EnableAutomaticFarming)returnfalse;if(!TryGetCropIdentifier(pickable,outvaridentifier))returnfalse;if(Config.SeedSource==SeedSource.Inventory)returntrue;if(player==null)returnfalse;varcontainers=FarmingContainers.Resolve(player);if(!containers.HasAnchors)returnfalse;if(containers.OwnedStorage.Count==0&&containers.OwnedCultivation.Count==0&&containers.OwnedSeedHarvest.Count==0)returnfalse;varposition=pickable.transform.position;varrange=Config.FarmingRange;if(!TryNearestAnchorDistance(containers,position,range,out_))returnfalse;varisSeed=ValheimObject.Flora.HasTag(identifier,"");returncontainers.StorageNear(position,range).Count>0||containers.MatchingPlantingNear(isSeed,position,range).Count>0;}' \
    "AutomaticFarming.ClaimsCrop(Player, Pickable) must keep its audited decision (R7.5): which crops Automatic Pickup leaves to Farming follows from it, and a claim that stopped matching, or matched everything, would leave crops unharvested or take them from Farming"

  for file in "$farming_dir"/*.cs; do
    found="$(strip_code "$file" | count_hits '(^|[^A-Za-z0-9_])Module *\. *Active([^A-Za-z0-9_]|$)')"
    if lookup_failed "$found"; then
      fail "could not search $file for Module.Active; the check was not run"
    elif ((found != 0)); then
      fail "$file must not read Module.Active (R7.5 / R1.5a): between the stop request and the teardown Active is still true while Farming harvests nothing, and a crop claimed on it is left to nobody; found $found"
    fi
  done

  require_single_code_match "$pickup_component" 'AutomaticFarming *\. *AutomaticFarming *\. *ClaimsCrop *\(' \
    "Automatic Pickup must ask AutomaticFarming.ClaimsCrop exactly once, from its periodic crop pickup (R7.5)"

  found="$(strip_code "$farming_patches" | count_hits '\[ *HarmonyPatch *\( *typeof *\(')"
  if lookup_failed "$found"; then
    fail "could not count the Farming patch targets; the check was not run"
  elif ((found != 1)); then
    fail "the Farming patch class must patch exactly its one audited target, Container.GetHoverText: found $found"
  fi
}

# The steps of one method, in the order given: each has to be found in the form given, and each
# after the one before it. Arguments: the file, the declaration pattern, the name the messages give
# the method, then one "what|pattern" row per step.
require_farming_steps_in_order() {
  local file="$1"
  local declaration="$2"
  local label="$3"
  local body
  local row
  local what
  local pattern
  local line
  local previous=0
  local previous_what=""

  shift 3
  body="$(method_body "$file" "$declaration")"
  if [[ -z "$body" ]]; then
    fail "cannot find $label in $file; the check on the order of its access checks and changes was not run"
    return
  fi

  for row in "$@"; do
    what="${row%%|*}"
    pattern="${row#*|}"
    line="$(printf '%s\n' "$body" | first_hit "$pattern")"
    if lookup_failed "$line"; then
      fail "could not search $label for $what; the check on the order of its access checks and changes was not run"
      return
    fi
    if [[ -z "$line" ]]; then
      fail "$label must contain $what, in the guard form this check reads (AC-S11): without it, what follows changes a container or the world unchecked"
      return
    fi
    if ((line <= previous)); then
      fail "$label must come to $what only after $previous_what (AC-S11): a change made first is made without the check that guards it"
    fi
    previous=$line
    previous_what="$what"
  done
}

# AC-S11: every change a farming pass or a designation makes to a container or the world comes after
# the checks that allow it. A container's role is written only after the access check, the claim and
# the owner check; a sapling is placed only where the player may build, and seeds are spent only
# after it is placed; a crop is harvested only by its owner, and replanted only after the harvest;
# and seeds leave a container only after it is claimed. Harvesting has no ward check, as in the game.
require_farming_checks_access_before_changes() {
  local body
  local first

  require_farming_steps_in_order "$farming_roles" 'public static bool TryCycleRole\(' \
    "ContainerRoles.TryCycleRole" \
    'the access check (a failed ContainerAccess.CanUseContainer returns false)|if *\( *! *ContainerAccess *\. *CanUseContainer *\( *player *, *container *\) *\) *return false *;' \
    'the claim (a failed ContainerAccess.TryClaimContainer returns false)|if *\( *! *ContainerAccess *\. *TryClaimContainer *\( *container *\) *\) *return false *;' \
    'the owner check (a container this peer does not own returns false)|if *\( *! *nview *\. *IsOwner *\( *\) *\) *return false *;' \
    'the role write (ZDO Set)|GetZDO *\( *\) *\. *Set *\('
  require_farming_steps_in_order "$farming_planting" \
    'public static bool TryPlant\(Player player, Piece sapling, Plant plant' "CropPlanting.TryPlant" \
    'the ward check (a failed PrivateArea.CheckAccess returns)|if *\( *! *PrivateArea *\. *CheckAccess *\( *pos *, *0f *, *false *\) *\)' \
    'the placement (PlacePiece)|player *\. *PlacePiece *\(' \
    'the seed consumption (ConsumeSeeds)|(^|[^A-Za-z0-9_])ConsumeSeeds *\('
  require_farming_steps_in_order "$farming_core" 'private static void HarvestAndReplant\(' \
    "AutomaticFarming.HarvestAndReplant" \
    'the crop owner check (a crop this peer does not own returns)|if *\( *! *zNetView *\. *IsOwner *\( *\) *\) *return *;' \
    'the harvest (PickableHarvester)|PickableHarvester *\. *Harvest(ToContainers)? *\(' \
    'the replant (Replant)|(^|[^A-Za-z0-9_])Replant *\('

  body="$(method_body "$farming_seed_pool" 'private int RemoveFromContainer\(')"
  first="$(printf '%s\n' "$body" | first_statement)"
  if [[ "$first" != 'if(!ContainerAccess.TryPrepareContainerMutation(_player,container))return0;' ]]; then
    fail "SeedPool.RemoveFromContainer must open with \"if (!ContainerAccess.TryPrepareContainerMutation(_player, container)) return 0;\" (AC-S11): seeds leave only a container this peer has claimed and may use now; found \"$first\""
  fi
}

# R5.1 / R5.2 and R1.5: every entry is bound, in its persisted order and with its default, and
# nothing in the module reads the setting as a flag.
require_farming_config_contract() {
  local body
  local stripped
  local row
  local key
  local pattern
  local line
  local previous=0
  local file

  if ((${#farming_config_entries[@]} != 11)); then
    fail "the Farming config table must name exactly its eleven persisted entries: found ${#farming_config_entries[@]}"
  fi

  stripped="$(strip_code "$farming_config" keep)"
  body="$(printf '%s\n' "$stripped" | extract_block - 'public static void Initialize\(\)')"
  if [[ -z "$body" ]]; then
    fail "the Farming config initializer could not be extracted; its bind contract was not checked"
    return
  fi

  for row in "${farming_config_entries[@]}"; do
    IFS='|' read -r key pattern <<<"$row"
    line="$(printf '%s\n' "$body" | first_match_line "$pattern")"
    if lookup_failed "$line"; then
      fail "could not search the Farming config for $key; its bind check was not run"
    elif [[ -z "$line" ]]; then
      fail "Farming config must bind $key with its audited default and constraints"
    elif ((line <= previous)); then
      fail "Farming config must keep its eleven entries in persisted order; $key is out of order"
    else
      previous=$line
    fi
  done

  require_source_pattern "$farming_config" \
    'public static ConfigEntry<AutomaticsModule> ModuleEntry *=> *_module *;' \
    "Farming Config.ModuleEntry must publicly expose the module toggle"

  for file in "$farming_dir"/*.cs; do
    reject_source_pattern "$file" '_module\s*\.\s*Value' \
      "$file must not read the module setting's value (R1.5): the registry follows the setting, and the entry points read Module.Running"
    reject_source_pattern "$file" 'ModuleEntry\s*\.\s*Value' \
      "$file must not read the module setting's value (R1.5): the registry follows the setting, and the entry points read Module.Running"
  done
}

require_farming_smoke_descriptor() {
  local body
  local found
  local stripped
  local row
  local pattern
  local message

  stripped="$(strip_code "$farming_smoke" keep)"
  body="$(printf '%s\n' "$stripped" | extract_block - 'private static ModuleDescriptor DescribeFarming\(\)')"
  if [[ -z "$body" ]]; then
    fail "the lifecycle smoke must define a Farming descriptor; its probes could not be checked"
    return
  fi

  for row in \
    'Argument *= *"farming"@@the Farming descriptor must be reached by the argument farming' \
    'Module *= *FarmingModule *\. *Instance@@the Farming descriptor must use the registered Farming module' \
    'Active *= *\(\) *=> *FarmingModule *\. *Active *,@@the Farming descriptor must read the Farming applied state' \
    'Running *= *\(\) *=> *FarmingModule *\. *Running *,@@the Farming descriptor must read the Farming Running predicate' \
    'PatchTypes *= *new\[\] *\{ *typeof *\( *FarmingPatches *\) *\}@@the Farming descriptor must declare its one patch type' \
    'Probe *\. *Handlers *\( *"Hooks.OnPlayerAwake" *, *\(\) *=> *Hooks *\. *OnPlayerAwake *, *1 *\)@@the Farming descriptor must see its OnPlayerAwake handler go and come back' \
    'Probe *\. *Handlers *\( *"Hooks.OnPlayerUpdate" *, *\(\) *=> *Hooks *\. *OnPlayerUpdate *, *1 *\)@@the Farming descriptor must see its OnPlayerUpdate handler go and come back' \
    'Probe *\. *Handlers *\( *"Hooks.OnPlayerFixedUpdate" *, *\(\) *=> *Hooks *\. *OnPlayerFixedUpdate *, *1 *\)@@the Farming descriptor must see its OnPlayerFixedUpdate handler go and come back' \
    'Probe *\. *Counter *\( *"farming.OnPlayerAwake.handler" *, *\(\) *=> *CountHandlersOf *\( *Hooks *\. *OnPlayerAwake *, *typeof *\( *FarmingModule *\) *, *"OnPlayerAwake" *\) *, *OffExpectation *\. *Zero *, *0 *, *\(\) *=> *1 *\)@@the Farming descriptor must pin its own OnPlayerAwake handler to exactly one' \
    'Probe *\. *Counter *\( *"farming.OnPlayerUpdate.handler" *, *\(\) *=> *CountHandlersOf *\( *Hooks *\. *OnPlayerUpdate *, *typeof *\( *FarmingModule *\) *, *"OnPlayerUpdate" *\) *, *OffExpectation *\. *Zero *, *0 *, *\(\) *=> *1 *\)@@the Farming descriptor must pin its own OnPlayerUpdate handler to exactly one' \
    'Probe *\. *Counter *\( *"farming.OnPlayerFixedUpdate.handler" *, *\(\) *=> *CountHandlersOf *\( *Hooks *\. *OnPlayerFixedUpdate *, *typeof *\( *FarmingModule *\) *, *"OnPlayerFixedUpdate" *\) *, *OffExpectation *\. *Zero *, *0 *, *\(\) *=> *1 *\)@@the Farming descriptor must pin its own OnPlayerFixedUpdate handler to exactly one' \
    'Probe *\. *Counter *\( *"farming.designated_containers" *, *CountDesignatedContainers *, *OffExpectation *\. *BelowBaseline *, *0 *\)@@the Farming descriptor must hold the number of containers with a farming role at its baseline' \
    'Precondition *= *FarmingPrecondition@@the Farming descriptor must use its crop-protecting precondition' \
    'new ExtraCheck *\( *"session_state_captured" *, *Phase *\. *BeforeRequest@@the Farming descriptor must record the claims, timers and sapling lookup just before every disable' \
    'claimedBeforeDisable *= *CountClaimedCrops *\( *\) *;@@the Farming descriptor must count the claimed crops before every disable' \
    'timersBeforeDisable *= *FarmingModule *\. *FarmingTimerCount *;@@the Farming descriptor must count the farming timers before every disable' \
    'saplingMapBeforeDisable *= *FarmingPlanting *\. *SaplingMapBuilt *;@@the Farming descriptor must record the sapling lookup before every disable' \
    'new ExtraCheck *\( *"claims_released_at_request" *, *Phase *\. *AtRequest@@the Farming descriptor must see the crop claim let go in the frame of the request' \
    'new ExtraCheck *\( *"claims_released_while_off" *, *Phase *\. *WhileOff@@the Farming descriptor must see no crop claimed while off' \
    'new ExtraCheck *\( *"timers_cleared" *, *Phase *\. *WhileOff@@the Farming descriptor must check timer cleanup while off' \
    'new ExtraCheck *\( *"sapling_map_cleared" *, *Phase *\. *WhileOff@@the Farming descriptor must check the sapling lookup is dropped while off' \
    'new ExtraCheck *\( *"claims_restored" *, *Phase *\. *AfterOn@@the Farming descriptor must see the crop claim come back after every enable' \
    'new ExtraCheck *\( *"interval_timer_resumed" *, *Phase *\. *AfterOn *, *IntervalTimerResumed *\)@@the Farming descriptor must see the interval hook take up its timer after every enable'; do
    pattern="${row%%@@*}"
    message="${row#*@@}"
    found="$(printf '%s\n' "$body" | count_hits "$pattern")"
    if lookup_failed "$found"; then
      fail "could not inspect the Farming lifecycle descriptor; the check behind '$message' was not run"
    elif ((found != 1)); then
      fail "$message exactly once: found $found"
    fi
  done

  require_exact_method_body "$farming_smoke" 'private static string FarmingPrecondition\(\)' \
    'privatestaticstringFarmingPrecondition(){varplayer=Player.m_localPlayer;if(!player)return"";if(!PickupModule.Running||!HasCropOrPlantInPickupReach(player))returnnull;return"";}' \
    "FarmingPrecondition must remain the audited decision: run only when Automatic Pickup is not running or reaches no crop and no growing plant"
  require_exact_method_body "$farming_smoke" 'private static bool HasCropOrPlantInPickupReach\(Player player\)' \
    'privatestaticboolHasCropOrPlantInPickupReach(Playerplayer){varreach=PickupConfig.AutomaticPickupRange+1f;varorigin=player.transform.position;foreach(varpickableinPickableCache.GetAllInstance()){if(!pickable||!IsFarmingCrop(pickable))continue;if(Vector3.Distance(origin,pickable.transform.position)<=reach)returntrue;}foreach(varplantinObject.FindObjectsByType<Plant>(FindObjectsInactive.Include,FindObjectsSortMode.None)){if(!plant)continue;if(Vector3.Distance(origin,plant.transform.position)<=reach)returntrue;}returnfalse;}' \
    "HasCropOrPlantInPickupReach must remain the audited scan: every crop Farming would claim, whether or not it runs, and every growing plant, within Automatic Pickup's range + 1 m"
  require_exact_method_body "$farming_smoke" 'private static bool IsFarmingCrop\(Pickable pickable\)' \
    'privatestaticboolIsFarmingCrop(Pickablepickable){returnValheimObject.Flora.GetIdentify(Objects.GetName(pickable),outvaridentifier)&&FarmingConfig.AllowFarmingCrops.Contains(identifier);}' \
    "IsFarmingCrop must remain the crop rule of AutomaticFarming.TryGetCropIdentifier"
  require_exact_method_body "$farming_core" 'private static bool TryGetCropIdentifier\(' \
    'privatestaticboolTryGetCropIdentifier(Pickablepickable,outstringidentifier){varname=Objects.GetName(pickable);returnValheimObject.Flora.GetIdentify(name,outidentifier)&&Config.AllowFarmingCrops.Contains(identifier);}' \
    "AutomaticFarming.TryGetCropIdentifier changed: the lifecycle smoke's IsFarmingCrop mirrors it to decide which crops Automatic Pickup must not reach, so change both together"
  require_exact_method_body "$farming_smoke" 'private static bool FarmsOnAnInterval\(\)' \
    'privatestaticboolFarmsOnAnInterval(){returnFarmingConfig.EnableAutomaticFarming&&FarmingConfig.FarmingInterval>=0.1f&&FarmingConfig.FarmingKey.MainKey==KeyCode.None;}' \
    "FarmsOnAnInterval must remain the guards with which Module.OnPlayerFixedUpdate decides whether it keeps a timer"
  require_exact_method_body "$farming_smoke" 'private static int CountClaimedCrops\(\)' \
    'privatestaticintCountClaimedCrops(){varplayer=Player.m_localPlayer;varclaimed=0;foreach(varpickableinPickableCache.GetAllInstance())if(pickable&&FarmingClaims.ClaimsCrop(player,pickable))claimed++;returnclaimed;}' \
    "CountClaimedCrops must count, and only count, the loaded crops Farming claims for the local player"
  require_exact_method_body "$farming_smoke" 'private static int CountDesignatedContainers\(\)' \
    'privatestaticintCountDesignatedContainers(){vardesignated=0;foreach(varcontainerinContainerCache.GetAllInstance())if(container&&FarmingRoles.GetRole(container)!=FarmingRole.None)designated++;returndesignated;}' \
    "CountDesignatedContainers must count, and only count, the loaded containers with a farming role"
  reject_source_pattern "$farming_smoke" \
    '(FarmingConfig|PickupConfig)\s*\.\s*(ModuleEntry|[A-Za-z_]+\s*=[^=])' \
    "the lifecycle smoke must not change the user's Farming or Pickup settings, or touch their module toggles"
}

require_enable_acquires_in_order "$door_module" scope patch subscribe reconcile
require_disable_lowers_the_flag_first "$door_module"
require_attach_paths_recheck_the_effective_state "$door_module" \
  "internal static void AttachAutomaticDoor"
require_module_enable_and_disable_shape "$door_module" door --patches \
  OnPlayerAwake OnPlayerUpdate OnPlayerFixedUpdate OnDedicatedServerFixedUpdate
require_door_entries_and_callbacks
require_door_acquisition_and_component_ownership
require_door_cleanup_is_non_destructive
require_door_access_checks_precede_opening
require_door_registration_contract
require_door_smoke_descriptor
require_door_existing_smokes_follow_runtime_state

# Automatic Mining: one patch class, three named hook subscriptions, three discovery targets and
# one scoped component. A mining pass is accepted only at its two player-hook gates.
require_enable_acquires_in_order "$mining_module" scope patch subscribe reconcile
require_disable_lowers_the_flag_first "$mining_module"
require_attach_paths_recheck_the_effective_state "$mining_module" \
  "internal static void AttachMineral"
require_module_enable_and_disable_shape "$mining_module" mining --patches \
  OnPlayerAwake OnPlayerUpdate OnPlayerFixedUpdate
require_mining_state_and_registration
require_mining_entries_and_attach_paths
require_mining_component_ownership
require_mining_gate_free_bodies_are_fixed
require_mining_cleanup_contract
require_mining_config_contract
require_mining_smoke_descriptor

# Automatic Feeding: one patch class, no hook subscription, and one scoped component on every
# tameable animal with a ZDO. A feeding call is accepted only at the gate of its static entry point.
require_enable_acquires_in_order "$feeding_module" scope patch reconcile
require_disable_lowers_the_flag_first "$feeding_module"
require_attach_paths_recheck_the_effective_state "$feeding_module" \
  "internal static void AttachFeeding"
require_feeding_state_and_registration
require_feeding_entries_and_call_sites
require_feeding_never_rechecks_after_a_change
require_feeding_checks_ownership_first
require_feeding_component_ownership
require_feeding_gate_free_bodies_are_fixed
require_feeding_config_contract
require_feeding_smoke_descriptor

# Automatic Farming: one patch class, three named hook subscriptions and no component. A farming
# pass is accepted only at its two player-hook gates; the hover postfix and the crop claim
# Automatic Pickup asks are the other two gates.
require_enable_acquires_in_order "$farming_module" patch subscribe
require_disable_lowers_the_flag_first "$farming_module" --no-scope
require_module_enable_and_disable_shape "$farming_module" farming --patches \
  OnPlayerAwake OnPlayerUpdate OnPlayerFixedUpdate
require_module_has_no_component_machinery "$farming_module"
require_hook_handlers_open_with_the_gate "$farming_module" \
  "private static void OnPlayerUpdate\\(" "private static void OnPlayerFixedUpdate\\("
require_item_entries_open_with_the_gate farming_entries "$farming_dir" "$farming_module" 2
require_farming_state_and_registration
require_farming_never_rechecks_after_a_change
require_farming_call_sites
require_farming_session_is_reset
require_farming_crop_claim_contract
require_farming_checks_access_before_changes
require_farming_config_contract
require_farming_smoke_descriptor

# Automatic Storage: one subscription, no patch, no component, and one entry point that moves
# items. StoragePlanner and Config run inside a storage pass, between its gate and its last
# transfer.
require_enable_acquires_in_order "$storage_module" subscribe
require_disable_lowers_the_flag_first "$storage_module" --no-scope
require_module_enable_and_disable_shape "$storage_module" storage OnPlayerUpdate
require_module_has_no_component_machinery "$storage_module"
require_hook_handlers_open_with_the_gate "$storage_module" "private static void OnPlayerUpdate\\("
require_item_entries_open_with_the_gate storage_entries "$storage_dir" "$storage_module" 1
require_item_entries_never_recheck_after_a_change storage_entries "$storage_changes" \
  "$storage_dir/StoragePlanner.cs" "$storage_config"

# Automatic Repair owns no component. Its dispatcher accepts item and piece batches separately;
# private repair helpers finish the accepted batch without another state check.
repair_dir="Automatics/AutomaticRepair"
repair_module="$repair_dir/Module.cs"
repair_item="$repair_dir/ItemRepair.cs"
repair_piece="$repair_dir/PieceRepair.cs"
repair_smoke="smoke/Automatics.SmokeTest/LifecycleSmokeModules.cs"
repair_entries=(
  "Module.OnPlayerFixedUpdate|$repair_module|private static void OnPlayerFixedUpdate\\(|if (!Module.Running) return;"
  "ItemRepair.Repair|$repair_item|public static void Repair\\(|if (!Module.Running) return;"
  "ItemRepair.CraftingStationInteractHook|$repair_item|public static void CraftingStationInteractHook\\(|if (!Module.Running) return;"
  "PieceRepair.Repair|$repair_piece|public static void Repair\\(|if (!Module.Running) return;"
)

require_enable_acquires_in_order "$repair_module" scope patch subscribe
require_disable_lowers_the_flag_first "$repair_module"
require_module_enable_and_disable_shape "$repair_module" repair --patches OnPlayerAwake OnPlayerFixedUpdate
# Repair retains the agreed scope/generation/discovery fields without attaching components.
reject_source_pattern "$repair_module" 'scope\.(Attach|Reconcile)|AddComponent|FindObjectsByType|Reconcile[A-Za-z]+\(' \
  'Repair must not acquire components or reconcile scene objects'
require_item_entries_open_with_the_gate repair_entries "$repair_dir" "$repair_module" 4
require_single_code_match "$repair_module" 'internal static bool Running *=> *Active *&& *!StopRequested *;' \
  'Repair Running must combine the applied state and stop request'
require_single_code_match "$repair_module" 'internal static bool Discovering *=> *Active *&& *CurrentScope *!= *null *;' \
  'Repair Discovering must combine the applied state and scope'
require_single_code_match "$repair_module" '\[AutomaticsInitializer\(5\)\]' \
  'Repair must retain initializer number 5'
require_source_pattern "$repair_module" 'public string Name => "automatic-repair";' 'Repair must retain its registry name'
require_source_pattern "$repair_module" 'public string ConfigSection => "automatic_repair";' 'Repair must retain its config section'
require_single_code_match "$repair_module" 'public bool SupportsRuntimeToggle *=> *true *;' 'Repair must support runtime toggling'
require_single_code_match "$repair_module" 'public ConfigEntry<AutomaticsModule> ModuleToggle *=> *Config.ModuleEntry *;' 'Repair must expose its toggle to the registry'

require_exact_method_body "$repair_module" 'public void SetStopRequested\(' \
  'publicvoidSetStopRequested(boolvalue){StopRequested=value;}' 'Repair SetStopRequested must only record the request'
require_exact_method_body "$repair_module" 'public void BindConfig\(' \
  'publicvoidBindConfig(){Config.Initialize();}' 'Repair BindConfig must bind unconditionally'
require_exact_method_body "$repair_module" 'private static void Initialize\(' \
  'privatestaticvoidInitialize(){ModuleRegistry.Register(Instance);}' 'Repair Initialize must only register its instance'
require_exact_method_body "$repair_module" 'public void Enable\(' \
  'publicvoidEnable(ModuleScopescope){ResetSession();CurrentScope=scope;Generation=scope.Generation;scope.PatchAll(typeof(Patches));scope.Subscribe<Action<Player,ZNetView>>(h=>Hooks.OnPlayerAwake+=h,h=>Hooks.OnPlayerAwake-=h,OnPlayerAwake);scope.Subscribe<Action<Player,float>>(h=>Hooks.OnPlayerFixedUpdate+=h,h=>Hooks.OnPlayerFixedUpdate-=h,OnPlayerFixedUpdate);Active=true;}' \
  'Repair Enable must reset the session, publish its scope, patch, subscribe its two named handlers and become active last'
require_exact_method_body "$repair_module" 'public void Disable\(' \
  'publicvoidDisable(ModuleScopescope){Active=false;Generation=0;CurrentScope=null;ResetSession();}' \
  'Repair Disable must lower Active, drop scope and generation, then reset the session without a gate'
require_exact_method_body "$repair_module" 'private static void ResetSession\(' \
  'privatestaticvoidResetSession(){ItemRepairTimers.Clear();PieceRepairTimers.Clear();ItemRepair.Cleanup();PieceRepair.Cleanup();}' \
  'Repair ResetSession must clear both timers and both repair helpers without a gate'
require_exact_method_body "$repair_module" 'private static void OnPlayerAwake\(' \
  'privatestaticvoidOnPlayerAwake(Playerplayer,ZNetViewzNetView){ResetSession();}' \
  'Repair OnPlayerAwake must only reset the session without a gate'
require_exact_method_body "$repair_item" 'internal static void Cleanup\(' \
  'internalstaticvoidCleanup(){WornItemsBuffer.Clear();}' 'Repair item cleanup must clear its entire worn-item buffer'
require_exact_method_body "$repair_piece" 'internal static void Cleanup\(' \
  'internalstaticvoidCleanup(){PieceBuffer.Clear();PieceSet.Clear();Array.Clear(ColliderBuffer,0,ColliderBuffer.Length);_skipRadiusMethodLookup=false;}' \
  'Repair piece cleanup must clear both collections, the full collider array and the lookup failure flag'

# The common nested-entry checker counts vanilla wearNTear.Repair as a gated Repair call too.
# Use qualified calls here, with a separate count of unqualified calls in each repair helper.
require_repair_call_sites() {
  local files status=0 file stripped count body row name pattern expected total
  files="$(rg --files "$repair_dir" -g '*.cs')" || status=$?
  if ((status != 0)) || [[ -z "$files" ]]; then
    fail "could not list Repair sources (rg exit $status); call-site checks were not run"
    return
  fi
  for name in ItemRepair PieceRepair; do
    total=0
    while IFS= read -r file; do
      stripped="$(strip_code "$file")"
      count="$(printf '%s\n' "$stripped" | count_hits "$name *\\. *Repair *\\(")"
      if lookup_failed "$count"; then fail "could not count $name.Repair calls"; return; fi
      total=$((total + count))
      # An unqualified Repair call outside Module is a second entry, not the vanilla instance call.
      if [[ "$file" != "$repair_module" ]]; then
        count="$(printf '%s\n' "$stripped" | count_hits '(^|[^A-Za-z0-9_.])Repair *\(')"
        expected=0
        [[ "$file" == "$repair_item" || "$file" == "$repair_piece" ]] && expected=1
        if lookup_failed "$count" || [[ "$count" != "$expected" ]]; then
          fail "$file must not call an unqualified gated Repair entry from a helper"
        fi
      fi
    done <<<"$files"
    body="$(method_body "$repair_module" 'private static void OnPlayerFixedUpdate\(')"
    count="$(printf '%s\n' "$body" | count_hits "$name *\\. *Repair *\\(")"
    if lookup_failed "$count" || [[ "$count" != 1 || "$total" != 1 ]]; then
      fail "$name.Repair must be called exactly once, inside Repair OnPlayerFixedUpdate"
    fi
  done
  require_single_code_match "$repair_dir/Patches.cs" 'nameof *\( *ItemRepair *\. *CraftingStationInteractHook *\)' \
    'Repair transpiler must inject the crafting-station entry exactly once'
  for file in "$repair_dir"/*.cs; do
    reject_source_pattern "$file" 'ClaimOwnership|ZDO\s*\.\s*Set|\.Set\(|AddItem\s*\(|RemoveItem\s*\(|ModuleEntry\s*\.\s*Value|_module\s*\.\s*Value' \
      'Repair must not claim ownership, write ZDO/inventory membership or read the module setting'
  done
}
require_repair_call_sites

# A call to the injected entry from another helper would open a second stop boundary. Its only
# production references are its declaration and the transpiler's nameof operand.
repair_hook_total=0
for repair_file in "$repair_dir"/*.cs; do
  repair_code="$(strip_code "$repair_file")"
  if [[ "$repair_file" != "$repair_module" ]]; then
    repair_reads="$(printf '%s\n' "$repair_code" | count_hits "$module_read")"
    repair_gates="$(printf '%s\n' "$repair_code" | count_hits "$module_running_read")"
    if lookup_failed "$repair_reads" "$repair_gates" || [[ "$repair_reads" != "$repair_gates" ]]; then
      fail "$repair_file must not read any module state besides its entry gates"
    fi
  fi
  repair_hooks="$(printf '%s\n' "$repair_code" | count_hits '(^|[^A-Za-z0-9_])CraftingStationInteractHook([^A-Za-z0-9_]|$)')"
  if lookup_failed "$repair_hooks"; then
    fail "could not count the crafting-station entry in $repair_file"
  else
    repair_hook_total=$((repair_hook_total + repair_hooks))
  fi
done
if ((repair_hook_total != 2)); then
  fail 'Repair crafting-station entry must be named only by its declaration and transpiler injection'
fi

for repair_entry in "${repair_entries[@]:1}"; do
  IFS='|' read -r qualified file declaration _ <<<"$repair_entry"
  body="$(method_body "$file" "$declaration")"
  # Both statements precede any local player access or mutation; no check may be delayed to a helper.
  first_two="$(printf '%s\n' "$body" | sed -n '/if (!Module.Running)/,+1p' | sed ':join;N;$!b join;s/[[:space:]]//g')"
  if [[ "$first_two" != 'if(!Module.Running)return;if(player==null||player!=Player.m_localPlayer||!Objects.HasValidOwnership(player))return;' ]]; then
    fail "$qualified must immediately check null, local player and valid ownership after its stop gate"
  fi
done

require_repair_config_contract() {
  local body row key pattern line previous=0 count
  body="$(strip_code "$repair_dir/Config.cs" keep)"
  count="$(printf '%s\n' "$body" | count_hits 'config *\. *Bind *\(')"
  if lookup_failed "$count" || [[ "$count" != 7 ]]; then fail 'Repair must keep exactly seven config binds'; fi
  for row in \
    'module|"module", AutomaticsModule.Disabled, initializer: x =>' \
    'enable_automatic_repair|"enable_automatic_repair", true\)' \
    'crafting_station_search_range|"crafting_station_search_range", 16, \(0, 64\)\)' \
    'repair_items_when_accessing_the_crafting_station|"repair_items_when_accessing_the_crafting_station", false\)' \
    'item_repair_message|"item_repair_message", Message.None\)' \
    'piece_search_range|"piece_search_range", 16, \(0, 64\)\)' \
    'piece_repair_message|"piece_repair_message", Message.None\)'; do
    IFS='|' read -r key pattern <<<"$row"
    line="$(printf '%s\n' "$body" | first_match_line "$pattern")"
    if lookup_failed "$line"; then fail "could not search Repair config for $key";
    elif [[ -z "$line" ]]; then fail "Repair config must bind $key with its audited default and constraints";
    elif ((line <= previous)); then fail "Repair config must keep its seven entries in persisted order; $key is out of order";
    else previous=$line; fi
  done
}
require_repair_config_contract

require_source_pattern "$repair_smoke" '\["repair"\] = DescribeRepair' 'Repair smoke must use the shared descriptor runner'
require_source_pattern "$repair_smoke" 'using RepairPatches = Automatics.AutomaticRepair.Patches;' 'Repair smoke must inspect the Repair patch type'
require_farming_steps_in_order "$repair_smoke" 'public IEnumerator Execute\(' 'Repair shared smoke runner' \
  'disabled setting skip|if *\( *_toggle *\. *Value *!= *AutomaticsModule *\. *Enabled *\) *\{ *_record *\. *SkipAll *\( *\$?"" *\) *; *yield break *; *\}' \
  'toggle ownership|SmokeSession *\. *BeginToggle *\('
require_exact_method_body "$repair_smoke" 'private static ModuleDescriptor DescribeRepair\(' \
  'privatestaticModuleDescriptorDescribeRepair(){varextras=newList<ExtraCheck>{newExtraCheck("",Phase.WhileOff,(record,cycle)=>{varname=record.Check("");if(RepairModule.Running){record.Cycle(name,cycle,false,"","");returnEnumerable.Empty<object>().GetEnumerator();}RepairItems.Repair(null);RepairPieces.Repair(null);RepairItems.CraftingStationInteractHook(null,null);record.Cycle(name,cycle,true,"","");returnEnumerable.Empty<object>().GetEnumerator();})};AddRepairCleanupCheck(extras,typeof(RepairModule),"");AddRepairCleanupCheck(extras,typeof(RepairModule),"");AddRepairCleanupCheck(extras,typeof(RepairItems),"");AddRepairCleanupCheck(extras,typeof(RepairPieces),"");AddRepairCleanupCheck(extras,typeof(RepairPieces),"");AddRepairCleanupCheck(extras,typeof(RepairPieces),"");AddRepairCleanupCheck(extras,typeof(RepairPieces),"");returnnewModuleDescriptor{Argument="",Module=RepairModule.Instance,Active=()=>RepairModule.Active,Running=()=>RepairModule.Running,PatchTypes=new[]{typeof(RepairPatches)},Probes=new[]{Probe.Handlers("",()=>Hooks.OnPlayerAwake,1),Probe.Handlers("",()=>Hooks.OnPlayerFixedUpdate,1),Probe.Counter("",()=>CountHandlersOf(Hooks.OnPlayerAwake,typeof(RepairModule),""),OffExpectation.Zero,0,()=>1),Probe.Counter("",()=>CountHandlersOf(Hooks.OnPlayerFixedUpdate,typeof(RepairModule),""),OffExpectation.Zero,0,()=>1)},Precondition=null,Extras=extras.ToArray()};}' \
  'Repair smoke must use its declared patch and two hook deltas, observe cleanup and call only stopped null entries without spawning or writing settings'
require_exact_method_body "$repair_smoke" 'private static void AddRepairCleanupCheck\(' \
  'privatestaticvoidAddRepairCleanupCheck(List<ExtraCheck>extras,Typeowner,stringfield){varbefore=-1;extras.Add(newExtraCheck(field+"",Phase.BeforeRequest,(record,cycle)=>{before=ReadRepairState(owner,field);record.Cycle(record.Check(field+""),cycle,before>=0,$"",$"");returnEnumerable.Empty<object>().GetEnumerator();}));extras.Add(newExtraCheck(field+"",Phase.WhileOff,(record,cycle)=>{varafter=ReadRepairState(owner,field);varname=record.Check(field+"");if(before<0||after!=0)record.Cycle(name,cycle,false,"",$"");elseif(before==0)record.CycleSkip(name,cycle,$"");elserecord.Cycle(name,cycle,true,$"","");returnEnumerable.Empty<object>().GetEnumerator();}));}' \
  'Repair smoke cleanup must fail unreadable or populated state and skip only a zero baseline'
require_exact_method_body "$repair_smoke" 'private static int ReadRepairState\(' \
  'privatestaticintReadRepairState(Typeowner,stringfieldName){varfield=AccessTools.Field(owner,fieldName);if(field==null)return-1;varvalue=field.GetValue(null);if(valueisboolflag)returnflag?1:0;if(valueisArrayarray)returnarray.Cast<object>().Count(x=>x!=null);if(value==null)return-1;varcount=AccessTools.Property(value.GetType(),"");returncount?.GetValue(value,null)isintnumber?number:-1;}' \
  'Repair smoke must read every retained state without treating failed reflection as empty'
for repair_state in 'RepairModule|ItemRepairTimers' 'RepairModule|PieceRepairTimers' \
  'RepairItems|WornItemsBuffer' 'RepairPieces|PieceBuffer' 'RepairPieces|PieceSet' \
  'RepairPieces|ColliderBuffer' 'RepairPieces|_skipRadiusMethodLookup'; do
  IFS='|' read -r owner field <<<"$repair_state"
  require_source_pattern "$repair_smoke" "AddRepairCleanupCheck\\(extras, typeof\\($owner\\), \"$field\"\\);" \
    "Repair smoke must observe and clear $owner.$field"
done

require_farming_steps_in_order "$repair_piece" 'public static void Repair\(' 'Repair piece batch' \
  'ward guard|if *\( *! *PrivateArea *\. *CheckAccess *\(position\) *\|\| *! *CheckCanRepairPiece *\(player, piece\) *\) *continue *;' \
  'stamina guard|if *\( *! *player *\. *HaveStamina *\(toolData *\. *m_attack *\. *m_attackStamina\) *\) *break *;' \
  'successful repair|if *\( *wearNTear *== *null *\|\| *! *wearNTear *\. *Repair *\(\) *\) *continue *;' \
  'repair effect|piece *\. *m_placeEffect *\. *Create *\(' \
  'stamina payment|player *\. *UseStamina *\(' \
  'eitr payment|player *\. *UseEitr *\(' \
  'durability payment|tool *\. *m_durability *-= *toolData *\. *m_useDurabilityDrain *\* *Game *\. *m_durabilityRate *;'

if ((failures > 0)); then
  exit 1
fi

printf 'PASS module lifecycle static invariant checks\n'
