namespace Automatics
{
    // Settings pass 5: Automatic Farming's container search range is split from its farming
    // range. A stored farming range is copied into the new entry once.
    internal static partial class Program
    {
        private static void RunFarmingRangeMigrationTests()
        {
            Run("a stored farming range is copied into the container search range", FarmingRangeIsCopied);
            Run("a config without a farming range gets no container search range", NoFarmingRangeNoCopy);
            Run("a container search range stored before the pass is overwritten", StoredContainerRangeIsOverwritten);
            Run("a file at settings revision 4 runs only pass 5", RevisionFourRunsOnlyPassFive);
        }

        private static string[] FarmingRangeLines(string range, string containerRange = null, string revision = "0")
        {
            var lines = new System.Collections.Generic.List<string>
            {
                "## Settings file was created by plugin Automatics v1.7.0", "", "[automatic_farming]", "module = Enabled"
            };
            if (range != null) lines.Add("farming_range = " + range);
            if (containerRange != null) lines.Add("container_search_range = " + containerRange);
            lines.AddRange(new[] { "", "[hidden]", "defaults_revision = 2", "pickup_key_decoupled = 1",
                ConfigMigration.SettingsRevisionKey + " = " + revision });
            return lines.ToArray();
        }

        private static void FarmingRangeIsCopied()
        {
            var migrated = MigrateRaw(FarmingRangeLines("20"));
            AssertSingle(migrated, "automatic_farming", "container_search_range", "20");
            AssertSingle(migrated, "automatic_farming", "farming_range", "20");
            AssertSettingsRevision(migrated);
        }

        private static void NoFarmingRangeNoCopy()
        {
            var migrated = MigrateRaw(FarmingRangeLines(null));
            AssertDoesNotContain(migrated, "container_search_range");
        }

        // BepInEx writes the default after a failed update; the retry at the next start copies the
        // farming range over it.
        private static void StoredContainerRangeIsOverwritten()
        {
            var migrated = MigrateRaw(FarmingRangeLines("20", "8"));
            AssertSingle(migrated, "automatic_farming", "container_search_range", "20");
            AssertSettingsRevision(migrated);
        }

        private static void RevisionFourRunsOnlyPassFive()
        {
            // Pass 4 is done, so a bound farming key keeps its positive interval.
            var lines = new System.Collections.Generic.List<string>(FarmingRangeLines("12", revision: "4"));
            lines.Insert(4, "farming_key = F");
            lines.Insert(5, "farming_interval = 1.5");
            var migrated = MigrateRaw(lines.ToArray());
            AssertSingle(migrated, "automatic_farming", "container_search_range", "12");
            AssertSingle(migrated, "automatic_farming", "farming_interval", "1.5");
            AssertSettingsRevision(migrated);
        }
    }
}
