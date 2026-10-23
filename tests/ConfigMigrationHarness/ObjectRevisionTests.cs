using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Automatics
{
    internal static partial class Program
    {
        private sealed class ObjectListFixture
        {
            public string Section;
            public string Key;
            public string OldDefault;
            public string Additions;
            public string Before160;
            public string After160;
            public string[] HistoricalDefaults;
            // The list a chain from OldDefault ends with once revision 3 has changed it again;
            // null where revision 3 leaves what revision 2 wrote.
            public string AfterRevision3;
        }

        // Frozen from the tagged Config.cs bindings and object JSON, independently of the
        // migrator's constants. HistoricalDefaults includes only non-colliding results
        // of the released and current MigrationFor160 passes.
        private static readonly ObjectListFixture[] ObjectListFixtures =
        {
            new ObjectListFixture
            {
                Section = "automatic_door",
                Key = "allow_automatic_door",
                OldDefault = "WoodDoor, WoodGate, IronGate, DarkwoodGate, PieceHexagonalDoor, AshwoodDoor, FlametalGate, DvergrDoor",
                Additions = "TimberwoodGate, RoundpoleGate, Drawbridge",
                Before160 = "WoodDoor, WoodGate, IronGate, DarkwoodGate, PieceHexagonalDoor",
                After160 = "WoodDoor, WoodGate, IronGate, DarkwoodGate, PieceHexagonalDoor, AshwoodDoor, FlametalGate, DvergrDoor",
                HistoricalDefaults = new[]
                {
                    "WoodDoor, WoodGate, IronGate, DarkwoodGate, PieceHexagonalDoor, AshwoodDoor, FlametalGate, DvergrDoor",
                    "WoodDoor, \" WoodGate\", \" IronGate\", \" DarkwoodGate\", \" PieceHexagonalDoor\", AshwoodDoor, FlametalGate, DvergrDoor",
                }
            },
            new ObjectListFixture
            {
                Section = "automatic_mapping",
                Key = "allow_pinning_animal",
                OldDefault = "Boar, Piggy, Deer, Wolf, WolfCub, Lox, LoxCalf, Hen, Chicken, Hare, Bird, Fish, Asksvin, AsksvinHatchling",
                Additions = "Moose, MooseCalf, Seal, BabySeal, RiktigFuling",
                Before160 = "Boar, Piggy, Deer, Wolf, WolfCub, Lox, LoxCalf, Hen, Chicken, Hare, Bird, Fish",
                After160 = "Boar, Piggy, Deer, Wolf, WolfCub, Lox, LoxCalf, Hen, Chicken, Hare, Bird, Fish, Asksvin, AsksvinHatchling",
                HistoricalDefaults = new[]
                {
                    "Boar, Piggy, Deer, Wolf, WolfCub, Lox, LoxCalf, Hen, Chicken, Hare, Bird, Fish, Asksvin, AsksvinHatchling",
                    "Boar, \" Piggy\", \" Deer\", \" Wolf\", \" WolfCub\", \" Lox\", \" LoxCalf\", \" Hen\", \" Chicken\", \" Hare\", \" Bird\", \" Fish\", Asksvin, AsksvinHatchling",
                }
            },
            new ObjectListFixture
            {
                Section = "automatic_mapping",
                Key = "allow_pinning_monster",
                OldDefault = "Greyling, Neck, Ghost, Greydwarf, GreydwarfBrute, GreydwarfShaman, RancidRemains, Skeleton, Troll, Abomination, Blob, Draugr, DraugrElite, Leech, Oozer, Surtling, Wraith, Drake, Fenring, StoneGolem, Deathsquito, Fuling, FulingBerserker, FulingShaman, Growth, Serpent, Bat, FenringCultist, Ulv, DvergrRogue, DvergrMage, Tick, Seeker, SeekerBrood, Gjall, SeekerSoldier, CharredMelee, CharredArcher, CharredMage, CharredTwitcher, CharredGrunt, Morgen, Volture, BonemawSerpent, FallenValkyrie, Fader, SkeletonFire, FrostBlob, Bear, Vile, LavaBlob",
                Additions = "Frysling, Gammeltroll, Krigen, Hexen, Barka, Kvastur, Elaking, EyelessOne, ShapelessPulp, TinyPulp, Shadow, Mistile, FallenWarrior",
                Before160 = "Greyling, Neck, Ghost, Greydwarf, GreydwarfBrute, GreydwarfShaman, RancidRemains, Skeleton, Troll, Abomination, Blob, Draugr, DraugrElite, Leech, Oozer, Surtling, Wraith, Drake, Fenring, StoneGolem, Deathsquito, Fuling, FulingBerserker, FulingShaman, Growth, Serpent, Bat, FenringCultist, Ulv, DvergrRogue, DvergrMage, Tick, Seeker, SeekerBrood, Gjall, SeekerSoldier",
                After160 = "Greyling, Neck, Ghost, Greydwarf, GreydwarfBrute, GreydwarfShaman, RancidRemains, Skeleton, Troll, Abomination, Blob, Draugr, DraugrElite, Leech, Oozer, Surtling, Wraith, Drake, Fenring, StoneGolem, Deathsquito, Fuling, FulingBerserker, FulingShaman, Growth, Serpent, Bat, FenringCultist, Ulv, DvergrRogue, DvergrMage, Tick, Seeker, SeekerBrood, Gjall, SeekerSoldier, CharredMelee, CharredArcher, CharredMage, CharredTwitcher, CharredGrunt, Morgen, Volture, BonemawSerpent, FallenValkyrie, Fader, SkeletonFire, FrostBlob, Bear, Vile, LavaBlob",
                HistoricalDefaults = new[]
                {
                    "Greyling, Neck, Ghost, Greydwarf, GreydwarfBrute, GreydwarfShaman, RancidRemains, Skeleton, Troll, Abomination, Blob, Draugr, DraugrElite, Leech, Oozer, Surtling, Wraith, Drake, Fenring, StoneGolem, Deathsquito, Fuling, FulingBerserker, FulingShaman, Growth, Serpent, Bat, FenringCultist, Ulv, DvergrRogue, DvergrMage, Tick, Seeker, SeekerBrood, Gjall, SeekerSoldier, CharredMelee, CharredArcher, CharredMage, CharredTwitcher, CharredGrunt, Morgen, Volture, BonemawSerpent, FallenValkyrie, Fader, SkeletonFire, FrostBlob, Bear, Vile, LavaBlob",
                    "Greyling, \" Neck\", \" Ghost\", \" Greydwarf\", \" GreydwarfBrute\", \" GreydwarfShaman\", \" RancidRemains\", \" Skeleton\", \" Troll\", \" Abomination\", \" Blob\", \" Draugr\", \" DraugrElite\", \" Leech\", \" Oozer\", \" Surtling\", \" Wraith\", \" Drake\", \" Fenring\", \" StoneGolem\", \" Deathsquito\", \" Fuling\", \" FulingBerserker\", \" FulingShaman\", \" Growth\", \" Serpent\", \" Bat\", \" FenringCultist\", \" Ulv\", \" DvergrRogue\", \" DvergrMage\", \" Tick\", \" Seeker\", \" SeekerBrood\", \" Gjall\", \" SeekerSoldier\", CharredMelee, CharredArcher, CharredMage, CharredTwitcher, CharredGrunt, Morgen, Volture, BonemawSerpent, FallenValkyrie, Fader, SkeletonFire, FrostBlob, Bear, Vile, LavaBlob",
                }
            },
            new ObjectListFixture
            {
                Section = "automatic_mapping",
                Key = "allow_pinning_mineral",
                OldDefault = "CopperDeposit, TinDeposit, MudPile, SilverVein, PetrifiedBone, SoftTissue, FlametalDeposit",
                Additions = "PetrifiedGammeltroll, SilverDeposit, AncientArmour, AncientSword",
                Before160 = "CopperDeposit, TinDeposit, MudPile, SilverVein, PetrifiedBone, SoftTissue",
                After160 = "CopperDeposit, TinDeposit, MudPile, SilverVein, PetrifiedBone, SoftTissue, FlametalDeposit",
                HistoricalDefaults = new[]
                {
                    "CopperDeposit, TinDeposit, MudPile, SilverVein, PetrifiedBone, SoftTissue, FlametalDeposit",
                    "CopperDeposit, \" TinDeposit\", \" MudPile\", \" SilverVein\", \" PetrifiedBone\", \" SoftTissue\", FlametalDeposit",
                    "-9, FlametalDeposit",
                }
            },
            new ObjectListFixture
            {
                Section = "automatic_mapping",
                Key = "allow_pinning_dungeon",
                OldDefault = "BurialChambers, TrollCave, SunkenCrypts, MountainCave, InfestedMine, MorgenHole",
                Additions = "BearCave, Morkhalla, WindingTunnels, TombOfLordReto",
                Before160 = "BurialChambers, TrollCave, SunkenCrypts, MountainCave, InfestedMine",
                After160 = "BurialChambers, TrollCave, SunkenCrypts, MountainCave, InfestedMine, MorgenHole",
                HistoricalDefaults = new[]
                {
                    "BurialChambers, TrollCave, SunkenCrypts, MountainCave, InfestedMine, MorgenHole",
                    "BurialChambers, \" TrollCave\", \" SunkenCrypts\", \" MountainCave\", \" InfestedMine\", MorgenHole",
                }
            },
            new ObjectListFixture
            {
                Section = "automatic_mining",
                Key = "allow_mining_mineral",
                OldDefault = "CopperDeposit, TinDeposit, MudPile, ObsidianDeposit, SilverVein, PetrifiedBone, SoftTissue, FlametalDeposit",
                Additions = "PetrifiedGammeltroll, SilverDeposit, AncientArmour, AncientSword",
                Before160 = "CopperDeposit, TinDeposit, MudPile, ObsidianDeposit, SilverVein, PetrifiedBone, SoftTissue",
                After160 = "CopperDeposit, TinDeposit, MudPile, ObsidianDeposit, SilverVein, PetrifiedBone, SoftTissue, FlametalDeposit",
                HistoricalDefaults = new[]
                {
                    "CopperDeposit, TinDeposit, MudPile, ObsidianDeposit, SilverVein, PetrifiedBone, SoftTissue, FlametalDeposit",
                    "CopperDeposit, \" TinDeposit\", \" MudPile\", \" ObsidianDeposit\", \" SilverVein\", \" PetrifiedBone\", \" SoftTissue\", FlametalDeposit",
                }
            },
            new ObjectListFixture
            {
                Section = "automatic_farming",
                Key = "allow_farming_crop",
                OldDefault = "Dandelion, Mushroom, Raspberries, Blueberries, Carrot, CarrotSeeds, YellowMushroom, Thistle, Turnip, TurnipSeeds, Onion, OnionSeeds, Barley, Cloudberries, Flex, JotunPuffs, Magecap, Fiddlehead, SmokePuff, Vineberry, VineberrySeeds",
                Additions = "Kale, KaleSeeds, Oat, Poteitr, Lingonberries, BlueMushroom",
                HistoricalDefaults = new[]
                {
                    "Dandelion, Mushroom, Raspberries, Blueberries, Carrot, CarrotSeeds, YellowMushroom, Thistle, Turnip, TurnipSeeds, Onion, OnionSeeds, Barley, Cloudberries, Flex, JotunPuffs, Magecap, Fiddlehead, SmokePuff, Vineberry, VineberrySeeds",
                },
                AfterRevision3 = "Carrot, CarrotSeeds, Turnip, TurnipSeeds, Onion, OnionSeeds, Barley, Flax, JotunPuffs, Magecap, Vineberry, Kale, KaleSeeds, Oat, Poteitr"
            },
            new ObjectListFixture
            {
                Section = "automatic_processing",
                Key = "allow_container",
                OldDefault = "PieceChestWood, PieceChest, PieceChestBlackmetal, PieceChestCharred",
                Additions = "PieceChestBarrel, PieceChestGrausten, PieceChestWardrobe",
                Before160 = "PieceChestWood, PieceChest, PieceChestBlackmetal",
                After160 = "PieceChestWood, PieceChest, PieceChestBlackmetal, PieceChestCharred",
                HistoricalDefaults = new[]
                {
                    "PieceChestWood, PieceChest, PieceChestBlackmetal, PieceChestCharred",
                    "PieceChestWood, \" PieceChest\", \" PieceChestBlackmetal\", PieceChestCharred",
                }
            },
            new ObjectListFixture
            {
                Section = "automatic_storage",
                Key = "allow_container",
                OldDefault = "PieceChestWood, PieceChest, PieceChestBlackmetal, PieceChestCharred",
                Additions = "PieceChestBarrel, PieceChestGrausten, PieceChestWardrobe",
                HistoricalDefaults = new[]
                {
                    "PieceChestWood, PieceChest, PieceChestBlackmetal, PieceChestCharred",
                }
            },
        };

        // The list the generic behaviour cases run on. The step handles every list through one
        // table-driven loop that never reads module state, so these cases need one list only.
        // The mapping minerals list has the most accepted sets: current, released 1.6.0 and -9.
        private static ObjectListFixture RepresentativeList =>
            ObjectListFixtures.Single(f => f.Key == "allow_pinning_mineral");

        private static void RunObjectRevisionTests()
        {
            foreach (var fixture in ObjectListFixtures)
            {
                var name = $"[{fixture.Section}] {fixture.Key}";
                Run(name + " appends exactly the new objects to the previous default", () =>
                    AssertMigratedList(fixture, fixture.OldDefault, fixture.AfterRevision3 ?? ExpectedList(fixture.OldDefault, fixture)));
                foreach (var historical in fixture.HistoricalDefaults)
                {
                    var value = historical;
                    Run(name + " recognizes historical default " + value, () =>
                        AssertMigratedList(fixture, value, fixture.AfterRevision3 ??
                            ExpectedList(string.Join(", ", ModUtils.Csv.ParseLine(value, true).Select(x => x.Trim())), fixture)));
                }
                if (fixture.Before160 != null)
                {
                    Run(name + " applies both migrations to untouched pre-1.6 defaults", () =>
                    {
                        var result = Migrate(RevisionLines(fixture, fixture.Before160, version: "1.5.1"));
                        AssertList(result, fixture, ExpectedList(fixture.After160, fixture));
                        AssertMarker(result);
                    });
                }
            }

            var representative = RepresentativeList;
            var representativeName = $"[{representative.Section}] {representative.Key}";
            Run(representativeName + " accepts reordered defaults and keeps their order", () =>
            {
                var reversed = string.Join(", ", representative.OldDefault.Split(", ").Reverse());
                AssertMigratedList(representative, reversed, ExpectedList(reversed, representative));
            });
            Run(representativeName + " keeps an extra identifier without appending", () =>
                AssertEditedList(representative, representative.OldDefault + ", CustomObject"));
            Run(representativeName + " keeps a removed default identifier without appending", () =>
                AssertEditedList(representative, string.Join(", ", representative.OldDefault.Split(", ").Skip(1))));
            Run(representativeName + " keeps an empty list empty", () => AssertEditedList(representative, ""));
            Run(representativeName + " appends defaults even when its module is disabled", () =>
            {
                var lines = RevisionLines(representative, representative.OldDefault).ToList();
                lines.Insert(2, "module = Disabled");
                var result = Migrate(lines.ToArray());
                AssertModule(result, representative.Section, "Disabled");
                AssertList(result, representative, ExpectedList(representative.OldDefault, representative));
            });
            // The accepted sets equal the independent historical fixtures and lie three or more
            // elements apart, so one- and two-element edits of an accepted set need no own case.
            Run("every list's accepted sets equal its historical fixtures and cannot equal one- or two-element edits", () =>
                AssertEachList(ObjectListFixtures, AssertAcceptedSetsHaveNoSmallEdits));
            Run("every list rejects a bare pre-1.6 default under a current header", () =>
                AssertEachList(ObjectListFixtures.Where(f => f.Before160 != null), f => AssertEditedList(f, f.Before160)));

            Run("migrated object lists are byte identical on the next run", ObjectRevisionIsIdempotent);
            Run("objects removed after revision two stay removed byte for byte", RemovedObjectStaysRemoved);
            Run("edited lists without flax keep their original bytes", EditedObjectLinesKeepBytes);
            Run("flax is renamed in edited farming and mapping lists at its original position", FlaxRenameKeepsPosition);
            Run("an existing Flax wins over Flex and lowercase flex stays", ExistingFlaxWins);
            Run("lowercase flex alone leaves its line byte identical", LowercaseFlexStays);
            Run("pre-1.4 All doors get version additions only while other defaults migrate", AllListsMigrate);
            Run("markerless 1.6 and 1.7 lists migrate while module handling stays unchanged", () =>
            {
                foreach (var version in new[] { "1.6.0", "1.7.0" })
                {
                    try
                    {
                        MarkerlessListsMigrate(version);
                    }
                    catch (Exception e)
                    {
                        throw new Exception($"{version}: {e.Message}");
                    }
                }
            });
            Run("an explicit revision zero migrates lists without changing module defaults", RevisionZeroWithMarker);
            Run("missing object keys are never added", MissingObjectKeysStayMissing);
            foreach (var value in new[] { "unknown", "-1", "4", "2147483648", "" })
            {
                var marker = value;
                Run("unsupported defaults revision '" + marker + "' skips only the object revision", () =>
                    UnsupportedRevisionStays(marker));
            }
            Run("the revision marker keeps its BepInEx position and description comments", MarkerStaysInPlace);
            Run("every repeated hidden revision marker is updated in place", RepeatedMarkersStayInPlace);
            Run("Flex in another object list keeps its line byte identical", OtherListFlexStays);
            Run("quoted leading spaces must match the released corruption exactly", () =>
                AssertMigratedList(ObjectListFixtures[0],
                    "WoodDoor, \"  WoodGate\", \" IronGate\", \" DarkwoodGate\", \" PieceHexagonalDoor\", AshwoodDoor, FlametalGate, DvergrDoor",
                    "WoodDoor, WoodGate, IronGate, DarkwoodGate, PieceHexagonalDoor, AshwoodDoor, FlametalGate, DvergrDoor"));
            Run("the renamed 1.3 mineral default gets only version additions", OldMineralDefaultMigrates);
            Run("the pre-1.3 numeric mineral default receives both additions", EnumMineralDefaultMigrates);
            Run("an edited pre-1.4 mineral All is not an unedited default", MineralAllStaysEdited);
            Run("a read-only revision one file warns and keeps its marker for retry", ReadOnlyObjectRevision);
            Run("quoted CSV, duplicate identifiers and empty elements still match an unedited set", CsvDefaultsMatch);
            Run("object identifiers and section and key names compare ordinally", ObjectRevisionIsOrdinal);
            Run("a new install writes exactly the markers, at revision three", NewInstallWritesExactlyMarker);
            RunWhitespaceRepairTests();
            RunWildCropRemovalTests();
        }

        // Runs the check on every list and names each list that fails it.
        private static void AssertEachList(IEnumerable<ObjectListFixture> fixtures, Action<ObjectListFixture> check)
        {
            var failures = new List<string>();
            foreach (var fixture in fixtures)
            {
                try
                {
                    check(fixture);
                }
                catch (Exception e)
                {
                    failures.Add($"[{fixture.Section}] {fixture.Key}: {e.Message}");
                }
            }
            if (failures.Count > 0)
                throw new Exception(string.Join(Environment.NewLine, failures));
        }

        private static void AssertAcceptedSetsHaveNoSmallEdits(ObjectListFixture fixture)
        {
            // Inspect the production table so adding a constant without a fixture cannot
            // bypass this check. Compare raw parsed elements, including quoted spaces.
            var revisions = (Array)typeof(ConfigMigration).GetField("ObjectListsRevision2",
                BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var revision = revisions.Cast<object>().Single(x =>
                (string)x.GetType().GetField("Section").GetValue(x) == fixture.Section &&
                (string)x.GetType().GetField("Key").GetValue(x) == fixture.Key);
            var sets = (HashSet<string>[])revision.GetType().GetField("Defaults").GetValue(revision);
            var expected = fixture.HistoricalDefaults.Select(x => new HashSet<string>(
                ModUtils.Csv.ParseLine(x, true).Where(v => !string.IsNullOrEmpty(v)), StringComparer.Ordinal)).ToArray();
            AssertEqual(expected.Length, sets.Length, "Every accepted set must have an independent historical fixture.");
            if (expected.Any(x => !sets.Any(y => y.SetEquals(x))))
                throw new Exception("The production accepted sets differ from the historical fixtures.");
            var pairs = 0;
            for (var i = 0; i < sets.Length; i++)
            {
                for (var j = i + 1; j < sets.Length; j++)
                {
                    var difference = new HashSet<string>(sets[i], StringComparer.Ordinal);
                    difference.SymmetricExceptWith(sets[j]);
                    Console.WriteLine($"collision scan: [{fixture.Section}] {fixture.Key} sets {i}/{j}: difference={difference.Count}");
                    if (difference.Count <= 2)
                        throw new Exception("Accepted defaults collide with a one- or two-element edit, or duplicate another set.");
                    pairs++;
                }
            }
            Console.WriteLine($"collision scan: [{fixture.Section}] {fixture.Key}: {sets.Length} sets, {pairs} pairs, no collisions");
        }

        private static string[] RevisionLines(ObjectListFixture fixture, string value,
            string revision = "1", string version = "1.7.0")
        {
            return new[] { "## Settings file was created by plugin Automatics v" + version,
                "[" + fixture.Section + "]", fixture.Key + " = " + value,
                "[hidden]", "defaults_revision = " + revision, "pickup_key_decoupled = 1" };
        }

        private static string ExpectedList(string value, ObjectListFixture fixture)
        {
            return (value + ", " + fixture.Additions).Replace("Flex", "Flax");
        }

        private static void AssertList(string text, ObjectListFixture fixture, string expected)
        {
            var values = ValuesOf(text, fixture.Section, fixture.Key);
            if (values.Count != 1 || values[0] != expected)
                throw new Exception($"Expected [{fixture.Section}] {fixture.Key} = {expected}, got [{string.Join("; ", values)}]");
        }

        private static void AssertMigratedList(ObjectListFixture fixture, string value, string expected)
        {
            var result = Migrate(RevisionLines(fixture, value));
            AssertList(result, fixture, expected);
            AssertMarker(result);
        }

        private static void AssertEditedList(ObjectListFixture fixture, string value)
        {
            var lines = RevisionLines(fixture, value);
            lines[2] = "  " + fixture.Key + "	=  " + value + "  ";
            var expected = (string[])lines.Clone();
            expected[4] = "defaults_revision = 3";
            // Flax spelling is corrected even when the list was edited.
            if (fixture.Key == "allow_farming_crop" && value.Split(", ").Contains("Flex"))
                expected[2] = fixture.Key + " = " + value.Replace("Flex", "Flax");
            var result = Migrate(lines);
            if (result != string.Join(Environment.NewLine, expected) + Environment.NewLine)
                throw new Exception("An edited list changed beyond the flax rename or the revision marker.");
        }

        private static void AssertBytesUnchanged(string content)
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            if (!MigrateBytes(bytes).SequenceEqual(bytes))
                throw new Exception("The config must stay byte for byte.");
        }

        // A revision two file: the revision two steps do not run again, so only the marker
        // changes, to revision three, and every other byte stays.
        private static void AssertOnlyMarkerAdvanced(string[] lines)
        {
            var content = string.Join("\r\n", lines);
            var expected = new UTF8Encoding(false).GetBytes(content.Replace("defaults_revision = 2", "defaults_revision = 3"));
            if (!MigrateBytes(new UTF8Encoding(false).GetBytes(content)).SequenceEqual(expected))
                throw new Exception("A revision two file may change only its revision marker.");
        }

        private static void ObjectRevisionIsIdempotent()
        {
            var lines = new List<string> { "# Automatics v1.7.0" };
            foreach (var f in ObjectListFixtures)
                lines.AddRange(new[] { "[" + f.Section + "]", f.Key + " = " + f.OldDefault });
            lines.AddRange(new[] { "[hidden]", "defaults_revision = 1" });
            var first = Migrate(lines.ToArray());
            AssertBytesUnchanged(first);
        }

        private static void RemovedObjectStaysRemoved()
        {
            var f = ObjectListFixtures[0];
            var value = ExpectedList(f.OldDefault, f).Replace(", TimberwoodGate", "");
            AssertBytesUnchanged(string.Join("\r\n", RevisionLines(f, value, "3")));
        }

        private static void EditedObjectLinesKeepBytes()
        {
            var f = ObjectListFixtures[0];
            foreach (var value in new[] { f.OldDefault + ", Extra", string.Join(", ", f.OldDefault.Split(", ").Skip(1)) })
            {
                var text = string.Join("\r\n", RevisionLines(f, value)) + "\r\n";
                text = text.Replace(f.Key + " = ", "  " + f.Key + "\t=   ");
                var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
                var expected = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text.Replace("defaults_revision = 1", "defaults_revision = 3"))).ToArray();
                if (!MigrateBytes(bytes).SequenceEqual(expected))
                    throw new Exception("An edited line's bytes, BOM or CRLF changed.");
            }
        }

        private static string MigrateFlax(string value)
        {
            return Migrate("# Automatics v1.7.0", "[automatic_farming]", "allow_farming_crop = " + value,
                "[automatic_mapping]", "allow_pinning_flora = " + value, "[hidden]", "defaults_revision = 1");
        }

        private static void FlaxRenameKeepsPosition()
        {
            var result = MigrateFlax("CustomBefore, Flex, CustomAfter");
            AssertContains(result, "allow_farming_crop = CustomBefore, Flax, CustomAfter");
            AssertContains(result, "allow_pinning_flora = CustomBefore, Flax, CustomAfter");
        }

        private static void ExistingFlaxWins()
        {
            foreach (var value in new[] { "Flex, Custom, Flax, flex, Flex", "Flax, Custom, Flex, flex" })
            {
                var result = MigrateFlax(value);
                foreach (var key in new[] { ("automatic_farming", "allow_farming_crop"), ("automatic_mapping", "allow_pinning_flora") })
                {
                    var actual = ValuesOf(result, key.Item1, key.Item2).Single();
                    var expected = value.StartsWith("Flex,", StringComparison.Ordinal) ? "Custom, Flax, flex" : "Flax, Custom, flex";
                    if (actual != expected) throw new Exception("The existing Flax must keep its position, with Flex removed.");
                }
            }
        }

        private static void LowercaseFlexStays()
        {
            var result = MigrateFlax("flex");
            AssertContains(result, "allow_farming_crop = flex");
            AssertContains(result, "allow_pinning_flora = flex");
        }

        private static void AllListsMigrate()
        {
            var mapping = ObjectListFixtures.Where(f => f.Section == "automatic_mapping" &&
                f.Key != "allow_pinning_mineral").ToArray();
            var lines = new List<string> { "# Automatics v1.3.2", "[automatic_door]", "allow_automatic_door = All", "[automatic_mapping]" };
            lines.AddRange(mapping.Select(f => f.Key + " = All"));
            lines.Add("allow_pinning_flora = All");
            var result = Migrate(lines.ToArray());
            AssertList(result, ObjectListFixtures[0],
                "WoodDoor, WoodGate, IronGate, DarkwoodGate, WoodShutter, PieceHexagonalDoor, AshwoodDoor, FlametalGate, DvergrDoor");
            foreach (var f in mapping)
                AssertList(result, f, ExpectedList(f.OldDefault, f));
            var flora = ObjectListFixtures.Single(f => f.Key == "allow_farming_crop").OldDefault.Replace("Flex", "Flax");
            if (ValuesOf(result, "automatic_mapping", "allow_pinning_flora").Single() != flora)
                throw new Exception("The historical All flora list must end with the flax spelling corrected.");
            AssertMarker(result);
        }

        private static void MarkerlessListsMigrate(string version)
        {
            var f = ObjectListFixtures[0];
            var lines = RevisionLines(f, f.OldDefault, version: version).Take(3).ToList();
            lines.AddRange(new[] { "module = Disabled", "[automatic_farming]", "module = unreadable" });
            var result = Migrate(lines.ToArray());
            AssertList(result, f, ExpectedList(f.OldDefault, f));
            foreach (var section in ModuleSections)
                AssertModule(result, section, section == "automatic_door" ? "Disabled" : "Enabled");
            AssertMarker(result);
            if (!result.TrimEnd().EndsWith("defaults_revision = 3" + Environment.NewLine + "pickup_key_decoupled = 1", StringComparison.Ordinal))
                throw new Exception("The marker, followed by the pickup key marker, must be last.");
        }

        private static void RevisionZeroWithMarker()
        {
            var f = ObjectListFixtures[0];
            var result = Migrate(RevisionLines(f, f.OldDefault, "0"));
            AssertList(result, f, ExpectedList(f.OldDefault, f));
            AssertDoesNotContain(result, "module =");
            AssertMarker(result);
        }

        private static void MissingObjectKeysStayMissing()
        {
            var lines = new[] { "# Automatics v1.7.0" }.Concat(ModuleSections.Select(x => "[" + x + "]"))
                .Concat(new[] { "[hidden]", "defaults_revision = 1" }).ToArray();
            var result = Migrate(lines);
            foreach (var f in ObjectListFixtures)
                AssertEqual(0, ValuesOf(result, f.Section, f.Key).Count, "Missing keys must not be added.");
        }

        private static void UnsupportedRevisionStays(string marker)
        {
            var f = ObjectListFixtures[0];
            var content = string.Join("\n", RevisionLines(f, f.OldDefault, marker)) +
                "\n[automatic_farming]\nallow_farming_crop = Flex\n[automatic_mapping]\nallow_pinning_flora = Flex\n";
            var warnings = Automatics.Logger.Warnings.Count;
            AssertBytesUnchanged(content);
            AssertEqual(warnings + 1, Automatics.Logger.Warnings.Count, "An unsupported revision must log one warning.");

            var oldLines = RevisionLines(f, "WoodDoor", marker, "1.2.1").Concat(new[] {
                "[automatic_repair]", "module = unreadable", "automatic_repair_enabled = false",
                "[automatic_farming]", "allow_farming_crop = Flex" }).ToArray();
            var result = Migrate(oldLines);
            // The renamed switch, false, turns the module off (settings pass 3).
            AssertDoesNotContain(result, "enable_automatic_repair");
            AssertDoesNotContain(result, "automatic_repair_enabled");
            AssertList(result, f, "WoodDoor, PieceHexagonalDoor, AshwoodDoor, FlametalGate, DvergrDoor");
            AssertContains(result, "allow_farming_crop = Flex");
            AssertModule(result, "automatic_repair", "Disabled");
            AssertEqual(1, CountOccurrences(result, "module ="), "A present marker keeps module handling unchanged.");
            if (ValuesOf(result, "hidden", "defaults_revision").Single() != marker)
                throw new Exception("The unsupported marker must remain unchanged while version migrations run.");
        }

        private static void MarkerStaysInPlace()
        {
            var f = ObjectListFixtures[0];
            var lines = new[] { "# Automatics v1.7.0", "[automatic_door]", f.Key + " = " + f.OldDefault,
                "[hidden]", "NexusID = 1170", "", "## Revision of the configuration defaults.",
                "# Setting type: Int32", "# Default value: 0", "defaults_revision = 1", "pickup_key_decoupled = 1", "",
                "[system]", "enable_logging = true" };
            var expected = (string[])lines.Clone();
            expected[2] = f.Key + " = " + ExpectedList(f.OldDefault, f);
            expected[9] = "defaults_revision = 3";
            var result = Migrate(lines);
            AssertMarker(result);
            AssertEqual(1, CountOccurrences(result, "[hidden]"), "The hidden header must not be duplicated.");
            if (result != string.Join(Environment.NewLine, expected) + Environment.NewLine)
                throw new Exception("Only the list and marker values should change in the BepInEx layout.");
        }

        private static void RepeatedMarkersStayInPlace()
        {
            var lines = new[] { "# Automatics v1.7.0", "[hidden]", "# first", "defaults_revision = 0",
                "NexusID = 1170", "# second", "defaults_revision = 1", "[system]", "defaults_revision = 1",
                "[hidden]", "# third", "defaults_revision = 1", "pickup_key_decoupled = 1" };
            var expected = (string[])lines.Clone();
            foreach (var i in new[] { 3, 6, 11 }) expected[i] = "defaults_revision = 3";
            if (Migrate(lines) != string.Join(Environment.NewLine, expected) + Environment.NewLine)
                throw new Exception("Every hidden marker should be updated without moving or deleting lines.");
        }

        private static void OtherListFlexStays()
        {
            AssertEditedList(ObjectListFixtures[2], "Greyling, Flex, CustomObject");
        }

        private static void OldMineralDefaultMigrates()
        {
            var f = ObjectListFixtures[3];
            var result = Migrate("# Automatics v1.3.2", "[automatic_mapping]",
                "allow_pinning_deposit = CopperDeposit, TinDeposit, MudPile, SilverVein");
            AssertList(result, f, "CopperDeposit, TinDeposit, MudPile, SilverVein, FlametalDeposit");
            AssertDoesNotContain(result, "allow_pinning_deposit");
            AssertMarker(result);
        }

        private static void EnumMineralDefaultMigrates()
        {
            var f = ObjectListFixtures[3];
            var result = Migrate("# Automatics v1.2.1", "[automatic_map_pinning]", "allow_pinning_vein = -9");
            AssertList(result, f, ExpectedList("-9, FlametalDeposit", f));
            AssertDoesNotContain(result, "allow_pinning_vein");
            AssertMarker(result);
        }

        private static void MineralAllStaysEdited()
        {
            var result = Migrate("# Automatics v1.3.2", "[automatic_mapping]", "allow_pinning_deposit = All");
            AssertList(result, ObjectListFixtures[3],
                "CopperDeposit, TinDeposit, MudPile, ObsidianDeposit, SilverVein, PetrifiedBone, SoftTissue, FlametalDeposit");
        }

        private static void ReadOnlyObjectRevision()
        {
            var f = ObjectListFixtures[0];
            var warnings = Automatics.Logger.Warnings.Count;
            var result = MigrateReadOnly(RevisionLines(f, f.OldDefault));
            if (result.Error != null || !result.Unchanged || result.OtherFiles != 0)
                throw new Exception("A failed defaults write must leave the old file for retry without stopping startup.");
            AssertEqual(warnings + 1, Automatics.Logger.Warnings.Count, "A failed defaults write must warn.");
        }

        private static void CsvDefaultsMatch()
        {
            var f = ObjectListFixtures[0];
            var values = f.OldDefault.Split(", ");
            var csv = string.Join(",", values.Select(x => "\"" + x + "\"")) + ",," + values[0] + ", ";
            AssertMigratedList(f, csv, ExpectedList(f.OldDefault, f));
        }

        private static void ObjectRevisionIsOrdinal()
        {
            var f = ObjectListFixtures[0];
            AssertEditedList(f, f.OldDefault.Replace("WoodDoor", "wooddoor"));
            var lines = RevisionLines(f, f.OldDefault);
            lines[1] = "[Automatic_Door]";
            var result = Migrate(lines);
            AssertContains(result, lines[2]);
            lines = RevisionLines(f, f.OldDefault);
            lines[2] = lines[2].Replace(f.Key, f.Key.ToUpperInvariant());
            result = Migrate(lines);
            AssertContains(result, lines[2]);
        }

        private static void NewInstallWritesExactlyMarker()
        {
            var expected = "[hidden]" + Environment.NewLine + "defaults_revision = 3" + Environment.NewLine +
                "pickup_key_decoupled = 1" + Environment.NewLine;
            if (MigrateWithoutAConfigFile() != expected)
                throw new Exception("A new install must write only the revision three marker and the pickup key marker.");
        }
    }
}
