using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;

namespace Automatics
{
    // Binding the Pickup All Nearby key used to switch periodic pickup off. It no longer does,
    // so a config whose key BepInEx reads as bound, and whose interval would run periodic
    // pickup, gets the interval set to 0 once: that user keeps the key-only behaviour. The pass
    // has a gate of its own, [hidden] pickup_key_decoupled, apart from defaults_revision.
    internal static partial class Program
    {
        private const string PickupGateKey = "pickup_key_decoupled";
        private const string PickupGateValue = "1";

        private static void RunPickupKeyMigrationTests()
        {
            Run("a bound key with a positive interval gets the interval set to 0", BoundKeyWithPositiveIntervalGetsZero);
            Run("a bound key without an interval entry gets an interval of 0 added", BoundKeyWithoutIntervalGetsZeroAdded);
            Run("an unparsable interval under a bound key reads as the default and is set to 0", BoundKeyWithUnparsableIntervalGetsZero);
            Run("an interval already at 0 or clamped to 0 is left as it is", NonPositiveIntervalIsKept);
            Run("an unbound key leaves the interval as it is", UnboundKeyKeepsTheInterval);
            Run("a key BepInEx cannot read counts as unbound", UnreadableKeyCountsAsUnbound);
            Run("every key BepInEx reads as bound is taken as bound", ReadableKeyFormsCountAsBound);
            Run("a config without the key entry gets no key written, so the Left Shift + E default applies", MissingKeyEntryIsLeftToTheDefault);
            Run("a revision-two config without the pickup gate runs the pass and gets the gate", RevisionTwoConfigWithoutTheGateRuns);
            Run("the gate alone is a change worth writing", GateAloneIsWritten);
            Run("the revision-two steps do not run again", RevisionTwoStepsDoNotRerun);
            Run("a config without either marker gets both, the pickup gate after the defaults marker", ConfigWithoutMarkersGetsBoth);
            Run("a new install gets the pickup gate", NewInstallGetsThePickupGate);
            Run("a read-only config records no gate, and the next start retries", ReadOnlyConfigRecordsNoGateAndRetries);
            Run("a gate BepInEx wrote as 0 after a failed write lets the pass run", GateAtZeroRuns);
            Run("an unrecognized gate value leaves the file as it is", UnrecognizedGateIsLeftAlone);
            Run("migrating a second time changes nothing", PickupMigrationIsIdempotent);
            Run("a positive interval set after the pass survives the next start", PositiveIntervalSetLaterSurvives);
        }

        // A current config: revision two, with the Pickup section holding the given entries. A
        // null key or interval leaves that entry out.
        private static string[] PickupLines(string key, string interval, string gate = null)
        {
            var lines = new List<string>
            {
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "",
                "[automatic_pickup]",
                "",
                "module = Enabled",
                "automatic_pickup_range = 4"
            };
            if (interval != null) lines.Add("automatic_pickup_interval = " + interval);
            if (key != null) lines.Add("pickup_all_nearby_key = " + key);
            lines.AddRange(new[] { "", "[hidden]", "", "defaults_revision = 2" });
            if (gate != null) lines.Add(PickupGateKey + " = " + gate);
            return lines.ToArray();
        }

        private static void AssertInterval(string text, string expected)
        {
            var values = ValuesOf(text, "automatic_pickup", "automatic_pickup_interval");
            if (values.Count != 1 || values[0] != expected)
                throw new Exception(
                    $"Expected automatic_pickup_interval = {expected} once, got [{string.Join(", ", values)}] in:\n{text}");
        }

        private static void AssertPickupGate(string text)
        {
            var values = ValuesOf(text, MarkerSection, PickupGateKey);
            if (values.Count != 1 || values[0] != PickupGateValue)
                throw new Exception(
                    $"Expected [{MarkerSection}] to hold {PickupGateKey} = {PickupGateValue} once, got [{string.Join(", ", values)}] in:\n{text}");
        }

        // The input with only the gate added after the defaults marker: what a config the pass
        // has nothing to change in has to come back as.
        private static string WithGateOnly(string[] lines)
        {
            var expected = lines.ToList();
            var marker = expected.FindLastIndex(x => x.Trim().StartsWith(MarkerKey, StringComparison.Ordinal));
            expected.Insert(marker + 1, PickupGateKey + " = " + PickupGateValue);
            return string.Join(Environment.NewLine, expected) + Environment.NewLine;
        }

        private static void AssertOnlyTheGateWasAdded(string[] lines)
        {
            var migrated = Migrate(lines);
            var expected = WithGateOnly(lines);
            if (migrated != expected)
                throw new Exception($"Only the pickup gate should have been added. Expected:\n{expected}\nGot:\n{migrated}");
        }

        private static void BoundKeyWithPositiveIntervalGetsZero()
        {
            var migrated = Migrate(PickupLines("E + LeftShift", "1.5"));

            AssertInterval(migrated, "0");
            AssertContains(migrated, "pickup_all_nearby_key = E + LeftShift");
            AssertPickupGate(migrated);
            AssertMarker(migrated);

            // The value is set in place, on the line it was on.
            var lines = PickupLines("LeftAlt", "0.5");
            var expected = lines.ToList();
            expected[expected.IndexOf("automatic_pickup_interval = 0.5")] = "automatic_pickup_interval = 0";
            expected.Add(PickupGateKey + " = " + PickupGateValue);
            var text = string.Join(Environment.NewLine, expected) + Environment.NewLine;
            var result = Migrate(lines);
            if (result != text)
                throw new Exception($"Only the interval and the gate should have changed. Expected:\n{text}\nGot:\n{result}");
        }

        private static void BoundKeyWithoutIntervalGetsZeroAdded()
        {
            // A missing interval takes its default, 0.5, which would run periodic pickup.
            var migrated = Migrate(PickupLines("E + LeftShift", null));

            AssertInterval(migrated, "0");
            AssertEqual(1, CountOccurrences(migrated, "[automatic_pickup]"), "The pickup section should appear once.");
            AssertPickupGate(migrated);
        }

        private static void BoundKeyWithUnparsableIntervalGetsZero()
        {
            // BepInEx ignores a value float.Parse cannot read and keeps the default, 0.5.
            AssertInterval(Migrate(PickupLines("E", "fast")), "0");
            AssertInterval(Migrate(PickupLines("E", "")), "0");
            // An invariant-culture decimal point only: "1,5" reads as 15, positive either way.
            AssertInterval(Migrate(PickupLines("E", "1,5")), "0");
        }

        private static void NonPositiveIntervalIsKept()
        {
            // 0 already runs no periodic pickup, and the accepted range clamps a negative value to 0.
            foreach (var interval in new[] { "0", "0.0", "-1", "NaN" })
            {
                var migrated = Migrate(PickupLines("E + LeftShift", interval));
                AssertInterval(migrated, interval);
                AssertPickupGate(migrated);
            }
        }

        private static void UnboundKeyKeepsTheInterval()
        {
            // BepInEx writes an unbound shortcut as an empty value; "None" is the other form it reads so.
            AssertOnlyTheGateWasAdded(PickupLines("", "1.5"));
            AssertOnlyTheGateWasAdded(PickupLines("None", "1.5"));
            AssertOnlyTheGateWasAdded(PickupLines("None", null));
        }

        private static void UnreadableKeyCountsAsUnbound()
        {
            // KeyboardShortcut.Deserialize parses every token with Enum.Parse, case-sensitively,
            // and reads the whole value as unbound when any of them fails. A main key of None is
            // unbound too, whatever follows it.
            foreach (var key in new[] { "E + NotAKey", "e", "leftshift + E", "\"E\"", "None + E", "+ ; |", "E +" + " Left Shift" })
                AssertOnlyTheGateWasAdded(PickupLines(key, "1.5"));
        }

        private static void ReadableKeyFormsCountAsBound()
        {
            // Every separator BepInEx splits on, empty tokens between them, a repeated key, a
            // modifier of None, and a number (Enum.Parse reads one; 101 is E) all read as bound.
            foreach (var key in new[] { "E", "E + LeftShift", "LeftShift,E", "E;LeftShift", "E|LeftShift", "E  ++  LeftShift", "E + E", "E + None", "101", "F + LeftControl + LeftAlt" })
            {
                var migrated = Migrate(PickupLines(key, "2"));
                AssertInterval(migrated, "0");
                AssertContains(migrated, "pickup_all_nearby_key = " + key);
            }
        }

        private static void MissingKeyEntryIsLeftToTheDefault()
        {
            // The key entry is not written: BepInEx binds its default, Left Shift + E, and
            // periodic pickup keeps running beside it at the stored interval.
            var lines = PickupLines(null, "1.5");
            AssertOnlyTheGateWasAdded(lines);
            AssertDoesNotContain(Migrate(lines), "pickup_all_nearby_key");
        }

        private static void RevisionTwoConfigWithoutTheGateRuns()
        {
            var migrated = Migrate(PickupLines("LeftAlt", "1.5"));
            AssertInterval(migrated, "0");
            AssertPickupGate(migrated);
            AssertMarker(migrated);
        }

        private static void GateAloneIsWritten()
        {
            // Nothing in the Pickup section changes, yet the file is written, so that the next
            // start knows the pass has run.
            var lines = new[]
            {
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_door]",
                "module = Disabled",
                "[hidden]",
                "defaults_revision = 2"
            };
            AssertOnlyTheGateWasAdded(lines);
        }

        private static void RevisionTwoStepsDoNotRerun()
        {
            // A list that still holds the default revision two appended to, a list with the flax
            // entry's old spelling, and module entries missing: revision two would change all of
            // them, and the marker says it has run. Only the pickup pass may act.
            var lines = new[]
            {
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_door]",
                "allow_automatic_door = WoodDoor, WoodGate, IronGate, DarkwoodGate, PieceHexagonalDoor, AshwoodDoor, FlametalGate, DvergrDoor",
                "[automatic_farming]",
                "allow_farming_crop = Flex",
                "[automatic_pickup]",
                "automatic_pickup_interval = 1",
                "pickup_all_nearby_key = E + LeftShift",
                "[hidden]",
                "defaults_revision = 2"
            };

            var migrated = Migrate(lines);
            var expected = lines.ToList();
            expected[expected.IndexOf("automatic_pickup_interval = 1")] = "automatic_pickup_interval = 0";
            expected.Add(PickupGateKey + " = " + PickupGateValue);
            var text = string.Join(Environment.NewLine, expected) + Environment.NewLine;
            if (migrated != text)
                throw new Exception($"Only the pickup pass should have changed the file. Expected:\n{text}\nGot:\n{migrated}");
        }

        private static void ConfigWithoutMarkersGetsBoth()
        {
            // A 1.7.0 config: revision two runs, and the pickup pass after it. The gate goes
            // right after the defaults marker.
            var migrated = Migrate(
                "## Settings file was created by plugin Automatics v1.7.0",
                "## Plugin GUID: net.eidee.valheim.automatics",
                "[automatic_pickup]",
                "module = Enabled",
                "automatic_pickup_interval = 0.5",
                "pickup_all_nearby_key = LeftAlt");

            AssertInterval(migrated, "0");
            AssertModule(migrated, "automatic_pickup", "Enabled");
            AssertMarker(migrated);
            AssertPickupGate(migrated);
            var entries = migrated
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0 && !x.StartsWith("#", StringComparison.Ordinal) &&
                            !x.StartsWith("[", StringComparison.Ordinal))
                .ToList();
            if (entries[entries.Count - 2] != "defaults_revision = 2" ||
                entries[entries.Count - 1] != PickupGateKey + " = " + PickupGateValue)
                throw new Exception($"The defaults marker and then the pickup gate should be the last entries in:\n{migrated}");
        }

        private static void NewInstallGetsThePickupGate()
        {
            // A new install has no stored key, so the default Left Shift + E binds, and periodic
            // pickup runs beside it; the gate keeps the next start from setting the interval to 0.
            var created = MigrateWithoutAConfigFile();
            if (created == null)
                throw new Exception("A new install must get a config file.");
            AssertMarker(created);
            AssertPickupGate(created);
            AssertDoesNotContain(created, "automatic_pickup");
        }

        private static void ReadOnlyConfigRecordsNoGateAndRetries()
        {
            var directory = NewTestDirectory();
            var path = Path.Combine(directory, "Automatics.cfg");
            File.WriteAllLines(path, PickupLines("E + LeftShift", "1.5"));
            var before = File.ReadAllBytes(path);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            try
            {
                var warnings = Automatics.Logger.Warnings.Count;
                ConfigMigration.Migration(new ConfigFile(path));
                if (!File.ReadAllBytes(path).SequenceEqual(before))
                    throw new Exception("A read-only config must be left byte for byte.");
                AssertEqual(warnings + 1, Automatics.Logger.Warnings.Count, "A failed write must warn once.");

                File.SetAttributes(path, FileAttributes.Normal);
                ConfigMigration.Migration(new ConfigFile(path));
                var retried = ReadWithoutSettingsRevision(path);
                AssertInterval(retried, "0");
                AssertPickupGate(retried);
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(directory, recursive: true);
            }
        }

        private static void GateAtZeroRuns()
        {
            // After a write that failed, BepInEx saves the bound default, 0, into the file. The
            // pass still has to run, and the gate is updated where it is.
            var lines = PickupLines("E + LeftShift", "1.5", "0");
            var migrated = Migrate(lines);
            AssertInterval(migrated, "0");
            AssertPickupGate(migrated);
            AssertEqual(1, CountOccurrences(migrated, PickupGateKey), "The gate should be updated in place.");
        }

        private static void UnrecognizedGateIsLeftAlone()
        {
            foreach (var gate in new[] { "yes", "-1" })
            {
                var lines = PickupLines("E + LeftShift", "1.5", gate);
                var warnings = Automatics.Logger.Warnings.Count;
                var migrated = Migrate(lines);
                var expected = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                if (migrated != expected)
                    throw new Exception($"A config with an unrecognized pickup gate must not be rewritten. Got:\n{migrated}");
                AssertEqual(warnings + 1, Automatics.Logger.Warnings.Count, "An unrecognized gate must warn once.");
            }

            // A gate from a later version is done as well.
            var later = PickupLines("E + LeftShift", "1.5", "2");
            if (Migrate(later) != string.Join(Environment.NewLine, later) + Environment.NewLine)
                throw new Exception("A config with a later pickup gate must not be rewritten.");
        }

        private static void PickupMigrationIsIdempotent()
        {
            var (first, second) = MigrateTwice(PickupLines("E + LeftShift", "1.5"));
            AssertInterval(first, "0");
            AssertPickupGate(first);
            if (second != first)
                throw new Exception($"A second migration must change nothing. First:\n{first}\nSecond:\n{second}");

            var current = PickupLines("E + LeftShift", "0", "1");
            if (Migrate(current) != string.Join(Environment.NewLine, current) + Environment.NewLine)
                throw new Exception("A config carrying both markers must be left byte for byte.");
        }

        private static void PositiveIntervalSetLaterSurvives()
        {
            // The user turns periodic pickup back on beside the key; the next start must not
            // take it away again.
            var directory = NewTestDirectory();
            var path = Path.Combine(directory, "Automatics.cfg");
            File.WriteAllLines(path, PickupLines("E + LeftShift", "1.5"));

            try
            {
                ConfigMigration.Migration(new ConfigFile(path));
                var first = ReadWithoutSettingsRevision(path);
                AssertInterval(first, "0");

                var edited = first.Replace("automatic_pickup_interval = 0", "automatic_pickup_interval = 1.5");
                File.WriteAllText(path, edited);
                ConfigMigration.Migration(new ConfigFile(path));
                var second = ReadWithoutSettingsRevision(path);
                if (second != edited)
                    throw new Exception($"The interval set after the pass must survive. Expected:\n{edited}\nGot:\n{second}");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
