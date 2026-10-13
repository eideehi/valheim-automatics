using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Automatics
{
    // Revision 3: Allow Farming Crop used to default to every Flora identifier, wild plants the
    // cultivator cannot plant included. A list still equal to that default loses them; any other
    // list stays as the user left it.
    internal static partial class Program
    {
        private static ObjectListFixture CropFixture =>
            ObjectListFixtures.Single(f => f.Key == "allow_farming_crop");

        // The 1.7.0 default after revision 2 and the Flax rename, in the order revision 2 writes
        // it, taken from the frozen fixture rather than the migrator's constants.
        private static string CropRevision2Default => ExpectedList(CropFixture.OldDefault, CropFixture);

        private static string[] WildCrops => CropRevision2Default.Split(", ")
            .Except(CropFixture.AfterRevision3.Split(", ")).ToArray();

        private static void RunWildCropRemovalTests()
        {
            Run("the revision two farming default loses the wild plants and keeps its order", RevisionTwoCropDefaultLosesWildPlants);
            Run("a 1.7.0 farming default ends as the plantable crops from every earlier revision", OldCropDefaultEndsPlantable);
            Run("farming lists other than the revision two default, and a missing one, stay as they are", OtherCropListsStay);
            Run("a revision three farming list stays byte for byte, re-added wild plants included", RevisionThreeCropListStays);
            Run("an unsupported marker leaves the revision two farming default as it is", UnsupportedRevisionKeepsCropDefault);
            Run("a revision two file keeps the 1.7.0 farming default without additions or the flax rename", () =>
                AssertOnlyMarkerAdvanced(RevisionLines(CropFixture, CropFixture.OldDefault, "2")));
            Run("a revision two file keeps Flex in edited farming and flora lists", () =>
                AssertOnlyMarkerAdvanced(new[] { "# Automatics v1.7.0", "[automatic_farming]",
                    "allow_farming_crop = CustomCrop, Flex", "[automatic_mapping]", "allow_pinning_flora = CustomFlora, Flex",
                    "[hidden]", "defaults_revision = 2", "pickup_key_decoupled = 1" }));
            Run("the wild plant removal compares identifiers and names ordinally", WildCropRemovalIsOrdinal);
            Run("a reordered revision two farming default keeps its own order without the wild plants", ReorderedCropDefaultKeepsOrder);
            Run("the wild plant removal accepts exactly the revision two farming default", WildCropRemovalAcceptsOneSet);
        }

        private static void RevisionTwoCropDefaultLosesWildPlants()
        {
            var messages = Automatics.Logger.Messages.Count;
            var result = Migrate(RevisionLines(CropFixture, CropRevision2Default, "2"));
            AssertList(result, CropFixture, CropFixture.AfterRevision3);
            AssertMarker(result);

            var kept = CropFixture.AfterRevision3.Split(", ");
            var logged = Automatics.Logger.Messages.Skip(messages).Where(x => x.Contains("allow_farming_crop") &&
                WildCrops.All(w => Regex.IsMatch(x, $@"\b{w}\b")) && !kept.Any(k => Regex.IsMatch(x, $@"\b{k}\b")));
            AssertEqual(1, logged.Count(), "One log line must name exactly the removed wild plants.");
        }

        private static void OldCropDefaultEndsPlantable()
        {
            foreach (var marker in new[] { "0", "1", null })
            {
                try
                {
                    var lines = RevisionLines(CropFixture, CropFixture.OldDefault, marker ?? "0");
                    var result = Migrate(marker == null ? lines.Take(3).ToArray() : lines);
                    AssertList(result, CropFixture, CropFixture.AfterRevision3);
                    AssertMarker(result);
                }
                catch (Exception e)
                {
                    throw new Exception($"marker {marker ?? "(missing)"}: {e.Message}");
                }
            }
        }

        private static void OtherCropListsStay()
        {
            var values = new[]
            {
                string.Join(", ", CropRevision2Default.Split(", ").Where(x => x != "Mushroom")),
                CropRevision2Default + ", CustomCrop",
                CropFixture.AfterRevision3,
                ""
            };
            foreach (var value in values)
                AssertOnlyMarkerAdvanced(RevisionLines(CropFixture, value, "2"));
            AssertOnlyMarkerAdvanced(new[] { "# Automatics v1.7.0", "[automatic_farming]", "module = Enabled",
                "[hidden]", "defaults_revision = 2", "pickup_key_decoupled = 1" });
        }

        private static void RevisionThreeCropListStays()
        {
            foreach (var value in new[] { CropFixture.AfterRevision3, CropFixture.AfterRevision3 + ", Mushroom", CropRevision2Default })
                AssertBytesUnchanged(string.Join("\r\n", RevisionLines(CropFixture, value, "3")) + "\r\n");
        }

        private static void UnsupportedRevisionKeepsCropDefault()
        {
            foreach (var marker in new[] { "4", "unknown" })
            {
                var warnings = Automatics.Logger.Warnings.Count;
                AssertBytesUnchanged(string.Join("\n", RevisionLines(CropFixture, CropRevision2Default, marker)) + "\n");
                AssertEqual(warnings + 1, Automatics.Logger.Warnings.Count, "An unsupported revision must log one warning.");
            }
        }

        private static void WildCropRemovalIsOrdinal()
        {
            AssertOnlyMarkerAdvanced(RevisionLines(CropFixture, CropRevision2Default.Replace("Carrot,", "carrot,"), "2"));
            var lines = RevisionLines(CropFixture, CropRevision2Default, "2");
            lines[1] = "[Automatic_Farming]";
            AssertOnlyMarkerAdvanced(lines);
            lines = RevisionLines(CropFixture, CropRevision2Default, "2");
            lines[2] = lines[2].Replace("allow_farming_crop", "ALLOW_FARMING_CROP");
            AssertOnlyMarkerAdvanced(lines);
        }

        private static void ReorderedCropDefaultKeepsOrder()
        {
            var reversed = CropRevision2Default.Split(", ").Reverse().ToArray();
            var result = Migrate(RevisionLines(CropFixture, string.Join(", ", reversed), "2"));
            AssertList(result, CropFixture, string.Join(", ", reversed.Except(WildCrops)));
        }

        // The production set must be the frozen default, and every list one element away from it,
        // removed or added, must stay.
        private static void WildCropRemovalAcceptsOneSet()
        {
            var field = typeof(ConfigMigration).GetField("CropRevision2Defaults", BindingFlags.NonPublic | BindingFlags.Static);
            var accepted = new HashSet<string>(ModUtils.Csv.ParseLine((string)field.GetValue(null), true), StringComparer.Ordinal);
            if (!accepted.SetEquals(CropRevision2Default.Split(", ")))
                throw new Exception("The accepted farming set differs from the frozen revision two default.");

            var elements = CropRevision2Default.Split(", ");
            var failures = new List<string>();
            foreach (var value in elements.Select(e => string.Join(", ", elements.Where(x => x != e)))
                         .Concat(new[] { CropRevision2Default + ", CustomCrop" }))
            {
                try
                {
                    AssertOnlyMarkerAdvanced(RevisionLines(CropFixture, value, "2"));
                }
                catch (Exception e)
                {
                    failures.Add($"{value}: {e.Message}");
                }
            }
            if (failures.Count > 0)
                throw new Exception(string.Join(Environment.NewLine, failures));
        }
    }
}
