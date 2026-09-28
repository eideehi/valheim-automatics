using System;
using System.Linq;

namespace Automatics
{
    // Settings pass 3: the modules with a single feature lose the feature switch they had beside
    // the module switch. A switch BepInEx read as false turns its module off.
    internal static partial class Program
    {
        private static readonly string[] SingleFeatureSections =
            { "automatic_door", "automatic_feeding", "automatic_repair", "automatic_storage", "automatic_processing" };

        private static void RunFeatureSwitchMigrationTests()
        {
            Run("a feature switch that is false turns its module off and is removed", FalseSwitchTurnsTheModuleOff);
            Run("a feature switch that is true is removed and the module is kept", TrueSwitchIsRemoved);
            Run("an unreadable feature switch is removed and the module is kept", UnreadableSwitchIsRemoved);
            Run("a false switch without a module line adds module = Disabled", FalseSwitchWithoutModuleLineAddsDisabled);
            Run("the switches of multi-feature modules stay", MultiFeatureSwitchesStay);
            Run("a file at settings revision 2 runs only pass 3", RevisionTwoRunsOnlyPassThree);
        }

        private static string[] SwitchLines(string value, string revision = "0", bool withModule = true)
        {
            var lines = new[] { "## Settings file was created by plugin Automatics v1.7.0", "" }.ToList();
            foreach (var section in SingleFeatureSections)
            {
                lines.Add("[" + section + "]");
                if (withModule) lines.Add("module = Enabled");
                lines.Add("enable_" + section + " = " + value);
                lines.Add("");
            }

            lines.AddRange(new[] { "[hidden]", "defaults_revision = 2", "pickup_key_decoupled = 1",
                ConfigMigration.SettingsRevisionKey + " = " + revision });
            return lines.ToArray();
        }

        private static void FalseSwitchTurnsTheModuleOff()
        {
            // Any case BepInEx reads as false, with or without surrounding spaces.
            foreach (var value in new[] { "false", "FALSE", " False " })
            {
                var migrated = MigrateRaw(SwitchLines(value));
                foreach (var section in SingleFeatureSections)
                    AssertFeatureSwitchFolded(migrated, section);
                AssertSettingsRevision(migrated);
            }
        }

        private static void TrueSwitchIsRemoved()
        {
            // Any case BepInEx reads as true.
            var migrated = MigrateRaw(SwitchLines("True"));
            foreach (var section in SingleFeatureSections)
            {
                AssertModule(migrated, section, "Enabled");
                AssertDoesNotContain(migrated, "enable_" + section);
            }
        }

        private static void UnreadableSwitchIsRemoved()
        {
            var migrated = MigrateRaw(SwitchLines("maybe"));
            foreach (var section in SingleFeatureSections)
            {
                AssertModule(migrated, section, "Enabled");
                AssertDoesNotContain(migrated, "enable_" + section);
            }
        }

        private static void FalseSwitchWithoutModuleLineAddsDisabled()
        {
            // The file has the defaults marker, so no module line is added as Enabled first.
            var migrated = MigrateRaw(SwitchLines("false", withModule: false));
            foreach (var section in SingleFeatureSections)
                AssertFeatureSwitchFolded(migrated, section);
        }

        private static void MultiFeatureSwitchesStay()
        {
            var lines = SwitchLines("false").ToList();
            lines.InsertRange(2, new[]
            {
                "[automatic_mining]", "module = Enabled", "enable_automatic_mining = false", "",
                "[automatic_farming]", "module = Enabled", "enable_automatic_farming = false", "",
                "[automatic_mapping]", "module = Enabled", "enable_automatic_mapping = false", ""
            });
            var migrated = MigrateRaw(lines.ToArray());
            foreach (var section in new[] { "automatic_mining", "automatic_farming", "automatic_mapping" })
            {
                AssertModule(migrated, section, "Enabled");
                AssertContains(migrated, "enable_" + section + " = false");
            }
        }

        private static void RevisionTwoRunsOnlyPassThree()
        {
            // Pass 2 is done, so a fermenter line left in this file stays.
            var lines = SwitchLines("false", "2").ToList();
            lines.Insert(2, "piece_fermenter_supply_only_when_materials_run_out = true");
            lines.Insert(2, "[automatic_processing]");
            var migrated = MigrateRaw(lines.ToArray());
            AssertContains(migrated, "piece_fermenter_supply_only_when_materials_run_out = true");
            foreach (var section in SingleFeatureSections)
                AssertFeatureSwitchFolded(migrated, section);
            AssertSettingsRevision(migrated);
        }
    }
}
