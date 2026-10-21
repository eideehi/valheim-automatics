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
- [Automatic woodcutting](#automatic-woodcutting)
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
lists whose defaults did not change get no additions. Objects an update drops from
a default are likewise removed once from lists that still hold exactly the previous
default. A list you edited is left as
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

Automatic supply and automatic woodcutting are the exceptions: they are new, so an
existing configuration without their `Module` entry keeps them off until you turn
them on.

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
| Automatic woodcutting | `Module` | While the game is running. No restart. |

When you turn automatic door, automatic pickup, automatic processing, automatic
mapping, automatic mining, automatic feeding, automatic farming, automatic repair, automatic
storage or automatic woodcutting off, it stops starting new automatic actions, such as door movement,
pickups, item transfers, pin updates, mining passes, feeding, harvests,
replanting and sowing, repairs, storage passes and woodcutting passes, right away, and lets an action that is
already in progress finish. For automatic
storage, `Store Items Key` does nothing from then on, and a storage pass that has
already started still moves every item it selected. For automatic mining, a
pass that has already started completes all of its selected hits, including
damage, pickaxe durability, noise and skill updates. For automatic feeding, an
animal that has already taken a feed item out of a container or your inventory
still eats it. For automatic farming, a pass that has already started replants
every crop it harvested. For automatic repair, an accepted batch finishes all of
its repairs and tool costs. For automatic woodcutting, a pass that has already
started completes its hit, including damage, axe durability, noise and
the skill update. The switch itself completes
over the next few frames, when the module's patches and hooks are removed, along
with the components automatic door adds to doors, automatic pickup adds to
players and items, automatic mapping adds to fish, birds, ships, rocks and flora,
automatic mining adds to destructible mineral objects, automatic feeding adds
to animals that can be tamed, and automatic woodcutting adds to trees, small
trees, logs and tree stumps.
Turning any of them
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

In automatic mapping, automatic mining, automatic farming and automatic
woodcutting, a second switch turns only the module's main automatic feature on or
off: `Pin Nearby Objects Automatically`, `Mine Nearby Minerals Automatically`,
`Harvest And Replant Automatically` and `Cut Nearby Trees Automatically`. The
module's other features, such as map navigation, area mining, grid planting and
area woodcutting, keep working while that switch is off. The other modules have
only the `Module` option. Before this version they also had a switch of their own,
such as `Automatic Door`; a configuration where that switch was off gets its
module turned off once, and the switch is removed.

### User-defined objects

Several allowlists use object definitions. Built-in definitions cover known
Valheim objects. The `General` section lets you add user-defined definitions for
animals, dungeons, flora, minerals, monsters, spawners, spots, doors, vehicles,
other objects, containers, and trees.

User-defined object fields:

| Field | Purpose |
| --- | --- |
| `Identifier` | A unique name used inside Automatics. White spaces are not allowed. |
| `Label` | The name shown in the configuration UI. Internal names are translated when possible. |
| `Pattern` | The internal name or prefab name to match. Prefix with `r/` to use a regular expression. |

After adding a user-defined object, enable it in the matching allowlist, such as
`Allow Pinning Spot` or `Allow Container`. A user-defined tree's pattern matches
the prefab name of the standing tree, or of a small tree that breaks without
falling; its fallen logs and its stump count as that tree. See
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
disable that shortcut behavior. The exception is `Grid Planting Key`: left
empty, every crop you place by hand plants a grid, except while alternate
placement (AltPlace) is held (turn `Grid Planting` off or set
`Grid Planting Size` to 1 to stop grid planting). Message settings can usually
be set to `None`, `Center`, or `TopLeft`.

### Containers in multiplayer

In multiplayer, the modules that move items in and out of containers (storage,
supply, processing, feeding and farming, including farming's container roles)
put items into and take them out of only the containers your game owns. The
game gives each container to one player's game at a time: the player who last
opened it, or a player near it. When processing, feeding or farming cannot do
what it needs with the containers your game owns and a container another
player's game owns would do, your game asks that game for it, the way opening
it by hand does, and uses it on a later pass once your game owns it; the
answer opens no window and shows no message. `Store Items Key` asks on one
press for a container another player's game owns that it would store into,
and stores into it on a later press; such a container counts toward
`Container Reference Limit` on the press that asks for it. `Designate Container
Role Key` asks on one press and changes the role on a later press. Automatic
supply uses only the containers your game already owns and never asks. A
container someone has open is never used or asked for, and a container that
has just changed hands is left alone for a moment. Rarely, a player who has
just opened a chest may still see its contents close and need to open it
again; no items are affected. Each request writes a line to the owner's game
log, as opening a chest does.

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
| `Module Toggle Key` | Optional shortcut that turns the Automatic door module on and off during play. It works while the module is off too. |
| `Module Toggle Message` | Controls where the toggle message appears. |

Doors must be allowed by the door definition list. You can add extra door
definitions with `User-defined Door` in the `General` section. Doors inside a
dungeon are never opened or closed automatically, even when the list allows them.

Automatic closing closes every open allowed door once no player who could open it
is near, including doors you opened by hand. A player within reach holds the door
open even when a piece stands between them and the door. While automatic opening
is on, a `Distance For Automatic Closing` smaller than `Distance For Automatic
Opening` is replaced by the opening distance, so a door you walk up to does not
close in front of you.

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

Bind `Pin Looked-at Object Key` (unbound by default) to pin what you look at within
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
gets an unnamed generic pin. An object that already has an automatic pin, other
than a creature or vehicle, gets that pin saved instead of a new one, as
clicking it on the large map does, and a flora patch, with or without an
automatic pin, gets one pin for the whole patch; pressing the key again there
reports a duplicate.
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
| `Location Search Range` | Controls the dungeon and spot scan range. Locations are scanned together with static objects, so they follow `Static Object Mapping Interval` and `Static Object Mapping Key`. |
| `Hide Pins Outside Explored Area` | Hides automatic pins outside the explored part of the map. Saved pins are hidden, not deleted; pins that are not saved are not added there at all until the area is explored. |
| `Static Object Mapping Interval` | Controls periodic static mapping. Set it to `0` to map only with the shortcut. |
| `Static Object Caching Interval` | Controls how often the static object cache is rebuilt. |
| `Static Object Mapping Key` | Optional shortcut that runs static mapping once per key press, whatever the interval is. |
| `Save Static Object Pins` | Controls whether static object pins are saved map pins. |
| `Remove Pins Of Destroyed Object` | Removes Automatics-owned static pins when the source object is destroyed. A saved pin is recognized again when you come back to it, reload the world or turn the module off and on, so it is removed too. |
| `Flora Pins Merge Range` | Groups nearby matching flora into one pin. |
| `Need To Equip Wishbone For Pinning Underground Minerals` | Requires Wishbone for pinning underground minerals. |
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
`Pin Looked-at Object Key` saves the automatic pin of what you look at the same
way, except for creatures and vehicles.

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

Bind `Navigate to Looked-at Object Key` (unbound by default) to start navigation
to what the crosshair is on, within `Object Pin Range` from the camera. It uses
the object's automatic pin when available, even if label search hides it.
Otherwise, including when its category or unexplored-area filter hides the pin,
it adds a temporary unsaved marker. On a plant that belongs to a flora patch,
it targets the patch as `Pin Looked-at Object Key` does: the patch's automatic
pin or the patch pin you saved, or, when the patch has no pin, a temporary
marker at the patch centre with the patch's merged name. A temporary marker is
removed when navigation ends, you choose another target, or the module is
turned off. Press the shortcut while
looking at the current target to clear navigation. The shortcut works only while
the module is running and no menu or text input has focus.

Creature targets selected with this shortcut or through their automatic map
pins are followed: the destination moves with the creature while it is loaded
and stays at its last position while it is not. Navigation continues when the
creature's dynamic pin disappears out of range, and ends when the creature dies
or its object is removed. When a creature is followed through a temporary
marker, navigation switches to its automatic pin and removes the marker as soon
as the pin appears, so the creature never shows two pins; while a category or
unexplored-area filter hides that pin, the marker stays. A pin you saved
yourself stays a fixed destination.

Navigation ends by itself when you come within `Navigation Arrival Distance`
(5 m by default; 0 turns this off), measured horizontally, of the target after
having been farther away than that during that navigation. It shows the same
"Navigation cleared" message as clearing it by hand. If you start navigating to
something already within that distance, navigation ends only after you have
moved away beyond it and come back. This happens whether `Navigation Guide
Swarm` is on or off.

While navigation is active and the large map is closed, a loaded non-boss
creature target shows Valheim's health and awareness HUD with the horizontal
distance added to its name in brackets, such as `Boar [10m]`, even from farther
away. Bosses keep their usual HUD
behaviour and use the navigation label. Non-creature targets and bosses show a
status-bar-style label with their name, distance and a white bar. For a loaded
player-built piece the bar shows its health; for other targets it is full and
decorative.
Navigation labels use the same bracketed distance format.
The label appears above the object when Automatics can resolve its loaded
instance; otherwise it appears about 2 m above the pin
position. Navigation labels are hidden while the game is paused or the HUD is
hidden.

A swarm of small glowing lights also leads you toward the target while you
navigate, whether the target is on screen or not. When a navigation starts or
switches to a new target, the lights burst out from you in all directions,
including up, down and behind, then turn and stream ahead toward the target a
little above the ground in a straight line, up to 18 m ahead or to the target
when it is nearer. After that, new lights do not spread out; they leave you and
flow straight into the stream. At the target they hover in a small column like a
swarm of midges. The column stands on the ground for a buried target and on the
water surface for an underwater one; a target above the ground keeps its own
height. It sways slowly, its top swaying most. When navigation ends because you
arrived or because you cleared it by hand, the lights scatter like insects and
fade out over about a second. Clearing with the looked-at-object key scatters
them at once. Clearing with a map click scatters them when you close the map,
because the large map hides the swarm; if you start another navigation before
closing the map, they do not scatter. When navigation ends for other reasons,
such as the target dying or disappearing or its pin going away, the lights are
removed at once. The swarm is hidden while the large map is open. It is only
drawn for you and adds nothing to the world.
Turn it off with `Navigation Guide Swarm`, or change its colour with `Navigation Guide Color`,
which also colours the navigation target's icon on the compass.

### Looked-at object bar

Turn on `Looked-at Object Bar` (off by default) to show a bar above the named object
at the crosshair, with its name and horizontal distance in brackets, such as
`Boar [10m]`. Creatures show
Valheim's own health and awareness bar, even beyond its usual display range;
player-built pieces show their health. Bosses and creatures with hidden HUDs
show a name and distance label without health. Other named objects use a full
decorative bar. Bare ground, unnamed meshes and whole locations get no bar, and
an object already shown by navigation or its creature bar gets no extra label.

`Looked-at Object Bar Range` limits the horizontal distance from you (50 meters by
default, configurable from 10 to 300). The object must also be within
`Object Pin Range` from the camera. The bar is hidden while the large map is
open, the game is paused or the HUD is hidden; the module must be running.

### Compass

Turn on `Compass` (off by default) to show a strip at the top centre of the
screen with the cardinal directions and the icons of nearby map pins. Set
`Compass Position` to `Bottom` to show it at the bottom centre instead; the
change applies at once. The strip covers the 180 degrees ahead of the camera:
what is straight ahead sits in the middle, and what is to your left or right at
the ends. Each pin's icon sits in the direction of the pin from you, and the
letters N, E, S and W mark the directions.

`Compass Pin Range` limits the horizontal distance from you (100 meters by
default, configurable from 10 to 300). Of the pins in range ahead of you, the
24 nearest are shown, nearer icons drawn over farther ones. Icons get smaller
and greyer with distance, most quickly close to you: white, fully opaque and
larger than the strip is tall next to you, and about a quarter of that size,
grey and 60 percent opaque at the edge of the range. The compass shows your own, automatic and
shared pins as the map shows them: an icon type you turned off on the map,
`Hide Pins Outside Explored Area`, the categories of `Enhance the Map` and a
search with `Apply to minimap` on hide pins on the compass too. Area markers
are not shown.

The navigation target has an icon of its own, in `Navigation Guide Color`, at
full size and drawn over the other icons, whatever its distance. It does not
take one of the 24 places, and like any pin it is hidden while it lies behind
you.

With `Compass Pin Names` on (the default), the pin you face and the navigation
target are named with their horizontal distance in brackets, such as
`Boar [10m]`. The pin you face is the one nearest the middle of the strip,
within 10 degrees of it. While the navigation target is within those 10
degrees, only the target is named. The names show below the strip at the top
and above it at the bottom.

At the top, the strip moves down while a boss's health bar or the bar of an
ongoing event, such as a raid, would cover it, and moves back once the bar is
gone. At the bottom it stays where it is.

The strip is hidden while the large map is open, the game is paused, the HUD is
hidden, you are dead or the world has no map; the module must be running.

### Large map controls

With `Enhance the Map` on (the default) and the module running, open the large
map to show category controls for Animals, Monsters, Flora, Minerals, Spawners,
Dungeons, Spots, Portals, Vehicles and Other. Turn a category off to hide its
automatic pins on both maps without deleting them. All categories are shown by
default; your choices are saved in `Hidden Map Categories`. You can also edit
that list using `Animal`, `Monster`, `Flora`, `Mineral`, `Spawner`, `Dungeon`,
`Spot`, `Portal`, `Vehicle` and `Other`; unknown names are ignored. Saved
automatic pins whose category is ambiguous or unknown are unaffected by these
category controls.

Type in the search field to show pins whose displayed label contains the text,
ignoring case and surrounding spaces. Automatic, manual and shared pins are
searched; unnamed pins are hidden while a search is active. Vanilla special
markers, such as death, bed, player and event markers, and the current navigation
target are always shown by search. Search always applies on the
large map. Turn on `Apply to minimap` (off by default) to keep it applied on the
minimap after closing the large map. Clear the field to stop searching. Search
text and `Apply to minimap` are not saved; turning off `Enhance the Map` or the module
clears them but keeps category choices.

Manual and shared pins are unaffected by category controls. Search
also leaves the current navigation target visible, but category hiding and
`Hide Pins Outside Explored Area` still apply to automatic target pins.
While the search field has focus, game and mod shortcuts are held as when
editing a pin name. Enter or Escape leaves the field; Enter used to confirm an
IME conversion keeps focus there.

### Pin hover details

With `Enhance the Map` on and the module running, hover a visible pin on the
large map to show its name and horizontal distance from you. Automatic pins
with an associated object also show these details when available:

| Pin kind | Details |
| --- | --- |
| Creature | Current and maximum HP, and level. |
| Pickable flora | `Ready`, or the estimated time until it respawns. If no estimate can be calculated, it says `Picked — respawn estimate unavailable`. |
| Merged flora | How many are ready, and the earliest known estimated respawn. If some members have no data, the count reads `At least … ready`. |
| Mineral deposit with sections | Remaining and total sections. |
| Other mineral deposit | Current and maximum HP. |
| Other pins, including locations, portals, vehicles and spawners | Name and distance, without the above object details. |

Respawn times are estimates from the recorded picking time and respawn interval,
rounded up to whole minutes; they do not guarantee that an object has already
respawned when the estimate reaches zero. Details come from the loaded object
or its stored data when it is not loaded. On a client, details from an unloaded
object carry `May be outdated`; if its data or object definition is unavailable,
the tooltip says `Not loaded — details unavailable`. These two lines are
client-only.

Your own saved flora and mineral pins also show details without a live link:
automatic pins you saved by clicking them or with `Pin Looked-at Object Key`,
the key's flora patch pins, and its other new pins as long as their name is
still the object's name in the current game language; after you change the
game language, those older pins no longer match. A pin you typed with exactly
that name is treated the same way, and a name that belongs to objects of
different categories gets no details.
Shared pins and your other manual pins, typed or made with
`Pin Looked-at Object Key`, show only name and distance. When
you hover a different pin,
Automatics looks in locally available object data from its map sector and
neighbouring sectors, matching the kind,
localized name and position. Single flora pins use the nearest match within
`Flora Pins Merge Range`; mineral pins use horizontal distance and a lookup
radius derived from the deposit's collider footprint, at least 4 meters. Merged
flora pins start with matching pickables within the merge range of the pin,
then include matching neighbours linked within that range, including chains.
These matches are kept while you hover the same pin. If no object can be found
for your saved flora or mineral pin, it shows
`No live link — details appear once the object is found again`. Automatic
creature pins and automatic pins whose category is unknown also use this line
when their live link is missing. Hover details are hidden while the search
field has focus.

## Automatic processing

Automatic processing uses nearby allowed containers to operate supported pieces.
Depending on the piece, Automatics can supply materials, add fuel, store
products, or charge consumable items such as Ballista ammo.

Its `Module` option takes effect without a restart. See
[Module disable options](#module-disable-options).

If you had automatic processing disabled, the first start of this version writes
all of its settings, about 110 entries, to the configuration file. Earlier
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
| `<piece>: Materials To Keep In Containers` | Keeps at least that many material items in containers before stopping supply. |
| `<piece>: Fuel To Keep In Containers` | Keeps at least that many fuel items in containers before stopping refuel. |
| `<piece>: Number Of Product Stacks To Stop Processing` | Stops the piece once all containers it uses together hold that many stacks of its product; a crafting piece also counts what it is still making. A crafting piece stops taking materials; a piece that only stores its product stops moving it into containers. `0` means no limit. |
| `<piece>: Supply Only If Materials Run Out` | Supplies a material only when the processor has no material queued. The fermenter has no such option: it only takes materials while empty. |
| `<piece>: Refuel Only If Fuels Run Out` | Refuels only when the processor has no fuel. |
| `<piece>: Refuel Only When Materials Supplied` | Refuels only while the piece holds materials waiting to be processed (for the stone oven, food waiting to be cooked), also after it has run out of fuel; with nothing waiting, it is not refuelled. A stone oven without fuel still takes food from containers while fuel for it is nearby, so it can start from empty. |
| `<piece>: Store Only If Product Exists In Container` | Stores only into containers that already contain the product. If no matching container is found, the normal output behavior continues. |
| `<piece>: Charge Items To Keep In Containers` | Keeps at least that many charge items in containers before stopping charge. |

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

At a cooking station, iron cooking station or stone oven, the player who built it
gains the skill and bonus food for what Automatics does there, as the game gives
them to whoever puts food in or takes it out by hand: supplying food counts as
putting it in, and storing cooked food as taking it out. Bonus food goes into the
same container as the food; bonus food that does not fit there is not given. A
player's skill can only be raised in their own game, so the builder gains these
only while their game owns the piece. While another player's game owns it, and
at pieces no player built, nobody gains them.

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
| `Need To Get Close To Eat The Feed` | Makes the animal move close to the container or player before eating. |
| `Allow To Feed From Container` | Selects whether wild animals, tamed animals, both, or neither can eat from containers. |
| `Allow To Feed From Player` | Selects whether wild animals, tamed animals, both, or neither can eat from the local player's inventory. |

When container feeding is allowed, Automatics searches nearby containers first.
If no container feed is found and player feeding is allowed, it checks the local
player's inventory. When a wild animal is allowed to feed from containers,
Automatics also prevents that animal from treating a feed box as an attack
target when the box contains edible food.

Feeding uses every nearby container you could open yourself; it does not use the
`Allow Container` lists of Automatic processing or Automatic storage. Warded,
private and in-use containers you cannot open are skipped, and so are graves.

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
| `Attempt Mining Key` | Optional shortcut that attempts mining once per key press, whatever the interval is. |
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
nearby allowed containers when `Store Items Key` is pressed. If `Store Items Key` is
empty, storage does not run.

By default, items go only into containers that already hold the same item name.
Quality and world level only determine which stacks can be joined. Storage order:

1. Fill matching existing stacks first.
2. Use free slots in containers holding the same item, nearest first.

Empty containers and containers holding only unrelated items are not used by default.
Enable `Store Into Any Container` to restore the broader routing: fill matching
stacks first, then use containers holding the same Valheim item type, empty
containers, and other containers with space.

After storing, only containers that received items are sorted by item type, then
localized display name, with matching partial stacks merged and rows filled from
the top-left; `Sort Containers After Storing` controls this and is on by default.

Key settings:

| Setting | Effect |
| --- | --- |
| `Store Items Key` | Shortcut that runs one storage pass. Empty means storage is disabled. |
| `Storage Search Range` | Controls how far Automatics searches for containers. |
| `Container Reference Limit` | Limits how many nearby containers are checked. `0` means unlimited. |
| `Allow Container` | Selects which container definitions Automatics may use. Private chests are excluded by default. |
| `Allowed Item Types` | Selects which Valheim item types can be stored. |
| `Excluded Items` | Keeps matching items in the player inventory. |
| `Store Hotbar Items` | Allows hotbar-row items to be stored when enabled. |
| `Store Into Any Container` | Allows storage into containers that do not already hold the same item. Off by default. |
| `Sort Containers After Storing` | Sorts containers that received items and merges matching partial stacks. On by default. |
| `Slot Lock Key` | Hold this key (Left Alt by default) and left-click a player inventory slot to toggle its lock. |
| `Storage Message` | Controls whether a nonzero storage result is shown. |

Equipped items, quest items, and hotbar-row items are skipped by default.
With the inventory open, you can lock even an empty slot. Items in locked slots are never
stored, and a key icon marks each locked slot while Automatic Storage runs. Locks are saved
with the character and remain saved if the module is turned off.

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
| Periodic pickup | Runs at `Automatic Pickup Interval` whether or not `Pickup All Nearby Key` is assigned. Set the interval to `0` to turn it off. |
| `Pickup All Nearby Key` (Left Shift + E by default) | Hold the shortcut while interacting with a pickable object or item to pick up matching nearby objects. |

Key settings:

| Setting | Effect |
| --- | --- |
| `Automatic Pickup Range` | Controls how far Automatics searches for pickup targets. |
| `Automatic Pickup Interval` | Controls periodic pickup. Set it to `0` to disable periodic pickup. |
| `Pickup All Nearby Key` | Shortcut for targeted pickup. Clear it to turn targeted pickup off. |

Periodic pickup includes pickable world objects, pickable item objects, and item
drops that allow automatic pickup. Targeted pickup matches the hovered object's
displayed pickable name or item name. Automatics still checks inventory space,
carry weight, tar restrictions, and pieces that should not be picked up.

## Automatic farming

With the module enabled, hold Left Alt while planting with the cultivator to add
more of the same crop in a grid aligned to the placement rotation. Tree saplings
get a grid the same way; their cells need no cultivated ground. `Grid Planting`
is independent of automatic harvesting and sowing. `Grid Planting Size` defaults
to 5 cells per side (1 disables it); `Grid Planting Key` can be changed or
left unbound to make every manual crop placement a grid, except while alternate
placement (AltPlace) is held. Spacing uses the crop's grow radius multiplied by
`Sowing Spacing Factor`. Each extra crop costs what a hand-placed crop costs:
inventory seeds (plus seeds from nearby containers while
[Automatic supply](#automatic-supply) is running), build stamina and cultivator
durability at the world's rate. As
in vanilla, seeds are not needed in free-build worlds. Invalid
cells are skipped, and planting stops at the first unaffordable cell. `Farming Mode`
and `Seed Reserve` apply only to automatic farming, not these manual grids.

While the modifier is held, the crop preview snaps to the lattice of the nearest
growing crop of the same kind within one grid width: the same spacing and
orientation, turned by the quarter turn closest to your own rotation. The hand-placed
crop and its grid then line up with the earlier rows, so a grid planted next to
an earlier one continues it. The snapped position goes through the game's usual
placement checks (cultivated ground, slope, ward, reach). Grown crops do not
count, and with no such crop nearby, placement is unchanged. The preview goes only
to lattice points where the crop has room to grow, which rules out a point next to
a growing crop at the default spacing or beside an obstacle: it takes the nearest
corner of the aimed lattice square that has room. When no corner has room, the
preview shows as invalid and placing it is refused with the game's
need-more-space message.

Automatic farming harvests grown crops around the local player and replants the
regrowing sapling at each harvested spot. It runs at the configured interval, and
once each time `Attempt Farming Key` is pressed; set the interval to `0` to farm only
with the key.

Each pass:

1. Harvests allowlisted crops within `Farming Range`, nearest first.
2. Replants the sapling that regrows each harvested crop at the same spot when
   the spot is plantable and the required input is available. Replanting does not
   keep `Seed Reserve`: it only puts back what was harvested.
3. Sows into nearby empty cultivated ground when `Proactive Sowing` is on, keeping
   `Seed Reserve` of each planting input.

Where farming works, where harvested yield goes and where planting input comes
from depend on `Farming Mode`, described below.

Key settings:

| Setting | Effect |
| --- | --- |
| `Harvest And Replant Automatically` | Turns automatic harvesting, replanting and sowing on or off. Grid planting is not affected. |
| `Farming Interval` | Controls how often farming runs. Set it to `0` to farm only with the shortcut. |
| `Attempt Farming Key` | Optional shortcut that runs farming once per key press, whatever the interval is. |
| `Farming Range` | How far farming harvests, replants and sows: around you, or around each designated container in `Nearby Containers` mode. |
| `Container Search Range` | How far farming looks for role containers (`Player With Containers` and `Nearby Containers` modes): from each crop, and when sowing, from the designated container or from you. An existing configuration starts with its `Farming Range` value. |
| `Allow Farming Crop` | Selects which crops are harvested and replanted. By default it lists only the crops the cultivator can plant; a wild plant you add, such as berries or mushrooms, has no plantable sapling and is harvested only. |
| `Seed Reserve` | Keeps at least this many of each planting input where farming takes it from; sowing new ground stops at the reserve. Replanting a harvested spot does not keep it. |
| `Farming Mode` | Selects where farming works and keeps its items: `Player Inventory`, `Player With Containers` or `Nearby Containers`. |
| `Proactive Sowing` | Also sows into nearby empty cultivated ground, continuing the rows of a growing crop of the same kind nearby. |
| `Sowing Spacing Factor` | Multiplies each sapling's grow radius to set the sowing grid spacing. Raise it if sown saplings are too crowded. |
| `Designate Container Role Key` | Optional shortcut that cycles the role of the container you are looking at (`Player With Containers` and `Nearby Containers` modes; see below). |

### Player Inventory mode

By default (`Farming Mode` = `Player Inventory`) farming is player-centered: crops
are searched, harvested, and replanted around you, harvested crops land in your
inventory, and replanting and sowing consume seeds from your inventory. `Seed
Reserve` keeps the reserve within your inventory.

### Player With Containers mode

Set `Farming Mode` to `Player With Containers` to farm around you while keeping the
items in chests. Crops within `Farming Range` of you are harvested into the role
containers (see the roles below) within `Container Search Range` of each crop:
storage boxes first, otherwise the matching planting box. Each spot is replanted from
the matching planting boxes near it, and sowing around you draws from the planting
boxes within `Container Search Range` of you and sows only where a container within
that range of the spot can take the grown crop. A crop with no container that can
take its yield is left standing. Your inventory is never used.

### Nearby Containers mode

Set `Farming Mode` to `Nearby Containers` to make farming container-based. Instead of
centering on the player, farming is anchored to containers you designate with the
`Designate Container Role Key` shortcut. Look at a chest and press the key to cycle its
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
  `Container Search Range` of the box, and `Seed Reserve` is kept across that local
  same-role pool.
- Crops within `Farming Range` of any designated box (cultivation, seed-harvest, or
  storage) are harvested. The yield is classified by tag and deposited into storage
  boxes within `Container Search Range` of the crop first, otherwise into the matching planting box (crops into a
  cultivation box, seeds into a seed-harvest box), nearest first. If no target has
  room for the full yield, the crop is left standing and retried on a later pass;
  nothing is awarded to the player or dropped. The player inventory is never used in
  this mode.
- Each harvested spot is replanted with the matching sapling, drawing its input from
  the matching-role boxes near the spot. With no matching-role box in range, the spot
  is not replanted.

A grave cannot be given a role. Designating, sourcing, and depositing only touch
containers this client owns and can open, so a chest behind a ward you lack access
to, or one another player is using, is read for its role but never modified. A box
another player's game owns is asked for when farming would use it (see
[Containers in multiplayer](#containers-in-multiplayer)). Roles
persist with the save and are visible to other players. Because the game can only
act on loaded objects, the area around the designated boxes must be loaded (you
nearby, or the field otherwise kept loaded); unloaded or remote fields are not
farmed. When two same-role boxes' ranges overlap they may both sow the shared
tiles, which is harmless (they plant the same kind of sapling); space the boxes by
`Farming Range` if you want separate fields.

Self-seeding crops (for example `Barley` and `Flax`) have no
separate seed, so only a cultivation box plants them, from the crop itself. `Vineberry`
grows on a support and is never auto-planted.

Automatic farming places saplings only where they would grow healthy: cultivated
ground when required, the correct biome, no roof overhead, no overheating or
freezing, and enough grow space from other healthy plants. Sowing targets only
ground that is already cultivated; Automatics never modifies terrain. Replanting
and sowing are skipped where the player lacks guard-stone build access.

Proactive sowing scans a grid around the planting origin (the player in the
`Player Inventory` and `Player With Containers` modes, each planting box in
`Nearby Containers` mode). When a growing crop of the same kind is within the
range, the grid takes its position and orientation (at the `Sowing Spacing Factor`
spacing), so sown crops line up with a grid you planted by hand; otherwise it is
aligned to the world axes. When
`Farming Range` is large relative to a crop's spacing, sowing covers a bounded
radius nearest the origin rather than the full range, to keep each pass cheap;
harvesting and replanting still use the full `Farming Range`.

While Automatic farming is active, Automatic pickup stops periodically collecting
the crops in `Allow Farming Crop` that farming would harvest itself, so farming
can harvest and replant them: crops your game controls within `Farming Range` of
you (`Player Inventory`), of you with a role container within `Container Search
Range` of the crop (`Player With Containers`), or of a designated box with such a
container near the crop (`Nearby Containers`). Periodic
pickup still collects the others, such as crops farther away and the wild berries
and mushrooms that `Allow Farming Crop` leaves out by default. In
multiplayer, a crop another player's game controls is left to pickup and is not
replanted.
Turning the Automatic Farming module off, turning off `Harvest And Replant Automatically`,
or setting `Farming Interval` to `0` while `Attempt Farming Key` is unbound
returns those crops to periodic pickup right away; crops Automatic pickup
collects while farming is off are not replanted. Container roles are kept while
the module is off and apply again when you turn it back on. The targeted
`Pickup All Nearby Key` shortcut still picks up those crops regardless of this.

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

## Automatic woodcutting

Automatic woodcutting chops trees, small trees, fallen logs and tree stumps
around the player with an axe. It is off by default, including in an existing
configuration: an update never turns it on for you.

Each attempt hits one target once, the way a swing of the axe would, and then
stops. The target is the nearest tree, log or stump that:

- is of a kind `Allow Woodcutting Tree` allows. A small tree is of the kind its
  own name gives, so a small beech is a beech, and a fallen log or a stump counts
  as the tree it came from;
- can take a hit now: a log that a felling has just spawned is skipped for the
  moment the game ignores hits on it;
- an axe can cut: the axe has durability left and a tool tier of at least the
  target's. It is the axe in your right hand; with `Need To Equip Axe For
  Woodcutting` off, when the axe in hand cannot cut the target or you hold none,
  it is the lowest-tier axe in your inventory that can;
- lies within `Woodcutting Range` of your eyes;
- nothing hides from your eyes: a wall, a rock or another tree in between stops
  the hit.

When no target qualifies, nothing happens and nothing is spent.

The hit counts as yours and lands on the side facing you, so a felled tree falls
away from you. It wears the axe by one hit's durability, scaled by the world's
durability modifier, makes noise and raises the skill a swing of that axe raises
on that target, if any. It uses no stamina and does not push a log.

The periodic run fells any allowed tree in range, including trees you planted and
trees next to your buildings, whenever you have a usable axe. With area
woodcutting, one swing can fell the tree you hit and up to `Area Woodcutting Max
Targets` more, nine trees in all by default. Falling trees and rolling logs can
hurt you and damage your buildings, as they do when you chop by hand. Set
`Woodcutting Interval` to `0` to chop only with the shortcut, turn `Area
Woodcutting` off, or put the axe away near trees you want to keep.

| Setting | Effect |
| --- | --- |
| `Cut Nearby Trees Automatically` | Turns automatic woodcutting on or off. Area woodcutting is not affected. On by default. |
| `Woodcutting Interval` | How often woodcutting is attempted, 1.5 seconds by default (0–4). Set it to `0` to chop only with the shortcut. |
| `Woodcutting Range` | How far from your eyes a tree, log or stump can be hit, 3 m by default (0–32). `0` uses the axe's attack range. |
| `Allow Woodcutting Tree` | Which kinds of tree automatic woodcutting and area woodcutting may cut. Every kind by default. |
| `Need To Equip Axe For Woodcutting` | Requires the axe to be in your right hand. If disabled, Automatics can use an axe from the inventory. On by default. |
| `Attempt Woodcutting Key` | Optional shortcut that attempts woodcutting once per key press, whatever the interval is. Unbound by default. |
| `Area Woodcutting` | When your own axe swing hits an allowed tree, log or stump, also hits other allowed ones near the hit. On by default. |
| `Area Woodcutting Radius` | How far from the hit area woodcutting reaches (3 meters by default, 1 to 10). |
| `Area Woodcutting Max Targets` | How many extra trees, logs or stumps one swing can hit (8 by default, 1 to 32). |
| `Area Woodcutting Same Kind First` | Takes the targets of the same kind as the one you hit before the others. On by default. |

When your own axe swing hits an allowed tree, log or stump, area woodcutting
applies the same hit to other allowed trees, logs and stumps within `Area
Woodcutting Radius` of that first hit, up to `Area Woodcutting Max Targets`.
With `Area Woodcutting Same Kind First` on (the default), targets of the same
kind as the one you hit come before the others: of the same form, a standing or
small tree, a fallen log or a stump, and of the same kind of tree. A swing at a
beech goes on to the other beeches around it before their logs and stumps and
before trees of other kinds. Within each group the nearest come first, and the
cap counts both groups together. With it off, the nearest targets are taken
whatever their kind.
It skips targets the swing already hit, targets the axe's tool tier does not reach
and logs that cannot take a hit yet, and it does not need a clear line to them.
Each extra target wears the axe by one hit's durability, scaled by the world's
durability modifier, and it stops when the axe breaks. Stamina and the skill gain
stay once per swing. Only your own axe swings expand: hits by automatic
woodcutting, other weapons and other characters do not, and the extra hits never
expand again. It works only while this module is running, and also while `Cut
Nearby Trees Automatically` is off.

Trees from other mods can be added in `User-defined Tree`; see
[User-defined objects](#user-defined-objects).

Its `Module` option takes effect without a restart. Turning it off stops new
woodcutting immediately and lets a pass already in progress finish its hit. The
module's patches, hooks and tree components are removed over the next few frames.
Turning it back on also completes over the next few frames and finds trees,
small trees, logs and stumps that appeared while it was off. See
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
