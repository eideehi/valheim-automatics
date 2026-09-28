# User guide

Use this guide when you want to understand what each Automatics feature does and
which settings change its behavior. For the exact configuration entries,
defaults, and accepted value ranges, see [CONFIG.md](../CONFIG.md).

## Quick links

- [Common configuration concepts](#common-configuration-concepts)
- [Automatic door](#automatic-door)
- [Automatic mapping](#automatic-mapping)
- [Automatic processing](#automatic-processing)
- [Automatic feeding](#automatic-feeding)
- [Automatic repair](#automatic-repair)
- [Automatic mining](#automatic-mining)
- [Automatic storage](#automatic-storage)
- [Automatic pickup](#automatic-pickup)
- [Automatic farming](#automatic-farming)
- [Automatic supply](#automatic-supply)
- [Console commands](#console-commands)

## Start here

Start with the README for a short overview. Use this guide for practical usage
notes, and use `CONFIG.md` when you need the exact option name, default value,
or accepted value range.

Automatics is split into feature modules. Each module has a `Module` option that
turns it on or off, and every module applies that option while the game is
running. Automatic mapping, automatic mining and automatic farming have several
features each, so they also have a switch for their main automatic feature; see
[Module disable options](#module-disable-options).

## Common configuration concepts

Use [Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager)
to edit Automatics options in game.

When an update changes an object list's default, Automatics adds the objects newly
included in that default once to lists that still hold a default it recognizes;
lists whose defaults did not change get no additions. A list you edited is left as
it is, as are some lists kept unchanged since 1.4.0 or earlier; add the new names to
those yourself. Objects you remove afterwards stay removed. Corrections to the
entries themselves, such as a renamed object, apply to every list.

### Module disable options

Each feature section has a `Module` entry. Turn it off when you do not want
Automatics to patch or run that feature at all.

A new install starts with every module turned off. Turn on the modules you want
by setting each section's `Module` option to `Enabled`; the settings
inside each module keep their usual defaults, so turning the module on is
normally all you need. Their other options remain available while the module is off.

An existing configuration file keeps each module on or off as it was: a module
you turned off stays off. If the file comes from an older version and has no
`Module` entry for a module, or one whose value Automatics cannot read,
that module is turned on, as it would have been before this change. Automatics
does this once, and records it in the hidden `defaults_revision` entry of the
file. If it cannot write the file, it logs a warning naming the modules that
stay off for that session, and tries again at the next start. If you delete the
configuration file, the next start is a new install, with every module turned
off.

Automatic supply is the exception: it is new, so an existing configuration
without its `Module` entry keeps it off until you turn it on.

| Module | Entry | When the change takes effect |
| --- | --- | --- |
| Automatic door | `Module` | While the game is running. No restart. |
| Automatic pickup | `Module` | While the game is running. No restart. |
| Automatic processing | `Module` | While the game is running. No restart. |
| Automatic mapping | `Module` | While the game is running. No restart. |
| Automatic mining | `Module` | While the game is running. No restart. |
| Automatic feeding | `Module` | While the game is running. No restart. |
| Automatic farming | `Module` | While the game is running. No restart. |
| Automatic storage | `Module` | While the game is running. No restart. |
| Automatic repair | `Module` | While the game is running. No restart. |
| Automatic supply | `Module` | While the game is running. No restart. |

When you turn automatic door, automatic pickup, automatic processing, automatic
mapping, automatic mining, automatic feeding, automatic farming, automatic repair or automatic
storage off, it stops starting new automatic actions, such as door movement,
pickups, item transfers, pin updates, mining passes, feeding, harvests,
replanting and sowing, repairs, and storage passes, right away, and lets an action that is
already in progress finish. For automatic
storage, `Store Items` does nothing from then on, and a storage pass that has
already started still moves every item it selected. For automatic mining, a
pass that has already started completes all of its selected hits, including
damage, pickaxe durability, noise and skill updates. For automatic feeding, an
animal that has already taken a feed item out of a container or your inventory
still eats it. For automatic farming, a pass that has already started replants
every crop it harvested. For automatic repair, an accepted batch finishes all of
its repairs and tool costs. The switch itself completes
over the next few frames, when the module's patches and hooks are removed, along
with the components automatic door adds to doors, automatic pickup adds to
players and items, automatic mapping adds to fish, birds, ships, rocks and flora,
automatic mining adds to destructible mineral objects, and automatic feeding adds
to animals that can be tamed. Turning any of them
back on also completes over the next few frames, and it then also works on
objects that appeared while it was off. If the Automatics
log reports that this Harmony build cannot remove patches at runtime, turning one
of these modules off only stops it and the patches stay applied until you
restart the game.

Turning automatic mapping off removes the pins it added that are not saved, such
as the pins of creatures and, unless `Save Static Object Pins` is on, of static
objects. Saved pins stay on the map: vehicle and portal pins, static object pins
saved by `Save Static Object Pins`, pins you saved by clicking them, your own
pins, and pins shared by other players. A vehicle pin is first moved to where its
vehicle is. While automatic mapping is off it adds, moves, hides and removes no
pin, so `Hide Pins Outside Explored Area` does not apply either. When you
turn it back on, it recognises the saved pins it added and adds the other pins
again as their objects come into range. A vehicle that moved more than 8 meters
while the module was off can get a second pin; delete the old one.

In automatic mapping, automatic mining and automatic farming, a second switch
turns only the module's main automatic feature on or off: `Pin Nearby Objects
Automatically`, `Mine Nearby Minerals Automatically` and `Harvest And Replant
Automatically`. The module's other features, such as map navigation, area mining
and grid planting, keep working while that switch is off. The other modules have
only the `Module` option. Before this version they also had a switch of their own,
such as `Automatic Door`; a configuration where that switch was off gets its
module turned off once, and the switch is removed.

### User-defined objects

Several allowlists use object definitions. Built-in definitions cover known
Valheim objects. The `General` section lets you add user-defined definitions for
animals, dungeons, flora, minerals, monsters, spawners, spots, doors, vehicles,
other objects, and containers.

User-defined object fields:

| Field | Purpose |
| --- | --- |
| `Identifier` | A unique name used inside Automatics. White spaces are not allowed. |
| `Label` | The name shown in the configuration UI. Internal names are translated when possible. |
| `Pattern` | The internal name or prefab name to match. Prefix with `r/` to use a regular expression. |

After adding a user-defined object, enable it in the matching allowlist, such as
`Allow Pinning Spot` or `Allow Container`. See
[Add user-defined object to Automatics](add-user-defined-object.md) for a
step-by-step example.

### Display name and internal name matching

Some options let you add targets by display name or internal name.

Display names are names shown in game, such as `Boar`, `Deer`, or `Dandelion`.
Display name matching uses case-insensitive partial matching.

Internal names are names used by the game or Automatics, such as `$enemy_boar`,
`$enemy_deer`, `$item_dandelion`, or `@internal_name`. Internal name matching
uses case-insensitive exact matching.

Example target data:

| Display name | Internal name |
| --- | --- |
| Greyling | `$enemy_greyling` |
| Greydwarf | `$enemy_greydwarf` |
| Surtling | `$enemy_surtling` |

Matching result:

| | `Grey` | `ling` | `$enemy_greyling` | `$enemy_greydwarf` | `$enemy_` |
| --- | --- | --- | --- | --- | --- |
| Greyling | Match | Match | Match | No match | No match |
| Greydwarf | Match | No match | No match | Match | No match |
| Surtling | No match | Match | No match | No match | No match |

### Shortcuts and messages

Shortcut settings use BepInEx `KeyboardShortcut` values. Empty shortcut settings
disable that shortcut behavior. Message settings can usually be set to `None`,
`Center`, or `TopLeft`.

## Automatic door

Automatic door opens and closes allowed doors near players.

Key settings:

| Setting | Effect |
| --- | --- |
| `Allow Automatic Door` | Selects which door definitions Automatics may open and close. |
| `Interval To Open` | Controls how often Automatics checks for doors to open. Values below `0.1` disable automatic opening. |
| `Interval To Close` | Controls how often Automatics checks for doors to close. Values below `0.1` disable automatic closing. |
| `Distance For Automatic Opening` | Controls how close a player must be for a door to open. |
| `Distance For Automatic Closing` | Controls how far players must be before an opened door can close. |
| `Automatic Door Enable/Disable Toggle` | Optional shortcut that turns the Automatic door module on and off during play. It works while the module is off too. |
| `Automatic Door Enable/Disable Toggle Message` | Controls where the toggle message appears. |

Doors must be allowed by the door definition list. You can add extra door
definitions with `User-defined Door` in the `General` section.

Automatic closing closes every open allowed door once no player is near it,
including doors you opened by hand. While automatic opening is on, a
`Distance For Automatic Closing` smaller than `Distance For Automatic Opening` is
replaced by the opening distance, so a door you walk up to does not close in
front of you.

Its `Module` option takes effect without a restart. New automatic opening,
closing and shortcut handling stop right away, while an action already in progress
finishes. The switch itself completes over the next few frames. Turning the module
off does not close doors or otherwise change their current open state. Turning it
back on attaches the automatic-door behavior to eligible loaded doors again,
including doors that appeared or were inactive while it was off. See
[Module disable options](#module-disable-options).

If you had automatic door disabled, the first start of this version writes all of
its settings to the configuration file, together with `custom_door` in the General
section. The module stays disabled. Values already present in the file are kept and
apply once you turn the module back on in game.

## Automatic mapping

Automatic mapping adds map pins for nearby configured targets. It handles moving
targets, static objects, locations, portals, and map navigation.

Its `Module` option takes effect without a restart. See
[Module disable options](#module-disable-options), which also describes what
happens to its pins.

If you had automatic mapping disabled, the first start of this version writes all
of its settings to the configuration file, together with `custom_vehicle` and
`custom_other` in the General section. Earlier versions wrote only the `Disable
Module` entry while the module was disabled. The module stays disabled. Values
you changed before you disabled it are kept if they are still in the file, and
they apply once you turn the module back on in game.

Bind `Pin Looked-at Object` (unbound by default) to pin what you look at within
`Object Pin Range` (100 meters, configurable from 10 to 300). Objects use their
localized name and icon-pack icon. A location’s structure, the ground within
its loaded exterior area (also when you look through water or tar) and a
dungeon’s interior get one pin at the location with the name and icon Automatic
Mapping recognizes. An unrecognized location uses its localized discovery label
when available; otherwise the usual object or ground pin applies.
World-generated pieces inside a loaded location’s area also pin the location,
even when detached from its hierarchy. Overlapping areas use the nearest
containing location, and when that location is not recognized the usual pin
applies. Player-built pieces, creatures, trees, deposits, loose items and other
objects such as rune stones keep their own pins. Bare ground outside a location
gets an unnamed generic pin.
These are ordinary saved pins: they survive reload and module toggles and are
not hidden by the unexplored-area filter. A saved non-automatic pin with the
same name within 2 meters prevents a duplicate. Adding a pin, finding a duplicate,
or hitting nothing shows a short message. The shortcut works only while this
module is running and no menu or text input has focus.

### Dynamic objects

Dynamic object mapping covers moving objects:

- animals, including configured fish and birds;
- monsters;
- vehicles, including ships and wagons.

`Dynamic Object Search Range` controls how far from the player Automatics scans.
Set it to `0` to remove dynamic pins and stop dynamic pinning. `Allow Pinning
Animal`, `Allow Pinning Monster`, and `Allow Pinning Vehicle` choose which
targets are pinned. `Not Pinning Tamed Animals` excludes tamed animals from the
animal scan.

Dynamic pins move as the target moves. Vehicle pins are saved map pins.

### Static objects and locations

Static object mapping covers configured flora, minerals, spawners, other
objects, and portals. Location mapping covers configured dungeons and spots.

Key settings:

| Setting | Effect |
| --- | --- |
| `Static Object Search Range` | Controls the static object scan range. Set it to `0` to stop static object, portal and location pinning and remove the unsaved pins it added. Saved pins, including portal pins, stay. |
| `Location Search Range` | Controls the dungeon and spot scan range. Locations are scanned together with static objects, so they follow `Static Object Mapping Interval` and the `Static Object Mapping` shortcut. |
| `Hide Pins Outside Explored Area` | Hides automatic pins outside the explored part of the map. Saved pins are hidden, not deleted; pins that are not saved are not added there at all until the area is explored. |
| `Static Object Mapping Interval` | Controls periodic static mapping. Set it to `0` to map only with the shortcut. |
| `Static Object Caching Interval` | Controls how often the static object cache is rebuilt. |
| `Static Object Mapping` | Optional shortcut that runs static mapping once per key press, whatever the interval is. |
| `Save Static Object Pins` | Controls whether static object pins are saved map pins. |
| `Remove Pins Of Destroyed Object` | Removes Automatics-owned static pins when the source object is destroyed. A saved pin is recognized again when you come back to it, reload the world or turn the module off and on, so it is removed too. |
| `Flora Pins Merge Range` | Groups nearby matching flora into one pin. |
| `Need To Equip Wishbone For Underground Minerals` | Requires Wishbone for pinning underground minerals. |
| `Mapping Performance Log` | Writes mapping performance metrics to the BepInEx log. |

`Allow Pinning Flora`, `Allow Pinning Mineral`, `Allow Pinning Spawner`, `Allow
Pinning Other Object`, `Allow Pinning Dungeon`, `Allow Pinning Spot`, and
`Allow Pinning Portal` choose what can be pinned.

Portal pins use the portal tag as the pin name when available and are always
saved map pins, whatever `Save Static Object Pins` is set to. Dungeons use the
entrance position when Automatics can find an entrance.

To keep an automatic pin that is not saved, left-click it on the large map. It
becomes a saved pin that Automatics no longer tracks, and a short message confirms
it; automatic mapping no longer moves or removes it. For a creature, the saved pin
stays where the creature was, and a new automatic pin follows the creature.

### User-defined mapping targets

Use the `General` user-defined object settings to add mapping targets that are
not covered by built-in definitions. The related allowlist must also be enabled.
For example, add a `User-defined Spot`, then enable it in `Allow Pinning Spot`.

See [Add user-defined object to Automatics](add-user-defined-object.md).

### Custom icon packs

Automatic mapping can load custom map icons from PNG and JSON files. See
[Custom icon pack](custom-icon-pack.md) for the directory layout and
`custom-map-icon.json` format.

### Map navigation

`Map Navigation Start Key` sets the modifier key used on the large map. Hold the
configured key and left-click a map pin to start navigation to that pin. Use the
same action on the current target pin to clear navigation.

While navigation is active, the HUD shows the target name and distance when the
large map is closed.

A swarm of small glowing lights also leads you toward the target while you
navigate, whether the target is on screen or not. It gathers around you and
streams ahead in a straight line a little above the ground, stops appearing
within 5 m of the target, and is hidden while the large map is open. It is only
drawn for you and adds nothing to the world. Turn it off with
`Navigation Guide Swarm`, or change its colour with `Navigation Guide Color`.

## Automatic processing

Automatic processing uses nearby allowed containers to operate supported pieces.
Depending on the piece, Automatics can supply materials, add fuel, store
products, or charge consumable items such as Ballista ammo.

Its `Module` option takes effect without a restart. See
[Module disable options](#module-disable-options).

If you had automatic processing disabled, the first start of this version writes
all of its settings, about 160 entries, to the configuration file. Earlier
versions wrote only the `Module` entry while the module was disabled.
The module stays disabled. Values you changed before you disabled it are kept if
they are still in the file, and they apply once you turn the module back on in
game.

Process types:

| Process | Meaning |
| --- | --- |
| `Craft` | Supplies material items to the processor. |
| `Refuel` | Supplies fuel items. |
| `Store` | Stores produced items in nearby containers. |
| `Charge` | Supplies consumable charge items such as ammo. |

Common settings:

| Setting | Effect |
| --- | --- |
| `Allow Container` | Selects which container definitions Automatics may use. Private chests are excluded by default. |
| `Storage Connection Effect Color` | Sets the color of the container connection effect shown while hovering supported processors. |
| `<piece>: Allow Process` | Selects which available process types are enabled for that piece. |
| `<piece>: Container Search Range` | Controls how far that piece searches for containers. |
| `<piece>: Container Reference Limit` | Limits how many nearby containers are checked. `0` means unlimited. |
| `<piece>: Number Of Materials To Stop Supplying` | Keeps at least that many material items in containers before stopping supply. |
| `<piece>: Number Of Fuels To Stop Refuel` | Keeps at least that many fuel items in containers before stopping refuel. |
| `<piece>: Number Of Product Stacks To Stop Processing` | Stops the piece once all containers it uses together hold that many stacks of its product; a crafting piece also counts what it is still making. A crafting piece stops taking materials; a piece that only stores its product stops moving it into containers. `0` means no limit. |
| `<piece>: Supply Only If Materials Run Out` | Supplies a material only when the processor has no material queued. |
| `<piece>: Refuel Only If Fuels Run Out` | Refuels only when the processor has no fuel. |
| `<piece>: Refuel Only When Materials Supplied` | Refuels only when a material was supplied. |
| `<piece>: Store Only If Product Exists In Container` | Stores only into containers that already contain the product. If no matching container is found, the normal output behavior continues. |
| `<piece>: Number Of Items To Stop Charge` | Keeps at least that many charge items in containers before stopping charge. |

Supported pieces:

| Piece | Available processes | Default enabled processes |
| --- | --- | --- |
| Beehive | Store | Store |
| Bonfire | Refuel | Refuel |
| Blast furnace | Craft, Refuel, Store | Craft, Refuel, Store |
| Campfire | Refuel | Refuel |
| Charcoal kiln | Craft, Store | Craft, Store |
| Cooking station | Craft, Store | Store |
| Fermenter | Craft, Store | Craft, Store |
| Hanging brazier | Refuel | Refuel |
| Hearth | Refuel | Refuel |
| Iron cooking station | Craft, Store | Store |
| Jack-o-turnip | Refuel | Refuel |
| Sconce | Refuel | Refuel |
| Smelter | Craft, Refuel, Store | Craft, Refuel, Store |
| Spinning wheel | Craft, Store | Store |
| Standing blue-burning iron torch | Refuel | Refuel |
| Standing brazier | Refuel | Refuel |
| Standing green-burning iron torch | Refuel | Refuel |
| Standing iron torch | Refuel | Refuel |
| Standing wood torch | Refuel | Refuel |
| Stone oven | Craft, Refuel, Store | Craft, Refuel, Store |
| Windmill | Craft, Store | Store |
| Wisp fountain | Store | Store |
| Sap extractor | Store | Store |
| Eitr refinery | Craft, Refuel, Store | Store |
| Ballista | Charge | Charge |

The fires and lights that only refuel (the bonfire, campfire, hearth, sconce,
jack-o-turnip, braziers and standing torches in the table) share one group of
settings instead of a set each: `Fires And Lights To Refuel` picks which of them
are refuelled (all by default), and the `Fires and lights:` entries (container
search range, container reference limit, fuel to keep, refuel only when out of
fuel) apply to all of them.

The connection effect appears when you hover a processor that Automatics can
identify for source processing and that processor has a positive container
search range.

In multiplayer, each processing piece follows the settings of the player whose
game currently owns it. Turning automatic processing off in your game stops it
for the pieces your game owns; pieces owned by another player keep being
processed with that player's settings. Dedicated servers are not supported, and
automatic processing is not guaranteed to work on one.

## Automatic feeding

Automatic feeding lets configured animals consume valid food from nearby
containers or from the local player's inventory. Automatics uses the animal's
normal consume item list, so the food must be something that animal can eat.

Key settings:

| Setting | Effect |
| --- | --- |
| `Feed Search Range` | Controls how far animals search for feed. `0` uses the animal's own search range. |
| `Need Get Close To Eat The Feed` | Makes the animal move close to the container or player before eating. |
| `Allow To Feed From Container` | Selects whether wild animals, tamed animals, both, or neither can eat from containers. |
| `Allow To Feed From Player` | Selects whether wild animals, tamed animals, both, or neither can eat from the local player's inventory. |

When container feeding is allowed, Automatics searches nearby containers first.
If no container feed is found and player feeding is allowed, it checks the local
player's inventory. When a wild animal is allowed to feed from containers,
Automatics also prevents that animal from treating a feed box as an attack
target when the box contains edible food.

Feeding uses every nearby container you could open yourself; it does not use the
`Allow Container` lists of Automatic processing or Automatic storage. Warded,
private and in-use containers you cannot open are skipped.

The wild and tamed choices can be combined for taming. Setting `Allow To Feed
From Container` to `All` (or `Wild`) lets wild animals eat from a feed box, which
tames them just as food on the ground does. Setting `Allow To Feed From Player`
to `Wild` feeds hungry wild animals near you from the food you carry, which also
tames them; tamed animals keep eating from containers while `Allow To Feed From
Container` includes `Tamed`.

Its `Module` option takes effect without a restart. See
[Module disable options](#module-disable-options).

## Automatic repair

Its `Module` option takes effect without a restart. Turning it off stops
new repair batches immediately; an accepted batch finishes its repairs and tool
costs. The switch completes over the next few frames. Turning it back on keeps
your settings and starts fresh one-second timers. See
[Module disable options](#module-disable-options).

Automatic repair repairs worn items near usable crafting stations and damaged
pieces while you hold a build tool with its repair action selected, when you have
access to the piece and meet its crafting-station requirement.

### Item repair

Automatics checks item repair once per second while the feature is enabled. It
repairs worn items when a usable crafting station is within `Crafting Station
Search Range`. Set that range to `0` to disable periodic item repair. With the
no-cost cheat on, items are repaired every second without a crafting station, even
when the range is `0`.

`Repair Items When Accessing The Crafting Station` also repairs items when the
crafting station GUI is opened.

`Item Repair Message` controls whether repaired item counts are shown.

### Piece repair

Automatics checks nearby pieces once per second while the feature is enabled.
Piece repair requires a build tool, such as a hammer, in your right hand with its
repair action selected, as when you repair by hand. `Piece Search Range` controls
the search range; set it to `0` to disable periodic piece repair.

Each repaired piece costs what a repair by hand costs: stamina and the other costs
of the tool's repair action, and the tool's durability at the world's rate. The
repair animation plays once for each batch.

Automatics respects private-area access and crafting-station requirements before
repairing a piece. `Piece Repair Message` controls whether repaired piece counts
are shown.

## Automatic mining

Automatic mining damages the nearest allowed mineral around the player by using
a usable pickaxe.

Its `Module` option takes effect without a restart. Turning it off stops
new mining immediately and lets a mining pass already in progress finish all of
its selected hits. The module's patches, hooks and mineral components are
removed over the next few frames. Turning it back on also completes over the
next few frames and discovers eligible mineral objects that appeared while it
was off. The settings below remain available while the module is off, ready for
the next enable.

Key settings:

| Setting | Effect |
| --- | --- |
| `Mine Nearby Minerals Automatically` | Turns automatic mining on or off. Area mining is not affected. |
| `Mining Interval` | Controls how often mining is attempted. Set it to `0` to mine only with the shortcut. |
| `Mining Range` | Controls the range used to search for mineral hit positions. `0` uses the pickaxe's attack range. |
| `Allow Mining Mineral` | Selects which mineral definitions Automatics may mine. |
| `Need To Equip Pickaxe For Mining` | Requires the pickaxe to be equipped. If disabled, Automatics can use a pickaxe from the inventory. |
| `Allow Mining Underground Minerals` | Allows underground minerals to be mined. |
| `Need To Equip Wishbone For Mining Underground Minerals` | Requires Wishbone for underground mineral mining. |
| `Attempt Mining` | Optional shortcut that attempts mining once per key press, whatever the interval is. |
| `Area Mining` | When your own pickaxe swing hits an allowed mineral, also hits other allowed minerals and rock sections near the hit. On by default. |
| `Area Mining Radius` | How far from the hit area mining reaches (3 meters by default, 1 to 10). |
| `Area Mining Max Targets` | How many extra minerals or rock sections one swing can hit (8 by default, 1 to 32). |

Ordinary stone rocks (mined for stone) can be added to `Allow Mining Mineral` as
`Rock`. They are not included by default, since rocks are everywhere and each
hit wears the pickaxe.

Automatics checks the pickaxe tool tier against the mineral, uses pickaxe
durability, raises the Pickaxes skill, and applies the pickaxe hit effects.

Area mining applies the same hit as your swing to the nearest allowed targets
the swing did not already hit, following the tool tier, `Allow Mining Mineral`
and the underground mineral settings. Other sections of a large rock count as
separate targets. Each extra target wears the pickaxe by one hit's durability,
scaled by the world's durability modifier, and stops when the pickaxe breaks.
Stamina and the Pickaxes skill gain stay once per swing. Only your own pickaxe
swings expand: hits by automatic mining, other weapons and other characters do
not, and the extra hits never expand again. It works only while this module is
running.

## Automatic storage

Automatic storage moves eligible items from the local player's inventory into
nearby allowed containers when `Store Items` is pressed. If `Store Items` is
empty, storage does not run.

Storage order:

1. Fill existing stacks of the same item.
2. Prefer containers that already contain the same Valheim item type.
3. Use empty containers, then other containers with space.

Key settings:

| Setting | Effect |
| --- | --- |
| `Store Items` | Shortcut that runs one storage pass. Empty means storage is disabled. |
| `Storage Search Range` | Controls how far Automatics searches for containers. |
| `Container Reference Limit` | Limits how many nearby containers are checked. `0` means unlimited. |
| `Allow Container` | Selects which container definitions Automatics may use. Private chests are excluded by default. |
| `Allowed Item Types` | Selects which Valheim item types can be stored. |
| `Excluded Items` | Keeps matching items in the player inventory. |
| `Store Hotbar Items` | Allows hotbar-row items to be stored when enabled. |
| `Storage Message` | Controls whether a nonzero storage result is shown. |

Equipped items, quest items, and hotbar-row items are skipped by default.
`Excluded Items` matches internal item names, prefab names, and localized display
names. Plain entries use case-insensitive partial matching. Prefix an entry with
`r/` to use a regular expression.

Automatic storage checks the same container access rules used by Valheim for
private chests, guard stones, and containers already in use.

Its `Module` option takes effect without a restart. See
[Module disable options](#module-disable-options).

If you had automatic storage disabled, the first start of this version writes
all of its settings to the configuration file. Earlier versions wrote only the
`Module` entry while the module was disabled. The module stays disabled.
Values you changed before you disabled it are kept if they are still in the
file, and they apply once you turn the module back on in game.

## Automatic pickup

Automatic pickup collects nearby items for the local player.

Its `Module` option takes effect without a restart. See
[Module disable options](#module-disable-options).

Modes:

| Mode | Behavior |
| --- | --- |
| Periodic pickup | Runs at `Automatic Pickup Interval` whether or not a `Pickup All Nearby` shortcut is assigned. Set the interval to `0` to turn it off. |
| `Pickup All Nearby` shortcut (Left Shift + E by default) | Hold the shortcut while interacting with a pickable object or item to pick up matching nearby objects. |

Key settings:

| Setting | Effect |
| --- | --- |
| `Automatic Pickup Range` | Controls how far Automatics searches for pickup targets. |
| `Automatic Pickup Interval` | Controls periodic pickup. Set it to `0` to disable periodic pickup. |
| `Pickup All Nearby` | Shortcut for targeted pickup. Clear it to turn targeted pickup off. |

Periodic pickup includes pickable world objects, pickable item objects, and item
drops that allow automatic pickup. Targeted pickup matches the hovered object's
displayed pickable name or item name. Automatics still checks inventory space,
carry weight, tar restrictions, and pieces that should not be picked up.

## Automatic farming

With the module enabled, hold Left Alt while planting with the cultivator to add
more of the same crop in a grid aligned to the placement rotation. `Grid Planting`
is independent of automatic harvesting and sowing. `Grid Planting Size` defaults
to 5 cells per side (1 disables it); `Grid Planting Modifier` can be changed or
left unbound to make every manual crop placement a grid, except while alternate
placement (AltPlace) is held. Spacing uses the crop's grow radius multiplied by
`Sowing Spacing Factor`. Each extra crop costs what a hand-placed crop costs:
inventory seeds (plus seeds from nearby containers while
[Automatic supply](#automatic-supply) is running), build stamina and cultivator
durability at the world's rate. As
in vanilla, seeds are not needed in free-build worlds. Invalid
cells are skipped, and planting stops at the first unaffordable cell. `Seed Source`
and `Seed Reserve` apply only to automatic farming, not these manual grids.

While the modifier is held, the crop preview snaps to the lattice of the nearest
growing crop of the same kind within one grid width: the same spacing and
orientation, turned by the quarter turn closest to your own rotation. The hand-placed
crop and its grid then line up with the earlier rows, so a grid planted next to
an earlier one continues it. The snapped position goes through the game's usual
placement checks (cultivated ground, slope, ward, reach). Grown crops do not
count, and with no such crop nearby, placement is unchanged.

Automatic farming harvests grown crops around the local player and replants the
regrowing sapling at each harvested spot. It runs at the configured interval, and
once each time `Attempt Farming` is pressed; set the interval to `0` to farm only
with the key.

Each pass:

1. Harvests allowlisted crops within `Farming Range`, nearest first.
2. Replants the sapling that regrows each harvested crop at the same spot when
   the spot is plantable and the required input stays at or above `Seed Reserve`.
3. Sows into nearby empty cultivated ground when `Proactive Sowing` is on.

Where harvested yield goes and where planting input comes from depend on
`Seed Source`, described below.

Key settings:

| Setting | Effect |
| --- | --- |
| `Harvest And Replant Automatically` | Turns automatic harvesting, replanting and sowing on or off. Grid planting is not affected. |
| `Farming Interval` | Controls how often farming runs. Set it to `0` to farm only with the shortcut. |
| `Attempt Farming` | Optional shortcut that runs farming once per key press, whatever the interval is. |
| `Farming Range` | Controls how far Automatics searches for crops, cultivated ground, and designated containers. |
| `Allow Farming Crop` | Selects which crops are harvested and replanted. Crops without a plantable sapling are harvested only. |
| `Seed Reserve` | Keeps at least this many of each planting input in the active source; planting stops at the reserve. |
| `Seed Source` | Selects the farming model: `Player Inventory` (the original player-centered behavior) or `Nearby Containers` (container-based, using designated boxes). |
| `Proactive Sowing` | Also sows into nearby empty cultivated ground. |
| `Sowing Spacing Factor` | Multiplies each sapling's grow radius to set the sowing grid spacing. Raise it if sown saplings are too crowded. |
| `Designate Container Role` | Optional shortcut that cycles the role of the container you are looking at (`Nearby Containers` mode; see below). |

### Player Inventory mode

By default (`Seed Source` = `Player Inventory`) farming is player-centered, exactly
as before: crops are searched, harvested, and replanted around you, harvested crops
land in your inventory, and replanting and sowing consume seeds from your inventory.
`Seed Reserve` keeps the reserve within your inventory.

### Nearby Containers mode

Set `Seed Source` to `Nearby Containers` to make farming container-based. Instead of
centering on the player, farming is anchored to containers you designate with the
`Designate Container Role` shortcut. Look at a chest and press the key to cycle its
role:

| Role | Purpose |
| --- | --- |
| `Cultivation` (栽培箱) | Plants the seeds it holds to grow crops. |
| `Seed harvest` (採種箱) | Plants the crops it holds to grow seeds. |
| `Storage` (収納箱) | Never plants; the priority destination for any harvested yield. |
| `None` | Not used by farming. |

Roles are named by purpose rather than by the item they store, because the two
farming activities flip the seed/crop relationship: growing crops plants seeds and
harvests crops, while multiplying seeds plants crops and harvests seeds. A cultivation
box and a seed-harvest box therefore never plant each other's stock, so ordinary seeds
are never planted into a field meant only to multiply seeds, and vice versa. Whether a
crop counts as a seed is read from data (the `seed` tag on the Flora definition,
for example on `CarrotSeeds`, `OnionSeeds`, `TurnipSeeds` and `VineberrySeeds`), not
guessed from its name.

In this mode each pass:

- A cultivation box sows the crop saplings whose seed it holds into empty cultivated
  ground within `Farming Range` of the box; a seed-harvest box sows the seed saplings
  whose crop it holds. The planting input is drawn only from same-role boxes within
  range of the spot, and `Seed Reserve` is kept across that local same-role pool.
- Crops within `Farming Range` of any designated box (cultivation, seed-harvest, or
  storage) are harvested. The yield is classified by tag and deposited into storage
  boxes in range first, otherwise into the matching planting box (crops into a
  cultivation box, seeds into a seed-harvest box), nearest first. If no target has
  room for the full yield, the crop is left standing and retried on a later pass;
  nothing is awarded to the player or dropped. The player inventory is never used in
  this mode.
- Each harvested spot is replanted with the matching sapling, drawing its input from
  the matching-role boxes near the spot. With no matching-role box in range, the spot
  is not replanted.

Designating, sourcing, and depositing only touch containers this client owns and can
open, so a chest behind a ward you lack access to, or one another player is using, is
read for its role but never modified. Roles persist with the save and are visible to
other players. Because the game can only act on loaded objects, the area around the
designated boxes must be loaded (you nearby, or the field otherwise kept loaded);
unloaded or remote fields are not farmed. When two same-role boxes' ranges overlap they
may both sow the shared tiles, which is harmless (they plant the same kind of sapling);
space the boxes by `Farming Range` if you want separate fields.

Self-seeding crops (for example `Barley` and `Flax`) have no
separate seed, so only a cultivation box plants them, from the crop itself. `Vineberry`
grows on a support and is never auto-planted.

Automatic farming places saplings only where they would grow healthy: cultivated
ground when required, the correct biome, no roof overhead, no overheating or
freezing, and enough grow space from other healthy plants. Sowing targets only
ground that is already cultivated; Automatics never modifies terrain. Replanting
and sowing are skipped where the player lacks guard-stone build access.

Proactive sowing scans a grid around the planting origin (the player in
`Player Inventory` mode, each planting box in `Nearby Containers` mode). When
`Farming Range` is large relative to a crop's spacing, sowing covers a bounded
radius nearest the origin rather than the full range, to keep each pass cheap;
harvesting and replanting still use the full `Farming Range`.

While Automatic farming is active, Automatic pickup stops periodically collecting
the crops in `Allow Farming Crop` so farming can harvest and replant them.
Turning the Automatic Farming module off, or turning off `Harvest And Replant Automatically`,
returns those crops to periodic pickup right away; crops Automatic pickup
collects while farming is off are not replanted. Container roles are kept while
the module is off and apply again when you turn it back on. The targeted
`Pickup All Nearby` shortcut still picks up those crops regardless of this.

Its `Module` option takes effect without a restart. See
[Module disable options](#module-disable-options).

## Automatic supply

Automatic supply lets nearby containers supply what you use by hand, as if you
carried it. It is off by default, including in an existing configuration: an
update never turns it on for you.

- **Crafting.** At a crafting station, items in nearby containers count toward
  each requirement, so the recipe list, the "can craft" state and the
  requirement panel show what you can craft with them. When you craft, what your
  inventory lacks is taken from the containers first, and the craft happens only
  when everything is in your inventory; otherwise nothing is crafted and the
  taken items go back. Station level, item quality, upgrade costs and the craft
  multiplier work as in vanilla, and so do worlds without crafting costs.
- **Building and planting.** With the hammer or the cultivator, the build panel
  and the "can build" state count nearby containers too. A piece is placed only
  after its whole cost is in your inventory; if something cannot be taken (your
  inventory is full, for example), nothing is placed and a message says why.
  [Grid planting](#automatic-farming) takes its seeds the same way, and returns
  the seeds it took and did not plant.
- **Station intake.** Adding fuel to a fire, ore or fuel to a smelter-type
  station, or food to a cooking station takes one suitable item from a nearby
  container when you carry none, in the order the station prefers.
- **Order and return.** Your inventory is used first, then containers from the
  nearest. Items taken for an action that does not use them go back to the
  container they came from; anything that cannot go back stays in your inventory.
  Nothing is dropped or created.
- **Access.** Warded, private or group-restricted containers, containers another
  player has open, containers on a wagon in use and graves are never used.

Recipes where you choose one of several ingredients work as in vanilla, and
eating, ammunition, repair, the fermenter and items used directly from the
hotbar are not supplied.

| Setting | Effect |
| --- | --- |
| `Supply Range` | How far from you containers are used, 10 m by default (1–64). |

Its `Module` option takes effect without a restart. See
[Module disable options](#module-disable-options).

## Console commands

Automatics adds console commands for discovery, object lookup, and map pin
cleanup.

### `automatics`

Usage: `automatics (OPTIONS...)`

Displays the usage of commands added by Automatics.

| Option | Description |
| --- | --- |
| `-i, --include=VALUE` | Show only commands whose names include the word. |
| `-e, --exclude=VALUE` | Exclude commands whose names contain the word. |
| `-v, --verbose` | Display detailed command usage. |
| `-h, --help` | Display help. |

### `printnames`

Usage: `printnames (OPTIONS...) (WORD|REGEXP)...`

Outputs internal or display names that contain the specified string or match the
specified regular expression. When multiple arguments are specified, only names
matching all arguments are output.

Use a plain word for partial matching. Prefix a regular expression with `r/`.

Examples:

- `printnames ling r/^[$]enemy_`
- `printnames r/^[$@]location_.+(?<!_(enter|exit))$`
- `printnames mushroom r/^[$]item_.+(?<!_description)$`

| Option | Description |
| --- | --- |
| `-h, --help` | Display help. |

### `printobjects`

Usage: `printobjects [TYPE] (OPTIONS...)`

Displays nearby objects that Automatics can handle. Use it when you need object
names for user-defined object settings.

Supported types: `animal`, `container`, `door`, `dungeon`, `flora`, `mineral`,
`monster`, `spawner`, `spot`, `vehicle`, `other`.

The result can include objects that feel unexpected for the selected type. For
example, Flint can appear in the Flora search because Automatics can treat it as
flora.

When automatic mapping is not running, the mapping types (every type except
`container` and `door`) start with a line saying so: fish, birds and ships are
then missing from the list, and nothing listed is pinned. The command never turns
the module on.

| Option | Description |
| --- | --- |
| `-r, --radius=VALUE` | Search radius. Default: `32` meters. |
| `-n, --number=VALUE` | Number of matching object types to display. Default: `4`. |
| `-i, --include=VALUE` | Show only objects whose internal or display names match the value. Prefix with `r/` for a regular expression. |
| `-e, --exclude=VALUE` | Exclude objects whose internal or display names match the value. Prefix with `r/` for a regular expression. |
| `-h, --help` | Display help. |

### `removemappins`

Usage: `removemappins (OPTIONS...)`

Removes map pins that match the specified conditions. By default, it runs in
safe mode and removes only duplicate pins: when several pins share exactly the
same position, one stays and the others are removed. Temporarily disable Automatic mapping before using this command.

| Option | Description |
| --- | --- |
| `-r, --radius=VALUE` | Maximum distance from the player's position. `0` targets all pins. Default: `0` meters. |
| `-i, --include=VALUE` | Target pins whose names contain this string. |
| `-e, --exclude=VALUE` | Exclude pins whose names contain this string. |
| `-n, --dry-run` | Print what would be removed without deleting pins. |
| `-d, --dangerous-mode` | Include non-duplicate pins. Use carefully because incorrect filters can delete many pins. |
| `-h, --help` | Display help. |

## Related references

- [Configuration reference](../CONFIG.md)
- [Add user-defined object to Automatics](add-user-defined-object.md)
- [Custom icon pack](custom-icon-pack.md)
