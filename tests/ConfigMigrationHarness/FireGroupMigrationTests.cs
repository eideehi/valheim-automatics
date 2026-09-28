using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;

namespace Automatics
{
    // Settings pass 1: the eleven Automatic Processing fire pieces had five settings each and now
    // share one group. The pass runs once, recorded by [hidden] settings_revision.
    internal static partial class Program
    {
        private static readonly string[] FirePieceKeys =
        {
            "piece_bonfire", "piece_firepit", "piece_brazierceiling01", "piece_hearth", "piece_jackoturnip",
            "piece_sconce", "piece_groundtorchblue", "piece_brazierfloor01", "piece_groundtorchgreen",
            "piece_groundtorch", "piece_groundtorchwood"
        };

        private static void RunFireGroupMigrationTests()
        {
            Run("fire pieces merge into one group and their old lines are removed", FirePiecesMergeIntoOneGroup);
            Run("a fire piece without Refuel is left out of the fires list", FirePieceWithoutRefuelIsLeftOut);
            Run("fires lists of every piece and of none are written as All and None", FiresListAllAndNone);
            Run("the most common value wins and pieces with another value are logged", MostCommonValueWins);
            Run("a tie goes to the default when it is tied", TieGoesToTheDefault);
            Run("a tie without the default goes to the smallest value", TieWithoutDefaultGoesToTheSmallest);
            Run("missing and unreadable fire values count as their defaults", MissingAndUnreadableFireValuesAreDefaults);
            Run("a config without fire lines gets only the settings revision", ConfigWithoutFireLinesGetsOnlyTheRevision);
            Run("the settings revision goes after the other markers", SettingsRevisionGoesAfterTheOtherMarkers);
            Run("a new install gets the settings revision", NewInstallGetsTheSettingsRevision);
            Run("a settings revision BepInEx wrote as 0 lets the passes run", SettingsRevisionAtZeroRuns);
            Run("a current settings revision runs nothing", CurrentSettingsRevisionRunsNothing);
            Run("an unrecognized settings revision leaves the file as it is", UnrecognizedSettingsRevisionIsLeftAlone);
            Run("merging the fire pieces a second time changes nothing", FireGroupMigrationIsIdempotent);
            Run("old allow values count the way BepInEx read them", OldAllowValuesCountAsBepInExReadThem);
            Run("every copy of an old fire line is removed, repeated sections included", EveryCopyOfAnOldFireLineIsRemoved);
            Run("a rerun after a failed write rewrites the group lines BepInEx added", RerunRewritesTheGroupLines);
            Run("the fermenter's supply-only line is removed", FermenterSupplyLineIsRemoved);
            Run("a file at settings revision 1 runs only the later passes", RevisionOneRunsOnlyLaterPasses);
        }

        private const string FermenterSupplyLine = "piece_fermenter_supply_only_when_materials_run_out = true";

        private static void FermenterSupplyLineIsRemoved()
        {
            var migrated = MigrateRaw(FireLines(new[] { FermenterSupplyLine, "piece_fermenter_store_only_if_product_exists = true" }));
            AssertDoesNotContain(migrated, "piece_fermenter_supply_only_when_materials_run_out");
            AssertContains(migrated, "piece_fermenter_store_only_if_product_exists = true");
            AssertSettingsRevision(migrated);
        }

        private static void RevisionOneRunsOnlyLaterPasses()
        {
            // Pass 1 is done: the old fire lines left in this file stay, while pass 2 still runs.
            var lines = FireLines(AllFireSettings().Concat(new[] { FermenterSupplyLine }), "1");
            var migrated = MigrateRaw(lines);
            AssertDoesNotContain(migrated, "piece_fermenter_supply_only_when_materials_run_out");
            AssertContains(migrated, "container_search_range_by_piece_bonfire = 8");
            AssertDoesNotContain(migrated, "refuel_fires");
            AssertSettingsRevision(migrated);
        }

        private static void OldAllowValuesCountAsBepInExReadThem()
        {
            // Accepted were None and Refuel; any other parsed value was clamped to None, and a value
            // that did not parse took the default, Refuel.
            var values = new Dictionary<string, string>
            {
                ["piece_bonfire"] = "All",
                ["piece_firepit"] = "Refuel, Store",
                ["piece_brazierceiling01"] = "3",
                ["piece_hearth"] = "refuel",
                ["piece_jackoturnip"] = "None,",
                ["piece_sconce"] = "0"
            };
            var migrated = MigrateRaw(FireLines(AllFireSettings((piece, key) =>
                key == "allow" && values.TryGetValue(piece, out var value) ? value : null)));
            AssertSingle(migrated, "automatic_processing", "refuel_fires",
                "Hearth, JackOTurnip, StandingBlueBurningIronTorch, StandingBrazier, StandingGreenBurningIronTorch, " +
                "StandingIronTorch, StandingWoodTorch");
        }

        private static void EveryCopyOfAnOldFireLineIsRemoved()
        {
            var lines = FireLines(AllFireSettings()).ToList();
            lines.AddRange(new[] { "", "[automatic_processing]", "container_search_range_by_piece_bonfire = 30" });
            var migrated = MigrateRaw(lines.ToArray());
            AssertNoOldFireLines(migrated);
        }

        private static void RerunRewritesTheGroupLines()
        {
            // BepInEx saved the group at its defaults and the marker at 0 after the write failed;
            // the old lines are still there as orphans.
            var lines = FireLines(new[] { "refuel_fires = All", "fires_container_search_range = 8" }
                .Concat(AllFireSettings((piece, key) => key == "range" ? "20" : null)), "0");
            var migrated = MigrateRaw(lines);
            AssertSingle(migrated, "automatic_processing", "fires_container_search_range", "20");
            AssertSingle(migrated, "automatic_processing", "refuel_fires", "All");
            AssertNoOldFireLines(migrated);
            AssertSettingsRevision(migrated);
        }

        // A revision-two config holding the given Automatic Processing lines and, when given, a
        // settings revision marker.
        private static string[] FireLines(IEnumerable<string> processing, string revision = null)
        {
            var lines = new List<string>
            {
                "## Settings file was created by plugin Automatics v1.7.0",
                "",
                "[automatic_processing]",
                "",
                "module = Enabled",
                "enable_automatic_processing = true"
            };
            lines.AddRange(processing);
            lines.AddRange(new[] { "allow_processing_by_piece_smelter = All", "", "[hidden]", "", "defaults_revision = 2", "pickup_key_decoupled = 1" });
            if (revision != null) lines.Add(ConfigMigration.SettingsRevisionKey + " = " + revision);
            return lines.ToArray();
        }

        // Every fire piece with all five settings, at the default unless overridden.
        private static List<string> AllFireSettings(Func<string, string, string> value = null)
        {
            var lines = new List<string>();
            foreach (var piece in FirePieceKeys)
            {
                string V(string key, string fallback) => value?.Invoke(piece, key) ?? fallback;
                lines.Add($"allow_processing_by_{piece} = {V("allow", "Refuel")}");
                lines.Add($"container_search_range_by_{piece} = {V("range", "8")}");
                lines.Add($"container_reference_limit_by_{piece} = {V("limit", "0")}");
                lines.Add($"{piece}_fuel_count_of_suppress_processing = {V("fuel", "1")}");
                lines.Add($"{piece}_refuel_only_when_out_of_fuel = {V("out", "false")}");
            }

            return lines;
        }

        private static void AssertSingle(string text, string section, string key, string expected)
        {
            var values = ValuesOf(text, section, key);
            if (values.Count != 1 || values[0] != expected)
                throw new Exception($"Expected [{section}] {key} = {expected} once, got [{string.Join(", ", values)}] in:\n{text}");
        }

        private static void AssertNoOldFireLines(string text)
        {
            foreach (var piece in FirePieceKeys)
                if (text.Contains("_by_" + piece + " ") || text.Contains(piece + "_fuel_count") ||
                    text.Contains(piece + "_refuel_only"))
                    throw new Exception($"An old line of {piece} is still there:\n{text}");
        }

        private static void AssertSettingsRevision(string text)
        {
            AssertSingle(text, MarkerSection, ConfigMigration.SettingsRevisionKey,
                ConfigMigration.SettingsRevision.ToString());
        }

        private static void FirePiecesMergeIntoOneGroup()
        {
            var migrated = MigrateRaw(FireLines(AllFireSettings()));

            AssertSingle(migrated, "automatic_processing", "refuel_fires", "All");
            AssertSingle(migrated, "automatic_processing", "fires_container_search_range", "8");
            AssertSingle(migrated, "automatic_processing", "fires_container_reference_limit", "0");
            AssertSingle(migrated, "automatic_processing", "fires_fuel_count_of_suppress_processing", "1");
            AssertSingle(migrated, "automatic_processing", "fires_refuel_only_when_out_of_fuel", "false");
            AssertNoOldFireLines(migrated);
            AssertContains(migrated, "allow_processing_by_piece_smelter = All");
            AssertContains(migrated, "enable_automatic_processing = true");
            AssertSettingsRevision(migrated);
        }

        private static void FirePieceWithoutRefuelIsLeftOut()
        {
            var migrated = MigrateRaw(FireLines(AllFireSettings((piece, key) =>
                key == "allow" && (piece == "piece_bonfire" || piece == "piece_sconce") ? "None" : null)));

            AssertSingle(migrated, "automatic_processing", "refuel_fires",
                "Campfire, HangingBrazier, Hearth, JackOTurnip, StandingBlueBurningIronTorch, StandingBrazier, " +
                "StandingGreenBurningIronTorch, StandingIronTorch, StandingWoodTorch");
        }

        private static void FiresListAllAndNone()
        {
            var none = MigrateRaw(FireLines(AllFireSettings((piece, key) => key == "allow" ? "None" : null)));
            AssertSingle(none, "automatic_processing", "refuel_fires", "None");

            // A value BepInEx reads as a number counts by its Refuel bit (2).
            var numbers = MigrateRaw(FireLines(AllFireSettings((piece, key) => key == "allow" ? "2" : null)));
            AssertSingle(numbers, "automatic_processing", "refuel_fires", "All");
        }

        private static void MostCommonValueWins()
        {
            Automatics.Logger.Messages.Clear();
            var migrated = MigrateRaw(FireLines(AllFireSettings((piece, key) =>
                key == "range" ? (piece == "piece_bonfire" ? "20" : "12") :
                key == "out" && piece == "piece_hearth" ? "true" : null)));

            AssertSingle(migrated, "automatic_processing", "fires_container_search_range", "12");
            AssertSingle(migrated, "automatic_processing", "fires_refuel_only_when_out_of_fuel", "false");
            if (!Automatics.Logger.Messages.Any(x => x.Contains("fires_container_search_range = 12") && x.Contains("piece_bonfire had 20")))
                throw new Exception("The piece whose range changed must be logged:\n" + string.Join("\n", Automatics.Logger.Messages));
            if (!Automatics.Logger.Messages.Any(x => x.Contains("fires_refuel_only_when_out_of_fuel = false") && x.Contains("piece_hearth had true")))
                throw new Exception("The piece whose flag changed must be logged:\n" + string.Join("\n", Automatics.Logger.Messages));
        }

        private static void TieGoesToTheDefault()
        {
            // Five at the default 8, five at 4, one at 12: the smallest tied value is not the default.
            var index = 0;
            var migrated = MigrateRaw(FireLines(AllFireSettings((piece, key) =>
                key != "range" ? null : index++ < 5 ? "8" : index <= 10 ? "4" : "12")));
            AssertSingle(migrated, "automatic_processing", "fires_container_search_range", "8");
        }

        private static void TieWithoutDefaultGoesToTheSmallest()
        {
            // Five at 16, five at 12, one at the default 8.
            var index = 0;
            var migrated = MigrateRaw(FireLines(AllFireSettings((piece, key) =>
                key != "range" ? null : index++ < 5 ? "16" : index <= 10 ? "12" : "8")));
            AssertSingle(migrated, "automatic_processing", "fires_container_search_range", "12");
        }

        private static void MissingAndUnreadableFireValuesAreDefaults()
        {
            // Only three pieces have lines; one of them holds values BepInEx cannot read.
            var migrated = MigrateRaw(FireLines(new[]
            {
                "allow_processing_by_piece_bonfire = Nonsense",
                "container_search_range_by_piece_bonfire = far",
                "piece_bonfire_refuel_only_when_out_of_fuel = maybe",
                "container_search_range_by_piece_hearth = 99",
                "allow_processing_by_piece_sconce = None"
            }));

            // The unreadable allow value takes the default (Refuel); only the sconce is left out.
            AssertSingle(migrated, "automatic_processing", "refuel_fires",
                "Bonfire, Campfire, HangingBrazier, Hearth, JackOTurnip, StandingBlueBurningIronTorch, " +
                "StandingBrazier, StandingGreenBurningIronTorch, StandingIronTorch, StandingWoodTorch");
            // 99 is clamped to 64, but the ten pieces at the default 8 win.
            AssertSingle(migrated, "automatic_processing", "fires_container_search_range", "8");
            AssertSingle(migrated, "automatic_processing", "fires_refuel_only_when_out_of_fuel", "false");
            AssertNoOldFireLines(migrated);
        }

        private static void ConfigWithoutFireLinesGetsOnlyTheRevision()
        {
            var lines = FireLines(Array.Empty<string>());
            var migrated = MigrateRaw(lines);
            var expected = lines.ToList();
            expected.Add(ConfigMigration.SettingsRevisionKey + " = " + ConfigMigration.SettingsRevision);
            var text = string.Join(Environment.NewLine, expected) + Environment.NewLine;
            if (migrated != text)
                throw new Exception($"Only the settings revision should have been added. Expected:\n{text}\nGot:\n{migrated}");
        }

        private static void SettingsRevisionGoesAfterTheOtherMarkers()
        {
            var onlyDefaults = MigrateRaw("[hidden]", "defaults_revision = 2", "", "[automatic_door]", "module = Enabled");
            var lines = onlyDefaults.Split(new[] { Environment.NewLine }, StringSplitOptions.None).ToList();
            var defaults = lines.IndexOf("defaults_revision = 2");
            var pickup = lines.FindIndex(x => x.StartsWith("pickup_key_decoupled", StringComparison.Ordinal));
            var settings = lines.FindIndex(x => x.StartsWith(ConfigMigration.SettingsRevisionKey, StringComparison.Ordinal));
            if (!(defaults >= 0 && pickup == defaults + 1 && settings == pickup + 1))
                throw new Exception("The markers must follow one another in the hidden section:\n" + onlyDefaults);

            var noMarkers = MigrateRaw("[automatic_door]", "module = Enabled");
            AssertSettingsRevision(noMarkers);
        }

        private static void NewInstallGetsTheSettingsRevision()
        {
            var directory = NewTestDirectory();
            var path = Path.Combine(directory, "Automatics.cfg");
            try
            {
                ConfigMigration.Migration(new ConfigFile(path));
                AssertSettingsRevision(File.ReadAllText(path));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static void SettingsRevisionAtZeroRuns()
        {
            var migrated = MigrateRaw(FireLines(AllFireSettings(), "0"));
            AssertSingle(migrated, "automatic_processing", "refuel_fires", "All");
            AssertNoOldFireLines(migrated);
            AssertSettingsRevision(migrated);
        }

        private static void CurrentSettingsRevisionRunsNothing()
        {
            var lines = FireLines(AllFireSettings(), ConfigMigration.SettingsRevision.ToString());
            var migrated = MigrateRaw(lines);
            var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            if (migrated != text)
                throw new Exception($"A current file must not change. Expected:\n{text}\nGot:\n{migrated}");
        }

        private static void UnrecognizedSettingsRevisionIsLeftAlone()
        {
            foreach (var value in new[] { "abc", "-1", "" })
            {
                Automatics.Logger.Warnings.Clear();
                var lines = FireLines(AllFireSettings(), value);
                var migrated = MigrateRaw(lines);
                var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                if (migrated != text)
                    throw new Exception($"An unrecognized revision '{value}' must leave the file. Got:\n{migrated}");
                if (!Automatics.Logger.Warnings.Any(x => x.Contains("settings revision")))
                    throw new Exception($"An unrecognized revision '{value}' must be warned about.");
            }
        }

        private static void FireGroupMigrationIsIdempotent()
        {
            var first = MigrateRaw(FireLines(AllFireSettings((piece, key) => key == "limit" ? "3" : null)));
            var second = MigrateRaw(first.TrimEnd('\r', '\n').Split(new[] { Environment.NewLine }, StringSplitOptions.None));
            if (first != second)
                throw new Exception($"A second run must change nothing. First:\n{first}\nSecond:\n{second}");
        }
    }
}
