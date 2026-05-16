# Automatics - Valheim Mod
Automatics is a mod that automates the tedious tasks of life in Valheim. Most of its features exist in existing mods, but it has been re-designed to make it easier for me to use.

> **IMPORTANT**:
> - This mod has been developed by an individual and is not associated with the game's developer in any way. Please refrain from asking the developer any questions regarding this mod.
> - This mod has been developed with the sole intention of single-player usage. Please be aware that it is not supported for server operation, and we kindly request your understanding in this matter.

## Features

> **TIP**: Each of the features described below can be completely disabled using the "Disable Module" option in the configuration.

For released feature documentation, see [README.adoc](https://github.com/eideehi/valheim-automatics/blob/1.5.1/README.adoc).

### Automatic door

Automatically opens and closes allowed doors near the player. The open and close intervals, detection distances, allowed doors, optional enable/disable shortcut, and toggle message position can be changed from the configuration.

### Automatic mapping

Automatically pins nearby dynamic objects, static objects, and locations to the map, including animals, monsters, flora, minerals, vehicles, portals, dungeons, spots, and other configured objects. You can configure search ranges, allowed targets, static pin saving, destroyed-object pin cleanup, and user-defined objects. The map navigation shortcut starts or clears navigation by holding the configured modifier key (Left Shift by default) and left-clicking a pin on the large map; while navigating, the HUD shows the target name and distance.

- **Custom icon pack**: You can also define your own icons in png and json files. See [docs/custom-icon-pack.adoc](https://github.com/eideehi/valheim-automatics/blob/1.5.1/docs/custom-icon-pack.adoc) for custom icon pack specifications.

![Custom Icon Pack Image](https://app.box.com/shared/static/ggj61oyrdik1jk08lohdqr91e1q5isqv.png)

### Automatic processing

Uses nearby allowed containers to automate supported processing tasks: supplying materials for crafting, refueling, storing produced items, and charging pieces such as Ballista. Per-piece operations, container search ranges and limits, stop thresholds, and the storage connection effect color can be changed from the configuration.

### Automatic feeding

Allows configured tamable creatures to eat valid food from nearby containers, the player's inventory, or both instead of only food dropped on the ground. Feeding range, animal types, and whether animals must move close to the feed can be changed from the configuration.

### Automatic repair

Automatically repairs items when the player is near usable crafting stations or, if enabled, when the crafting station GUI is opened. It can also repair nearby pieces while a build tool such as a hammer is equipped.

### Automatic mining

Automatically mines allowed minerals around the player. Mining can run at the configured interval or only when the mining shortcut is pressed, and it can require a pickaxe or wishbone for underground minerals.

### Automatic pickup

Automatically picks up nearby items at the configured interval when no Pickup All Nearby shortcut is assigned. If the shortcut is assigned, interacting with a pickable object while pressing it picks up matching nearby objects instead of running interval pickup.

## Console commands

Automatics adds console commands for command discovery, name lookup, nearby object lookup, and map pin cleanup.

- `automatics`: Shows Automatics command usage.
- `printnames`: Finds internal and display names.
- `printobjects`: Shows nearby objects Automatics can handle.
- `removemappins`: Removes duplicate or filtered map pins.

See [Console commands](https://github.com/eideehi/valheim-automatics/blob/1.5.1/README.adoc#console-commands) in the released documentation for usage and options.

## Configurations

I recommend using [Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager).

![Configuration Menu](https://app.box.com/shared/static/3v57rjpauzzyv0xeugohnw8bn2ye3q2h.png)

Use [CONFIG.adoc](https://github.com/eideehi/valheim-automatics/blob/1.5.1/CONFIG.adoc) for every configuration entry, default value, and accepted value range. Use [README.adoc](https://github.com/eideehi/valheim-automatics/blob/1.5.1/README.adoc) for feature-by-feature usage notes.

### Adding object definitions to Automatics

You can use the [Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager) to define objects that you want Automatics to work with.

![User-defined objects in GUI](https://app.box.com/shared/static/5f6dvpg1elczu9froqkepxamv03ci9cd.png)

Open [docs/add-user-defined-object.adoc](https://github.com/eideehi/valheim-automatics/blob/1.5.1/docs/add-user-defined-object.adoc) to learn more about adding user-defined objects.

## Languages

| Language | Translators       | Status |
|----------|-------------------|--------|
| English  | Translation Tools | 100%   |
| Japanese | EideeHi           | 100%   |

## Contacts

[Open an issue](https://github.com/eideehi/valheim-automatics/issues) for bug reports, questions, suggestions, and requests.

## Credits

- **Dependencies**:
  - [Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager)
  - [LitJSON](https://litjson.net)
  - [NDesk.Options](http://ndesk.org/Options)

## License

Automatics is developed and released under the MIT license. For the full text of the license, please see the [LICENSE](https://github.com/eideehi/valheim-automatics/blob/1.5.1/LICENSE) file.
