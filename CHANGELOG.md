#### Unreleased
- Automatic Door no longer opens or closes doors inside a dungeon, even when `Allow Automatic Door` allows them. Before, a dungeon door that the list allowed was opened and closed like any other door, including closing one you had opened by hand.
- Automatic Processing now gives the player who built a cooking station, iron cooking station or stone oven the skill and bonus food the game gives for putting food in or taking it out by hand: supplying food counts as putting it in, and storing cooked food as taking it out. The builder gains them only while their own game owns the station; in multiplayer, nobody does while another player's game owns it.
- Automatic Farming's `Allow Farming Crop` now lists by default only the crops the cultivator can plant. Wild plants that the cultivator cannot plant, such as dandelions, mushrooms, berries and thistle, are left out, so farming no longer harvests them and periodic pickup collects them again. An existing configuration whose list still holds the previous default loses them on update; a list you edited is left as it is. To have farming harvest a wild plant again, add it back to `Allow Farming Crop`; farming harvests it but cannot replant it.
- Automatic Mapping adds `Compass`, off by default: a strip at the top centre of the screen, or at the bottom with `Compass Position`, shows the cardinal directions and, in the direction they lie, the icons of the map pins within `Compass Pin Range` (100 m by default), up to the 24 nearest in the 180 degrees ahead of the camera, nearer icons larger and brighter. It shows the pins as the map does, so the map's icon filters, `Hide Pins Outside Explored Area` and `Enhance the Map`'s categories and minimap search apply. The navigation target has an icon of its own in `Navigation Guide Color`, shown at any distance over the other icons. `Compass Pin Names`, on by default, names the pin you face and the navigation target with their distance, such as `Boar [10m]`. At the top, the strip moves down out of the way of a boss's health bar and the event bar. The strip is hidden while the large map is open, the game is paused, the HUD is hidden, you are dead or the world has no map.
- Automatic Mapping now ends navigation by itself when you come within `Navigation Arrival Distance` (5 m by default, 0 to 50; 0 turns this off), measured horizontally, of the target after having been farther away than that during that navigation, and shows the same "Navigation cleared" message as clearing it by hand. If you start navigating to something already within that distance, it ends only after you have moved away beyond it and come back. This happens whether `Navigation Guide Swarm` is on or off. The swarm now looks like a cloud of small lights: when a navigation starts or switches to a new target they burst out from you in all directions, without dipping into the ground on steep slopes, then turn and stream toward the target in a straight line a little above the ground, up to 18 m ahead or to the target when it is nearer, and hover in a small column over the target, while new lights after that leave you and flow straight into the stream. The column stands on the ground over a buried target and on the water surface over an underwater one, keeps its own height over a target above the ground, sways slowly with its top swaying most, and stays around a target that keeps moving. On arrival, and when you clear navigation by hand, the lights scatter and fade out over about a second; with a map click they scatter when you close the map, unless you start another navigation first. The swarm no longer stops appearing near the target.
- Added Automatic Woodcutting, off by default for new and existing configurations. With `Cut Nearby Trees Automatically` on (the default), it hits the nearest allowed tree, small tree, fallen log or tree stump within `Woodcutting Range` (3 m by default) once per attempt, as a swing would, every `Woodcutting Interval` (1.5 s by default; 0 turns this off) and each time `Attempt Woodcutting Key` is pressed. `Allow Woodcutting Tree` chooses the kinds of tree, every kind by default; a small tree is of the kind its own name gives (a small beech is a beech), a log or stump counts as the tree it came from, and `User-defined Tree` adds other trees. A hit needs an axe strong enough for the target and nothing between your eyes and it. It uses the axe in your right hand, or, with `Need To Equip Axe For Woodcutting` off, the lowest-tier axe in your inventory that can cut the target when the one in hand cannot. It wears that axe, makes noise and raises the skill a swing raises on that target, but uses no stamina. A felled tree falls away from you. `Area Woodcutting`, on by default: when your own axe swing hits an allowed tree, log or stump, up to `Area Woodcutting Max Targets` (8 by default) other allowed ones within `Area Woodcutting Radius` (3 m by default) of the hit take the same hit, those of the same kind as the one you hit (also a tree, a log or a stump, of the same kind of tree) before the others and the nearest first within each, unless `Area Woodcutting Same Kind First` is turned off, which takes the nearest first whatever their kind; each wears the axe once more, while stamina and skill gain stay once per swing. Periodic runs and area woodcutting fell any tree in reach, including ones you planted, one swing can fell several trees at once, and falling trees and rolling logs can hurt you and your buildings.
- Under certain world settings, Automatic Mining's own hits were refused as too hard yet still wore down the pickaxe. They now count as a swing of that pickaxe.
- While the grid planting key (Left Alt by default) was held, Automatic Farming's crop preview could snap onto a spot in the rows with no room to grow, such as next to a growing crop at the default spacing or beside an obstacle, and the crop planted there, or its neighbour, withered. The preview now moves to the nearest of the four spots in the rows around the aim that has room; when none has, it shows as invalid and the game refuses the placement as needing more space.
- In multiplayer, another player coming into range no longer resets your own Automatic Door, Farming, Mining, Woodcutting, Repair, Processing and Supply timers and pending work; they now reset only when your own character spawns.
- Automatic Mapping now starts every world with its pin caches and scan state empty. Before, leaving a world and entering one again in the same game session carried them over from the previous world. Navigation still survives a respawn.
- Starting the game now writes Automatics' config file once, after every setting is set up, instead of once per setting. If the file cannot be written at that point, Automatics still starts and logs a warning.
- When a feature fails to start, turn on or turn off, or ends up faulted, its details are now always written to the BepInEx log, even with `Logging` off, as the faulted-feature message says.
- In multiplayer, Automatic Storage, Supply, Processing, Feeding and Farming no longer take over a container another player's game owns, which could duplicate or lose items when both games changed the same container. Processing, Feeding and Farming now ask that game for a container they need when your own game's containers cannot do the job, as opening it by hand asks, and use it on a later pass once your game owns it; `Store Items Key` and `Designate Container Role Key` ask on one press and use the container on a later press, and a container asked for counts toward `Container Reference Limit` on that press. The answer opens no window and shows no message, each request writes a line to that player's game log, and a container that has just changed hands is left alone for a moment. Supply uses only containers your own game owns. A container on a cart or a ship is never taken over or asked for, and a container nobody owns yet is left until the game assigns it.
- Automatic modules now read a container's stored contents again right before changing it. Before, a container changed right after it loaded could lose what it held, and a change another player had just made could be overwritten. The product stack limit of Automatic Processing and Automatic Supply's stock also count what containers hold at that moment.
- Without a player (dead, respawning or loading), a multiplayer client no longer moves items, and a multiplayer server uses only public containers that nobody has open. Single-player keeps storing as before.
- An animal whose feed box cannot be used now carries on as usual instead of standing still until its next search.
- Automatic ammunition charging took ammunition of the right type that the piece does not accept, and that ammunition was lost. It now takes only ammunition the piece accepts, and passes over an accepted kind that is at its reserve.
- After a world was loaded, the pins Automatic Mapping had saved, such as portal pins, were not recognized as its own: every portal in range got another saved pin each session, and saved pins ignored category hiding, `Hide Pins Outside Explored Area` and `Remove Pins Of Destroyed Object` until the module was turned off and on. They are now recognized when the world loads, and `Pin Looked-at Object Key` no longer adds a second pin beside an object's saved pin. To clear duplicate portal pins left by earlier versions, turn Automatic Mapping off and run `removemappins`, which keeps one pin of each group at exactly the same position (add `-n` first to see what it would remove).
- Automatic Mapping no longer removes the pins of objects in a nearby area that is still loading; they stay until the area has loaded. Whether an area has loaded is now checked only when a scan is due, once per zone in each static scan, instead of every frame and for every object in range.
- Automatic Processing, Automatic Farming's harvest into containers and Automatic Supply now check a container for room, or for the item they take, before using it. Before, a full chest got a failed add, a warning in the log and a save on every attempt. What fits is still stored, and a crop is still harvested into containers only when everything it yields fits.
- With `Remove Pins Of Destroyed Object` on, Automatic Mapping now also removes a static pin when another player's game destroys its object, and when a mineral deposit of a kind whose pin used to stay is used up. A ship's or cart's pin is now also removed when another player's game destroys it. Unloading an object still never removes a pin, pins you placed, saved by clicking or got shared are never removed, and a flora patch's pin is still removed only by the game that destroys its last plant.
- On a multiplayer client, Automatic Mapping now pins the spots `Allow Pinning Spot` allows, from the locations loaded around you; before, it pinned none there. `printobjects dungeon` and `printobjects spot` now also list nearby locations on a client instead of nothing.
- `printobjects` with a missing or unknown type now prints its usage and the types it accepts, instead of nothing.
- With `Need To Get Close To Eat The Feed` on, Automatic Feeding now checks a container for food and access before asking whether the animal can walk to it, and stops at the nearest one it can reach, instead of asking for every container in range; the same container is chosen.
- Automatic Door and the modules that use containers now follow the game's rule where wards overlap: a door or container covered by several active wards can be used when any one of them grants you access, as by hand, instead of being refused because of another ward you have no access to. A spot covered only by wards that refuse you is still refused.
- When a smelter-type station catches up after its area was unloaded, Automatic Processing no longer plays the fuel-added effect once for every fuel unit it adds; it adds the same fuel, one unit per item taken, and plays the effect once.
- Automatic Processing now stores from beehives and the two later-area collectors what taking their product out by hand gives, including the world's resource rate, in whole units, so nothing is lost or doubled when a container fills up partway. A collector of the kind that stops at its maximum while nothing could be stored now resumes storing once a container has room, and a beehive no longer stores more than it can hold after its area was unloaded for a while.
- Automatic Mapping now keeps a partly mined mineral deposit's one saved pin as the deposit's centre moves, also after a world reload, instead of adding a second pin beside it, and `Pin Looked-at Object Key` finds that pin too. Neighbouring deposits of the same kind never take each other's saved pin; when two are equally near it, neither does. Static mapping also does less work each pass for deposits it cannot pin yet, such as underground ones while no Wishbone is equipped.
- Automatic Mapping no longer adds a centre pin and logs that a dungeon has no entrance while the dungeon's location has not loaded yet; the dungeon gets one pin at its entrance once it loads.
- Automatic Mapping's static scan now matches each distinct object name against the object definitions once instead of on every scan for every object in range, which lowers the cost of its periodic pass. Renamed or re-parented objects and changes to the object definitions are still picked up on the next scan.
- Automatic Processing now stores cooked food left in a cooking station's slot on a later pass when storing it was blocked at the moment it finished, for example by a full container, also while the station has no fuel or fire. Before, the food stayed in the slot, for good at a station that does not burn food. Burnt food is still left in place.
- Automatic Processing's connection lines, shown while you look at a station, no longer leave copies of their materials in memory each time they appear, and looking at something that is not a station no longer repeats the station lookup every frame.
- Automatic Supply now rebuilds the crafting panel's recipe list once per supplied craft instead of two or three times, and while a recipe where you choose one ingredient is selected, the other recipe rows now follow changes in nearby containers' stock.
- Automatic Mapping now pins the plants of a flora you add to `User-defined Flora` while the game runs, once it is in `Allow Pinning Flora`, also plants that are already loaded; before, they got pins only after their area loaded again or the module was turned off and on. Grouping plants into flora patches also takes less work when an area with many plants loads; the patches are the same as before.
- Clicking away from the focused map search field no longer writes a selection error to the log; editing still ends and the search is applied.
- The looked-at object bar now shows only the name and distance for bosses and creatures whose health display is hidden, as documented, instead of a full white bar.
- Automatic Repair now raises the Crafting skill when it repairs an item, as repairing by hand does, both on its periodic pass and when you open a crafting station. Its periodic piece repair no longer flashes wards that refuse you, checks only pieces that are damaged, and stops a batch once the hammer's durability is used up; item repair makes no station checks while nothing you carry is worn.
- Automatic Mining and Automatic Woodcutting do less work: unloading a zone or turning the module off no longer slows down with many rocks or trees around, a tree's kind is looked up once per kind, a mining pass without a usable pickaxe stops at once, and rocks and trees out of reach are passed over before any ray is cast. What they cut and mine is unchanged.
- Automatic Pickup now does nothing while you are dead, waiting to respawn or teleporting. Its periodic pass no longer starts the game's delayed pickup for a drop that cannot be picked up yet, which kept polling, logged and skipped the carry check; it asks for the drop as the game's own auto-pickup does and takes it on a later pass. `Pickup All Nearby Key` no longer takes fish that are still in the water, and placed item pieces keep the game's own take.
- Automatic Pickup no longer reads the farming containers again for every crop in a pickup pass, which lowers the cost of passes near large farms; which crops it leaves to Automatic Farming is unchanged.
- Automatic Farming's proactive sowing now tests for cultivated ground before casting a ray and turns down crowded spots before the ward and roof checks, so a farming pass does less work; the same spots are sown as before. Grid planting's row snapping no longer builds a name for every nearby plant while the grid key is held.
- A `User-defined` object list, such as `User-defined Flora`, whose label, pattern or tag had a dollar sign next to another dollar sign, a backslash, a double quote or a non-ASCII character no longer changes or disappears when the config is loaded again. Before, such an entry loaded as different text, or the whole list was dropped with a "Failed to parse custom object config" warning. Lists saved by earlier versions load as before; an entry an earlier version already changed has to be entered again.
- Automatic Farming now replants a harvested crop at the turn it was planted with instead of the grown crop's random turn, so planted rows no longer drift over harvest cycles. Saplings already replanted with a stray turn no longer skew the rows that grid planting and proactive sowing continue from them.
- Automatic Farming now picks up a `User-defined Flora` added or changed while the game runs: its replanting sapling lookup follows the change without a reload or turning the module off and on.
- Module shortcuts (`Module Toggle Key`, `Store Items Key`, `Attempt Mining Key`, `Attempt Woodcutting Key`, `Attempt Farming Key`, `Designate Container Role Key`, `Pickup All Nearby Key`, `Static Object Mapping Key`, `Pin Looked-at Object Key`, `Navigate to Looked-at Object Key` and `Map Navigation Start Key`) now work while you hold other keys, such as the movement keys; before, holding any other key stopped them. A shortcut still does not work while you hold a modifier key (Shift, Ctrl, Alt) it does not include, so with the game's default controls a shortcut without modifiers does not work while you hold Left Shift (run) or Left Ctrl (crouch), and the default `Pickup All Nearby Key` (Left Shift + E) now also works while you walk or run.

#### v1.8.0 [2026-10-10]
- Automatic Mapping adds `Navigate to Looked-at Object Key`, unbound by default: press it to navigate to what the crosshair is on, using its automatic pin or a temporary unsaved marker that is removed when navigation ends, the target changes or the module is turned off. A plant in a flora patch targets the patch, as `Pin Looked-at Object Key` does. Press it on the current target to clear navigation. Creature targets selected with the key or their automatic map pins are followed while loaded; navigation keeps their last position while unloaded, continues if their dynamic pin disappears out of range, and ends when they die or are removed. A creature followed through a temporary marker switches to its automatic pin once one appears, and the marker is removed.
- Automatic Mapping adds `Looked-at Object Bar`, off by default: the object at the crosshair shows its name and distance above it, with Valheim's own health bar for creatures and health for player-built pieces. Bosses and creatures with hidden HUDs show a name and distance label without health; bare ground, unnamed meshes and whole locations get no bar. `Looked-at Object Bar Range` defaults to 50 m horizontally from you, and the object must also be within `Object Pin Range` from the camera. A navigated player-built piece now shows its health too.
- Automatic Mapping adds `Enhance the Map`, on by default: category controls on the large map hide automatic pins on both maps without deleting them. Choices are saved in `Hidden Map Categories`, empty by default so all categories are shown. Manual and shared pins are unaffected, and saved automatic pins with ambiguous or unknown categories are unaffected by category hiding. Turning enhancements off keeps category choices and clears search; `Hide Pins Outside Explored Area` still applies independently.
- With `Enhance the Map` on, the large map adds a search field that filters automatic, manual and shared pins by text in their displayed label, ignoring case; unnamed pins are hidden while searching. Vanilla special markers and the current navigation target stay visible. `Apply to minimap`, off by default, applies the search on the minimap too. Search text and this toggle are not saved; category controls still apply only to automatic pins.
- With `Enhance the Map` on (the default), hovering a pin on the large map shows its name and distance. Automatic pins add a creature's HP and level, pickable readiness or an estimated respawn time, merged flora's ready count and earliest estimated respawn, and a mineral deposit's remaining sections or HP. Details use loaded objects or stored data; clients mark unloaded object details `May be outdated` and unavailable details `Not loaded — details unavailable`. Your own saved flora and mineral pins also find matching objects by kind, name and position in locally available data when the hovered pin changes: automatic pins you clicked to save, and pins made with `Pin Looked-at Object Key` or typed by hand whose name is still the object's name in the current game language. Single pins use the nearest match (minerals use horizontal distance within the deposit's collider footprint, at least 4 m), and merged flora pins follow matching neighbours within the merge range, including chains. If no match is found for a flora or mineral label, the no-live-link reason line remains. Shared pins and your other manual pins show only name and distance.
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
- Automatic Mapping's pin navigation adds a guide swarm, on by default: small glowing lights gather around you and stream along the ground toward the navigation target, stop appearing within 5 m of it, and hide while the large map is open. Its colour is configurable; the default is a magenta that stands out against grass, rock, snow and water.
- Automatic Mapping's navigation now shows Valheim's own health and awareness bar for a creature target, with the distance appended in brackets (for example `Boar [10m]`), whenever the creature is loaded, and hides the navigation label for it; bosses keep their usual bar and the label. Navigation and looked-at object labels use the same bracketed distance format. The navigation label now looks like a creature's bar, with a full white bar instead of health, and other targets show it above the object instead of at its base, or about 2 m above the pin while the object is not loaded.
- Automatic Storage now stores an item only into containers that already hold the same item: it tops up their stacks first, then uses their free slots. Before, items went into any container in range, including empty and unrelated ones, so it was hard to tell where they ended up. Turn on the new `Store Into Any Container` to store as before.
- Automatic Storage now sorts each container it stored into, by item type and then name, merging partial stacks of the same item; containers it did not store into are left as they are. `Sort Containers After Storing` turns this off.
- Automatic Storage adds slot locks: with the inventory open, hold `Slot Lock Key` (Left Alt by default) and left-click a slot of your inventory to lock or unlock it, empty or not. Items in locked slots are never stored, and each locked slot shows a key icon while the module runs. Locks are saved with each character.
- Added an optional shortcut to save a map pin for the object or ground you look at, using object names and icon-pack icons and skipping nearby duplicates. Looking at a dungeon or special spot that Automatic Mapping recognizes (its structure, the ground within its area or a dungeon's interior) pins that place once with its recognized name and icon; player-built pieces, creatures and other objects keep their own pins. An object that already has an automatic pin, other than a creature or vehicle, gets that pin saved instead of a new one, as a click on the large map does, and a flora patch, with or without an automatic pin, gets one pin for the whole patch.
- Automatic Door, Automatic Pickup, Automatic Processing, Automatic Mapping, Automatic Mining, Automatic Feeding, Automatic Farming, Automatic Repair, Automatic Storage and Automatic Supply can all be turned on and off while the game is running.
- Automatic Repair and Automatic Mining now wear your tools by the world's durability modifier, as the game does when you repair or mine by hand; on a default world nothing changes.
- Automatic Farming's proactive sowing now continues the rows of a growing crop of the same kind nearby (its position and orientation), so sown crops line up with grids planted by hand instead of leaving gaps at the boundary.
- Automatic Farming's `Seed Source` is now called `Farming Mode` and has a third choice, `Player With Containers`: farming works around you, as with `Player Inventory`, but puts the harvest into and takes planting input from the role containers near each crop, never your inventory.
- Automatic Farming has a new `Container Search Range` for how far it looks for role containers; `Farming Range` now only sets where it harvests, replants and sows. An existing configuration starts with its farming range as the container range, so nothing changes.
- Automatic Farming now replants a harvested spot even when the planting input is at or below `Seed Reserve`; the reserve only holds input back from sowing new ground.
- While Automatic Farming runs, periodic pickup now leaves only the crops farming would harvest itself: those your game controls within `Farming Range` of you (or of a designated box). Before, with `Player Inventory` every crop in `Allow Farming Crop` was left, so wild berries and mushrooms beyond the farming range, or crops another player's game controls, were picked up by nobody. In multiplayer, a crop another player's game controls is now picked up by periodic pickup and is not replanted. When farming never runs by itself (`Farming Interval` of 0 and no `Attempt Farming Key`), periodic pickup collects the crops too.
- Graves are no longer used by any module: Automatic Farming cannot give one a role, and feeding, storage and processing never take from or put into one.
- Automatic Door no longer closes a door on a player who stands within reach with a piece between them and the door, and it now closes an open door inside a ward you cannot use once nobody who could open it is near.
- Automatic Mapping now recognizes its own saved pins (those saved by 1.7.0 or later) again when you come back to them, reload the world or turn the module off and on. `Remove Pins Of Destroyed Object` then removes a saved static pin when its object is destroyed, and a portal no longer gets a second pin unless its tag changed meanwhile. Pins you placed or saved yourself are never taken over.
- With `Pin Nearby Objects Automatically` off, Automatic Mapping no longer redraws every map pin on every frame, which lowered the frame rate on maps with many pins.
- Automatic Mining, Automatic Farming and Automatic Mapping's static object mapping now follow the same rule as Automatic Pickup: an interval of 0 turns the periodic runs off, and the shortcut runs once each time it is pressed without stopping them. Before, assigning the shortcut stopped the periodic runs, and the mining and farming intervals could not be set to 0. An existing config whose shortcut is assigned gets its interval set to 0 once, so it keeps running on the key only.
- The `Disable Module` option is now called `Module`, and its choices read `Enabled` and `Disabled` instead of "Do Not Disable This Module" and "Disable This Module"; the stored values are unchanged. Automatic Door, Automatic Feeding, Automatic Repair, Automatic Storage and Automatic Processing no longer have a second on/off switch beside it: a configuration where that switch was off gets its module turned off once, and the switch is removed. The Automatic Door shortcut now turns the module itself on and off, and works while the module is off. Automatic Mapping, Automatic Mining and Automatic Farming keep their second switch, renamed after what it does (`Pin Nearby Objects Automatically`, `Mine Nearby Minerals Automatically` and `Harvest And Replant Automatically`); their other features, such as navigation, area mining and grid planting, keep working while it is off.
- Every module is now off on a new install; turn on the ones you want with their `Module` option. An existing config keeps each module on or off as it was (a module whose `Module` entry is missing from an older config, or holds a value Automatics cannot read, is turned on; the new Automatic Supply is not), and the settings inside each module keep their defaults.
- Automatics recognizes more of the objects Valheim 1.0 added: crops, containers, doors, deposits, creatures, spawners, vehicles and dungeons. An update adds, once, the objects newly included in each list's default to lists that still hold a default Automatics recognizes; lists whose defaults did not change get no additions. A list you edited is left as it is, as are some lists kept unchanged since 1.4.0 or earlier, so add the new names to those yourself.
- The object list entry for flax is now spelled `Flax`; existing configurations are updated.
- Object list entries written with a leading space by the 1.6.0 update no longer matched their targets; existing configurations are now repaired.
- Automatic Pickup's `Pickup All Nearby Key` is now Left Shift + E by default on a new install; an existing configuration keeps the key it has, so a key left unassigned stays unassigned. Binding it no longer turns periodic pickup off; only an `Automatic Pickup Interval` of 0 does. An existing config whose key is bound gets its interval set to 0 once, so it keeps picking up by key only.
- Improved the Japanese translation: fixed mistranslations (the `Hide Pins Outside Explored Area` option name said the opposite), used the game's own Japanese terms for vanilla items, stations and concepts, and made wording and style consistent across options and messages. Automatic Repair and Automatic Supply have new Japanese names (自動修理 and 自動供給).
- Automatic Storage's `Allowed Item Types` list now shows item types by translated names instead of internal names; the saved values are unchanged.
- Some options have clearer names; their keys and values are unchanged. Automatic Processing's `Number Of Materials To Stop Supplying`, `Number Of Fuels To Stop Refuel` and `Number Of Items To Stop Charge` are now `Materials To Keep In Containers`, `Fuel To Keep In Containers` and `Charge Items To Keep In Containers`. Shortcut options end in `Key` (for example `Attempt Mining Key` and `Store Items Key`), and Automatic Door's toggle shortcut and its message are `Module Toggle Key` and `Module Toggle Message`. Automatic Mapping's wishbone option is now `Need To Equip Wishbone For Pinning Underground Minerals`.
- `Log Level To Allow Logging`, `Static Object Caching Interval` and `Mapping Performance Log` are now advanced settings: Configuration Manager lists them only when advanced settings are shown.
- Automatic Storage's `Storage Message` now defaults to `Center` on a new install, so you see how many items were stored; an existing configuration keeps its value.
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
