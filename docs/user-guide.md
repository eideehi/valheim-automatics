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
- [Automatic pickup](#automatic-pickup)
- [Console commands](#console-commands)

## Start here

Start with the README for a short overview. Use this guide for practical usage
notes, and use `CONFIG.md` when you need the exact option name, default value,
or accepted value range.

Automatics is split into feature modules. Each module has a `Disable Module
(Reboot Required)` option, and most modules also have a separate on/off option
for the feature behavior.

## Common configuration concepts

Use [Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager)
to edit Automatics options in game.

### Module disable options

Each feature section has a `Disable Module (Reboot Required)` entry. Use it when
you do not want Automatics to patch or run that feature at all. Restart the game
after changing this option.

Feature-specific enable options, such as `Automatic Door` or `Automatic
Mapping`, only turn the feature behavior on or off while the module stays
loaded.

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
| `Automatic Door` | Turns automatic opening and closing on or off. |
| `Allow Automatic Door` | Selects which door definitions Automatics may open and close. |
| `Interval To Open` | Controls how often Automatics checks for doors to open. Values below `0.1` disable automatic opening. |
| `Interval To Close` | Controls how often Automatics checks for doors to close. Values below `0.1` disable automatic closing. |
| `Distance For Automatic Opening` | Controls how close a player must be for a door to open. |
| `Distance For Automatic Closing` | Controls how far players must be before an opened door can close. |
| `Automatic Door Enable/Disable Toggle` | Optional shortcut to toggle the feature during play. |
| `Automatic Door Enable/Disable Toggle Message` | Controls where the toggle message appears. |

Doors must be allowed by the door definition list. You can add extra door
definitions with `User-defined Door` in the `General` section.

## Automatic mapping

Automatic mapping adds map pins for nearby configured targets. It handles moving
targets, static objects, locations, portals, and map navigation.

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
| `Static Object Search Range` | Controls the static object scan range. Set it to `0` to remove static pins and stop static object pinning. |
| `Location Search Range` | Controls the dungeon and spot scan range. |
| `Static Object Mapping Interval` | Controls periodic static mapping when no static mapping shortcut is assigned. |
| `Static Object Caching Interval` | Controls how often the static object cache is rebuilt. |
| `Static Object Mapping` | Optional shortcut that runs static mapping once per key press instead of using the interval. |
| `Save Static Object Pins` | Controls whether static object pins are saved map pins. |
| `Remove Pins Of Destroyed Object` | Removes Automatics-owned static pins when the source object is destroyed. |
| `Flora Pins Merge Range` | Groups nearby matching flora into one pin. |
| `Need To Equip Wishbone For Underground Minerals` | Requires Wishbone for pinning underground minerals. |
| `Mapping Performance Log` | Writes mapping performance metrics to the BepInEx log. |

`Allow Pinning Flora`, `Allow Pinning Mineral`, `Allow Pinning Spawner`, `Allow
Pinning Other Object`, `Allow Pinning Dungeon`, `Allow Pinning Spot`, and
`Allow Pinning Portal` choose what can be pinned.

Portal pins use the portal tag as the pin name when available. Dungeons use the
entrance position when Automatics can find an entrance.

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

## Automatic processing

Automatic processing uses nearby allowed containers to operate supported pieces.
Depending on the piece, Automatics can supply materials, add fuel, store
products, or charge consumable items such as Ballista ammo.

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
| `Automatic Processing` | Turns automatic processing on or off. |
| `Allow Container` | Selects which container definitions Automatics may use. Private chests are excluded by default. |
| `Storage Connection Effect Color` | Sets the color of the container connection effect shown while hovering supported processors. |
| `<piece>: Allow Process` | Selects which available process types are enabled for that piece. |
| `<piece>: Container Search Range` | Controls how far that piece searches for containers. |
| `<piece>: Container Reference Limit` | Limits how many nearby containers are checked. `0` means unlimited. |
| `<piece>: Number Of Materials To Stop Supplying` | Keeps at least that many material items in containers before stopping supply. |
| `<piece>: Number Of Fuels To Stop Refuel` | Keeps at least that many fuel items in containers before stopping refuel. |
| `<piece>: Number Of Product Stacks To Stop Craft` | Stops crafting when enough product stacks are already stored. |
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

The connection effect appears when you hover a processor that Automatics can
identify for source processing and that processor has a positive container
search range.

## Automatic feeding

Automatic feeding lets configured animals consume valid food from nearby
containers or from the local player's inventory. Automatics uses the animal's
normal consume item list, so the food must be something that animal can eat.

Key settings:

| Setting | Effect |
| --- | --- |
| `Automatic Feeding` | Turns automatic feeding on or off. |
| `Feed Search Range` | Controls how far animals search for feed. `0` uses the animal's own search range. |
| `Need Get Close To Eat The Feed` | Makes the animal move close to the container or player before eating. |
| `Allow To Feed From Container` | Selects whether wild animals, tamed animals, both, or neither can eat from containers. |
| `Allow To Feed From Player` | Selects whether wild animals, tamed animals, both, or neither can eat from the local player's inventory. |

When container feeding is allowed, Automatics searches nearby containers first.
If no container feed is found and player feeding is allowed, it checks the local
player's inventory. When a wild animal is allowed to feed from containers,
Automatics also prevents that animal from treating a feed box as an attack
target when the box contains edible food.

## Automatic repair

Automatic repair repairs worn items near usable crafting stations and damaged
pieces when the player has a build tool and meets access or station
requirements.

### Item repair

Automatics checks item repair once per second while the feature is enabled. It
repairs worn items when a usable crafting station is within `Crafting Station
Search Range`. Set that range to `0` to disable periodic item repair.

`Repair Items When Accessing The Crafting Station` also repairs items when the
crafting station GUI is opened.

`Item Repair Message` controls whether repaired item counts are shown.

### Piece repair

Automatics checks nearby pieces once per second while the feature is enabled.
Piece repair requires a build tool, such as a hammer, in the player's hand or
current weapon slot. `Piece Search Range` controls the search range; set it to
`0` to disable periodic piece repair.

Automatics respects private-area access and crafting-station requirements before
repairing a piece. `Piece Repair Message` controls whether repaired piece counts
are shown.

## Automatic mining

Automatic mining damages the nearest allowed mineral around the player by using
a usable pickaxe.

Key settings:

| Setting | Effect |
| --- | --- |
| `Automatic Mining` | Turns automatic mining on or off. |
| `Mining Interval` | Controls how often mining is attempted when no mining shortcut is assigned. |
| `Mining Range` | Controls the range used to search for mineral hit positions. |
| `Allow Mining Mineral` | Selects which mineral definitions Automatics may mine. |
| `Need To Equip Pickaxe For Mining` | Requires the pickaxe to be equipped. If disabled, Automatics can use a pickaxe from the inventory. |
| `Allow Mining Underground Minerals` | Allows underground minerals to be mined. |
| `Need To Wishbone For Mining Underground Minerals` | Requires Wishbone for underground mineral mining. |
| `Attempt Mining` | Optional shortcut. When assigned, mining runs only when the shortcut is pressed. |

Automatics checks the pickaxe tool tier against the mineral, uses pickaxe
durability, raises the Pickaxes skill, and applies the pickaxe hit effects.

## Automatic pickup

Automatic pickup collects nearby items for the local player.

Modes:

| Mode | Behavior |
| --- | --- |
| No `Pickup All Nearby` shortcut assigned | Automatics runs periodic pickup using `Automatic Pickup Interval`. |
| `Pickup All Nearby` shortcut assigned | Periodic pickup is disabled. Hold the shortcut while interacting with a pickable object or item to pick up matching nearby objects. |

Key settings:

| Setting | Effect |
| --- | --- |
| `Automatic Pickup Range` | Controls how far Automatics searches for pickup targets. |
| `Automatic Pickup Interval` | Controls periodic pickup when no shortcut is assigned. Set it to `0` to disable periodic pickup. |
| `Pickup All Nearby` | Optional shortcut for targeted pickup. |

Periodic pickup includes pickable world objects, pickable item objects, and item
drops that allow automatic pickup. Targeted pickup matches the hovered object's
displayed pickable name or item name. Automatics still checks inventory space,
carry weight, tar restrictions, and pieces that should not be picked up.

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
safe mode and removes only duplicate pins. Temporarily disable Automatic mapping
before using this command.

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
