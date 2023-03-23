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
