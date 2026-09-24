using System;
using System.Linq;

namespace Automatics
{
    internal static partial class Program
    {
        private static void RunWhitespaceRepairTests()
        {
            var fixtures = ObjectListFixtures.Where(f => f.Before160 != null).Concat(new[]
            {
                new ObjectListFixture { Section = "automatic_mapping", Key = "allow_pinning_vehicle",
                    OldDefault = "Karve, Longship, Drakkar",
                    HistoricalDefaults = new[] { "Karve, \" Longship\", Drakkar" } },
                new ObjectListFixture { Section = "automatic_mapping", Key = "allow_pinning_spot",
                    OldDefault = "InfestedTree, FireHole, DrakeNest, GoblinCamp, TarPit, DvergrExcavation, DvergrGuardTower, DvergrHarbour, DvergrLighthouse, PetrifiedBone, CharredFortress, LeviathanLava, BogWitchCamp",
                    HistoricalDefaults = new[] { "InfestedTree, \" FireHole\", \" DrakeNest\", \" GoblinCamp\", \" TarPit\", \" DvergrExcavation\", \" DvergrGuardTower\", \" DvergrHarbour\", \" DvergrLighthouse\", \" PetrifiedBone\", CharredFortress, LeviathanLava, BogWitchCamp" } },
                new ObjectListFixture { Section = "automatic_mapping", Key = "allow_pinning_flora",
                    OldDefault = "Dandelion, Mushroom, Flax, Fiddlehead, SmokePuff, Vineberry, VineberrySeeds",
                    HistoricalDefaults = new[] { "Dandelion, \" Mushroom\", \" Flex\", Fiddlehead, SmokePuff, Vineberry, VineberrySeeds" } },
            }).ToArray();

            foreach (var fixture in fixtures)
            {
                var name = $"[{fixture.Section}] {fixture.Key}";
                var corrupted = Corrupted(fixture);
                Run(name + " repairs edited quoted elements without adding targets", () =>
                {
                    var expected = string.Join(", ", ModUtils.Csv.ParseLine(corrupted, true)
                        .Select(x => x.Trim() == "Flex" ? "Flax" : x.Trim())) + ", CustomObject";
                    var result = Migrate(RevisionLines(fixture, corrupted + ", CustomObject"));
                    AssertList(result, fixture, expected);
                    AssertMarker(result);
                    AssertBytesUnchanged(result);
                });
            }

            var representative = RepresentativeList;
            Run($"[{representative.Section}] {representative.Key} leaves quoted spaces untouched at revision two", () =>
                AssertBytesUnchanged(string.Join("\r\n", RevisionLines(representative, Corrupted(representative), "2"))));

            Run("trimming deduplicates ordinally and keeps first positions with CSV escaping", () =>
            {
                var fixture = ObjectListFixtures[0];
                const string value = "\" CustomObject \" , Second, CustomObject, \" Second\", customobject, \"  With,Comma  \", \" With\"\"Quote \"";
                const string expected = "CustomObject, Second, customobject, \"With,Comma\", \"With\"\"Quote\"";
                var result = Migrate(RevisionLines(fixture, value));
                AssertList(result, fixture, expected);
                AssertBytesUnchanged(result);
            });
            // The unsupported defaults revision cases cover each way a marker goes unrecognized.
            Run("unrecognized marker '3' leaves corrupted lists untouched", () =>
            {
                var lines = fixtures.SelectMany(f => new[] { "[" + f.Section + "]",
                    f.Key + " = \" First \", \" Second\", First" });
                var content = "# Automatics v1.7.0\n" + string.Join("\n", lines) +
                    "\n[hidden]\ndefaults_revision = 3\npickup_key_decoupled = 1\n";
                AssertBytesUnchanged(content);
            });
            Run("whitespace repair leaves lists outside the released migration alone", () =>
            {
                AssertEditedList(ObjectListFixtures[6], "\" CustomCrop \", OtherCrop");
                AssertEditedList(ObjectListFixtures[8], "\" CustomContainer \", OtherContainer");
            });
        }

        // The form released 1.6.0 wrote: separator spaces kept inside quotes.
        private static string Corrupted(ObjectListFixture fixture)
        {
            return fixture.HistoricalDefaults.First(x => x.Contains("\" "));
        }
    }
}
