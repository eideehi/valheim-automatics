#### Unreleased
- Automatic Processing now refuels a stone oven that has run out of fuel, including a newly built one. Before, an oven that reached zero fuel was never refuelled automatically again.
- The stone oven's `Refuel Only When Materials Supplied` now refuels while food is waiting to be cooked. Before, it stopped refuelling once every slot was full, and the oven then stayed out of fuel. An oven without fuel also takes food from containers while fuel for it is available nearby, so it can start from empty.
- Automatic Processing now fills a smelter-type station's fuel to its maximum instead of stopping one short.
- Automatic Processing's `Number Of Product Stacks To Stop Processing` now counts the product in all containers a station uses together, plus what a crafting station is still making. Before, a crafting station kept going while any one container was under the limit and then stored past it, and each storing station treated the limit as a cap per container. Check this value if you set it for several containers.
- Automatic Processing's fires and lights that only refuel now share one group of five settings instead of five settings each: `Fires And Lights To Refuel` chooses which of them are refuelled, and the `Fires and lights:` settings apply to all of them. An existing configuration is converted once: a fire that was allowed to refuel stays in the list, and each shared value is the one most of them had (the default wins a tie; otherwise the smaller value). Fires whose value changed are named in the log.
- The fermenter no longer has a `Supply Only If Materials Run Out` option; a fermenter only ever takes materials while it is empty, so the option did nothing. Its line is removed from existing configurations.
- Automatic Mining can now mine ordinary stone rocks by adding `Rock` to `Allow Mining Mineral`; off by default, since rocks are everywhere and each hit wears the pickaxe. Rocks you added yourself through `custom_mineral` now resolve to `Rock`, so add `Rock` to your lists to keep mining or pinning them.
- Added Automatic Supply, off by default for new and existing configurations: when you craft at a crafting station, build with the hammer or plant with the cultivator (grid planting included), items in the containers within `Supply Range` (10 m by default) count toward the requirements, and what your inventory lacks is taken from them, nearest first. When you add fuel to a fire, ore or fuel to a smelter-type station, or food to a cooking station without carrying a suitable item, one comes from a nearby container. A craft or placement never completes without being fully paid for, and items taken for an action that does not use them go back. Warded, private, in-use and other containers you cannot access are never used, and recipes where you choose one ingredient work as before.
- Automatic Farming adds grid planting: hold Left Alt while planting with the cultivator to plant more of the same crop in a configurable grid (5×5 by default). Each extra crop costs what a hand-placed one does (seeds, stamina and cultivator durability; no seeds in free-build worlds, as in vanilla); invalid cells are skipped, and planting stops when you cannot pay for another crop. While Left Alt is held, the crop preview snaps to the rows of a growing crop of the same kind nearby (same spacing and orientation), so a new grid continues an earlier one; the snapped position goes through the game's usual placement checks.
- Automatic Mining adds area mining, on by default while the module runs: when your pickaxe swing hits an allowed mineral, it also hits up to 8 other allowed minerals or rock sections within 3 meters of the hit, following the tool tier, allow list and underground rules. Each extra target wears the pickaxe by one hit's durability, scaled by the world's durability modifier; stamina and skill gain stay once per swing.
- Automatic Mapping's pin navigation adds a guide swarm, on by default: small glowing lights gather around you and stream along the ground toward the navigation target, stop appearing within 5 m of it, and hide while the large map is open. Its colour is configurable.
- Added an optional shortcut to save a map pin for the object or ground you look at, using object names and icon-pack icons and skipping nearby duplicates. Looking at a dungeon or special spot that Automatic Mapping recognizes (its structure, the ground within its area or a dungeon's interior) pins that place once with its recognized name and icon; player-built pieces, creatures and other objects keep their own pins.
- Automatic Door, Automatic Pickup, Automatic Processing, Automatic Mapping, Automatic Mining, Automatic Feeding, Automatic Farming, Automatic Repair, Automatic Storage and Automatic Supply can all be turned on and off while the game is running.
- Automatic Repair and Automatic Mining now wear your tools by the world's durability modifier, as the game does when you repair or mine by hand; on a default world nothing changes.
- Every module is now off on a new install; turn on the ones you want with their `Disable Module` option. An existing config keeps each module on or off as it was (a module whose `Disable Module` entry is missing from an older config, or holds a value Automatics cannot read, is turned on; the new Automatic Supply is not), and the settings inside each module keep their defaults.
- Automatics recognizes more of the objects Valheim 1.0 added: crops, containers, doors, deposits, creatures, spawners, vehicles and dungeons. An update adds, once, the objects newly included in each list's default to lists that still hold a default Automatics recognizes; lists whose defaults did not change get no additions. A list you edited is left as it is, as are some lists kept unchanged since 1.4.0 or earlier, so add the new names to those yourself.
- The object list entry for flax is now spelled `Flax`; existing configurations are updated.
- Object list entries written with a leading space by the 1.6.0 update no longer matched their targets; existing configurations are now repaired.
- Automatic Pickup's `Pickup All Nearby` key is now Left Shift + E by default, and binding it no longer turns periodic pickup off; only an `Automatic Pickup Interval` of 0 does. An existing config whose key is bound gets its interval set to 0 once, so it keeps picking up by key only.
- Improved the Japanese translation: fixed mistranslations (the `Hide Pins Outside Explored Area` option name said the opposite), used the game's own Japanese terms for vanilla items, stations and concepts, and made wording and style consistent across options and messages. Automatic Repair and Automatic Supply have new Japanese names (自動修理 and 自動供給).
- Automatic Storage's `Allowed Item Types` list now shows item types by translated names instead of internal names; the saved values are unchanged.
- Corrected English option descriptions that did not match what the options do, and fixed typos and grammar in the English texts.

#### v1.7.0 [2026-07-19]
- Added Automatic Storage for moving eligible inventory items into nearby containers by hotkey.
- Added Automatic Farming for harvesting, replanting, sowing, seed reserves, and role-based container workflows.
- Improved Automatic Mapping performance, cache coherence, navigation, cleanup, and object matching.
- Improved Automatic Processing performance and container handling, including dedicated-server-safe mutations and storage effects.
- Improved multiplayer access and ownership handling for doors, feeding, farming, pickup, processing, and containers.
- Fixed config migration and console command edge cases, including bounded regular-expression matching.
- Added optional in-game smoke tests for live Storage, Door, Pickup, and Processing workflows.

#### v1.6.0 [2026-05-17]
- Updated for Valheim 0.221.12 and added automation data for Ashlands, Bog Witch, and Call to Arms content.
- Added a config migration that appends new default automation targets to existing allow lists.
- Added map navigation from selected pins, including HUD distance display while navigating.
- Improved Automatic Mapping performance and stability with throttled dynamic scans, spatial pin lookups, safer static scan invalidation, dense-area scan fallback, and cached MineRock5 collider data.
- Improved Automatic Door behavior for predicted player movement and multiplayer or dedicated-server sessions.
- Added configurable Automatic Processing connection effects and fixed stale container lookup reuse across different search limits.
- Fixed Automatic Pickup and Automatic Feeding ownership handling in multiplayer or dedicated-server sessions to avoid unsaved item changes or lost pickups.
- Fixed translations not refreshing after language changes.
- Modernized the build scripts, release checks, README, configuration reference, and user guide.

#### v1.5.1 [2023-10-15]
- Fixed an issue that could potentially cause exceptions when attempting to place containers added by specific mods. **([#61])**

#### v1.5.0 [2023-10-08]
- Update for Valheim 0.217.24
- The removal of the previously deprecated "module_disable" option and the "resources_directory"

#### v1.4.8 [2023-07-09]
- Update for Valheim 0.216.9
- Removed the "allow_non_duplicate_pins" option from the "removemappins" command. Instead, added the "dry-run" and "dangerous-mode" options.
- Enabled specifying prefab names as targets for custom icons. For more details, please refer to [custom-icon-pack.adoc](https://github.com/eideehi/valheim-automatics/blob/1.4.8/docs/custom-icon-pack.adoc#format-of-custom-map-iconjson).

#### v1.4.7 [2023-05-13]
- Added a console command to remove map pins.

#### v1.4.6 [2023-05-02]
- Fixed a bug where static pins were immediately deleted when the "Save Static Object Pins" option was not enabled.

#### v1.4.5 [2023-05-01]
- Fixed a bug where pins for static objects were sometimes duplicated when the player moved using portals.
- Fixed a bug where an exception could occur when trying to delete a pin by right-clicking it.
- Automatic Pickup no longer pickup items if it would cause the player to become overweight.
- Added an option to the Automatic Processing to store only if products exist in the container.
- Added a Ballista to Automatic Processing targets.
- Changed the display name and description of the Automatic Processing config.

#### v1.4.4 [2023-03-23]
- Fixed a bug where the automatic door stops functioning when the player moves a certain distance away from it.
- Fixed a bug where pins would be duplicated and added when the vehicle moves, if the vehicle's automatic mapping is enabled.

#### v1.4.3 [2023-03-18]
- Updated for changes in Valheim 0.214.2.
- Fixed a bug where the "Pickup All Nearby" shortcut key wouldn't work if it included an interact key (e.g. Shift + E).
- Fixed a bug that caused wild animals to attack containers with food under certain conditions, even if they were allowed to eat from them.
- Fixed a bug where portals wouldn't be automatically pinned, even if "Allow Pinning Portal" was enabled.
- Fixed a bug where pins would remain on the map even after disabling automatic mapping in the configuration.

#### v1.4.2 [2023-03-12]
- Fixed a bug where the game won't start if some modules are disabled.

#### v1.4.1 [2023-03-11]
- Fixed a bug where the toggle key for automatic doors would become disabled after using it to turn off the function.
- Fixed a bug where the automatic mapping function would delete unrelated pins, such as pins added by users or boss pins.
- Fixed a bug where the automatic mapping function would cause an exception when loading the world without exiting the game after logging out of the world.
- Fixed a bug where the automatic door closing function would not work, causing the door to remain open.
- The "printobjects" command can now output doors and containers.
- Fixed a bug where the automatic repair function would cause an exception under certain conditions.
- Fixed a bug where the "Distance For Automatic Opening" configuration for automatic doors was not functioning and instead referenced "Distance For Automatic Closing."
- Modified the "Disable Module" item in the configuration to be more intuitive.
- Added "Hexagonal gate" to the door presets.

#### v1.4.0 [2022-12-26]
- Overall Rewriting
  * AutomaticDoor
    - Logic Improvements
  * AutomaticFeeding
    - A little refactoring
  * AutomaticMapping
    - Add Vehicle category
    - Portal pinning is now a stand-alone option, not in the Other category
    - Static object pins are no longer saved by default. They can be changed to be saved in the options
    - You can now hold down the left Shift key and click on a pin on the map to force the pin to change to save
    - Added option to automatically remove pins when destructible objects are destroyed. (probably won't work perfectly, so don't expect too much)
  * AutomaticMining
    - Limit the number of parts that can be damaged by automatic mining to a maximum of 3 at a time
  * AutomaticProcessing
    - Removed the option to limit crafting by the total number of items in a product, instead added an option to limit crafting by the number of product stacks
    - Added option to replenish material only when the material in the processor runs out
    - Added option to refuel only when the fuel in the processor runs out
    - Smelter's automatic crafting has been improved to process time-based quantities of material even when away from it for extended periods of time
  * AutomaticRepair
    - A little refactoring
- Added AutomaticPickup feature
- Custom objects have been renamed User-defined objects and moved to the General category of the Config
  * I have documentation on how to add user-defined objects, so please read through it if necessary
  * https://github.com/eideehi/valheim-automatics/blob/1.4.0/docs/add-user-defined-object.adoc
- Mistlands content has been added to the automation
- Added options to completely disable each feature
- Change the console command to take arguments like the CLI

#### v1.3.2 [2022-12-06]
- Rebuild for Mistlands Update
  * Content added in the Mistlands Update is not yet available for automation by Automatics

#### v1.3.1 [2022-11-13]
- Fix an error that occurred if ConfigurationManager was not installed
- Custom map icons can now be treated as child mod of Automatics
  * For details, refer to the "Directory structure" section of the README
- Add option to hide pin names on custom map icon
  * For details, refer to the "options" entry in the "Format of custom-map-icon.json" section of the README
- Add option to set the scale at which custom map icons appear on the map
  * For details, refer to the "options" entry in the "Format of custom-map-icon.json" section of the README

#### v1.3.0 [2022-04-26]
- Add the feature of 'Automatic Mining'
- Add option to change the directory to load resources such as translation files and custom icons
- Add option to change the position of the message displayed when enabling/disabling the automatic door function with a shortcut key
- Change so that the slider for handling floating decimals in the configurator increases or decreases the numerical value by 0.01 increments
- Reviewed and corrected the key names and descriptions in the configuration options
  * The configurations are automatically migrated, but be sure to check the values of the configurations after starting the game to be sure
- Rewrote most of the automatic mapping feature
  * Fix inability to apply custom icons with internal names to birds
  * Change the deposit pinning location to the center of the object
    - Due to this change, large deposits such as copper may be pinned in duplicate with existing pins
  * Add custom options for other objects, dungeons, and spots

#### v1.2.1 [2022-04-19]
- Change the automatic door to not work if there is an obstacle between the player and the door
- Change the minimum automatic door process interval to 0
  * Setting the value below 0.1 disables automatic opening or closing. The substantive minimum interval remains unchanged from 0.1
- Change so that the printnames command searches all translations
  * With this change, the printnames command can now take multiple arguments, allowing for more powerful filtering
  * See [printnames usage](https://github.com/eideehi/valheim-automatics/blob/1.2.1/README.adoc#printnames-wordregexp) for command details

#### v1.2.0 [2022-04-16]
- Change the format of the Cutom Map Icon settings file from csv to json
  * See ["Format of custom-map-icon.json"](https://github.com/eideehi/valheim-automatics/tree/1.2.0/README.adoc#format-of-custom-map-iconjson) for more information on json files
- Change translation files format from csv to json
- Fix 'Standing brazier' was omitted from 'Automatic Processing'
- Fix to be able to output names of fish, birds and dungeons with the 'printnames' command
- Fix a bug that prevented some config values from being read accurately

#### v1.1.2 [2022-04-14]
- Add command to output internal and display names
  * See ["Console commands"](https://github.com/eideehi/valheim-automatics/tree/1.1.2/README.adoc#console-commands) in the README for details
- Add options to suppress automatic processing based on item count
- Add option to automatic refuel only when materials supplied
- Change 'Automatic Repair' not to work during game pauses
- Change 'Allow...(Custom)' options to evaluate for exact match if an internal name is specified, or partial match if a display name is specified
  * The "$enemy_boar" matches only "Boar", but "Greydwarf" matches "Greydwarf brute" and "Greydwarf shaman" in addition to "Greydwarf"
  * The same goes for TARGET for [Custom Map Icon](https://github.com/eideehi/valheim-automatics/tree/1.1.2/README.adoc#custom-map-icons)

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

<!-- Issue / PR links -->
[#61]: https://github.com/eideehi/valheim-automatics/issues/61
