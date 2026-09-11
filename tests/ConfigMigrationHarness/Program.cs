using System;
using System.IO;
using System.Linq;
using BepInEx.Configuration;

namespace Automatics
{
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            Run("regex migrations preserve captured processing suffixes", RegexMigrationsPreserveCapturedProcessingSuffixes);
            Run("old automatic_repair section keeps disabled repair", OldAutomaticRepairSectionKeepsDisabledRepair);
            Run("misplaced automatic_feeding repair key remains compatible", MisplacedAutomaticFeedingRepairKeyRemainsCompatible);
            Run("rename-category block still renames its config keys", RenameCategoryBlockStillRenamesConfigKeys);
            Run("map pinning rename chains through 1.4.0 mineral rename", MapPinningRenameChainsThroughMineralRename);
            Run("bepinex-style version header still migrates", BepInExStyleVersionHeaderStillMigrates);
            Run("config without a version header is left unchanged", ConfigWithoutVersionHeaderIsLeftUnchanged);
            Run("door migration appends PieceHexagonalDoor", DoorMigrationAppendsPieceHexagonalDoor);
            Run("F-A: an enabled automatic_pickup module survives migration", EnabledPickupModuleSurvivesMigration);
            Run("F-B: a disabled automatic_pickup module survives migration", DisabledPickupModuleSurvivesMigration);
            Run("F-C: user values inside automatic_pickup survive migration", PickupInnerValuesSurviveMigration);
            Run("F-D: a missing config file migrates without doing anything", MissingConfigFileMigratesToNothing);
            Run("P-A: an enabled automatic_processing module survives migration", EnabledProcessingModuleSurvivesMigration);
            Run("P-B: a disabled automatic_processing module survives migration", DisabledProcessingModuleSurvivesMigration);
            Run("P-C: user values inside automatic_processing survive migration", ProcessingInnerValuesSurviveMigration);
            Run("P-D: 1.2.0 automatic_processing renames keep a disabled module disabled", OldProcessingKeysMigrateUnderADisabledModule);
            Run("P-E: a current config with automatic_processing disabled is left untouched", CurrentDisabledProcessingConfigIsLeftUntouched);

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

        // The same as Migrate, but for the case R5.6 calls a first install: no file at all.
        // Returns whether the migrator left the directory as empty as it found it.
        private static bool MigrateWithoutAConfigFile()
        {
            var directory = Path.Combine(Path.GetTempPath(), "automatics-config-migration-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "Automatics.cfg");

            try
            {
                ConfigMigration.Migration(new ConfigFile(path));
                return !File.Exists(path);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
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
            AssertEqual(1, CountOccurrences(migrated, "module = Enabled"), "The pickup module value should be carried over once.");
            AssertDoesNotContain(migrated, "module = Disabled");

            // A file that is already current is not rewritten at all.
            var current = Migrate(
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_pickup]",
                "module = Enabled");

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
            AssertDoesNotContain(migrated, "module = Enabled");
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

        private static void MissingConfigFileMigratesToNothing()
        {
            // A first install has no config file. The migrator has to return quietly rather
            // than throw or invent one; BepInEx creates the file when the entries are bound.
            if (!MigrateWithoutAConfigFile())
                throw new Exception("The migrator must not create a config file that was not there.");
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
            AssertEqual(1, CountOccurrences(migrated, "module = Enabled"), "The processing module value should be carried over once.");
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
            AssertDoesNotContain(migrated, "module = Enabled");
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
            AssertDoesNotContain(migrated, "module = Enabled");
        }

        private static void CurrentDisabledProcessingConfigIsLeftUntouched()
        {
            // A file already written by 1.7.0 needs no migration, and a disabled module is not a
            // reason to rewrite it: the file has to come back byte for byte.
            var lines = new[]
            {
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_processing]",
                "module = Disabled"
            };

            var migrated = Migrate(lines);
            var expected = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            if (migrated != expected)
                throw new Exception($"A current config must not be rewritten. Expected:\n{expected}\nGot:\n{migrated}");
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

        private static void ConfigWithoutVersionHeaderIsLeftUnchanged()
        {
            // No parseable version means the version is unknown; migrating as 0.0.0 would
            // destructively re-run every pass on a possibly already-current file, so the
            // migrator must leave such a file untouched.
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
