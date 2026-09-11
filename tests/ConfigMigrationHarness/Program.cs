using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;

namespace Automatics
{
    internal static class Program
    {
        private static int _failures;

        // The sections of the nine modules, each of which binds a `module` entry. Written out here
        // rather than taken from the migrator, so that a section missing from its list shows up as
        // a module that was not kept running.
        private static readonly string[] ModuleSections =
        {
            "automatic_door",
            "automatic_farming",
            "automatic_feeding",
            "automatic_mapping",
            "automatic_mining",
            "automatic_pickup",
            "automatic_processing",
            "automatic_repair",
            "automatic_storage"
        };

        private const string MarkerSection = "hidden";
        private const string MarkerKey = "defaults_revision";
        private const string MarkerValue = "1";

        private static int Main()
        {
            Run("regex migrations preserve captured processing suffixes", RegexMigrationsPreserveCapturedProcessingSuffixes);
            Run("old automatic_repair section keeps disabled repair", OldAutomaticRepairSectionKeepsDisabledRepair);
            Run("an enabled automatic_repair module survives migration", () => RepairModuleSurvivesMigration("Enabled"));
            Run("a disabled automatic_repair module survives migration", () => RepairModuleSurvivesMigration("Disabled"));
            Run("all six automatic_repair user values survive migration", RepairInnerValuesSurviveMigration);
            Run("old repair keys keep the module disabled", OldRepairKeysKeepModuleDisabled);
            Run("a current repair config is left byte for byte", CurrentRepairConfigIsLeftUntouched);
            Run("misplaced automatic_feeding repair key remains compatible", MisplacedAutomaticFeedingRepairKeyRemainsCompatible);
            Run("rename-category block still renames its config keys", RenameCategoryBlockStillRenamesConfigKeys);
            Run("map pinning rename chains through 1.4.0 mineral rename", MapPinningRenameChainsThroughMineralRename);
            Run("bepinex-style version header still migrates", BepInExStyleVersionHeaderStillMigrates);
            Run("config without a version header skips the version migrations", ConfigWithoutVersionHeaderSkipsTheVersionMigrations);
            Run("door migration appends PieceHexagonalDoor", DoorMigrationAppendsPieceHexagonalDoor);
            Run("an enabled automatic_door module survives migration", EnabledDoorModuleSurvivesMigration);
            Run("a disabled automatic_door module survives migration", DisabledDoorModuleSurvivesMigration);
            Run("user values inside automatic_door survive migration", DoorInnerValuesSurviveMigration);
            Run("old automatic_door renames and allowlist additions keep the module disabled", OldDoorKeysMigrateUnderADisabledModule);
            Run("a current disabled automatic_door config is left byte for byte", CurrentDisabledDoorConfigIsLeftUntouched);
            Run("F-A: an enabled automatic_pickup module survives migration", EnabledPickupModuleSurvivesMigration);
            Run("F-B: a disabled automatic_pickup module survives migration", DisabledPickupModuleSurvivesMigration);
            Run("F-C: user values inside automatic_pickup survive migration", PickupInnerValuesSurviveMigration);
            Run("P-A: an enabled automatic_processing module survives migration", EnabledProcessingModuleSurvivesMigration);
            Run("P-B: a disabled automatic_processing module survives migration", DisabledProcessingModuleSurvivesMigration);
            Run("P-C: user values inside automatic_processing survive migration", ProcessingInnerValuesSurviveMigration);
            Run("P-D: 1.2.0 automatic_processing renames keep a disabled module disabled", OldProcessingKeysMigrateUnderADisabledModule);
            Run("P-E: a config with the defaults marker and automatic_processing disabled is left untouched", CurrentDisabledProcessingConfigIsLeftUntouched);
            Run("M-A: an enabled automatic_mapping module survives migration", EnabledMappingModuleSurvivesMigration);
            Run("M-B: a disabled automatic_mapping module survives migration", DisabledMappingModuleSurvivesMigration);
            Run("M-C: user values inside automatic_mapping survive migration", MappingInnerValuesSurviveMigration);
            Run("an enabled automatic_mining module survives migration", EnabledMiningModuleSurvivesMigration);
            Run("a disabled automatic_mining module survives migration", DisabledMiningModuleSurvivesMigration);
            Run("user values inside automatic_mining survive migration", MiningInnerValuesSurviveMigration);
            Run("an enabled automatic_feeding module survives migration", EnabledFeedingModuleSurvivesMigration);
            Run("a disabled automatic_feeding module survives migration", DisabledFeedingModuleSurvivesMigration);
            Run("user values inside automatic_feeding survive migration", FeedingInnerValuesSurviveMigration);
            Run("1.2.0 automatic_feeding renames keep a disabled module disabled", OldFeedingKeysMigrateUnderADisabledModule);
            Run("a current disabled automatic_feeding config is left byte for byte", CurrentDisabledFeedingConfigIsLeftUntouched);
            Run("a 1.7.0 config without the defaults marker keeps automatic_farming Enabled and gains the marker", EnabledFarmingModuleSurvivesMigration);
            Run("a 1.7.0 config without the defaults marker keeps automatic_farming Disabled, once", DisabledFarmingModuleSurvivesMigration);
            Run("user values inside a disabled automatic_farming survive migration", FarmingInnerValuesSurviveMigration);
            Run("a 1.7.0 config without the defaults marker keeps automatic_storage Disabled and gains the marker", DisabledStorageModuleStaysDisabled);
            Run("user values inside a disabled automatic_storage survive migration", StorageInnerValuesSurviveMigration);
            Run("a config from before module entries existed gets every module Enabled", ConfigFromBeforeModuleEntriesGetsEveryModuleEnabled);
            Run("a 1.7.0 config keeps each module's value, Disabled included", VersionOneSevenConfigKeepsEachModuleValue);
            Run("a config without a header gets the module entries it lacks as Enabled", HeaderlessConfigGetsItsMissingModulesEnabled);
            Run("an empty config file gets every module Enabled", EmptyConfigGetsEveryModuleEnabled);
            Run("a config carrying the defaults marker is left byte for byte", ConfigWithTheMarkerIsLeftUntouched);
            Run("a new install gets only the defaults marker, so every module keeps its Disabled default", NewInstallGetsOnlyTheDefaultsMarker);
            Run("migrating a config a second time changes nothing", MigratingTwiceChangesNothing);
            Run("a module value BepInEx cannot read is written as Enabled; one it reads is kept", UnreadableModuleValuesBecomeEnabled);
            Run("a read-only config file is left byte for byte without stopping the start", ReadOnlyConfigIsLeftAlone);
            Run("a read-only config the version passes would change still stops the start", ReadOnlyConfigNeedingVersionPassesStillFails);
            Run("the migrator swaps the file in and leaves no temporary file", MigratorLeavesNoTemporaryFile);
            Run("a write that fails leaves no temporary file and does not stop the start", FailedWriteLeavesNoTemporaryFile);
            Run("a rewrite keeps the file's byte order mark, line breaks and final newline", RewriteKeepsEncodingAndLineBreaks);
            Run("a reload that keeps failing stops the start instead of letting defaults be saved", ReloadThatKeepsFailingStopsTheStart);
            Run("a reload that fails once is tried again", ReloadThatFailsOnceIsTriedAgain);

            return _failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine($"ok: {name}");
            }
            catch (Exception e)
            {
                _failures++;
                Console.Error.WriteLine($"FAIL: {name}");
                Console.Error.WriteLine(e.Message);
            }
        }

        private static string Migrate(params string[] lines)
        {
            var directory = Path.Combine(Path.GetTempPath(), "automatics-config-migration-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "Automatics.cfg");
            File.WriteAllLines(path, lines);

            try
            {
                ConfigMigration.Migration(new ConfigFile(path));
                return File.ReadAllText(path);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        // The same as Migrate, but for a new install: no file at all. Returns what the migrator
        // left at the config path, or null when it left nothing there.
        private static string MigrateWithoutAConfigFile()
        {
            var directory = Path.Combine(Path.GetTempPath(), "automatics-config-migration-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "Automatics.cfg");

            try
            {
                ConfigMigration.Migration(new ConfigFile(path));
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        // Migrates the same file twice, as two starts in a row would, and returns both results.
        private static (string First, string Second) MigrateTwice(params string[] lines)
        {
            var directory = Path.Combine(Path.GetTempPath(), "automatics-config-migration-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "Automatics.cfg");
            File.WriteAllLines(path, lines);

            try
            {
                ConfigMigration.Migration(new ConfigFile(path));
                var first = File.ReadAllText(path);
                ConfigMigration.Migration(new ConfigFile(path));
                return (first, File.ReadAllText(path));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        // Automatic Door binds its settings and object registry whether it starts on or off. The
        // rewriting fixtures carry an older Mapping entry so the migration has real work to do;
        // they prove the user's Door values survive that rewrite rather than merely being skipped.
        private static void EnabledDoorModuleSurvivesMigration()
        {
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar",
                "[automatic_door]",
                "module = Enabled");

            AssertContains(migrated, "Morgen");
            AssertModule(migrated, "automatic_door", "Enabled");
        }

        private static void DisabledDoorModuleSurvivesMigration()
        {
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar",
                "[automatic_door]",
                "module = Disabled");

            AssertContains(migrated, "Morgen");
            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"),
                "The door module value should be carried over once.");
            AssertModule(migrated, "automatic_door", "Disabled");
        }

        private static void DoorInnerValuesSurviveMigration()
        {
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar",
                "[automatic_door]",
                "module = Disabled",
                "enable_automatic_door = false",
                "interval_to_open = 1.25",
                "distance_for_automatic_closing = 7",
                "automatic_door_enable_disable_toggle = LeftAlt",
                "[general]",
                "custom_door = MyDoor, Custom door, MyDoorPrefab");

            AssertModule(migrated, "automatic_door", "Disabled");
            AssertContains(migrated, "enable_automatic_door = false");
            AssertContains(migrated, "interval_to_open = 1.25");
            AssertContains(migrated, "distance_for_automatic_closing = 7");
            AssertContains(migrated, "automatic_door_enable_disable_toggle = LeftAlt");
            AssertContains(migrated, "custom_door = MyDoor, Custom door, MyDoorPrefab");
        }

        private static void OldDoorKeysMigrateUnderADisabledModule()
        {
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.2.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_door]",
                "module = Disabled",
                "automatic_door_enabled = false",
                "player_search_radius_to_open = 6",
                "toggle_automatic_door_enabled_key = F",
                "allow_automatic_door = WoodDoor");

            AssertModule(migrated, "automatic_door", "Disabled");
            AssertContains(migrated, "enable_automatic_door = false");
            AssertContains(migrated, "distance_for_automatic_opening = 6");
            AssertContains(migrated, "automatic_door_enable_disable_toggle = F");
            AssertDoesNotContain(migrated, "automatic_door_enabled = false");
            AssertDoesNotContain(migrated, "player_search_radius_to_open");
            AssertDoesNotContain(migrated, "toggle_automatic_door_enabled_key");

            var allowlist = migrated
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Single(line => line.StartsWith("allow_automatic_door = ", StringComparison.Ordinal));
            AssertContains(allowlist, "WoodDoor");
            AssertContains(allowlist, "PieceHexagonalDoor");
        }

        private static void CurrentDisabledDoorConfigIsLeftUntouched()
        {
            var lines = new[]
            {
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_door]",
                "module = Disabled",
                "enable_automatic_door = false",
                "allow_automatic_door = WoodDoor, PieceHexagonalDoor",
                "interval_to_open = 1.25",
                "interval_to_close = 1.5",
                "distance_for_automatic_opening = 6",
                "distance_for_automatic_closing = 7",
                "automatic_door_enable_disable_toggle_message = TopLeft",
                "automatic_door_enable_disable_toggle = F",
                "[general]",
                "custom_door = MyDoor, Custom door, MyDoorPrefab",
                "[hidden]",
                "defaults_revision = 1"
            };

            var migrated = Migrate(lines);
            var expected = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            if (migrated != expected)
                throw new Exception($"A current Door config must not be rewritten. Expected:\n{expected}\nGot:\n{migrated}");
        }

        // Automatic Pickup binds its inner settings unconditionally from this slice on, so its
        // section is the one the migrator must not disturb: the module value is the user's
        // choice and BepInEx would happily write back whatever survives here. Each fixture
        // includes an automatic_mapping section from an older version so the 1.6.0 pass really
        // rewrites the file - a fixture the migrator skips entirely proves nothing about what
        // it preserves.
        private static void EnabledPickupModuleSurvivesMigration()
        {
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar",
                "[automatic_pickup]",
                "module = Enabled");

            AssertContains(migrated, "Morgen");
            AssertContains(migrated, "[automatic_pickup]");
            AssertModule(migrated, "automatic_pickup", "Enabled");
            AssertDoesNotContain(migrated, "module = Disabled");

            // A file that already carries the defaults marker is not rewritten at all.
            var current = Migrate(
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_pickup]",
                "module = Enabled",
                "[hidden]",
                "defaults_revision = 1");

            AssertContains(current, "[automatic_pickup]");
            AssertEqual(1, CountOccurrences(current, "module = Enabled"), "A current config should keep its pickup module value.");
        }

        private static void DisabledPickupModuleSurvivesMigration()
        {
            // The setting is the user's, and no part of this mod is allowed to write it back
            // (R1.3b). A migration that turned Disabled into Enabled would re-enable a module
            // the user switched off.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar",
                "[automatic_pickup]",
                "module = Disabled");

            AssertContains(migrated, "Morgen");
            AssertContains(migrated, "[automatic_pickup]");
            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"), "The pickup module value should be carried over once.");
            AssertModule(migrated, "automatic_pickup", "Disabled");
        }

        private static void PickupInnerValuesSurviveMigration()
        {
            // These three are the entries that start being bound even for a disabled module.
            // Their stored values have to survive, or the bind that follows re-creates them at
            // their defaults and silently discards what the user set.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar",
                "[automatic_pickup]",
                "module = Disabled",
                "automatic_pickup_range = 12",
                "automatic_pickup_interval = 1.5",
                "pickup_all_nearby_key = LeftAlt");

            AssertContains(migrated, "Morgen");
            AssertContains(migrated, "module = Disabled");
            AssertContains(migrated, "automatic_pickup_range = 12");
            AssertContains(migrated, "automatic_pickup_interval = 1.5");
            AssertContains(migrated, "pickup_all_nearby_key = LeftAlt");
        }

        // Automatic Processing binds its inner settings unconditionally from this change on, so
        // a user who had turned the module off gets the whole section written back on the next
        // start. The migrator runs before that bind: it has to keep the module value the user
        // chose and every inner value already in the file, because whatever survives here is
        // what the bind picks up and what applies once the module is turned on. As with the
        // pickup fixtures, the rewriting ones carry an older automatic_mapping section so the
        // migrator really rewrites the file.
        private static void EnabledProcessingModuleSurvivesMigration()
        {
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar",
                "[automatic_processing]",
                "module = Enabled");

            AssertContains(migrated, "Morgen");
            AssertContains(migrated, "[automatic_processing]");
            AssertModule(migrated, "automatic_processing", "Enabled");
            AssertDoesNotContain(migrated, "module = Disabled");
        }

        private static void DisabledProcessingModuleSurvivesMigration()
        {
            // The setting is the user's, and no part of this mod is allowed to write it back
            // (R1.3b). A migration that turned Disabled into Enabled would re-enable a module the
            // user switched off.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar",
                "[automatic_processing]",
                "module = Disabled");

            AssertContains(migrated, "Morgen");
            AssertContains(migrated, "[automatic_processing]");
            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"), "The processing module value should be carried over once.");
            AssertModule(migrated, "automatic_processing", "Disabled");
        }

        private static void ProcessingInnerValuesSurviveMigration()
        {
            // Entries a disabled module now binds, one of each shape: a per-station range, a
            // per-station process set, a per-station count and the colour string. Their stored
            // values have to survive, or the bind that follows re-creates them at their defaults
            // and silently discards what the user set.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar",
                "[automatic_processing]",
                "module = Disabled",
                "container_search_range_by_piece_smelter = 16",
                "allow_processing_by_piece_smelter = Refuel",
                "piece_smelter_fuel_count_of_suppress_processing = 5",
                "storage_connection_effect_color = #FF0000");

            AssertContains(migrated, "Morgen");
            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"), "The processing module value should be carried over once.");
            AssertContains(migrated, "container_search_range_by_piece_smelter = 16");
            AssertContains(migrated, "allow_processing_by_piece_smelter = Refuel");
            AssertContains(migrated, "piece_smelter_fuel_count_of_suppress_processing = 5");
            AssertContains(migrated, "storage_connection_effect_color = #FF0000");
        }

        private static void OldProcessingKeysMigrateUnderADisabledModule()
        {
            // A 1.2.0 file still carries the old key names and the old "All" value. The renames
            // and the value replacement apply to the whole section whatever the module value is,
            // which is what makes the renamed values the ones a later enable uses. What they must
            // not touch is the module value itself.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.2.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_processing]",
                "module = Disabled",
                "smelter_allow_automatic_processing = All",
                "smelter_fuel_count_that_suppress_automatic_process = 20");

            AssertContains(migrated, "allow_processing_by_smelter = Craft, Refuel, Store");
            AssertContains(migrated, "smelter_fuel_count_of_suppress_processing = 20");
            AssertDoesNotContain(migrated, "smelter_allow_automatic_processing");
            AssertDoesNotContain(migrated, "_that_suppress_automatic_process");
            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"), "The processing module value should be carried over once.");
            AssertModule(migrated, "automatic_processing", "Disabled");
        }

        private static void CurrentDisabledProcessingConfigIsLeftUntouched()
        {
            // A file that already carries the defaults marker needs no migration, and a disabled
            // module is not a reason to rewrite it: the file has to come back byte for byte.
            var lines = new[]
            {
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_processing]",
                "module = Disabled",
                "[hidden]",
                "defaults_revision = 1"
            };

            var migrated = Migrate(lines);
            var expected = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            if (migrated != expected)
                throw new Exception($"A current config must not be rewritten. Expected:\n{expected}\nGot:\n{migrated}");
        }

        // Automatic Mapping binds its inner settings unconditionally from this change on, so a
        // user who had turned it off gets the whole section written back on the next start. Its
        // section is also the one the 1.6.0 pass rewrites: the migrator appends the new monsters,
        // animals and flora to its allowlists in place. That rewrite has to carry over the module
        // value the user chose and every inner value, because whatever survives here is what the
        // bind picks up and what applies once the module is turned on.
        private static void EnabledMappingModuleSurvivesMigration()
        {
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "module = Enabled",
                "allow_pinning_monster = Boar");

            AssertContains(migrated, "Morgen");
            AssertContains(migrated, "[automatic_mapping]");
            AssertModule(migrated, "automatic_mapping", "Enabled");
            AssertDoesNotContain(migrated, "module = Disabled");
        }

        private static void DisabledMappingModuleSurvivesMigration()
        {
            // The setting is the user's, and no part of this mod is allowed to write it back
            // (R1.3b). A migration that turned Disabled into Enabled would re-enable a module the
            // user switched off.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "module = Disabled",
                "allow_pinning_monster = Boar");

            AssertContains(migrated, "Morgen");
            AssertContains(migrated, "[automatic_mapping]");
            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"), "The mapping module value should be carried over once.");
            AssertModule(migrated, "automatic_mapping", "Disabled");
        }

        private static void MappingInnerValuesSurviveMigration()
        {
            // Entries a disabled module now binds, one of each shape: a range, a flag, a key, and
            // an allowlist the 1.6.0 pass itself extends. Their stored values have to survive, or
            // the bind that follows re-creates them at their defaults and silently discards what
            // the user set.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "module = Disabled",
                "dynamic_object_mapping_range = 100",
                "save_static_object_pins = true",
                "navigation_start_key = LeftAlt",
                "allow_pinning_monster = Boar");

            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"), "The mapping module value should be carried over once.");
            AssertContains(migrated, "dynamic_object_mapping_range = 100");
            AssertContains(migrated, "save_static_object_pins = true");
            AssertContains(migrated, "navigation_start_key = LeftAlt");

            // The extended allowlist keeps what the user had and gains what 1.6.0 adds.
            var monsters = migrated
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Single(line => line.StartsWith("allow_pinning_monster = ", StringComparison.Ordinal));
            AssertContains(monsters, "Boar");
            AssertContains(monsters, "Morgen");
        }

        // Automatic Mining now binds its inner settings even while the module is disabled. Its
        // mineral allowlist is also rewritten by the 1.6.0 migration, so the rewrite must retain
        // the user's module choice and every inner value while appending FlametalDeposit.
        private static void EnabledMiningModuleSurvivesMigration()
        {
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mining]",
                "module = Enabled",
                "allow_mining_mineral = CopperDeposit");

            AssertModule(migrated, "automatic_mining", "Enabled");
            AssertContains(migrated, "FlametalDeposit");
        }

        private static void DisabledMiningModuleSurvivesMigration()
        {
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mining]",
                "module = Disabled",
                "allow_mining_mineral = CopperDeposit");

            AssertModule(migrated, "automatic_mining", "Disabled");
            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"),
                "The mining module value should be carried over once.");
            AssertContains(migrated, "FlametalDeposit");
        }

        private static void MiningInnerValuesSurviveMigration()
        {
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mining]",
                "module = Disabled",
                "enable_automatic_mining = false",
                "mining_interval = 2.5",
                "mining_range = 17",
                "allow_mining_mineral = CopperDeposit",
                "need_to_equip_pickaxe = false",
                "allow_mining_underground_minerals = false",
                "need_to_equip_wishbone_for_mining_underground_minerals = false",
                "mining_key = LeftAlt");

            AssertModule(migrated, "automatic_mining", "Disabled");
            AssertContains(migrated, "enable_automatic_mining = false");
            AssertContains(migrated, "mining_interval = 2.5");
            AssertContains(migrated, "mining_range = 17");
            AssertContains(migrated, "need_to_equip_pickaxe = false");
            AssertContains(migrated, "allow_mining_underground_minerals = false");
            AssertContains(migrated,
                "need_to_equip_wishbone_for_mining_underground_minerals = false");
            AssertContains(migrated, "mining_key = LeftAlt");

            var minerals = migrated
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Single(line => line.StartsWith("allow_mining_mineral = ",
                    StringComparison.Ordinal));
            AssertContains(minerals, "CopperDeposit");
            AssertContains(minerals, "FlametalDeposit");
        }

        // Automatic Feeding binds its inner settings even while the module is disabled, so a user
        // who had turned it off gets the whole section written back on the next start. The
        // rewriting fixtures carry an older Mapping entry so the migration has real work to do,
        // and that rewrite has to carry over the module value the user chose and every inner
        // value: whatever survives here is what the bind picks up once the module is turned on.
        private static void EnabledFeedingModuleSurvivesMigration()
        {
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar",
                "[automatic_feeding]",
                "module = Enabled");

            AssertContains(migrated, "Morgen");
            AssertContains(migrated, "[automatic_feeding]");
            AssertModule(migrated, "automatic_feeding", "Enabled");
            AssertDoesNotContain(migrated, "module = Disabled");
        }

        private static void DisabledFeedingModuleSurvivesMigration()
        {
            // The setting is the user's, and no part of this mod is allowed to write it back
            // (R1.3b). A migration that turned Disabled into Enabled would re-enable a module the
            // user switched off.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar",
                "[automatic_feeding]",
                "module = Disabled");

            AssertContains(migrated, "Morgen");
            AssertContains(migrated, "[automatic_feeding]");
            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"), "The feeding module value should be carried over once.");
            AssertModule(migrated, "automatic_feeding", "Disabled");
        }

        private static void FeedingInnerValuesSurviveMigration()
        {
            // One entry of each shape a disabled module now binds: a range, a switch and an animal
            // type set. Their stored values have to survive, or the bind that follows re-creates
            // them at their defaults and silently discards what the user set.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar",
                "[automatic_feeding]",
                "module = Disabled",
                "feed_search_range = 12",
                "need_get_close_to_eat_the_feed = true",
                "allow_to_feed_from_player = Wild, Tamed");

            AssertContains(migrated, "Morgen");
            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"), "The feeding module value should be carried over once.");
            AssertModule(migrated, "automatic_feeding", "Disabled");
            AssertContains(migrated, "feed_search_range = 12");
            AssertContains(migrated, "need_get_close_to_eat_the_feed = true");
            AssertContains(migrated, "allow_to_feed_from_player = Wild, Tamed");
        }

        private static void OldFeedingKeysMigrateUnderADisabledModule()
        {
            // A 1.2.0 file still carries the old key names. The renames apply to the whole section
            // whatever the module value is, which is what makes the renamed values the ones a later
            // enable uses. What they must not touch is the module value itself.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.2.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_feeding]",
                "module = Disabled",
                "automatic_feeding_enabled = false",
                "need_close_to_eat_the_feed = true");

            AssertContains(migrated, "enable_automatic_feeding = false");
            AssertContains(migrated, "need_get_close_to_eat_the_feed = true");
            AssertDoesNotContain(migrated, "automatic_feeding_enabled");
            AssertDoesNotContain(migrated, "need_close_to_eat_the_feed");
            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"), "The feeding module value should be carried over once.");
            AssertModule(migrated, "automatic_feeding", "Disabled");
        }

        private static void CurrentDisabledFeedingConfigIsLeftUntouched()
        {
            // A file that already carries the defaults marker needs no migration, and a disabled
            // module is not a reason to rewrite it: the file has to come back byte for byte.
            var lines = new[]
            {
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_feeding]",
                "module = Disabled",
                "[hidden]",
                "defaults_revision = 1"
            };

            var migrated = Migrate(lines);
            var expected = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            if (migrated != expected)
                throw new Exception($"A current config must not be rewritten. Expected:\n{expected}\nGot:\n{migrated}");
        }

        // Automatic Farming binds its inner settings unconditionally from this change on, so a
        // user who had turned it off gets the whole section written back on the next start. What
        // the migrator meets is a 1.7.0 file without the defaults marker: it adds the marker and
        // the module entries the file lacks, and has to carry the farming module value and every
        // inner value over as they are, because whatever survives here is what the bind picks up
        // and what applies once the module is turned on.
        private static void EnabledFarmingModuleSurvivesMigration()
        {
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_farming]",
                "module = Enabled");

            AssertEqual(1, CountOccurrences(migrated, "[automatic_farming]"), "The farming section should appear once.");
            AssertModule(migrated, "automatic_farming", "Enabled");
            AssertMarker(migrated);
        }

        private static void DisabledFarmingModuleSurvivesMigration()
        {
            // The setting is the user's, and no part of this mod is allowed to write it back
            // (R1.3b). A migration that turned Disabled into Enabled would re-enable a module the
            // user switched off, and one that added a second entry would leave BepInEx to pick one.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_farming]",
                "module = Disabled");

            AssertEqual(1, CountOccurrences(migrated, "[automatic_farming]"), "The farming section should appear once.");
            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"), "The farming module value should be carried over once.");
            AssertModule(migrated, "automatic_farming", "Disabled");
            AssertMarker(migrated);
        }

        private static void FarmingInnerValuesSurviveMigration()
        {
            // Entries a disabled module now binds, one of each shape: a range, an enum, an object
            // list, a float and a key. Their stored values have to survive, or the bind that
            // follows re-creates them at their defaults and silently discards what the user set.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_farming]",
                "module = Disabled",
                "farming_range = 32",
                "seed_source = Containers",
                "allow_farming_crop = Carrot, Turnip",
                "sowing_spacing_factor = 2",
                "designate_container_key = G");

            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"), "The farming module value should be carried over once.");
            AssertModule(migrated, "automatic_farming", "Disabled");
            AssertMarker(migrated);
            AssertContains(migrated, "farming_range = 32");
            AssertContains(migrated, "seed_source = Containers");
            AssertContains(migrated, "allow_farming_crop = Carrot, Turnip");
            AssertContains(migrated, "sowing_spacing_factor = 2");
            AssertContains(migrated, "designate_container_key = G");
        }

        // Automatic Storage binds its inner settings unconditionally from this change on, so a
        // user who had turned it off gets the whole section written back on the next start. The
        // section first appeared in 1.7.0, which wrote the module entry, so what the migrator
        // meets is a 1.7.0 file without the defaults marker: it adds the marker and the entries of
        // the modules the file lacks, and has to carry the module value and every inner value over
        // as they are, because whatever survives here is what the bind picks up and what applies
        // once the module is turned on.
        private static void DisabledStorageModuleStaysDisabled()
        {
            // The setting is the user's, and no part of this mod is allowed to write it back
            // (R1.3b). A migration that turned Disabled into Enabled would re-enable a module the
            // user switched off.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_storage]",
                "module = Disabled");

            AssertEqual(1, CountOccurrences(migrated, "[automatic_storage]"), "The storage section should appear once.");
            AssertEqual(1, CountOccurrences(migrated, "module = Disabled"), "The storage module value should be carried over once.");
            AssertModule(migrated, "automatic_storage", "Disabled");
            AssertMarker(migrated);
        }

        private static void StorageInnerValuesSurviveMigration()
        {
            // Entries a disabled module now binds, one of each shape: a range, a flag, a key, a
            // plain list and a list entry that is a regular expression. Their stored values have to
            // survive, or the bind that follows re-creates them at their defaults and silently
            // discards what the user set.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_storage]",
                "module = Disabled",
                "storage_search_range = 16",
                "store_hotbar_items = true",
                "store_items_key = F",
                "excluded_items = r/Trophy",
                "allowed_item_types = Material");

            AssertModule(migrated, "automatic_storage", "Disabled");
            AssertMarker(migrated);
            AssertContains(migrated, "storage_search_range = 16");
            AssertContains(migrated, "store_hotbar_items = true");
            AssertContains(migrated, "store_items_key = F");
            AssertContains(migrated, "excluded_items = r/Trophy");
            AssertContains(migrated, "allowed_item_types = Material");
        }

        // Every module's `module` entry defaults to Disabled, but only a new install is meant to
        // start that way. A file without the defaults marker was written before that default,
        // when a missing entry meant Enabled, so the migrator writes Enabled for each module entry
        // such a file lacks and then the marker. It never touches an entry the file has.

        private static void ConfigFromBeforeModuleEntriesGetsEveryModuleEnabled()
        {
            // Versions before 1.4.0 wrote no module entry at all and ran every module, so every
            // one comes out Enabled. The version passes still run over the same file.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.3.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_door]",
                "enable_automatic_door = false",
                "[automatic_mapping]",
                "allow_pinning_deposit = CopperDeposit",
                "[hidden]",
                "NexusID = 1700");

            foreach (var section in ModuleSections)
                AssertModule(migrated, section, "Enabled");
            AssertMarker(migrated);

            // The marker comes last, after the sections added for the modules the file lacked, even
            // though the file already has a [hidden] section before them.
            var lastEntry = migrated
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(x => x.Trim())
                .Last(x => x.Length > 0 && !x.StartsWith("#", StringComparison.Ordinal) &&
                           !x.StartsWith("[", StringComparison.Ordinal));
            if (lastEntry != "defaults_revision = 1")
                throw new Exception($"The marker should be the last entry, got '{lastEntry}' in:\n{migrated}");

            // A section the file has gains the entry under its header; it is not written twice.
            AssertEqual(1, CountOccurrences(migrated, "[automatic_door]"), "The door section should appear once.");
            AssertEqual(1, CountOccurrences(migrated, "[automatic_mapping]"), "The mapping section should appear once.");
            AssertContains(migrated, "enable_automatic_door = false");
            AssertContains(migrated, "allow_pinning_mineral = CopperDeposit");
        }

        private static void VersionOneSevenConfigKeepsEachModuleValue()
        {
            // 1.7.0 wrote every module entry, so each value in such a file is the user's: a
            // Disabled one stays Disabled, and only the marker is added. The comment lines are
            // the ones BepInEx writes above each entry and must not be read as entries.
            var values = new Dictionary<string, string>
            {
                { "automatic_door", "Disabled" },
                { "automatic_farming", "Enabled" },
                { "automatic_feeding", "Enabled" },
                { "automatic_mapping", "Disabled" },
                { "automatic_mining", "Enabled" },
                { "automatic_pickup", "Enabled" },
                { "automatic_processing", "Enabled" },
                { "automatic_repair", "Enabled" },
                { "automatic_storage", "Enabled" }
            };

            var lines = new List<string>
            {
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics"
            };
            foreach (var pair in values)
            {
                lines.Add("");
                lines.Add($"[{pair.Key}]");
                lines.Add("");
                lines.Add("# Default value: Enabled");
                lines.Add($"module = {pair.Value}");
            }

            var migrated = Migrate(lines.ToArray());

            foreach (var pair in values)
                AssertModule(migrated, pair.Key, pair.Value);
            AssertMarker(migrated);
        }

        private static void HeaderlessConfigGetsItsMissingModulesEnabled()
        {
            // With no version in the header the version passes are skipped, but the file still
            // has no marker: the module entries it lacks come out Enabled, and the one it has
            // stays as it is.
            var migrated = Migrate(
                "[automatic_pickup]",
                "module = Disabled",
                "[automatic_door]",
                "enable_automatic_door = false");

            AssertModule(migrated, "automatic_pickup", "Disabled");
            foreach (var section in ModuleSections.Where(x => x != "automatic_pickup"))
                AssertModule(migrated, section, "Enabled");
            AssertMarker(migrated);
            AssertContains(migrated, "enable_automatic_door = false");
        }

        private static void EmptyConfigGetsEveryModuleEnabled()
        {
            // An empty file is not a new install: the file exists, and it carries no marker.
            var migrated = Migrate();

            foreach (var section in ModuleSections)
                AssertModule(migrated, section, "Enabled");
            AssertMarker(migrated);
        }

        private static void ConfigWithTheMarkerIsLeftUntouched()
        {
            // The marker says the file was written with the Disabled defaults. A module entry
            // missing from it was removed by hand and takes its default, so the file has to come
            // back byte for byte.
            var lines = new[]
            {
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "",
                "[automatic_door]",
                "",
                "module = Disabled",
                "",
                "[hidden]",
                "",
                "defaults_revision = 1",
                ""
            };

            var migrated = Migrate(lines);
            var expected = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            if (migrated != expected)
                throw new Exception($"A config with the defaults marker must not be rewritten. Expected:\n{expected}\nGot:\n{migrated}");
        }

        private static void NewInstallGetsOnlyTheDefaultsMarker()
        {
            // No file means a new install. The migrator writes the marker and nothing else: every
            // module entry is left to its bind, whose default is Disabled, and the marker keeps the
            // next start from taking this file for one written before that default.
            var created = MigrateWithoutAConfigFile();
            if (created == null)
                throw new Exception("A new install must get a config file carrying the defaults marker.");

            AssertMarker(created);
            AssertDoesNotContain(created, "module");
            foreach (var section in ModuleSections)
                AssertEqual(0, ValuesOf(created, section, "module").Count,
                    $"A new install must leave [{section}] module to its Disabled default.");
        }

        private static void MigratingTwiceChangesNothing()
        {
            // The first start writes the missing entries and the marker; the second finds the
            // marker and leaves the file exactly as the first one left it.
            var (first, second) = MigrateTwice(
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_door]",
                "module = Disabled",
                "[automatic_pickup]",
                "automatic_pickup_range = 12");

            AssertModule(first, "automatic_door", "Disabled");
            AssertModule(first, "automatic_pickup", "Enabled");
            AssertModule(first, "automatic_storage", "Enabled");
            AssertMarker(first);
            if (second != first)
                throw new Exception($"A second migration must change nothing. First:\n{first}\nSecond:\n{second}");
        }

        private static void UnreadableModuleValuesBecomeEnabled()
        {
            // BepInEx reads a module value with Enum.Parse, case ignored. A misspelt, quoted or empty
            // value is one it cannot read, and for it the default applied, which was Enabled: those
            // come out Enabled. A value it reads stays exactly as written, including a number (1
            // reads as Disabled) and a name in another case.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_door]",
                "module = Enable",
                "[automatic_farming]",
                "module = \"Disabled\"",
                "[automatic_feeding]",
                "module = 1",
                "[automatic_mapping]",
                "module = disabled",
                "[automatic_mining]",
                "module = Disabled",
                "[automatic_pickup]",
                "module =");

            AssertModule(migrated, "automatic_door", "Enabled");
            AssertModule(migrated, "automatic_farming", "Enabled");
            AssertModule(migrated, "automatic_feeding", "1");
            AssertModule(migrated, "automatic_mapping", "disabled");
            AssertModule(migrated, "automatic_mining", "Disabled");
            AssertModule(migrated, "automatic_pickup", "Enabled");
            AssertModule(migrated, "automatic_storage", "Enabled");
            AssertMarker(migrated);
        }

        private static void ReadOnlyConfigIsLeftAlone()
        {
            // Without the marker this file would get the module entries it lacks. Read-only, it is
            // left exactly as it is and the start goes on; the next start tries again.
            var result = MigrateReadOnly(
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_door]",
                "module = Disabled");

            if (result.Error != null)
                throw new Exception($"A read-only config must not stop the start, but the migrator threw: {result.Error}");
            if (!result.Unchanged)
                throw new Exception("A read-only config must be left byte for byte.");
            AssertEqual(0, result.OtherFiles, "A read-only config must leave no temporary file.");
        }

        private static void ReadOnlyConfigNeedingVersionPassesStillFails()
        {
            // The version passes keep the behaviour they always had: when they have changes the file
            // cannot take, the start stops. Going on would let BepInEx write the current version into
            // the header, after which they would never run on this file.
            var result = MigrateReadOnly(
                "## Settings file was created by plugin Automatics v1.5.1",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_mapping]",
                "allow_pinning_monster = Boar");

            if (result.Error == null)
                throw new Exception("A read-only config the version passes would change must still stop the start.");
            if (!result.Unchanged)
                throw new Exception("A read-only config must be left byte for byte.");
        }

        private static void MigratorLeavesNoTemporaryFile()
        {
            var directory = NewTestDirectory();
            var path = Path.Combine(directory, "Automatics.cfg");
            File.WriteAllLines(path, new[] { "[automatic_door]", "module = Disabled" });

            try
            {
                ConfigMigration.Migration(new ConfigFile(path));

                AssertMarker(File.ReadAllText(path));
                var entries = Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).ToArray();
                if (entries.Length != 1 || entries[0] != "Automatics.cfg")
                    throw new Exception($"Only the config file should remain, got: {string.Join(", ", entries)}");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static void FailedWriteLeavesNoTemporaryFile()
        {
            // A directory where the config file should be makes the final move fail after the
            // temporary file was written. That failure must not stop the start, and the temporary
            // file must go.
            var directory = NewTestDirectory();
            var path = Path.Combine(directory, "Automatics.cfg");
            Directory.CreateDirectory(path);

            try
            {
                ConfigMigration.Migration(new ConfigFile(path));

                var entries = Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).ToArray();
                if (entries.Length != 1 || entries[0] != "Automatics.cfg")
                    throw new Exception($"A failed write must leave no temporary file, got: {string.Join(", ", entries)}");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static void RewriteKeepsEncodingAndLineBreaks()
        {
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            var utf8 = new System.Text.UTF8Encoding(false);

            // CRLF line breaks, a byte order mark and a final newline stay as they were.
            var crlf = MigrateBytes(bom.Concat(utf8.GetBytes("[automatic_door]\r\nmodule = Disabled\r\n")).ToArray());
            if (!crlf.Take(3).SequenceEqual(bom))
                throw new Exception("The byte order mark the file had was dropped.");
            var crlfText = utf8.GetString(crlf, 3, crlf.Length - 3);
            if (crlfText.Replace("\r\n", "").Contains('\n'))
                throw new Exception($"Every line break should stay CRLF:\n{crlfText}");
            if (!crlfText.EndsWith("\r\n", StringComparison.Ordinal))
                throw new Exception("The final newline the file had was dropped.");
            AssertModule(crlfText, "automatic_door", "Disabled");
            AssertMarker(crlfText);

            // LF line breaks, no byte order mark and no final newline stay as they were.
            var lf = MigrateBytes(utf8.GetBytes("[automatic_door]\nmodule = Disabled"));
            if (lf.Take(3).SequenceEqual(bom))
                throw new Exception("A byte order mark was added to a file that had none.");
            var lfText = utf8.GetString(lf);
            if (lfText.Contains('\r'))
                throw new Exception($"Every line break should stay LF:\n{lfText}");
            if (lfText.EndsWith("\n", StringComparison.Ordinal))
                throw new Exception("A final newline was added to a file that had none.");
            AssertModule(lfText, "automatic_door", "Disabled");
            AssertMarker(lfText);
        }

        private static void ReloadThatKeepsFailingStopsTheStart()
        {
            // BepInEx's Reload empties its copy of the file before it reads it again, so after a
            // failed Reload the first bind would save defaults over every setting. The migrator
            // tries again and then lets the failure stop the start; the file stays migrated.
            var directory = NewTestDirectory();
            var path = Path.Combine(directory, "Automatics.cfg");
            File.WriteAllLines(path, new[] { "[automatic_door]", "module = Disabled" });
            ConfigFile.ReloadFailures = 10;

            try
            {
                Exception error = null;
                try
                {
                    ConfigMigration.Migration(new ConfigFile(path));
                }
                catch (Exception e)
                {
                    error = e;
                }

                if (error == null)
                    throw new Exception("A reload that keeps failing must stop the start.");
                var written = File.ReadAllText(path);
                AssertModule(written, "automatic_door", "Disabled");
                AssertMarker(written);
            }
            finally
            {
                ConfigFile.ReloadFailures = 0;
                Directory.Delete(directory, recursive: true);
            }
        }

        private static void ReloadThatFailsOnceIsTriedAgain()
        {
            var directory = NewTestDirectory();
            var path = Path.Combine(directory, "Automatics.cfg");
            File.WriteAllLines(path, new[] { "[automatic_door]", "module = Disabled" });
            ConfigFile.ReloadFailures = 1;

            try
            {
                ConfigMigration.Migration(new ConfigFile(path));
                AssertEqual(0, ConfigFile.ReloadFailures, "The failing reload should have been made.");
            }
            finally
            {
                ConfigFile.ReloadFailures = 0;
                Directory.Delete(directory, recursive: true);
            }
        }

        private static string NewTestDirectory()
        {
            var directory = Path.Combine(Path.GetTempPath(), "automatics-config-migration-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        // Migrates a file given as raw bytes and returns the bytes it was left with.
        private static byte[] MigrateBytes(byte[] content)
        {
            var directory = NewTestDirectory();
            var path = Path.Combine(directory, "Automatics.cfg");
            File.WriteAllBytes(path, content);

            try
            {
                ConfigMigration.Migration(new ConfigFile(path));
                return File.ReadAllBytes(path);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        // Migrates a read-only file. Reports what the migrator threw, if anything, whether the file
        // came back byte for byte, and how many other entries were left beside it.
        private static (Exception Error, bool Unchanged, int OtherFiles) MigrateReadOnly(params string[] lines)
        {
            var directory = NewTestDirectory();
            var path = Path.Combine(directory, "Automatics.cfg");
            File.WriteAllLines(path, lines);
            var before = File.ReadAllBytes(path);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            try
            {
                Exception error = null;
                try
                {
                    ConfigMigration.Migration(new ConfigFile(path));
                }
                catch (Exception e)
                {
                    error = e;
                }

                var unchanged = File.ReadAllBytes(path).SequenceEqual(before);
                var others = Directory.GetFileSystemEntries(directory).Length - 1;
                return (error, unchanged, others);
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(directory, recursive: true);
            }
        }

        private static void RegexMigrationsPreserveCapturedProcessingSuffixes()
        {
            var migrated = Migrate(
                "# Automatics v1.2.0",
                "[automatic_processing]",
                "smelter_allow_automatic_processing = All",
                "blast_furnace_container_search_range = 42",
                "charcoal_kiln_material_count_that_suppress_automatic_process = 10",
                "smelter_fuel_count_that_suppress_automatic_process = 20");

            AssertContains(migrated, "allow_processing_by_smelter = Craft, Refuel, Store");
            AssertContains(migrated, "container_search_range_by_blast_furnace = 42");
            AssertContains(migrated, "charcoal_kiln_material_count_of_suppress_processing = 10");
            AssertContains(migrated, "smelter_fuel_count_of_suppress_processing = 20");
            AssertDoesNotContain(migrated, "r/^");
            AssertDoesNotContain(migrated, "$1");
        }

        private static void RepairModuleSurvivesMigration(string value)
        {
            var migrated = Migrate("## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics", "[automatic_repair]", "module = " + value);
            AssertModule(migrated, "automatic_repair", value);
            AssertEqual(1, CountOccurrences(migrated, "defaults_revision = 1"), "The defaults marker is added once.");
        }

        private static void RepairInnerValuesSurviveMigration()
        {
            var values = new[] { "enable_automatic_repair = false", "crafting_station_search_range = 11",
                "repair_items_when_accessing_the_crafting_station = true", "item_repair_message = Center",
                "piece_search_range = 23", "piece_repair_message = TopLeft" };
            var lines = new[] { "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics", "[automatic_repair]", "module = Disabled" };
            var migrated = Migrate(lines.Concat(values).ToArray());
            AssertModule(migrated, "automatic_repair", "Disabled");
            foreach (var value in values) AssertContains(migrated, value);
        }

        private static void OldRepairKeysKeepModuleDisabled()
        {
            var migrated = Migrate("## Settings file was created by plugin Automatics v1.2.0",
                "## Plugin GUID: net.eidee.valheim.automatics", "[automatic_repair]",
                "module = Disabled", "automatic_repair_enabled = false");
            AssertModule(migrated, "automatic_repair", "Disabled");
            AssertContains(migrated, "enable_automatic_repair = false");
            AssertDoesNotContain(migrated, "automatic_repair_enabled");
        }

        private static void CurrentRepairConfigIsLeftUntouched()
        {
            var lines = new[] { "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics", "[automatic_repair]", "module = Disabled",
                "crafting_station_search_range = 11", "[hidden]", "defaults_revision = 1" };
            var expected = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            if (Migrate(lines) != expected)
                throw new Exception("A current Repair config must not be rewritten.");
        }

        private static void OldAutomaticRepairSectionKeepsDisabledRepair()
        {
            var migrated = Migrate(
                "# Automatics v1.2.0",
                "[automatic_repair]",
                "automatic_repair_enabled = false");

            AssertContains(migrated, "[automatic_repair]");
            AssertContains(migrated, "enable_automatic_repair = false");
            AssertDoesNotContain(migrated, "automatic_repair_enabled = false");
        }

        private static void MisplacedAutomaticFeedingRepairKeyRemainsCompatible()
        {
            var migrated = Migrate(
                "# Automatics v1.2.0",
                "[automatic_feeding]",
                "automatic_repair_enabled = false");

            AssertContains(migrated, "[automatic_feeding]");
            AssertContains(migrated, "enable_automatic_repair = false");
            AssertDoesNotContain(migrated, "automatic_repair_enabled = false");
        }

        private static void RenameCategoryBlockStillRenamesConfigKeys()
        {
            // A RenameCategory followed by RenameConfig in the same block (e.g. [logging] -> [system])
            // must rename both the section header AND its config keys; the keys must not be orphaned.
            var migrated = Migrate(
                "# Automatics v1.2.0",
                "[logging]",
                "logging_enabled = false",
                "allowed_log_level = Info");

            AssertContains(migrated, "[system]");
            AssertContains(migrated, "enable_logging = false");
            AssertContains(migrated, "log_level_to_allow_logging = Info");
            AssertDoesNotContain(migrated, "[logging]");
            AssertDoesNotContain(migrated, "logging_enabled");
            AssertDoesNotContain(migrated, "allowed_log_level");
        }

        private static void MapPinningRenameChainsThroughMineralRename()
        {
            // [automatic_map_pinning] -> [automatic_mapping] renames allow_pinning_vein -> allow_pinning_deposit
            // in 1.3.0, which 1.4.0 then renames to allow_pinning_mineral. If the 1.3.0 key rename is dropped,
            // the 1.4.0 rename has nothing to match and the value is lost.
            var migrated = Migrate(
                "# Automatics v1.2.0",
                "[automatic_map_pinning]",
                "automatic_map_pinning_enabled = false",
                "allow_pinning_vein = CopperDeposit");

            AssertContains(migrated, "[automatic_mapping]");
            AssertContains(migrated, "enable_automatic_mapping = false");
            AssertContains(migrated, "allow_pinning_mineral = CopperDeposit");
            AssertDoesNotContain(migrated, "[automatic_map_pinning]");
            AssertDoesNotContain(migrated, "allow_pinning_vein");
        }

        private static void BepInExStyleVersionHeaderStillMigrates()
        {
            // The real config header is a BepInEx comment, not a bare "# Automatics vX".
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.2.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_repair]",
                "automatic_repair_enabled = false");

            AssertContains(migrated, "enable_automatic_repair = false");
            AssertDoesNotContain(migrated, "automatic_repair_enabled = false");
        }

        private static void ConfigWithoutVersionHeaderSkipsTheVersionMigrations()
        {
            // No parseable version means the version is unknown; migrating as 0.0.0 would
            // destructively re-run every pass on a possibly already-current file, so the
            // version passes must leave such a file's entries as they are. (It still gets the
            // module entries it lacks; see the headerless case above.)
            var migrated = Migrate(
                "[automatic_repair]",
                "automatic_repair_enabled = false");

            AssertContains(migrated, "automatic_repair_enabled = false");
            AssertDoesNotContain(migrated, "enable_automatic_repair");
        }

        private static void DoorMigrationAppendsPieceHexagonalDoor()
        {
            var migrated = Migrate(
                "# Automatics v1.5.1",
                "[automatic_door]",
                "allow_automatic_door = WoodDoor, AshwoodDoor");

            var valueLine = migrated
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Single(line => line.StartsWith("allow_automatic_door = ", StringComparison.Ordinal));

            AssertContains(valueLine, "WoodDoor");
            AssertContains(valueLine, "AshwoodDoor");
            AssertContains(valueLine, "PieceHexagonalDoor");
            AssertEqual(1, CountOccurrences(valueLine, "AshwoodDoor"), "AshwoodDoor should not be duplicated.");
            AssertEqual(1, CountOccurrences(valueLine, "PieceHexagonalDoor"), "PieceHexagonalDoor should be appended once.");
        }

        // Every value the text holds for section/key, in file order, read the way BepInEx's
        // ConfigFile.Reload reads a file: trimmed lines, '#' comments, [section] headers, and
        // key = value split at the first '='. Kept apart from the migrator's own reader so that
        // a mistake in one is not repeated in the other.
        private static List<string> ValuesOf(string text, string section, string key)
        {
            var values = new List<string>();
            var current = "";
            foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = raw.Trim();
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    current = line.Substring(1, line.Length - 2);
                    continue;
                }

                var split = line.Split(new[] { '=' }, 2);
                if (split.Length == 2 && current == section && split[0].Trim() == key)
                    values.Add(split[1].Trim());
            }

            return values;
        }

        // The section holds exactly one module entry, and it has the expected value.
        private static void AssertModule(string text, string section, string expected)
        {
            var values = ValuesOf(text, section, "module");
            if (values.Count != 1 || values[0] != expected)
                throw new Exception(
                    $"Expected [{section}] to hold module = {expected} once, got [{string.Join(", ", values)}] in:\n{text}");
        }

        private static void AssertMarker(string text)
        {
            var values = ValuesOf(text, MarkerSection, MarkerKey);
            if (values.Count != 1 || values[0] != MarkerValue)
                throw new Exception(
                    $"Expected [{MarkerSection}] to hold {MarkerKey} = {MarkerValue} once, got [{string.Join(", ", values)}] in:\n{text}");
        }

        private static int CountOccurrences(string value, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static void AssertContains(string value, string expected)
        {
            if (!value.Contains(expected))
                throw new Exception($"Expected to find '{expected}' in:\n{value}");
        }

        private static void AssertDoesNotContain(string value, string unexpected)
        {
            if (value.Contains(unexpected))
                throw new Exception($"Did not expect to find '{unexpected}' in:\n{value}");
        }

        private static void AssertEqual(int expected, int actual, string message)
        {
            if (expected != actual)
                throw new Exception($"{message} Expected {expected}, got {actual}.");
        }
    }
}
