# Automatics - Valheim Mod

Automatics is a mod that automates the tedious tasks of life in Valheim. Most of its features exist in existing mods, but it has been re-designed to make it easier for me to use.

## Features

### Automatic door

Automatically opens and closes the door near the player. The interval and distance to detect the player can be changed from the configuration.

### Automatic map pinning

Automatic pinning animals, monsters, floras, veins, dungeons, etc. that exist around the player to the map. The pinning allows for each object and the detection range of the object can be changed from the config.

**Custom map icons**

Using png files and csv, you can customize icons for pins added by
Automatic map pinning.

[![Custom Map Icons](https://app.box.com/shared/static/ggj61oyrdik1jk08lohdqr91e1q5isqv.png)](https://app.box.com/shared/static/yhdd2v0mrwzgh54tbkc7twjen17q22gn.jpg "Custom Map Icons (Click to view full size)")

-   **Files to be prepared**

    -   CustomMapIcon.csv: Describe information such as which icons to customize and which images to use. Detailed specifications are described below.

    -   ICON\_NAME.png: Image files required for each icon. A png file with transparency information is preferred.

Place the prepared files in the following configuration.

-   **/Valheim/BepInEx/plugins/Automatics/Textures**

    -   CustomMapIcon.csv

    -   ICON\_1\_NAME.png

    -   ICON\_2\_NAME.png

**Format of CustomMapIcon.csv**

Four columns are defined in this csv: TARGET, FILE, WIDTH, and HEIGHT. The first line is considered a header and is skipped, so information should be written in the second and subsequent lines.

-   TARGET: Specify a display name such as "Boar" or an internal name such as "$enemy\_boar"; If a display name is specified, the custom icon will be applied if the target display name partially matches the specified display name. If you specify an internal name, the custom icon will be applied only if the target internal name matches the specified internal name exactly.

-   FILE: Specify the name of the image file to be used for the icon. It is best to keep the image size between 16x16 and 32x32, as too large an icon will be obtrusive.

-   WIDTH: Specify the width of the icon.

-   HEIGHT: Specify the height of the icon.

**Example files**

I don’t feel I have explained it very well, so I have prepared a sample file. Please click [here](https://github.com/eideehi/valheim-automatics/blob/main/package/extra/custom-icon-example/Automatics/Textures) to check the structure of the file that actually works. You can also download the [zip file](https://app.box.com/shared/static/n8l56o2l5or24bx1061jjly4jnm21gm3.zip) and check the operation on your PC.

-   [Example files (Github)](https://github.com/eideehi/valheim-automatics/blob/main/package/extra/custom-icon-example/Automatics/Textures)

-   [Example files (Zip)](https://app.box.com/shared/static/n8l56o2l5or24bx1061jjly4jnm21gm3.zip)

### Automatic processing

Refueling pieces that need fuel. Deliver materials to pieces that
process materials, and store items produced by pieces. These tasks can be automated via containers around the piece.

### Automatic feeding

Animals and other creatures that consume food will be able to consume food from containers and the player's inventory, not just from food on the ground.

### Automatic repair
It can automatically repair items when the player is near a crafting station or when the crafting station GUI is opened or automatically repair nearby pieces when the player has a hammer equipped.

## Console commands
Automatics add a few commands to help the user.

### automatics [COMMAND]
Displays the usage of commands added by Automatics.

- COMMAND: Command name to display usage. If there is an exact match, it will be displayed. Otherwise, it will display the first partially matched command found. If omitted, the usage of the automatics command itself is displayed.

### printnames (WORD|REGEXP)
Outputs internal or display names that contain the specified string or match the specified regular expression.

- WORD: A text contained in the internal or display name. (e.g. $enemy_, $item_, $piece_, Boar, Mushroom, Wood door); All partially matching internal and display names are output.
- REGEXP: A regular expression of the internal or display name to be output. Must be prefixed with "r/". (e.g. r/^[$]item_, r/^boar$)

Examples:

- printnames greydwarf
- printnames r/deposit_[ct].+$

## Configurations

I recommend using [Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager).

[![Configuration Menu](https://app.box.com/shared/static/3v57rjpauzzyv0xeugohnw8bn2ye3q2h.png)](https://app.box.com/shared/static/vfzsn69i950l48er2u69tssod6xxsh8u.jpg "Configuration Menu (Click to view full size)")

### [ #1 Logging ] / [logging]
#### Logging / [logging_enabled]
Enable logging. Regardless of the value set here, the log of Automatics initialization process will always be output.

* Default value: false

#### Allowed Log Level / [allowed_log_level]
Specifies the level at which logging is allowed.

* Default value: Fatal, Error, Warning, Message
* Acceptable values: None, Fatal, Error, Warning, Message, Info, Debug, All

### [ #2 Automatic Door ] / [automatic_door]
#### Automatic Door / [automatic_door_enabled]
Enable the function to open and close the door automatically.

* Default value: true

#### Allow Automatic Door / [allow_automatic_door]
Specify which doors are allowed to open and close automatically.

* Default value: All
* Acceptable values: None, WoodDoor, WoodGate, IronGate, DarkwoodGate, WoodShutter, All

#### Allow Automatic Door (Custom) / [allow_automatic_door_custom]
Specify the display or internal name of any door that is allowed to open and close automatically. A display name is a name displayed in the game, such as "Wood door", and the specified value will enable the automatic door if it partially matches the target display name. The internal name is the name used on the program side, such as "$piece_wooddoor", and the specified value must exactly match the target's internal name for the automatic door to be enabled.

* Default value:

#### Interval To Open / [interval_to_open]
the interval at which to call the process to determine whether to open the door. (Unit: second)

* Default value: 0.1
* Acceptable value range: From 0.1 to 8

#### Interval To Close / [interval_to_close]
the interval at which to call the process to determine whether to close the door. (Unit: second)

* Default value: 0.1
* Acceptable value range: From 0.1 to 8

#### Player Search Radius To Open / [player_search_radius_to_open]
The door automatically open when the player is inside the specified radius, with the door as the origin. (Unit: meter)

* Default value: 2.5
* Acceptable value range: From 1 to 8

#### Player Search Radius To Close / [player_search_radius_to_close]
The door automatically close when the player is inside the specified radius, with the door as the origin. (Unit: meter)

* Default value: 2.5
* Acceptable value range: From 1 to 8

#### Toggle Automatic Door Enabled / [toggle_automatic_door_enabled_key]
Shortcut key to enable/disable the automatic door.

* Default value:

### [ #3 Automatic Map Pinning ] / [automatic_map_pinning]
#### Automatic Map Pinning / [automatic_map_pinning_enabled]
Enables automatic pinning to the map.

* Default value: true

#### Dynamic Object Search Range / [dynamic_object_search_range]
Specify the range of dynamic objects to be explored. "Dynamic Object" are objects that change position, such as animals, monsters, etc. Set to 0 to disable pinning of dynamic objects. (Unit: meter)

* Default value: 64
* Acceptable value range: From 0 to 256

#### Static Object Search Range / [static_object_search_range]
Specify the range of static objects to be explored. "Static Object" are objects that do not change their position, such as plants, veins, etc. Setting this to 0 disables pinning of static objects. (Unit: meter)

* Default value: 16
* Acceptable value range: From 0 to 256

#### Location Search Range / [location_search_range]
Specify the range of location to be explored. "Location" is a specific place, such as dungeon, fuling village, etc. Setting this to 0 disables location pinning. (Unit: meter)

* Default value: 96
* Acceptable value range: From 0 to 256

#### Allow Pinning Animal / [allow_pinning_animal]
Specify the animals to be automatic pinning.

* Default value: All
* Acceptable values: None, Boar, Deer, Wolf, Lox, Bird, Fish, All

#### Allow Pinning Monster / [allow_pinning_monster]
Specify the monsters to be automatic pinning.

* Default value: All
* Acceptable values: None, Greyling, Neck, Ghost, Greydwarf, GreydwarfBrute, GreydwarfShaman, RancidRemains, Skeleton, Troll, Abomination, Blob, Draugr, DraugrElite, Leech, Oozer, Surtling, Wraith, Drake, Fenring, StoneGolem, Deathsquito, Fuling, FulingBerserker, FulingShaman, Growth, Serpent, Bat, FenringCultist, Ulv, All

#### Allow Pinning Flora / [allow_pinning_flora]
Specify the flora to be automatic pinning.

* Default value: Mushroom, Raspberries, Blueberries, CarrotSeeds, Thistle, TurnipSeeds, Cloudberries
* Acceptable values: None, Dandelion, Mushroom, Raspberries, Blueberries, Carrot, CarrotSeeds, YellowMushroom, Thistle, Turnip, TurnipSeeds, Onion, OnionSeeds, Barley, Cloudberries, Flex, All

#### Allow Pinning Vein / [allow_pinning_vein]
Specify the veins to be automatic pinning.

* Default value: -9
* Acceptable values: None, Copper, Tin, MudPile, Obsidian, Silver, All

#### Allow Pinning Spawner / [allow_pinning_spawner]
Specify the spawners to be automatic pinning.

* Default value: None
* Acceptable values: None, GreydwarfNest, EvilBonePile, BodyPile, All

#### Allow Pinning Other Object / [allow_pinning_other]
Specify the other objects to be automatic pinning.

* Default value: WildBeehive
* Acceptable values: None, Vegvisir, Runestone, WildBeehive, Portal, All

#### Allow Pinning Dungeon / [allow_pinning_dungeon]
Specify the dungeons to be automatic pinning.

* Default value: All
* Acceptable values: None, BurialChambers, TrollCave, SunkenCrypts, MountainCave, All

#### Allow Pinning Spot / [allow_pinning_spot]
Specify the spots to be automatic pinning.

* Default value: All
* Acceptable values: None, InfestedTree, FireHole, DrakeNest, GoblinCamp, TarPit, All

#### Allow Pinning Ship / [allow_pinning_ship]
Enable automatic pinning for the ship.

* Default value: true

#### Allow Pinning Animal (Custom) / [allow_pinning_animal_custom]
Specify the display or internal name of any animals that is allowed to automatic map pinning. A display name is the name displayed in the game, such as "Boar", and the specified value will enable the pinning if it partially matches the target display name. Internal name is the name used on the program side, such as "$enemy_boar", and the specified value must exactly match the target's internal name for the pinning to be enabled.

* Default value:

#### Allow Pinning Monster (Custom) / [allow_pinning_monster_custom]
Specify the display or internal name of any monster that is allowed to automatic map pinning. A display name is the name displayed in the game, such as "Greyling", and the specified value will enable the pinning if it partially matches the target display name. Internal name is the name used on the program side, such as "$enemy_greyling", and the specified value must exactly match the target's internal name for the pinning to be enabled.

* Default value:

#### Allow Pinning Flora (Custom) / [allow_pinning_flora_custom]
Specify the display or internal name of any flora that is allowed to automatic map pinning. A display name is the name displayed in the game, such as "Dandelion", and the specified value will enable the pinning if it partially matches the target display name. Internal name is the name used on the program side, such as "$item_dandelion", and the specified value must exactly match the target's internal name for the pinning to be enabled.

* Default value:

#### Allow Pinning Vein (Custom) / [allow_pinning_vein_custom]
Specify the display or internal name of any veins that is allowed to automatic map pinning. A display name is the name displayed in the game, such as "Copper deposit", and the specified value will enable the pinning if it partially matches the target display name. Internal name is the name used on the program side, such as "$piece_deposit_copper", and the specified value must exactly match the target's internal name for the pinning to be enabled.

* Default value:

#### Allow Pinning Spawner (Custom) / [allow_pinning_spawner_custom]
Specify the display or internal name of any spawners that is allowed to automatic map pinning. A display name is the name displayed in the game, such as "Greydwarf nest", and the specified value will enable the pinning if it partially matches the target display name. Internal name is the name used on the program side, such as "$enemy_greydwarfspawner", and the specified value must exactly match the target's internal name for the pinning to be enabled.

* Default value:

#### Not Pinning Tamed Animals / [ignore_tamed_animals]
Exclude tamed animals from automatic pinning.

* Default value: true

#### Static Object Search Interval / [static_object_search_interval]
Specify the interval at which static object search. Setting to 0 disables periodic static object search. (Unit: second)

* Default value: 0.25
* Acceptable value range: From 0 to 8

#### Flora Pins Merge Range / [flora_pins_merge_range]
When pinning flora, it recursively searches for the same flora that exist within a specified range and merge them into a single pin. (Unit: meter)

* Default value: 8
* Acceptable value range: From 0 to 16

#### In Ground Veins Need Wishbone / [in_ground_veins_need_wishbone]
Specify whether need to equip a Wishbone to pinning a vein that in ground.

* Default value: true

#### Static Object Search / [static_object_search_key]
Specify shortcut keys for searching static objects. Setting this item disables the static object search at regular intervals, so that it is performed only once each time the shortcut key is pressed.

* Default value:

### [ #4 Automatic Processing ] / [automatic_processing]
#### Automatic Processing / [automatic_processing_enabled]
Enable automatic functions for tasks such as cooking, refining, and refilling fuel.

* Default value: true

#### Beehive Allow Processing / [piece_beehive_allow_automatic_processing]
Specify the automatic processing to be allowed for Beehive

* Default value: Store
* Acceptable values: None, Store

#### Beehive Container Search Range / [piece_beehive_container_search_range]
Specify the maximum distance which Beehive will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Products To Suppress Automatic Processing For Beehive / [piece_beehive_product_count_that_suppress_automatic_store]
Suppresses automatic processing of Beehive to prevent the count of products stored in a container from exceeding a specified quantity. Set to 0 to disable automatic processing suppression based on the count of products.

* Default value: 0
* Acceptable value range: From 0 to 9999

#### Bonfire Allow Processing / [piece_bonfire_allow_automatic_processing]
Specify the automatic processing to be allowed for Bonfire

* Default value: Refuel
* Acceptable values: None, Refuel

#### Bonfire Container Search Range / [piece_bonfire_container_search_range]
Specify the maximum distance which Bonfire will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Fuels To Suppress Automatic Processing For Bonfire / [piece_bonfire_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Bonfire to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Blast furnace Allow Processing / [piece_blastfurnace_allow_automatic_processing]
Specify the automatic processing to be allowed for Blast furnace

* Default value: Craft, Refuel, Store
* Acceptable values: None, Craft, Refuel, Store

#### Blast furnace Container Search Range / [piece_blastfurnace_container_search_range]
Specify the maximum distance which Blast furnace will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Materials To Suppress Automatic Processing For Blast furnace / [piece_blastfurnace_material_count_that_suppress_automatic_process]
Suppresses automatic processing of Blast furnace to prevent the count of materials stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of materials.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Count Of Fuels To Suppress Automatic Processing For Blast furnace / [piece_blastfurnace_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Blast furnace to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Count Of Products To Suppress Automatic Processing For Blast furnace / [piece_blastfurnace_product_count_that_suppress_automatic_store]
Suppresses automatic processing of Blast furnace to prevent the count of products stored in a container from exceeding a specified quantity. Set to 0 to disable automatic processing suppression based on the count of products.

* Default value: 0
* Acceptable value range: From 0 to 9999

#### Refuel Blast furnace Only When Materials Supplied / [piece_blastfurnace_refuel_only_when_materials_supplied]
Performs automatic refuel only when materials are supplied to Blast furnace.

* Default value: false

#### Campfire Allow Processing / [piece_firepit_allow_automatic_processing]
Specify the automatic processing to be allowed for Campfire

* Default value: Refuel
* Acceptable values: None, Refuel

#### Campfire Container Search Range / [piece_firepit_container_search_range]
Specify the maximum distance which Campfire will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Fuels To Suppress Automatic Processing For Campfire / [piece_firepit_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Campfire to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Charcoal kiln Allow Processing / [piece_charcoalkiln_allow_automatic_processing]
Specify the automatic processing to be allowed for Charcoal kiln

* Default value: Craft, Store
* Acceptable values: None, Craft, Store

#### Charcoal kiln Container Search Range / [piece_charcoalkiln_container_search_range]
Specify the maximum distance which Charcoal kiln will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Materials To Suppress Automatic Processing For Charcoal kiln / [piece_charcoalkiln_material_count_that_suppress_automatic_process]
Suppresses automatic processing of Charcoal kiln to prevent the count of materials stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of materials.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Count Of Products To Suppress Automatic Processing For Charcoal kiln / [piece_charcoalkiln_product_count_that_suppress_automatic_store]
Suppresses automatic processing of Charcoal kiln to prevent the count of products stored in a container from exceeding a specified quantity. Set to 0 to disable automatic processing suppression based on the count of products.

* Default value: 0
* Acceptable value range: From 0 to 9999

#### Cooking station Allow Processing / [piece_cookingstation_allow_automatic_processing]
Specify the automatic processing to be allowed for Cooking station

* Default value: Store
* Acceptable values: None, Craft, Store

#### Cooking station Container Search Range / [piece_cookingstation_container_search_range]
Specify the maximum distance which Cooking station will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Materials To Suppress Automatic Processing For Cooking station / [piece_cookingstation_material_count_that_suppress_automatic_process]
Suppresses automatic processing of Cooking station to prevent the count of materials stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of materials.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Count Of Products To Suppress Automatic Processing For Cooking station / [piece_cookingstation_product_count_that_suppress_automatic_store]
Suppresses automatic processing of Cooking station to prevent the count of products stored in a container from exceeding a specified quantity. Set to 0 to disable automatic processing suppression based on the count of products.

* Default value: 0
* Acceptable value range: From 0 to 9999

#### Fermenter Allow Processing / [piece_fermenter_allow_automatic_processing]
Specify the automatic processing to be allowed for Fermenter

* Default value: Craft, Store
* Acceptable values: None, Craft, Store

#### Fermenter Container Search Range / [piece_fermenter_container_search_range]
Specify the maximum distance which Fermenter will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Materials To Suppress Automatic Processing For Fermenter / [piece_fermenter_material_count_that_suppress_automatic_process]
Suppresses automatic processing of Fermenter to prevent the count of materials stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of materials.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Count Of Products To Suppress Automatic Processing For Fermenter / [piece_fermenter_product_count_that_suppress_automatic_store]
Suppresses automatic processing of Fermenter to prevent the count of products stored in a container from exceeding a specified quantity. Set to 0 to disable automatic processing suppression based on the count of products.

* Default value: 0
* Acceptable value range: From 0 to 9999

#### Hanging brazier Allow Processing / [piece_brazierceiling01_allow_automatic_processing]
Specify the automatic processing to be allowed for Hanging brazier

* Default value: Refuel
* Acceptable values: None, Refuel

#### Hanging brazier Container Search Range / [piece_brazierceiling01_container_search_range]
Specify the maximum distance which Hanging brazier will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Fuels To Suppress Automatic Processing For Hanging brazier / [piece_brazierceiling01_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Hanging brazier to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Hearth Allow Processing / [piece_hearth_allow_automatic_processing]
Specify the automatic processing to be allowed for Hearth

* Default value: Refuel
* Acceptable values: None, Refuel

#### Hearth Container Search Range / [piece_hearth_container_search_range]
Specify the maximum distance which Hearth will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Fuels To Suppress Automatic Processing For Hearth / [piece_hearth_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Hearth to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Iron cooking station Allow Processing / [piece_cookingstation_iron_allow_automatic_processing]
Specify the automatic processing to be allowed for Iron cooking station

* Default value: Store
* Acceptable values: None, Craft, Store

#### Iron cooking station Container Search Range / [piece_cookingstation_iron_container_search_range]
Specify the maximum distance which Iron cooking station will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Materials To Suppress Automatic Processing For Iron cooking station / [piece_cookingstation_iron_material_count_that_suppress_automatic_process]
Suppresses automatic processing of Iron cooking station to prevent the count of materials stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of materials.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Count Of Products To Suppress Automatic Processing For Iron cooking station / [piece_cookingstation_iron_product_count_that_suppress_automatic_store]
Suppresses automatic processing of Iron cooking station to prevent the count of products stored in a container from exceeding a specified quantity. Set to 0 to disable automatic processing suppression based on the count of products.

* Default value: 0
* Acceptable value range: From 0 to 9999

#### Jack-o-turnip Allow Processing / [piece_jackoturnip_allow_automatic_processing]
Specify the automatic processing to be allowed for Jack-o-turnip

* Default value: Refuel
* Acceptable values: None, Refuel

#### Jack-o-turnip Container Search Range / [piece_jackoturnip_container_search_range]
Specify the maximum distance which Jack-o-turnip will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Fuels To Suppress Automatic Processing For Jack-o-turnip / [piece_jackoturnip_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Jack-o-turnip to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Sconce Allow Processing / [piece_sconce_allow_automatic_processing]
Specify the automatic processing to be allowed for Sconce

* Default value: Refuel
* Acceptable values: None, Refuel

#### Sconce Container Search Range / [piece_sconce_container_search_range]
Specify the maximum distance which Sconce will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Fuels To Suppress Automatic Processing For Sconce / [piece_sconce_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Sconce to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Smelter Allow Processing / [piece_smelter_allow_automatic_processing]
Specify the automatic processing to be allowed for Smelter

* Default value: Craft, Refuel, Store
* Acceptable values: None, Craft, Refuel, Store

#### Smelter Container Search Range / [piece_smelter_container_search_range]
Specify the maximum distance which Smelter will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Materials To Suppress Automatic Processing For Smelter / [piece_smelter_material_count_that_suppress_automatic_process]
Suppresses automatic processing of Smelter to prevent the count of materials stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of materials.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Count Of Fuels To Suppress Automatic Processing For Smelter / [piece_smelter_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Smelter to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Count Of Products To Suppress Automatic Processing For Smelter / [piece_smelter_product_count_that_suppress_automatic_store]
Suppresses automatic processing of Smelter to prevent the count of products stored in a container from exceeding a specified quantity. Set to 0 to disable automatic processing suppression based on the count of products.

* Default value: 0
* Acceptable value range: From 0 to 9999

#### Refuel Smelter Only When Materials Supplied / [piece_smelter_refuel_only_when_materials_supplied]
Performs automatic refuel only when materials are supplied to Smelter.

* Default value: false

#### Spinning wheel Allow Processing / [piece_spinningwheel_allow_automatic_processing]
Specify the automatic processing to be allowed for Spinning wheel

* Default value: Store
* Acceptable values: None, Craft, Store

#### Spinning wheel Container Search Range / [piece_spinningwheel_container_search_range]
Specify the maximum distance which Spinning wheel will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Materials To Suppress Automatic Processing For Spinning wheel / [piece_spinningwheel_material_count_that_suppress_automatic_process]
Suppresses automatic processing of Spinning wheel to prevent the count of materials stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of materials.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Count Of Products To Suppress Automatic Processing For Spinning wheel / [piece_spinningwheel_product_count_that_suppress_automatic_store]
Suppresses automatic processing of Spinning wheel to prevent the count of products stored in a container from exceeding a specified quantity. Set to 0 to disable automatic processing suppression based on the count of products.

* Default value: 0
* Acceptable value range: From 0 to 9999

#### Standing blue-burning iron torch Allow Processing / [piece_groundtorchblue_allow_automatic_processing]
Specify the automatic processing to be allowed for Standing blue-burning iron torch

* Default value: Refuel
* Acceptable values: None, Refuel

#### Standing blue-burning iron torch Container Search Range / [piece_groundtorchblue_container_search_range]
Specify the maximum distance which Standing blue-burning iron torch will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Fuels To Suppress Automatic Processing For Standing blue-burning iron torch / [piece_groundtorchblue_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Standing blue-burning iron torch to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Standing green-burning iron torch Allow Processing / [piece_groundtorchgreen_allow_automatic_processing]
Specify the automatic processing to be allowed for Standing green-burning iron torch

* Default value: Refuel
* Acceptable values: None, Refuel

#### Standing green-burning iron torch Container Search Range / [piece_groundtorchgreen_container_search_range]
Specify the maximum distance which Standing green-burning iron torch will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Fuels To Suppress Automatic Processing For Standing green-burning iron torch / [piece_groundtorchgreen_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Standing green-burning iron torch to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Standing iron torch Allow Processing / [piece_groundtorch_allow_automatic_processing]
Specify the automatic processing to be allowed for Standing iron torch

* Default value: Refuel
* Acceptable values: None, Refuel

#### Standing iron torch Container Search Range / [piece_groundtorch_container_search_range]
Specify the maximum distance which Standing iron torch will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Fuels To Suppress Automatic Processing For Standing iron torch / [piece_groundtorch_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Standing iron torch to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Standing wood torch Allow Processing / [piece_groundtorchwood_allow_automatic_processing]
Specify the automatic processing to be allowed for Standing wood torch

* Default value: Refuel
* Acceptable values: None, Refuel

#### Standing wood torch Container Search Range / [piece_groundtorchwood_container_search_range]
Specify the maximum distance which Standing wood torch will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Fuels To Suppress Automatic Processing For Standing wood torch / [piece_groundtorchwood_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Standing wood torch to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Stone oven Allow Processing / [piece_oven_allow_automatic_processing]
Specify the automatic processing to be allowed for Stone oven

* Default value: Craft, Refuel, Store
* Acceptable values: None, Craft, Refuel, Store

#### Stone oven Container Search Range / [piece_oven_container_search_range]
Specify the maximum distance which Stone oven will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Materials To Suppress Automatic Processing For Stone oven / [piece_oven_material_count_that_suppress_automatic_process]
Suppresses automatic processing of Stone oven to prevent the count of materials stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of materials.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Count Of Fuels To Suppress Automatic Processing For Stone oven / [piece_oven_fuel_count_that_suppress_automatic_process]
Suppresses automatic processing of Stone oven to prevent the count of fuels stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of fuels.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Count Of Products To Suppress Automatic Processing For Stone oven / [piece_oven_product_count_that_suppress_automatic_store]
Suppresses automatic processing of Stone oven to prevent the count of products stored in a container from exceeding a specified quantity. Set to 0 to disable automatic processing suppression based on the count of products.

* Default value: 0
* Acceptable value range: From 0 to 9999

#### Refuel Stone oven Only When Materials Supplied / [piece_oven_refuel_only_when_materials_supplied]
Performs automatic refuel only when materials are supplied to Stone oven.

* Default value: false

#### Windmill Allow Processing / [piece_windmill_allow_automatic_processing]
Specify the automatic processing to be allowed for Windmill

* Default value: Store
* Acceptable values: None, Craft, Store

#### Windmill Container Search Range / [piece_windmill_container_search_range]
Specify the maximum distance which Windmill will search for containers. (Unit: meter)

* Default value: 8
* Acceptable value range: From 1 to 64

#### Count Of Materials To Suppress Automatic Processing For Windmill / [piece_windmill_material_count_that_suppress_automatic_process]
Suppresses automatic processing of Windmill to prevent the count of materials stored in a container from falling below a specified quantity. Set to 0 to disable automatic processing suppression based on the count of materials.

* Default value: 1
* Acceptable value range: From 0 to 9999

#### Count Of Products To Suppress Automatic Processing For Windmill / [piece_windmill_product_count_that_suppress_automatic_store]
Suppresses automatic processing of Windmill to prevent the count of products stored in a container from exceeding a specified quantity. Set to 0 to disable automatic processing suppression based on the count of products.

* Default value: 0
* Acceptable value range: From 0 to 9999

### [ #5 Automatic Feeding ] / [automatic_feeding]
#### Automatic Feeding / [automatic_feeding_enabled]
Enable automatic feeding for animals.

* Default value: true

#### Feed Search Range / [feed_search_range]
Specify the maximum distance which animal will search for food. 0 disables the feed box search and -1 uses the default value for each animal. (Unit: meter)

* Default value: -1
* Acceptable value range: From -1 to 64

#### Need Close To Eat The Feed / [need_close_to_eat_the_feed]
Specify whether or not the animal needs to approach the food in order to eat it.

* Default value: false

#### Allow To Feed From Container / [allow_to_feed_from_container]
Specify the types of animals allowed to feed from the container.

* Default value: Tamed
* Acceptable values: None, Wild, Tamed, All

### [ #6 Automatic Repair ] / [automatic_repair]
#### Automatic Repair / [automatic_repair_enabled]
Enable automatic repair of items and pieces.

* Default value: true

#### Crafting Station Search Range / [crafting_station_search_range]
Specify the range to search for a crafting station to be used to repair items. Setting to 0 disables periodic item repair. (Unit: meters)

* Default value: 16
* Acceptable value range: From 0 to 64

#### Repair Items When Accessing The Crafting Station / [repair_items_when_accessing_the_crafting_station]
Specify whether or not to repair all items that can be repaired when the workbench GUI is opened.

* Default value: false

#### Item Repair Message / [item_repair_message]
Specify where the message is displayed when an item is repaired.

* Default value: None

#### Piece Search Range / [piece_search_range]
Specify the range to search for a pieces to repair. Setting to 0 disables periodic pieces repairs. (Unit: meters)

* Default value: 16
* Acceptable value range: From 0 to 64

#### Piece Repair Message / [piece_repair_message]
Specify where the message is displayed when a piece is repaired.

* Default value: None


## Languages

<table>
<tbody>
<tr class="odd">
<td style="text-align: left;"><p>Language</p></td>
<td style="text-align: left;"><p>Translators</p></td>
<td style="text-align: left;"><p>Status</p></td>
</tr>
<tr class="even">
<td style="text-align: left;"><p>English</p></td>
<td style="text-align: left;"><p>Translation Tools</p></td>
<td style="text-align: left;"><p>100%</p></td>
</tr>
<tr class="odd">
<td style="text-align: left;"><p>Japanese</p></td>
<td style="text-align: left;"><p>EideeHi</p></td>
<td style="text-align: left;"><p>100%</p></td>
</tr>
</tbody>
</table>

## Credits

-   Dependencies:

    -   [Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager)

## License

Automatics is developed and released under the MIT license. For the full text of the license, please see the [LICENSE](https://github.com/eideehi/valheim-automatics/blob/main/LICENSE) file.

## Changelog
#### v1.1.2 [2022-04-14]
- Add command to output internal and display names
  * See ["Console commands"](https://github.com/eideehi/valheim-automatics#console-commands) in the README for details.
- Add options to suppress automatic processing based on item count
- Add option to automatic refuel only when materials supplied
- Change 'Automatic Repair' not to work during game pauses
- Change 'Allow...(Custom)' options to evaluate for exact match if an internal name is specified, or partial match if a display name is specified
  * The "$enemy_boar" matches only "Boar", but "Greydwarf" matches "Greydwarf brute" and "Greydwarf shaman" in addition to "Greydwarf"
  * The same goes for TARGET for [Custom Map Icon](https://github.com/eideehi/valheim-automatics#custom-map-icons)
#### v1.1.1 [2022-04-10]
- Fix package task of mod files for Thunderstore
- Fix a bug in which the 'Repair Pieces' function continued to work even though the 'Automatic Repair' feature was disabled in the configuration
#### v1.1.0 [2022-04-07]
- Add the feature of 'Automatic Feeding'
- Add the feature of 'Automatic Repair'
- Add option to allow automatic opening/closing for any door
- Fix a bug where the generated items would be lost if Smelter could not find the container when 'Automatic Store' was enabled
- Fix a bug where the honey to be set over the maximum if Beehive could not find the container when 'Automatic Store' was enabled
#### v1.0.6 [2022-04-04]
- Fix a bug that prevented automatic pinning of player existing in the open air under certain conditions, judging that they exist in the dungeon
- Add elements added in patch 0.207.20 to be the target for automatic pinning
- Change the maximum value of the container search range in 'Automatic Processing' to 64
- Change to remove pins added by automatic pinning when some veins are destroyed
#### v1.0.5 [2022-03-28]
- Add shortcut key to perform searches for static objects
- Add shortcut key to enable/disable automatic door
- Fix a bug that caused dynamic pins to remain on the map when auto-pinning was disabled from the config
- Change the initial value of the 'Automatic Map Pinning' configuration
#### v1.0.4 [2022-03-18]
- Fix a bug fish were not pinned
- Fix a bug that caused pins to be deleted at unintended times
- Fix a bug in which automatic pinning of ship was enabled regardless of the config value
- Add the feature to customize the icons of pins added by auto pinning
  * There are no custom icons bundled with Automatics, please add your own
#### v1.0.3 [2022-03-16]
- Fix a bug where tamed animals continue to be pinned
- Add options for users to add map pinning targets (animals, monsters, flora, veins, and spawners)
  * As a result, elements added by mods are no longer automatic pinning unless this option is used
- Add an option to allow ships to be automatic pinning
  * This is dynamic pinning, but unlike other dynamic pins, the pins is saved in the save data
#### v1.0.2 [2022-03-15]
- Improvement of FPS drop due to Automatic Map Pinning
  * In particular, FPS is greatly improved in areas with a lot of flora, such as farms
- Reduce the processing load on Automatic Door
- Add Portal to the Automatic Map Pinning target
  * Portal is added to 'Allow Pinning Other Objects'
#### v1.0.1 [2022-03-06]
- Fix a bug that prevented some Tar Pits from being pinning
- Fix a bug in which automatic map pinning of static objects causes a significant drop in FPS
- Fix logic for determining if a vein is in ground
- Change the Automatic Map Pinning process is not called when the game is paused
- Change Automatic Door options so that Open and Close can be specified separately
#### v1.0.0 [2022-01-23]
- Initial release
