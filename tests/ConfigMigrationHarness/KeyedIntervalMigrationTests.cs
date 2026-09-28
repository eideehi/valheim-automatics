using System.Linq;

namespace Automatics
{
    // Settings pass 4: a bound shortcut no longer stops the periodic runs of automatic mining,
    // farming and static mapping. A config whose key is bound gets the interval set to 0 once, so
    // it keeps running on the key only.
    internal static partial class Program
    {
        private static readonly (string Section, string Key, string Interval)[] KeyedIntervalFixtures =
        {
            ("automatic_mining", "mining_key", "mining_interval"),
            ("automatic_farming", "farming_key", "farming_interval"),
            ("automatic_mapping", "static_object_mapping_key", "static_object_mapping_interval")
        };

        private static void RunKeyedIntervalMigrationTests()
        {
            Run("a bound key sets its module's interval to 0", BoundKeySetsTheIntervalToZero);
            Run("a bound key without an interval line adds an interval of 0", BoundKeyWithoutIntervalAddsZero);
            Run("an unbound or unreadable key leaves the interval", UnboundKeyLeavesTheInterval);
            Run("a file at settings revision 3 runs only pass 4", RevisionThreeRunsOnlyPassFour);
            Run("a file at settings revision 4 keeps a positive interval under a bound key", RevisionFourKeepsTheInterval);
            Run("an interval already at 0 is left as written", ZeroIntervalIsLeftAsWritten);
            Run("only the modules with a bound key change", OnlyModulesWithABoundKeyChange);
        }

        private static void RevisionFourKeepsTheInterval()
        {
            var lines = KeyedIntervalLines("F", "1.5", ConfigMigration.SettingsRevision.ToString());
            var migrated = MigrateRaw(lines);
            var text = string.Join(System.Environment.NewLine, lines) + System.Environment.NewLine;
            if (migrated != text)
                throw new System.Exception($"A file past pass 4 must not change. Got:\n{migrated}");
        }

        private static void ZeroIntervalIsLeftAsWritten()
        {
            foreach (var zero in new[] { "0", "0.0" })
            {
                var migrated = MigrateRaw(KeyedIntervalLines("F", zero));
                foreach (var (section, _, interval) in KeyedIntervalFixtures)
                    AssertSingle(migrated, section, interval, zero);
            }
        }

        private static void OnlyModulesWithABoundKeyChange()
        {
            var lines = KeyedIntervalLines(null, "2").ToList();
            var farming = lines.IndexOf("[automatic_farming]");
            lines.Insert(farming + 1, "farming_key = F");
            var migrated = MigrateRaw(lines.ToArray());
            AssertSingle(migrated, "automatic_farming", "farming_interval", "0");
            AssertSingle(migrated, "automatic_mining", "mining_interval", "2");
            AssertSingle(migrated, "automatic_mapping", "static_object_mapping_interval", "2");
        }

        private static string[] KeyedIntervalLines(string key, string interval, string revision = "0")
        {
            var lines = new[] { "## Settings file was created by plugin Automatics v1.7.0", "" }.ToList();
            foreach (var (section, keyKey, intervalKey) in KeyedIntervalFixtures)
            {
                lines.Add("[" + section + "]");
                lines.Add("module = Enabled");
                if (interval != null) lines.Add(intervalKey + " = " + interval);
                if (key != null) lines.Add(keyKey + " = " + key);
                lines.Add("");
            }

            lines.AddRange(new[] { "[hidden]", "defaults_revision = 2", "pickup_key_decoupled = 1",
                ConfigMigration.SettingsRevisionKey + " = " + revision });
            return lines.ToArray();
        }

        private static void BoundKeySetsTheIntervalToZero()
        {
            var migrated = MigrateRaw(KeyedIntervalLines("F + LeftShift", "1.5"));
            foreach (var (section, _, interval) in KeyedIntervalFixtures)
                AssertSingle(migrated, section, interval, "0");
            AssertSettingsRevision(migrated);
        }

        private static void BoundKeyWithoutIntervalAddsZero()
        {
            var migrated = MigrateRaw(KeyedIntervalLines("F", null));
            foreach (var (section, _, interval) in KeyedIntervalFixtures)
                AssertSingle(migrated, section, interval, "0");
        }

        private static void UnboundKeyLeavesTheInterval()
        {
            foreach (var key in new[] { "None", "", "NotAKey" })
            {
                var migrated = MigrateRaw(KeyedIntervalLines(key, "2"));
                foreach (var (section, _, interval) in KeyedIntervalFixtures)
                    AssertSingle(migrated, section, interval, "2");
            }

            var missingKey = MigrateRaw(KeyedIntervalLines(null, "2"));
            foreach (var (section, _, interval) in KeyedIntervalFixtures)
                AssertSingle(missingKey, section, interval, "2");
        }

        private static void RevisionThreeRunsOnlyPassFour()
        {
            // Pass 3 is done, so a single-feature switch left in this file stays.
            var lines = KeyedIntervalLines("F", "1.5", "3").ToList();
            lines.InsertRange(2, new[] { "[automatic_door]", "module = Enabled", "enable_automatic_door = false", "" });
            var migrated = MigrateRaw(lines.ToArray());
            AssertContains(migrated, "enable_automatic_door = false");
            AssertModule(migrated, "automatic_door", "Enabled");
            foreach (var (section, _, interval) in KeyedIntervalFixtures)
                AssertSingle(migrated, section, interval, "0");
            AssertSettingsRevision(migrated);
        }
    }
}
