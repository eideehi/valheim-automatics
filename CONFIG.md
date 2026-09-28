# Configurations

Every module is off on a new install; turn on the ones you want by setting their `Module` entry to `Enabled`. An existing configuration file keeps each module on or off as it was; Automatic Supply, which is new, starts off there too. Only the default of `Module` changed: the settings inside each module keep their defaults.

## [ #1 System ] / [system]
### Logging / [enable_logging]
Specify whether to enable log output. System initialization logs for Automatics are always output regardless of the value of this option.

- Default value: false

### Log Level To Allow Logging / [log_level_to_allow_logging]
Specify the log level that allows logging.

- Default value: Fatal, Error, Warning, Message
- Acceptable values: None, Fatal, Error, Warning, Message, Info, Debug, All
- Multiple values can be set at the same time by separating them with , (e.g. Fatal, Error)

## [ #2 General ] / [general]
### User-defined Animal / [custom_animal]
Any animal can be added. Identifier: A unique string that does not overlap with any other identifier. Whitespace is not allowed. Label: The name of the animal. Mainly used in the GUI. If you specify an internal name, it will be translated automatically. Pattern: A pattern matching the internal name of the animal. Exact matches are evaluated. Prefix the pattern with r/ to use a regular expression.

- Default value:

### User-defined Dungeon / [custom_dungeon]
Any dungeon can be added. Identifier: A unique string that does not overlap with any other identifier. Whitespace is not allowed. Label: The name of the dungeon. Mainly used in the GUI. If you specify an internal name, it will be translated automatically. Pattern: A pattern matching the prefab name of the dungeon. Exact matches are evaluated. Prefix the pattern with r/ to use a regular expression.

- Default value:

### User-defined Flora / [custom_flora]
Any flora can be added. Identifier: A unique string that does not overlap with any other identifier. Whitespace is not allowed. Label: The name of the flora. Mainly used in the GUI. If you specify an internal name, it will be translated automatically. Pattern: A pattern matching the internal name of the flora. Exact matches are evaluated. Prefix the pattern with r/ to use a regular expression.

- Default value:

### User-defined Mineral / [custom_mineral]
Any mineral can be added. Identifier: A unique string that does not overlap with any other identifier. Whitespace is not allowed. Label: The name of the mineral. Mainly used in the GUI. If you specify an internal name, it will be translated automatically. Pattern: A pattern matching the internal name of the mineral. Exact matches are evaluated. Prefix the pattern with r/ to use a regular expression.

- Default value:

### User-defined Monster / [custom_monster]
Any monster can be added. Identifier: A unique string that does not overlap with any other identifier. Whitespace is not allowed. Label: The name of the monster. Mainly used in the GUI. If you specify an internal name, it will be translated automatically. Pattern: A pattern matching the internal name of the monster. Exact matches are evaluated. Prefix the pattern with r/ to use a regular expression.

- Default value:

### User-defined Spawner / [custom_spawner]
Any spawner can be added. Identifier: A unique string that does not overlap with any other identifier. Whitespace is not allowed. Label: The name of the spawner. Mainly used in the GUI. If you specify an internal name, it will be translated automatically. Pattern: A pattern matching the internal name of the spawner. Exact matches are evaluated. Prefix the pattern with r/ to use a regular expression.

- Default value:

### User-defined Spot / [custom_spot]
Any spot can be added. Identifier: A unique string that does not overlap with any other identifier. Whitespace is not allowed. Label: The name of the spot. Mainly used in the GUI. If you specify an internal name, it will be translated automatically. Pattern: A pattern matching the prefab name of the spot. Exact matches are evaluated. Prefix the pattern with r/ to use a regular expression.

- Default value:

### User-defined Door / [custom_door]
Any door can be added. Identifier: A unique string that does not overlap with any other identifier. Whitespace is not allowed. Label: The name of the door. Mainly used in the GUI. If you specify an internal name, it will be translated automatically. Pattern: A pattern matching the internal name of the door. Exact matches are evaluated. Prefix the pattern with r/ to use a regular expression.

- Default value:

### User-defined Vehicle / [custom_vehicle]
Any vehicle can be added. Identifier: A unique string that does not overlap with any other identifier. Whitespace is not allowed. Label: The name of the vehicle. Mainly used in the GUI. If you specify an internal name, it will be translated automatically. Pattern: A pattern matching the internal name of the vehicle. Exact matches are evaluated. Prefix the pattern with r/ to use a regular expression.

- Default value:

### User-defined Other Object / [custom_other]
Any object can be added. Identifier: A unique string that does not overlap with any other identifier. Whitespace is not allowed. Label: The name of the object. Mainly used in the GUI. If you specify an internal name, it will be translated automatically. Pattern: A pattern matching the internal or prefab name of the object. Exact matches are evaluated. Prefix the pattern with r/ to use a regular expression.

- Default value:

### User-defined Container / [custom_container]
Any container can be added. Identifier: A unique string that does not overlap with any other identifier. Whitespace is not allowed. Label: The name of the container. Mainly used in the GUI. If you specify an internal name, it will be translated automatically. Pattern: A pattern matching the internal or prefab name of the container. Exact matches are evaluated. Prefix the pattern with r/ to use a regular expression.

- Default value:

## [ #3 Automatic Door ] / [automatic_door]
### Module / [module]
Turn this module on or off. While it is off, Automatics adds none of its logic to the game. The change applies while the game is running, so no restart is needed.

- Default value: Disabled
- Acceptable values: Enabled, Disabled

### Allow Automatic Door / [allow_automatic_door]
Specify which doors are allowed to open and close automatically.

- Default value: WoodDoor, WoodGate, IronGate, DarkwoodGate, PieceHexagonalDoor, AshwoodDoor, FlametalGate, DvergrDoor, TimberwoodGate, RoundpoleGate, Drawbridge

### Interval To Open / [interval_to_open]
Specify the interval at which the process of automatically opening the door is called. If set to less than 0.1, the function to automatically open the door will be disabled. (Unit: second)

- Default value: 0.1
- Acceptable value range: From 0 to 8

### Interval To Close / [interval_to_close]
Specify the interval at which the process of automatically closing the door is called. If set to less than 0.1, the function to automatically close the door will be disabled. (Unit: second)

- Default value: 0.1
- Acceptable value range: From 0 to 8

### Distance For Automatic Opening / [distance_for_automatic_opening]
Specify how close the player must be to the door in order for it to open automatically. (Unit: meter)

- Default value: 2.5
- Acceptable value range: From 1 to 8

### Distance For Automatic Closing / [distance_for_automatic_closing]
Specify how far the player must be from the door for the door to close automatically. While automatic opening is active, a value smaller than Distance For Automatic Opening is replaced by that distance. (Unit: meter)

- Default value: 2.5
- Acceptable value range: From 1 to 8

### Automatic Door Enable/Disable Toggle Message / [automatic_door_enable_disable_toggle_message]
Specify where a message is displayed when the shortcut key turns the Automatic Door module on or off.

- Default value: Center
- Acceptable values: None, Center, TopLeft

### Automatic Door Enable/Disable Toggle / [automatic_door_enable_disable_toggle]
Specify a shortcut key that turns the Automatic Door module on and off.

- Default value:

## [ #4 Automatic Mapping ] / [automatic_mapping]
### Module / [module]
Turn this module on or off. While it is off, Automatics adds none of its logic to the game. The change applies while the game is running, so no restart is needed.

- Default value: Disabled
- Acceptable values: Enabled, Disabled

### Pin Nearby Objects Automatically / [enable_automatic_mapping]
Specify whether to enable the feature that automatically pins objects and locations to the map. The module's other features, such as map navigation and pinning the object you look at, are not affected.

- Default value: true

### Dynamic Object Search Range / [dynamic_object_mapping_range]
Specify the range of dynamic objects to be explored. "Dynamic objects" are objects that change position, such as animals and monsters. Set to 0 to disable pinning of dynamic objects. (Unit: meter)

- Default value: 64
- Acceptable value range: From 0 to 128

### Static Object Search Range / [static_object_mapping_range]
Specify the range of static objects to be explored. "Static objects" are objects that do not change their position, such as flora and minerals. Setting this to 0 disables pinning of static objects, including portals and locations. (Unit: meter)

- Default value: 32
- Acceptable value range: From 0 to 128

### Location Search Range / [location_mapping_range]
Specify the range of locations to be explored. A "location" is a specific place, such as a dungeon or a fuling village. Setting this to 0 disables location pinning. (Unit: meter)

- Default value: 96
- Acceptable value range: From 0 to 128

### Hide Pins Outside Explored Area / [hide_unexplored_automatic_mapping_pins]
Hide Automatic Mapping pins whose position is outside the explored map. Saved pins are hidden, not deleted.

- Default value: false

### Allow Pinning Animal / [allow_pinning_animal]
Specify the animals to pin automatically.

- Default value: Boar, Piggy, Deer, Wolf, WolfCub, Lox, LoxCalf, Hen, Chicken, Hare, Bird, Fish, Asksvin, AsksvinHatchling, Moose, MooseCalf, Seal, BabySeal, RiktigFuling

### Allow Pinning Monster / [allow_pinning_monster]
Specify the monsters to pin automatically.

- Default value: Greyling, Neck, Ghost, Greydwarf, GreydwarfBrute, GreydwarfShaman, RancidRemains, Skeleton, Troll, Abomination, Blob, Draugr, DraugrElite, Leech, Oozer, Surtling, Wraith, Drake, Fenring, StoneGolem, Deathsquito, Fuling, FulingBerserker, FulingShaman, Growth, Serpent, Bat, FenringCultist, Ulv, DvergrRogue, DvergrMage, Tick, Seeker, SeekerBrood, Gjall, SeekerSoldier, CharredMelee, CharredArcher, CharredMage, CharredTwitcher, CharredGrunt, Morgen, Volture, BonemawSerpent, FallenValkyrie, Fader, SkeletonFire, FrostBlob, Bear, Vile, LavaBlob, Frysling, Gammeltroll, Krigen, Hexen, Barka, Kvastur, Elaking, EyelessOne, ShapelessPulp, TinyPulp, Shadow, Mistile, FallenWarrior

### Allow Pinning Flora / [allow_pinning_flora]
Specify the flora to pin automatically.

- Default value: Mushroom, Raspberries, Blueberries, CarrotSeeds, Thistle, TurnipSeeds, Cloudberries, JotunPuffs, Magecap, Fiddlehead, SmokePuff, Vineberry, VineberrySeeds

### Allow Pinning Mineral / [allow_pinning_mineral]
Specify the minerals to pin automatically.

- Default value: CopperDeposit, TinDeposit, MudPile, SilverVein, PetrifiedBone, SoftTissue, FlametalDeposit, PetrifiedGammeltroll, SilverDeposit, AncientArmour, AncientSword

### Allow Pinning Spawner / [allow_pinning_spawner]
Specify the spawners to pin automatically.

- Default value:

### Allow Pinning Vehicle / [allow_pinning_vehicle]
Specify the vehicles to pin automatically.

- Default value: Karve, Longship, Drakkar

### Allow Pinning Other Object / [allow_pinning_other]
Specify the other objects to pin automatically.

- Default value: WildBeehive

### Allow Pinning Dungeon / [allow_pinning_dungeon]
Specify the dungeons to pin automatically.

- Default value: BurialChambers, TrollCave, SunkenCrypts, MountainCave, InfestedMine, MorgenHole, BearCave, Morkhalla, WindingTunnels, TombOfLordReto

### Allow Pinning Spot / [allow_pinning_spot]
Specify the spots to pin automatically.

- Default value: InfestedTree, FireHole, DrakeNest, GoblinCamp, TarPit, DvergrExcavation, DvergrGuardTower, DvergrHarbour, DvergrLighthouse, PetrifiedBone, CharredFortress, LeviathanLava, BogWitchCamp

### Allow Pinning Portal / [allow_pinning_portal]
Enable automatic pinning of portals. Portals are treated as static objects.

- Default value: true

### Not Pinning Tamed Animals / [not_pinning_tamed_animals]
Exclude tamed animals from automatic pinning.

- Default value: true

### Static Object Mapping Interval / [static_object_mapping_interval]
Specify the interval at which static objects are mapped. Setting to 0 disables periodic static object mapping. (Unit: second)

- Default value: 0.25
- Acceptable value range: From 0 to 4

### Static Object Caching Interval / [static_object_caching_interval]
Specify the interval at which static objects will be cached. Mapping of static objects uses this cache. (Unit: second)

- Default value: 3
- Acceptable value range: From 1 to 8

### Save Static Object Pins / [save_static_object_pins]
Specify whether to save the pins of static objects.

- Default value: false

### Remove Pins Of Destroyed Object / [remove_pins_of_destroyed_object]
Specify whether or not to automatically remove pins of objects that have been destroyed and no longer exist.

- Default value: true

### Flora Pins Merge Range / [flora_pins_merge_range]
When pinning flora, it recursively searches for the same flora that exist within a specified range and merges them into a single pin. (Unit: meter)

- Default value: 8
- Acceptable value range: From 0 to 16

### Need To Equip Wishbone For Underground Minerals / [need_to_equip_wishbone_for_underground_minerals]
Specify whether the wishbone needs to be equipped to pin underground minerals.

- Default value: true

### Static Object Mapping / [static_object_mapping_key]
Specify shortcut keys for mapping static objects. Setting this item disables the static object mapping at regular intervals, so that it is performed only once each time the shortcut key is pressed.

- Default value:

### Pin Looked-at Object / [pin_object_key]
Press this shortcut to save a map pin for the object you are looking at, using its display name and icon-pack icon. A location’s structure, the ground within its loaded area (also when looking through water or tar) and a dungeon’s interior get one pin at the location with the name and icon Automatic Mapping recognizes. World-generated pieces inside a loaded location’s area also pin the location, even when detached from its hierarchy. Overlapping areas use the nearest containing location; when that location is not recognized, the usual object or ground pin applies. Player-built pieces, creatures, trees, deposits, loose items and other objects such as rune stones keep their own pins. Other bare terrain gets an unnamed pin. Unbound by default; requires the module to be running.

- Default value: unbound

### Object Pin Range / [pin_object_range]
Maximum distance in meters from the camera for pinning the object you are looking at.

- Default value: 100
- Acceptable value range: From 10 to 300

### Map Navigation Start Key / [navigation_start_key]
Specify the modifier key used to start navigation by left-clicking a pin on the large map. Left click is fixed.

- Default value: LeftShift

### Navigation Guide Swarm / [enable_navigation_guide]
Specify whether a swarm of small glowing lights leads you toward the navigation target. It follows the ground in a straight line, stops spawning within 5 m of the target and is hidden while the large map is open.

- Default value: true

### Navigation Guide Color / [navigation_guide_color]
Specify the HTML color code of the navigation guide swarm. Examples: #C6F27A, #C6F27AFF

- Default value: #C6F27A

### Mapping Performance Log / [mapping_performance_log]
Specify whether to measure automatic mapping performance and write the metrics to the BepInEx log at the Info level. The metrics are written only when Logging is enabled and Log Level To Allow Logging includes Info.

- Default value: false

## [ #5 Automatic Processing ] / [automatic_processing]
### Module / [module]
Turn this module on or off. While it is off, Automatics adds none of its logic to the game. The change applies while the game is running, so no restart is needed.

- Default value: Disabled
- Acceptable values: Enabled, Disabled

### Storage Connection Effect Color / [storage_connection_effect_color]
Specify the HTML color code used for the storage connection effect shown by automatic processing. Examples: #4FA9FF, #4FA9FFFF

- Default value: #4FA9FF

### Allow Container / [allow_container]
Specify containers that allow automatic operations such as item supply and storage.

- Default value: PieceChestWood, PieceChest, PieceChestBlackmetal, PieceChestCharred, PieceChestBarrel, PieceChestGrausten, PieceChestWardrobe

### Fires And Lights To Refuel / [refuel_fires]
Specify the fires and lights that are refuelled from nearby containers. They share the settings whose names start with "Fires and lights:".

- Default value: All
- Acceptable values: None, Bonfire, Campfire, HangingBrazier, Hearth, JackOTurnip, Sconce, StandingBlueBurningIronTorch, StandingBrazier, StandingGreenBurningIronTorch, StandingIronTorch, StandingWoodTorch, All
- Multiple values can be set at the same time by separating them with , (e.g. Bonfire, Campfire)

### Fires and lights: Container Search Range / [fires_container_search_range]
Specify the maximum distance Fires and lights will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Fires and lights: Container Reference Limit / [fires_container_reference_limit]
Specify the maximum number of nearby containers that Fires and lights will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Fires and lights: Number Of Fuels To Stop Refuel / [fires_fuel_count_of_suppress_processing]
When the number of fuel items stored in the container is about to become less than the specified quantity, refuel to Fires and lights will be stopped. If set to 0, stopping refuel based on the number of fuel items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Fires and lights: Refuel Only If Fuels Run Out / [fires_refuel_only_when_out_of_fuel]
Refuel only when the fuel for Fires and lights runs out.

- Default value: false

### Beehive: Allow Process / [allow_processing_by_piece_beehive]
Specify the automatic processes to allow for Beehive. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Store
- Acceptable values: None, Store

### Beehive: Container Search Range / [container_search_range_by_piece_beehive]
Specify the maximum distance Beehive will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Beehive: Container Reference Limit / [container_reference_limit_by_piece_beehive]
Specify the maximum number of nearby containers that Beehive will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Beehive: Number Of Product Stacks To Stop Processing / [piece_beehive_product_stacks_of_suppress_processing]
Stops Beehive once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Beehive: Store Only If Product Exists In Container / [piece_beehive_store_only_if_product_exists]
Store only if the product item for Beehive is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Blast furnace: Allow Process / [allow_processing_by_piece_blastfurnace]
Specify the automatic processes to allow for Blast furnace. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Craft, Refuel, Store
- Acceptable values: None, Craft, Refuel, Store
- Multiple values can be set at the same time by separating them with , (e.g. Craft, Refuel)

### Blast furnace: Container Search Range / [container_search_range_by_piece_blastfurnace]
Specify the maximum distance Blast furnace will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Blast furnace: Container Reference Limit / [container_reference_limit_by_piece_blastfurnace]
Specify the maximum number of nearby containers that Blast furnace will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Blast furnace: Number Of Materials To Stop Supplying / [piece_blastfurnace_material_count_of_suppress_processing]
When the number of material items stored in the container is about to become less than the specified quantity, the supply of materials to Blast furnace will be stopped. If set to 0, stopping the material supply based on the number of material items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Blast furnace: Number Of Product Stacks To Stop Processing / [piece_blastfurnace_product_stacks_of_suppress_processing]
Stops Blast furnace once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Blast furnace: Supply Only If Materials Run Out / [piece_blastfurnace_supply_only_when_materials_run_out]
Supply one material item only when the materials supplied to Blast furnace run out.

- Default value: false

### Blast furnace: Number Of Fuels To Stop Refuel / [piece_blastfurnace_fuel_count_of_suppress_processing]
When the number of fuel items stored in the container is about to become less than the specified quantity, refuel to Blast furnace will be stopped. If set to 0, stopping refuel based on the number of fuel items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Blast furnace: Refuel Only If Fuels Run Out / [piece_blastfurnace_refuel_only_when_out_of_fuel]
Refuel only when the fuel for Blast furnace runs out.

- Default value: false

### Blast furnace: Refuel Only When Materials Supplied / [piece_blastfurnace_refuel_only_when_materials_supplied]
Refuel only when material items are supplied to Blast furnace.

- Default value: false

### Blast furnace: Store Only If Product Exists In Container / [piece_blastfurnace_store_only_if_product_exists]
Store only if the product item for Blast furnace is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Charcoal kiln: Allow Process / [allow_processing_by_piece_charcoalkiln]
Specify the automatic processes to allow for Charcoal kiln. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Craft, Store
- Acceptable values: None, Craft, Store
- Multiple values can be set at the same time by separating them with , (e.g. Craft, Store)

### Charcoal kiln: Container Search Range / [container_search_range_by_piece_charcoalkiln]
Specify the maximum distance Charcoal kiln will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Charcoal kiln: Container Reference Limit / [container_reference_limit_by_piece_charcoalkiln]
Specify the maximum number of nearby containers that Charcoal kiln will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Charcoal kiln: Number Of Materials To Stop Supplying / [piece_charcoalkiln_material_count_of_suppress_processing]
When the number of material items stored in the container is about to become less than the specified quantity, the supply of materials to Charcoal kiln will be stopped. If set to 0, stopping the material supply based on the number of material items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Charcoal kiln: Number Of Product Stacks To Stop Processing / [piece_charcoalkiln_product_stacks_of_suppress_processing]
Stops Charcoal kiln once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Charcoal kiln: Supply Only If Materials Run Out / [piece_charcoalkiln_supply_only_when_materials_run_out]
Supply one material item only when the materials supplied to Charcoal kiln run out.

- Default value: false

### Charcoal kiln: Store Only If Product Exists In Container / [piece_charcoalkiln_store_only_if_product_exists]
Store only if the product item for Charcoal kiln is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Cooking station: Allow Process / [allow_processing_by_piece_cookingstation]
Specify the automatic processes to allow for Cooking station. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Store
- Acceptable values: None, Craft, Store
- Multiple values can be set at the same time by separating them with , (e.g. Craft, Store)

### Cooking station: Container Search Range / [container_search_range_by_piece_cookingstation]
Specify the maximum distance Cooking station will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Cooking station: Container Reference Limit / [container_reference_limit_by_piece_cookingstation]
Specify the maximum number of nearby containers that Cooking station will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Cooking station: Number Of Materials To Stop Supplying / [piece_cookingstation_material_count_of_suppress_processing]
When the number of material items stored in the container is about to become less than the specified quantity, the supply of materials to Cooking station will be stopped. If set to 0, stopping the material supply based on the number of material items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Cooking station: Number Of Product Stacks To Stop Processing / [piece_cookingstation_product_stacks_of_suppress_processing]
Stops Cooking station once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Cooking station: Supply Only If Materials Run Out / [piece_cookingstation_supply_only_when_materials_run_out]
Supply one material item only when the materials supplied to Cooking station run out.

- Default value: false

### Cooking station: Store Only If Product Exists In Container / [piece_cookingstation_store_only_if_product_exists]
Store only if the product item for Cooking station is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Fermenter: Allow Process / [allow_processing_by_piece_fermenter]
Specify the automatic processes to allow for Fermenter. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Craft, Store
- Acceptable values: None, Craft, Store
- Multiple values can be set at the same time by separating them with , (e.g. Craft, Store)

### Fermenter: Container Search Range / [container_search_range_by_piece_fermenter]
Specify the maximum distance Fermenter will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Fermenter: Container Reference Limit / [container_reference_limit_by_piece_fermenter]
Specify the maximum number of nearby containers that Fermenter will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Fermenter: Number Of Materials To Stop Supplying / [piece_fermenter_material_count_of_suppress_processing]
When the number of material items stored in the container is about to become less than the specified quantity, the supply of materials to Fermenter will be stopped. If set to 0, stopping the material supply based on the number of material items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Fermenter: Number Of Product Stacks To Stop Processing / [piece_fermenter_product_stacks_of_suppress_processing]
Stops Fermenter once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Fermenter: Store Only If Product Exists In Container / [piece_fermenter_store_only_if_product_exists]
Store only if the product item for Fermenter is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Iron cooking station: Allow Process / [allow_processing_by_piece_cookingstation_iron]
Specify the automatic processes to allow for Iron cooking station. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Store
- Acceptable values: None, Craft, Store
- Multiple values can be set at the same time by separating them with , (e.g. Craft, Store)

### Iron cooking station: Container Search Range / [container_search_range_by_piece_cookingstation_iron]
Specify the maximum distance Iron cooking station will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Iron cooking station: Container Reference Limit / [container_reference_limit_by_piece_cookingstation_iron]
Specify the maximum number of nearby containers that Iron cooking station will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Iron cooking station: Number Of Materials To Stop Supplying / [piece_cookingstation_iron_material_count_of_suppress_processing]
When the number of material items stored in the container is about to become less than the specified quantity, the supply of materials to Iron cooking station will be stopped. If set to 0, stopping the material supply based on the number of material items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Iron cooking station: Number Of Product Stacks To Stop Processing / [piece_cookingstation_iron_product_stacks_of_suppress_processing]
Stops Iron cooking station once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Iron cooking station: Supply Only If Materials Run Out / [piece_cookingstation_iron_supply_only_when_materials_run_out]
Supply one material item only when the materials supplied to Iron cooking station run out.

- Default value: false

### Iron cooking station: Store Only If Product Exists In Container / [piece_cookingstation_iron_store_only_if_product_exists]
Store only if the product item for Iron cooking station is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Smelter: Allow Process / [allow_processing_by_piece_smelter]
Specify the automatic processes to allow for Smelter. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Craft, Refuel, Store
- Acceptable values: None, Craft, Refuel, Store
- Multiple values can be set at the same time by separating them with , (e.g. Craft, Refuel)

### Smelter: Container Search Range / [container_search_range_by_piece_smelter]
Specify the maximum distance Smelter will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Smelter: Container Reference Limit / [container_reference_limit_by_piece_smelter]
Specify the maximum number of nearby containers that Smelter will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Smelter: Number Of Materials To Stop Supplying / [piece_smelter_material_count_of_suppress_processing]
When the number of material items stored in the container is about to become less than the specified quantity, the supply of materials to Smelter will be stopped. If set to 0, stopping the material supply based on the number of material items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Smelter: Number Of Product Stacks To Stop Processing / [piece_smelter_product_stacks_of_suppress_processing]
Stops Smelter once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Smelter: Supply Only If Materials Run Out / [piece_smelter_supply_only_when_materials_run_out]
Supply one material item only when the materials supplied to Smelter run out.

- Default value: false

### Smelter: Number Of Fuels To Stop Refuel / [piece_smelter_fuel_count_of_suppress_processing]
When the number of fuel items stored in the container is about to become less than the specified quantity, refuel to Smelter will be stopped. If set to 0, stopping refuel based on the number of fuel items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Smelter: Refuel Only If Fuels Run Out / [piece_smelter_refuel_only_when_out_of_fuel]
Refuel only when the fuel for Smelter runs out.

- Default value: false

### Smelter: Refuel Only When Materials Supplied / [piece_smelter_refuel_only_when_materials_supplied]
Refuel only when material items are supplied to Smelter.

- Default value: false

### Smelter: Store Only If Product Exists In Container / [piece_smelter_store_only_if_product_exists]
Store only if the product item for Smelter is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Spinning wheel: Allow Process / [allow_processing_by_piece_spinningwheel]
Specify the automatic processes to allow for Spinning wheel. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Store
- Acceptable values: None, Craft, Store
- Multiple values can be set at the same time by separating them with , (e.g. Craft, Store)

### Spinning wheel: Container Search Range / [container_search_range_by_piece_spinningwheel]
Specify the maximum distance Spinning wheel will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Spinning wheel: Container Reference Limit / [container_reference_limit_by_piece_spinningwheel]
Specify the maximum number of nearby containers that Spinning wheel will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Spinning wheel: Number Of Materials To Stop Supplying / [piece_spinningwheel_material_count_of_suppress_processing]
When the number of material items stored in the container is about to become less than the specified quantity, the supply of materials to Spinning wheel will be stopped. If set to 0, stopping the material supply based on the number of material items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Spinning wheel: Number Of Product Stacks To Stop Processing / [piece_spinningwheel_product_stacks_of_suppress_processing]
Stops Spinning wheel once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Spinning wheel: Supply Only If Materials Run Out / [piece_spinningwheel_supply_only_when_materials_run_out]
Supply one material item only when the materials supplied to Spinning wheel run out.

- Default value: false

### Spinning wheel: Store Only If Product Exists In Container / [piece_spinningwheel_store_only_if_product_exists]
Store only if the product item for Spinning wheel is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Stone oven: Allow Process / [allow_processing_by_piece_oven]
Specify the automatic processes to allow for Stone oven. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Craft, Refuel, Store
- Acceptable values: None, Craft, Refuel, Store
- Multiple values can be set at the same time by separating them with , (e.g. Craft, Refuel)

### Stone oven: Container Search Range / [container_search_range_by_piece_oven]
Specify the maximum distance Stone oven will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Stone oven: Container Reference Limit / [container_reference_limit_by_piece_oven]
Specify the maximum number of nearby containers that Stone oven will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Stone oven: Number Of Materials To Stop Supplying / [piece_oven_material_count_of_suppress_processing]
When the number of material items stored in the container is about to become less than the specified quantity, the supply of materials to Stone oven will be stopped. If set to 0, stopping the material supply based on the number of material items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Stone oven: Number Of Product Stacks To Stop Processing / [piece_oven_product_stacks_of_suppress_processing]
Stops Stone oven once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Stone oven: Supply Only If Materials Run Out / [piece_oven_supply_only_when_materials_run_out]
Supply one material item only when the materials supplied to Stone oven run out.

- Default value: false

### Stone oven: Number Of Fuels To Stop Refuel / [piece_oven_fuel_count_of_suppress_processing]
When the number of fuel items stored in the container is about to become less than the specified quantity, refuel to Stone oven will be stopped. If set to 0, stopping refuel based on the number of fuel items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Stone oven: Refuel Only If Fuels Run Out / [piece_oven_refuel_only_when_out_of_fuel]
Refuel only when the fuel for Stone oven runs out.

- Default value: false

### Stone oven: Refuel Only When Materials Supplied / [piece_oven_refuel_only_when_materials_supplied]
Refuel only when material items are supplied to Stone oven.

- Default value: false

### Stone oven: Store Only If Product Exists In Container / [piece_oven_store_only_if_product_exists]
Store only if the product item for Stone oven is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Windmill: Allow Process / [allow_processing_by_piece_windmill]
Specify the automatic processes to allow for Windmill. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Store
- Acceptable values: None, Craft, Store
- Multiple values can be set at the same time by separating them with , (e.g. Craft, Store)

### Windmill: Container Search Range / [container_search_range_by_piece_windmill]
Specify the maximum distance Windmill will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Windmill: Container Reference Limit / [container_reference_limit_by_piece_windmill]
Specify the maximum number of nearby containers that Windmill will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Windmill: Number Of Materials To Stop Supplying / [piece_windmill_material_count_of_suppress_processing]
When the number of material items stored in the container is about to become less than the specified quantity, the supply of materials to Windmill will be stopped. If set to 0, stopping the material supply based on the number of material items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Windmill: Number Of Product Stacks To Stop Processing / [piece_windmill_product_stacks_of_suppress_processing]
Stops Windmill once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Windmill: Supply Only If Materials Run Out / [piece_windmill_supply_only_when_materials_run_out]
Supply one material item only when the materials supplied to Windmill run out.

- Default value: false

### Windmill: Store Only If Product Exists In Container / [piece_windmill_store_only_if_product_exists]
Store only if the product item for Windmill is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Wisp fountain: Allow Process / [allow_processing_by_piece_wisplure]
Specify the automatic processes to allow for Wisp fountain. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Store
- Acceptable values: None, Store

### Wisp fountain: Container Search Range / [container_search_range_by_piece_wisplure]
Specify the maximum distance Wisp fountain will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Wisp fountain: Container Reference Limit / [container_reference_limit_by_piece_wisplure]
Specify the maximum number of nearby containers that Wisp fountain will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Wisp fountain: Number Of Product Stacks To Stop Processing / [piece_wisplure_product_stacks_of_suppress_processing]
Stops Wisp fountain once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Wisp fountain: Store Only If Product Exists In Container / [piece_wisplure_store_only_if_product_exists]
Store only if the product item for Wisp fountain is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Sap extractor : Allow Process / [allow_processing_by_piece_sapcollector]
Specify the automatic processes to allow for Sap extractor . "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Store
- Acceptable values: None, Store

### Sap extractor : Container Search Range / [container_search_range_by_piece_sapcollector]
Specify the maximum distance Sap extractor  will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Sap extractor : Container Reference Limit / [container_reference_limit_by_piece_sapcollector]
Specify the maximum number of nearby containers that Sap extractor  will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Sap extractor : Number Of Product Stacks To Stop Processing / [piece_sapcollector_product_stacks_of_suppress_processing]
Stops Sap extractor  once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Sap extractor : Store Only If Product Exists In Container / [piece_sapcollector_store_only_if_product_exists]
Store only if the product item for Sap extractor  is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Eitr refinery: Allow Process / [allow_processing_by_piece_eitrrefinery]
Specify the automatic processes to allow for Eitr refinery. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Store
- Acceptable values: None, Craft, Refuel, Store
- Multiple values can be set at the same time by separating them with , (e.g. Craft, Refuel)

### Eitr refinery: Container Search Range / [container_search_range_by_piece_eitrrefinery]
Specify the maximum distance Eitr refinery will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Eitr refinery: Container Reference Limit / [container_reference_limit_by_piece_eitrrefinery]
Specify the maximum number of nearby containers that Eitr refinery will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Eitr refinery: Number Of Materials To Stop Supplying / [piece_eitrrefinery_material_count_of_suppress_processing]
When the number of material items stored in the container is about to become less than the specified quantity, the supply of materials to Eitr refinery will be stopped. If set to 0, stopping the material supply based on the number of material items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Eitr refinery: Number Of Product Stacks To Stop Processing / [piece_eitrrefinery_product_stacks_of_suppress_processing]
Stops Eitr refinery once the containers it uses hold this many stacks of its product in total; a station that crafts also counts what it is still making. A station that crafts stops taking materials; a station that only stores its product stops moving it into containers. If set to 0, there is no limit.

- Default value: 0
- Acceptable value range: From 0 to 99

### Eitr refinery: Supply Only If Materials Run Out / [piece_eitrrefinery_supply_only_when_materials_run_out]
Supply one material item only when the materials supplied to Eitr refinery run out.

- Default value: false

### Eitr refinery: Number Of Fuels To Stop Refuel / [piece_eitrrefinery_fuel_count_of_suppress_processing]
When the number of fuel items stored in the container is about to become less than the specified quantity, refuel to Eitr refinery will be stopped. If set to 0, stopping refuel based on the number of fuel items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

### Eitr refinery: Refuel Only If Fuels Run Out / [piece_eitrrefinery_refuel_only_when_out_of_fuel]
Refuel only when the fuel for Eitr refinery runs out.

- Default value: false

### Eitr refinery: Refuel Only When Materials Supplied / [piece_eitrrefinery_refuel_only_when_materials_supplied]
Refuel only when material items are supplied to Eitr refinery.

- Default value: false

### Eitr refinery: Store Only If Product Exists In Container / [piece_eitrrefinery_store_only_if_product_exists]
Store only if the product item for Eitr refinery is already stored in the container. If there is no container in the range that stores product items, items are produced as usual.

- Default value: false

### Ballista: Allow Process / [allow_processing_by_piece_turret]
Specify the automatic processes to allow for Ballista. "Craft" produces items by automatically consuming the required materials. "Refuel" automatically supplies fuel items. "Store" automatically stores the produced items in containers. "Charge" automatically replenishes consumable items such as ammo.

- Default value: Charge
- Acceptable values: None, Charge

### Ballista: Container Search Range / [container_search_range_by_piece_turret]
Specify the maximum distance Ballista will search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Ballista: Container Reference Limit / [container_reference_limit_by_piece_turret]
Specify the maximum number of nearby containers that Ballista will reference in distance order. Set to 0 for unlimited. The default is 0.

- Default value: 0
- Acceptable value range: From 0 to 999

### Ballista: Number Of Items To Stop Charge / [piece_turret_number_of_items_to_stop_charge]
When the number of items stored in the container is about to become less than the specified quantity, charge to Ballista will be stopped. If set to 0, stopping charge based on the number of items will be disabled.

- Default value: 1
- Acceptable value range: From 0 to 999

## [ #6 Automatic Feeding ] / [automatic_feeding]
### Module / [module]
Turn this module on or off. While it is off, Automatics adds none of its logic to the game. The change applies while the game is running, so no restart is needed.

- Default value: Disabled
- Acceptable values: Enabled, Disabled

### Feed Search Range / [feed_search_range]
Specify the range in which the animal searches for food. If set to 0, the default value for each animal will be used. (Unit: meter)

- Default value: 0
- Acceptable value range: From 0 to 64

### Need To Get Close To Eat The Feed / [need_get_close_to_eat_the_feed]
Specify whether the animal needs to approach the container or player to eat the feed.

- Default value: false

### Allow To Feed From Container / [allow_to_feed_from_container]
Specify the types of animals allowed to feed from the container.

- Default value: Tamed
- Acceptable values: None, Wild, Tamed, All
- Multiple values can be set at the same time by separating them with , (e.g. Wild, Tamed)

### Allow To Feed From Player / [allow_to_feed_from_player]
Specify the types of animals allowed to feed from the player inventory.

- Default value: None
- Acceptable values: None, Wild, Tamed, All
- Multiple values can be set at the same time by separating them with , (e.g. Wild, Tamed)

## [ #7 Automatic Repair ] / [automatic_repair]
### Module / [module]
Turn this module on or off. While it is off, Automatics adds none of its logic to the game. The change applies while the game is running, so no restart is needed.

- Default value: Disabled
- Acceptable values: Enabled, Disabled

### Crafting Station Search Range / [crafting_station_search_range]
Specify the range to search for a crafting station to be used to repair items. Setting to 0 disables periodic item repair. (Unit: meters)

- Default value: 16
- Acceptable value range: From 0 to 64

### Repair Items When Accessing The Crafting Station / [repair_items_when_accessing_the_crafting_station]
Specify whether or not to repair all items that can be repaired when a crafting station's GUI is opened.

- Default value: false

### Item Repair Message / [item_repair_message]
Specify where the message is displayed when an item is repaired.

- Default value: None
- Acceptable values: None, Center, TopLeft

### Piece Search Range / [piece_search_range]
Specify the range to search for pieces to repair. Setting to 0 disables periodic piece repair. (Unit: meters)

- Default value: 16
- Acceptable value range: From 0 to 64

### Piece Repair Message / [piece_repair_message]
Specify where the message is displayed when a piece is repaired.

- Default value: None
- Acceptable values: None, Center, TopLeft

## [ #8 Automatic Mining ] / [automatic_mining]
### Module / [module]
Turn this module on or off. While it is off, Automatics adds none of its logic to the game. The change applies while the game is running, so no restart is needed.

- Default value: Disabled
- Acceptable values: Enabled, Disabled

### Mine Nearby Minerals Automatically / [enable_automatic_mining]
Specify whether to mine nearby minerals automatically. The module's other features, such as area mining, are not affected.

- Default value: true

### Mining Interval / [mining_interval]
Specify the interval between mining attempts. (Unit: seconds)

- Default value: 1.5
- Acceptable value range: From 0.1 to 4

### Mining Range / [mining_range]
Specify the maximum distance to search for a mineral. If set to 0, the pickaxe's attack range is used. (Unit: meters)

- Default value: 3
- Acceptable value range: From 0 to 32

### Allow Mining Mineral / [allow_mining_mineral]
Specify the minerals to be automatically mined.

- Default value: CopperDeposit, TinDeposit, MudPile, ObsidianDeposit, SilverVein, PetrifiedBone, SoftTissue, FlametalDeposit, PetrifiedGammeltroll, SilverDeposit, AncientArmour, AncientSword

### Need To Equip Pickaxe For Mining / [need_to_equip_pickaxe]
Specify whether a pickaxe needs to be equipped to attempt automatic mining. Disabling this option uses the equipped pickaxe if it can mine the target, otherwise the lowest-tier pickaxe in the player's inventory that can. If no suitable pickaxe is found, automatic mining is not attempted.

- Default value: true

### Allow Mining Underground Minerals / [allow_mining_underground_minerals]
Specify whether or not to allow mining underground minerals.

- Default value: true

### Need To Equip Wishbone For Mining Underground Minerals / [need_to_equip_wishbone_for_mining_underground_minerals]
Specify whether or not to require a wishbone to mine underground minerals.

- Default value: true

### Attempt Mining / [mining_key]
Specify a shortcut key to attempt mining. If a valid shortcut key is assigned to this option, mining at regular intervals is disabled.

- Default value:

### Area Mining / [enable_area_mining]
Specify whether a pickaxe swing that hits an allowed mineral also hits other allowed minerals and rock sections near the hit point. Each extra target wears the pickaxe as a hit does; stamina and skill gain stay once per swing. Requires the module to be running.

- Default value: true

### Area Mining Radius / [area_mining_radius]
Specify how far from the hit point area mining reaches. (Unit: meters)

- Default value: 3
- Acceptable value range: From 1 to 10

### Area Mining Max Targets / [area_mining_max_targets]
Specify the maximum number of extra minerals or rock sections one swing can hit.

- Default value: 8
- Acceptable value range: From 1 to 32

## [ #9 Automatic Storage ] / [automatic_storage]
### Module / [module]
Turn this module on or off. While it is off, Automatics adds none of its logic to the game. The change applies while the game is running, so no restart is needed.

- Default value: Disabled
- Acceptable values: Enabled, Disabled

### Storage Search Range / [storage_search_range]
Specify the maximum distance to search for containers. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Container Reference Limit / [container_reference_limit]
Specify the maximum number of nearby containers to reference in distance order. Set to 0 for unlimited.

- Default value: 0
- Acceptable value range: From 0 to 999

### Allow Container / [allow_container]
Specify containers that allow automatic storage. Private chests are excluded by default.

- Default value: PieceChestWood, PieceChest, PieceChestBlackmetal, PieceChestCharred, PieceChestBarrel, PieceChestGrausten, PieceChestWardrobe

### Allowed Item Types / [allowed_item_types]
Specify Valheim item types that can be stored automatically.

- Default value: Material, Consumable, OneHandedWeapon, Bow, Shield, Helmet, Chest, Ammo, Customization, Legs, Hands, Trophy, TwoHandedWeapon, Torch, Misc, Shoulder, Utility, Tool, Attach_Atgeir, Fish, TwoHandedWeaponLeft, AmmoNonEquipable, Trinket

### Excluded Items / [excluded_items]
Specify items that automatic storage must keep in the player inventory. Plain entries are case-insensitive partial matches against internal names, prefab names, and localized display names. Prefix an entry with r/ to use a regular expression.

- Default value:

### Store Hotbar Items / [store_hotbar_items]
Specify whether items in the hotbar row can be stored automatically.

- Default value: false

### Store Items / [store_items_key]
Specify the shortcut key that stores eligible inventory items into nearby containers once. If no key is assigned, automatic storage does not run.

- Default value:

### Storage Message / [storage_message]
Specify where the message is displayed after items are stored.

- Default value: None
- Acceptable values: None, Center, TopLeft

## [ #10 Automatic Pickup ] / [automatic_pickup]
### Module / [module]
Turn this module on or off. While it is off, Automatics adds none of its logic to the game. The change applies while the game is running, so no restart is needed.

- Default value: Disabled
- Acceptable values: Enabled, Disabled

### Automatic Pickup Range / [automatic_pickup_range]
Specify the range within which objects are automatically picked up. (Unit: meters)

- Default value: 4
- Acceptable value range: From 1 to 64

### Automatic Pickup Interval / [automatic_pickup_interval]
Specify the interval at which objects are automatically picked up. Setting it to 0 disables periodic automatic pickup. (Unit: second)

- Default value: 0.5
- Acceptable value range: From 0 to 4

### Pickup All Nearby / [pickup_all_nearby_key]
Interacting with a pickable object using this key picks up all of the same objects within range at once. Periodic automatic pickup keeps running at its interval whether or not this key is assigned.

- Default value: E + LeftShift

## [ #11 Automatic Farming ] / [automatic_farming]
### Module / [module]
Turn this module on or off. While it is off, Automatics adds none of its logic to the game. The change applies while the game is running, so no restart is needed.

- Default value: Disabled
- Acceptable values: Enabled, Disabled

### Harvest And Replant Automatically / [enable_automatic_farming]
Specify whether to enable the feature that harvests grown crops and replants the regrowing sapling at the harvested spot. With the Player Inventory seed source it works around the player; with Nearby Containers it works around the containers you designate. The module's other features, such as grid planting, are not affected.

- Default value: true

### Farming Interval / [farming_interval]
Specify the interval between farming attempts. (Unit: seconds)

- Default value: 1.5
- Acceptable value range: From 0.1 to 4

### Farming Range / [farming_range]
Specify the maximum distance to search for crops to harvest. (Unit: meters)

- Default value: 8
- Acceptable value range: From 1 to 64

### Allow Farming Crop / [allow_farming_crop]
Specify the crops to be automatically harvested and replanted. Crops that have no plantable sapling are harvested only.

- Default value: Dandelion, Mushroom, Raspberries, Blueberries, Carrot, CarrotSeeds, YellowMushroom, Thistle, Turnip, TurnipSeeds, Onion, OnionSeeds, Barley, Cloudberries, Flax, JotunPuffs, Magecap, Fiddlehead, SmokePuff, Vineberry, VineberrySeeds, Kale, KaleSeeds, Oat, Poteitr, Lingonberries, BlueMushroom

### Seed Reserve / [seed_reserve]
Specify the number of each seed item to keep in the active seed source. Planting stops consuming a seed once its count would drop below this value.

- Default value: 10
- Acceptable value range: From 0 to 999

### Seed Source / [seed_source]
Specify where planting draws seeds from. Player Inventory keeps the original behavior: crops are searched, harvested, and replanted around you and seeds come from your inventory. Nearby Containers makes farming container-based: the search is anchored to the containers you designate (see Designate Container Role), each planting box draws its input only from same-role boxes within Farming Range, and harvested yield is deposited into storage boxes first, otherwise the matching planting box, never the player inventory. The Seed Reserve is kept within whichever pool is in use.

- Default value: Inventory
- Acceptable values: Inventory, Containers

### Proactive Sowing / [enable_proactive_sowing]
Specify whether to also sow seeds into nearby empty cultivated ground, in addition to replanting harvested spots.

- Default value: true

### Sowing Spacing Factor / [sowing_spacing_factor]
Specify the multiplier applied to each sapling's grow radius to derive the sowing grid spacing. Higher values space saplings farther apart.

- Default value: 1
- Acceptable value range: From 0.5 to 4

### Attempt Farming / [farming_key]
Specify a shortcut key to attempt farming. If a valid shortcut key is assigned to this option, farming at regular intervals is disabled.

- Default value:

### Designate Container Role / [designate_container_key]
Specify a shortcut key that cycles the role of the container you are looking at between none, cultivation, seed harvest, and storage. When Seed Source is set to Nearby Containers, a cultivation box grows crops from the seeds it holds, a seed-harvest box grows seeds from the crops it holds, and harvested yield is deposited into nearby storage boxes first, otherwise back into the matching planting box. Changing a role claims ownership of the chest, so it only works on containers you can access.

- Default value:

### Grid Planting / [enable_grid_planting]
Hold the grid planting key while planting by hand to add a grid of the same crop. Each extra crop costs what a hand-placed crop costs: seeds from your inventory (and from nearby containers while Automatic Supply is running), stamina and cultivator durability. As in vanilla, seeds are not needed in free-build worlds. Invalid cells are skipped; planting stops when you cannot pay. Independent of automatic farming, Seed Source and Seed Reserve.

- Default value: true

### Grid Planting Modifier / [grid_planting_key]
Hold this key while placing a crop to plant a grid. While it is held, the crop preview also snaps to the rows of a growing crop of the same kind nearby (same spacing and orientation), so a new grid lines up with an earlier one. An unbound key makes every manual crop placement plant a grid, except while alternate placement (AltPlace) is held. Left Alt does not conflict with vanilla alternate placement.

- Default value: LeftAlt

### Grid Planting Size / [grid_planting_size]
Number of cells per side, including the manually planted crop. Set to 1 to disable. The grid follows the placement rotation and uses grow radius multiplied by Sowing Spacing Factor for spacing.

- Default value: 5
- Acceptable value range: From 1 to 9

## [ #12 Automatic Supply ] / [automatic_supply]
### Module / [module]
Turn this module on or off. While it is off, Automatics adds none of its logic to the game. The change applies while the game is running, so no restart is needed.

- Default value: Disabled
- Acceptable values: Enabled, Disabled

### Supply Range / [supply_range]
Specify the range around the player within which containers supply items for manual crafting, building, planting and station intake. Items in the inventory are used first, then containers from the nearest. Containers you cannot access, such as warded, private or in-use ones, are never used. (Unit: meters)

- Default value: 10
- Acceptable value range: From 1 to 64
