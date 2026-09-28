using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using ModUtils;
using UnityEngine;

namespace Automatics
{
    internal static class ConfigMigration
    {
        private static readonly Regex VersionPattern;
        private static readonly char[] KeyValueSeparator;
        private static readonly Dictionary<string, List<Config>> ConfigCache;

        static ConfigMigration()
        {
            VersionPattern = new Regex(@"v(\d+)\.(\d+)\.(\d+)$", RegexOptions.Compiled);
            KeyValueSeparator = new[] { '=' };
            ConfigCache = new Dictionary<string, List<Config>>();
        }

        private class Config
        {
            public int Offset;
            public string Category;
            public string Key;
            public string Value;
        }

        private delegate bool Operation(string category, List<string> lines, int begin, int end);

        private static Operation RenameCategory(string newCategory)
        {
            return (category, lines, begin, end) =>
            {
                var original = lines[begin];

                lines[begin] = newCategory;

                Automatics.Logger.Message($"Rename category: {original} => {newCategory}");
                return true;
            };
        }

        private static Operation RemoveConfig(string key)
        {
            return (category, lines, begin, end) =>
            {
                var configs = FindConfig(category, key, lines, begin, end);
                foreach (var config in configs)
                {
                    var original = config.Key;

                    config.Key = "";
                    UpdateConfig(config, lines);

                    Automatics.Logger.Message($"Remove config: {category} {original}");
                }
                return true;
            };
        }

        private static Operation RenameConfig(string key, string newKey)
        {
            return (category, lines, begin, end) =>
            {
                var configs = FindConfig(category, key, lines, begin, end);
                foreach (var config in configs)
                {
                    var original = config.Key;

                    var matcher = GetMatcher(key);
                    config.Key = matcher.IsRegex
                        ? Regex.Replace(config.Key, matcher.Pattern, newKey, RegexOptions.IgnoreCase)
                        : newKey;
                    UpdateConfig(config, lines);

                    Automatics.Logger.Message($"Rename config: {category} {original} => {config.Key}");
                }
                return true;
            };
        }

        private static Operation ReplaceValue(string key, string value, string newValue)
        {
            return (category, lines, begin, end) =>
            {
                var configs = FindConfig(category, key, lines, begin, end);
                foreach (var config in configs.Where(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase)))
                {
                    var original = config.Value;

                    config.Value = newValue;
                    UpdateConfig(config, lines);

                    Automatics.Logger.Message($"Replace value: {category} {key} {original} => {newValue}");
                }
                return true;
            };
        }

        private static Operation AppendValues(string key, string[] valuesToAppend)
        {
            return (category, lines, begin, end) =>
            {
                var configs = FindConfig(category, key, lines, begin, end).ToList();
                if (!configs.Any()) return false;

                foreach (var config in configs)
                {
                    var original = config.Value ?? "";

                    var existing = string.IsNullOrWhiteSpace(original)
                        ? new List<string>()
                        : Csv.ParseLine(original, true)
                            .Where(x => !string.IsNullOrEmpty(x))
                            .ToList();

                    var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);
                    var added = new List<string>();

                    foreach (var candidate in valuesToAppend)
                    {
                        if (string.IsNullOrWhiteSpace(candidate)) continue;
                        var trimmed = candidate.Trim();
                        if (existingSet.Add(trimmed))
                        {
                            existing.Add(trimmed);
                            added.Add(trimmed);
                        }
                    }

                    if (added.Count == 0) continue;

                    config.Value = string.Join(", ", existing.Select(Csv.Escape));
                    UpdateConfig(config, lines);

                    Automatics.Logger.Message(
                        $"Append values: {category} {key} += [{string.Join(", ", added)}]");
                }

                return true;
            };
        }

        private static IEnumerable<Config> FindConfig(string category, string key, List<string> lines, int begin = 0, int end = int.MaxValue)
        {
            var matcher = GetMatcher(key);
            if (ConfigCache.TryGetValue(category, out var configs))
                return configs.Where(x => x.Category == category && IsMatch(x.Key, matcher));

            end = Mathf.Min(end, lines.Count);
            begin = Mathf.Clamp(begin, 0, end);

            configs = new List<Config>(0);
            var currentCategory = "";
            for (var i = begin; i < end; i++)
            {
                var line = lines[i];
                if (line.StartsWith("#")) continue;

                if (Regex.IsMatch(line, @"^\[[\w\d_]+\]$"))
                {
                    // Key the block under the caller-supplied category, not the header line. A prior
                    // RenameCategory in the same block mutates lines[begin] in place, so reading the
                    // line here would cache entries under the new name and the lookup below (by the
                    // original category) would miss, silently dropping every later RenameConfig.
                    currentCategory = category;
                    if (!ConfigCache.TryGetValue(currentCategory, out configs))
                    {
                        configs = new List<Config>();
                        ConfigCache[currentCategory] = configs;
                    }
                    continue;
                }

                if (configs.Any(x => x.Offset == i)) continue;

                var split = line.Split(KeyValueSeparator, 2, StringSplitOptions.None);
                if (split.Length != 2) continue;

                var config = new Config
                {
                    Offset = i,
                    Category = currentCategory,
                    Key = split[0].Trim(),
                    Value = split[1].Trim()
                };
                configs.Add(config);
            }

            if (ConfigCache.TryGetValue(category, out configs))
                return configs.Where(x => x.Category == category && IsMatch(x.Key, matcher));

            Automatics.Logger.Warning($"No config matching condition exists: {category} / {key}");
            return Array.Empty<Config>();
        }

        private static void UpdateConfig(Config config, List<string> lines)
        {
            if (config.Offset < 0 || config.Offset >= lines.Count) return;
            if (string.IsNullOrEmpty(config.Key))
                lines[config.Offset] = "";
            else
                lines[config.Offset] = $"{config.Key} = {config.Value}";
        }

        private static (bool IsRegex, string Pattern) GetMatcher(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return (false, "");
            return pattern.StartsWith("r/") ? (true, pattern.Substring(2)) : (false, pattern);
        }

        private static bool IsMatch(string value, (bool IsRegex, string Pattern) matcher)
        {
            return matcher.IsRegex
                ? Regex.IsMatch(value, matcher.Pattern, RegexOptions.IgnoreCase)
                : !string.IsNullOrEmpty(matcher.Pattern) && string.Equals(value, matcher.Pattern, StringComparison.OrdinalIgnoreCase);
        }

        // The sections of the nine modules kept running across the update. Each binds a `module`
        // entry whose default is Disabled. Automatic Supply is left out on purpose: it did not
        // exist when those files were written, so it stays off for every config, upgraders included.
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

        private const string ModuleKey = "module";

        // A hidden entry recording the revision of the defaults a config file has been brought to.
        // Revision 1 is the one where every module's `module` entry defaults to Disabled. The
        // marker, not the version in the file header, tells a file written before that change
        // from one written after it, so the check does not depend on what the next version is
        // called.
        internal const string DefaultsRevisionSection = "hidden";
        internal const string DefaultsRevisionKey = "defaults_revision";
        internal const int DefaultsRevision = 2;

        // A hidden entry recording that the pass for the Pickup All Nearby key has run. Binding
        // the key used to turn periodic pickup off; it no longer does, so that pass sets the
        // interval of a config whose key is bound to 0 once. It has a marker of its own so that
        // it runs on a file the defaults revision is done with, without running that revision
        // again, and never again after it has run, whatever the user sets later.
        internal const string PickupKeyDecoupledKey = "pickup_key_decoupled";
        internal const int PickupKeyDecoupled = 1;

        // A hidden entry recording which of the numbered settings passes (SettingsPasses) a config
        // file has been through. A pass runs once, on a file whose recorded revision is below its
        // number, so a pass added later still runs on a file the earlier passes are done with.
        internal const string SettingsRevisionKey = "settings_revision";
        internal const int SettingsRevision = 5;

        private const string ProcessingSection = "automatic_processing";

        // The Automatic Processing pieces whose only process is Refuel: the piece's key part and
        // its flag name in the fires list. They used to have five settings each and now share
        // one group.
        private static readonly (string Piece, string Flag)[] FirePieces =
        {
            ("piece_bonfire", "Bonfire"),
            ("piece_firepit", "Campfire"),
            ("piece_brazierceiling01", "HangingBrazier"),
            ("piece_hearth", "Hearth"),
            ("piece_jackoturnip", "JackOTurnip"),
            ("piece_sconce", "Sconce"),
            ("piece_groundtorchblue", "StandingBlueBurningIronTorch"),
            ("piece_brazierfloor01", "StandingBrazier"),
            ("piece_groundtorchgreen", "StandingGreenBurningIronTorch"),
            ("piece_groundtorch", "StandingIronTorch"),
            ("piece_groundtorchwood", "StandingWoodTorch")
        };

        private const string PickupSection = "automatic_pickup";
        private const string PickupKeyKey = "pickup_all_nearby_key";
        private const string PickupIntervalKey = "automatic_pickup_interval";

        // The separators BepInEx's KeyboardShortcut.Deserialize splits a stored shortcut on.
        private static readonly char[] ShortcutSeparators = { ' ', '+', ',', ';', '|' };

        // Has to run before anything binds an entry: the first bind writes the file, and from then
        // on a new install can no longer be told apart from an existing one.
        public static void Migration(ConfigFile config)
        {
            var path = config.ConfigFilePath;
            if (!File.Exists(path))
            {
                CreateForNewInstall(config, path);
                return;
            }

            ConfigText file;
            try
            {
                file = ConfigText.Read(path);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                // The version passes share this read, so they wait for the next start too.
                Automatics.Logger.Warning(
                    $"Could not read the config file {path} ({e.Message}). This session, every " +
                    "module whose Disable Module entry the file lacks uses the new default, Disabled. " +
                    "The next start tries again.");
                return;
            }

            var recognizedRevision = TryReadDefaultsRevision(file.Lines, out var revision);

            var versionsChanged = MigrateVersions(file.Lines);
            var keptRunning = KeepModulesRunning(file.Lines);
            var defaultsChanged = recognizedRevision && MigrateObjectDefaults(file.Lines, revision);
            var pickupChanged = DecouplePickupKey(file.Lines);
            var settingsChanged = MigrateSettings(file.Lines);
            if (!versionsChanged && !defaultsChanged && !pickupChanged && !settingsChanged)
                return;

            try
            {
                // A read-only file is left as the user set it.
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                    throw new UnauthorizedAccessException($"{path} is read-only.");

                Replace(path, file.Render(), file.Encoding);
            }
            catch (Exception e) when (IsIoFailure(e) && !versionsChanged)
            {
                // Only defaults were at stake; the unchanged marker lets the next start retry.
                // While the version passes have changes, a failure still stops the start, as it
                // always has: once BepInEx rewrote the header, they would not run again.
                Automatics.Logger.Warning(
                    $"Could not update the config file {path} ({e.Message}). " +
                    DescribeSessionDefaults(keptRunning) +
                    (defaultsChanged ? " Object lists keep their stored values this session." : "") +
                    (pickupChanged
                        ? " Automatic Pickup keeps its stored interval this session, so periodic pickup may run beside the Pickup All Nearby key."
                        : "") +
                    (settingsChanged
                        ? " Settings that this version moves or merges take their defaults this session, and a module whose own on/off switch was off runs until the file can be updated."
                        : "") +
                    " The next start tries again.");
                return;
            }

            ReloadOrFail(config);
        }

        // A new install. Every module takes its default, Disabled, when it is bound. Only the
        // markers are written, so that the next start does not take this file for one written
        // before the change, nor set the interval of the default Pickup All Nearby key to 0.
        // Should that fail, the defaults apply all the same.
        private static void CreateForNewInstall(ConfigFile config, string path)
        {
            Automatics.Logger.Message("New config file: every module starts disabled.");
            var lines = new List<string>();
            AppendSection(lines, DefaultsRevisionSection, DefaultsRevisionKey, FormatRevision());
            lines.Add($"{PickupKeyDecoupledKey} = {FormatPickupKeyDecoupled()}");
            lines.Add($"{SettingsRevisionKey} = {FormatSettingsRevision()}");
            var created = new ConfigText(lines, new UTF8Encoding(false), Environment.NewLine, true);

            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                Replace(path, created.Render(), created.Encoding);

                // BepInEx has nothing loaded yet that a failed reload could lose.
                config.Reload();
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                Automatics.Logger.Warning(
                    $"Could not create the config file {path} ({e.Message}). Every module starts " +
                    "disabled all the same.");
            }
        }

        // The file on disk is already the migrated one. BepInEx's Reload empties its copy of the file
        // before it reads the file again, so going on after a failed Reload would let the first bind
        // save defaults over every setting. It is tried a few times; if it still fails, the failure
        // stops the start and leaves the file, as written, for the next start to read.
        private static void ReloadOrFail(ConfigFile config)
        {
            for (var attempt = 1;; attempt++)
            {
                try
                {
                    config.Reload();
                    return;
                }
                catch (Exception e) when (IsIoFailure(e) && attempt < 3)
                {
                    Automatics.Logger.Warning($"Could not reload the config file ({e.Message}); trying again.");
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

        private static bool IsIoFailure(Exception e)
        {
            return e is IOException || e is UnauthorizedAccessException ||
                   e is System.Security.SecurityException;
        }

        private static string DescribeSessionDefaults(List<string> sections)
        {
            return sections == null || sections.Count == 0
                ? "Every module keeps the value it has in the file."
                : "This session, these modules use the new default, Disabled: " +
                  string.Join(", ", sections) + ".";
        }

        private static bool MigrateVersions(List<string> lines)
        {
            if (!lines.Any())
                return false;

            if (!TryParseVersion(lines, out var version))
            {
                Automatics.Logger.Warning(
                    "Could not determine the config version from its header; skipping the version " +
                    "migrations to avoid re-running them on an already-current file.");
                return false;
            }

            var dirty = false;

            var migrateVersion = new Version(1, 3, 0);
            if (version < migrateVersion)
            {
                Automatics.Logger.Message($"Migrating config from {version} to {migrateVersion}");
                dirty = true;
                MigrationFor130(lines);
            }

            migrateVersion = new Version(1, 4, 0);
            if (version < migrateVersion)
            {
                Automatics.Logger.Message($"Migrating config from {version} to {migrateVersion}");
                dirty = true;
                MigrationFor140(lines);
            }

            migrateVersion = new Version(1, 4, 5);
            if (version < migrateVersion)
            {
                Automatics.Logger.Message($"Migrating config from {version} to {migrateVersion}");
                dirty = true;
                MigrationFor145(lines);
            }

            migrateVersion = new Version(1, 6, 0);
            if (version < migrateVersion)
            {
                Automatics.Logger.Message($"Migrating config from {version} to {migrateVersion}");
                dirty = true;
                MigrationFor160(lines);
            }

            return dirty;
        }

        // A file without the marker was written before the Disabled default, when a `module` entry
        // that BepInEx could not use - missing, or holding a value it cannot read - meant Enabled.
        // Each such entry is written as Enabled, so that updating turns no module off. A value
        // BepInEx reads is the user's and is kept, Disabled included. A file with the marker is left
        // alone: an entry missing from it was removed by hand and takes its default. Returns the
        // sections written as Enabled, or null for a file left alone.
        private static List<string> KeepModulesRunning(List<string> lines)
        {
            var entries = ReadEntries(lines);
            if (entries.ContainsKey((DefaultsRevisionSection, DefaultsRevisionKey)))
                return null;

            var keptRunning = new List<string>();

            // Unreadable values are rewritten in place before any line is added, because adding
            // lines moves the positions ReadEntries recorded.
            foreach (var section in ModuleSections)
            {
                if (!entries.TryGetValue((section, ModuleKey), out var entry) || IsReadableModuleValue(entry.Value))
                    continue;

                lines[entry.Index] = $"{ModuleKey} = Enabled";
                keptRunning.Add(section);
                Automatics.Logger.Message(
                    $"Keep the module running: [{section}] {ModuleKey} = {entry.Value} => Enabled");
            }

            foreach (var section in ModuleSections.Where(x => !entries.ContainsKey((x, ModuleKey))))
            {
                AddEntry(lines, section, ModuleKey, "Enabled");
                keptRunning.Add(section);
                Automatics.Logger.Message($"Keep the module running: [{section}] {ModuleKey} = Enabled");
            }

            return keptRunning;
        }

        // Revision 2 checks values after the current version passes. Bare pre-1.6 defaults
        // cannot reach this check unedited. Released 1.6.0 instead preserved separator spaces
        // and quoted them (fixed by eff53b0). Freeze those simulated outputs verbatim, so a
        // clean edited list cannot match them. Matching uses raw parsed elements; afterwards,
        // all ten lists touched by released 1.6.0 are repaired, edited or not.
        // Reject older/rarer sets within one or two ordinal symmetric-difference elements
        // of another accepted set, retaining fresh defaults to protect user edits.
        // The released 1.6.0 door pass lacked PieceHexagonalDoor; 1.7.0 and the current pass add it.
        // 1.6.0/1.7.0 and 5d5cc7f^ fresh defaults (module Config.cs and object JSON).
        // The 1.4.x–1.5.1 defaults plus current MigrationFor160 yield this set too.
        private const string DoorDefaults = "WoodDoor, WoodGate, IronGate, DarkwoodGate, PieceHexagonalDoor, AshwoodDoor, FlametalGate, DvergrDoor";

        // Released 1.6.0 AppendValues on the 1.4.1–1.5.1 door default serialized with ", ".
        private const string DoorReleased160Defaults = "WoodDoor, \" WoodGate\", \" IronGate\", \" DarkwoodGate\", \" PieceHexagonalDoor\", AshwoodDoor, FlametalGate, DvergrDoor";

        // Drop the pre-1.4 All clean set: fresh default plus WoodShutter (one edit).
        // Drop the 1.3.x/1.4.0 released-1.6.0 form: the retained corrupt set minus
        // " PieceHexagonalDoor" (one edit). Drop the corrupt pre-1.4 All form too:
        // it replaces " PieceHexagonalDoor" with " WoodShutter" (two edits).

        // 1.6.0/1.7.0 and 5d5cc7f^ fresh defaults (module Config.cs and object JSON).
        // The 1.4.x–1.5.1 defaults plus current MigrationFor160 yield this set too.
        private const string AnimalDefaults = "Boar, Piggy, Deer, Wolf, WolfCub, Lox, LoxCalf, Hen, Chicken, Hare, Bird, Fish, Asksvin, AsksvinHatchling";

        // Released 1.6.0 AppendValues on the 1.4.0–1.5.1 default serialized with ", ".
        // The pre-1.4 All replacement is identical.
        private const string AnimalReleased160Defaults = "Boar, \" Piggy\", \" Deer\", \" Wolf\", \" WolfCub\", \" Lox\", \" LoxCalf\", \" Hen\", \" Chicken\", \" Hare\", \" Bird\", \" Fish\", Asksvin, AsksvinHatchling";

        // 1.6.0/1.7.0 and 5d5cc7f^ fresh defaults (module Config.cs and object JSON).
        // The 1.4.x–1.5.1 defaults plus current MigrationFor160 yield this set too.
        private const string MonsterDefaults = "Greyling, Neck, Ghost, Greydwarf, GreydwarfBrute, GreydwarfShaman, RancidRemains, Skeleton, Troll, Abomination, Blob, Draugr, DraugrElite, Leech, Oozer, Surtling, Wraith, Drake, Fenring, StoneGolem, Deathsquito, Fuling, FulingBerserker, FulingShaman, Growth, Serpent, Bat, FenringCultist, Ulv, DvergrRogue, DvergrMage, Tick, Seeker, SeekerBrood, Gjall, SeekerSoldier, CharredMelee, CharredArcher, CharredMage, CharredTwitcher, CharredGrunt, Morgen, Volture, BonemawSerpent, FallenValkyrie, Fader, SkeletonFire, FrostBlob, Bear, Vile, LavaBlob";

        // Released 1.6.0 AppendValues on the 1.4.0–1.5.1 default serialized with ", ".
        // The pre-1.4 All replacement is identical.
        private const string MonsterReleased160Defaults = "Greyling, \" Neck\", \" Ghost\", \" Greydwarf\", \" GreydwarfBrute\", \" GreydwarfShaman\", \" RancidRemains\", \" Skeleton\", \" Troll\", \" Abomination\", \" Blob\", \" Draugr\", \" DraugrElite\", \" Leech\", \" Oozer\", \" Surtling\", \" Wraith\", \" Drake\", \" Fenring\", \" StoneGolem\", \" Deathsquito\", \" Fuling\", \" FulingBerserker\", \" FulingShaman\", \" Growth\", \" Serpent\", \" Bat\", \" FenringCultist\", \" Ulv\", \" DvergrRogue\", \" DvergrMage\", \" Tick\", \" Seeker\", \" SeekerBrood\", \" Gjall\", \" SeekerSoldier\", CharredMelee, CharredArcher, CharredMage, CharredTwitcher, CharredGrunt, Morgen, Volture, BonemawSerpent, FallenValkyrie, Fader, SkeletonFire, FrostBlob, Bear, Vile, LavaBlob";

        // 1.6.0/1.7.0 and 5d5cc7f^ fresh defaults (module Config.cs and object JSON).
        // The 1.4.x–1.5.1 defaults plus current MigrationFor160 yield this set too.
        private const string MappingMineralDefaults = "CopperDeposit, TinDeposit, MudPile, SilverVein, PetrifiedBone, SoftTissue, FlametalDeposit";

        // Released 1.6.0 AppendValues on the 1.4.0–1.5.1 default serialized with ", ".
        private const string MappingMineralReleased160Defaults = "CopperDeposit, \" TinDeposit\", \" MudPile\", \" SilverVein\", \" PetrifiedBone\", \" SoftTissue\", FlametalDeposit";

        // Drop both clean and released-1.6.0 forms of the 1.3.x default: each is
        // its retained counterpart minus PetrifiedBone and SoftTissue (two edits).

        // 1.0.0–1.2.1 bind Vein.Flag.All (-1L) ^ Obsidian (8), serialized as -9.
        // MigrationFor130/140 only rename its key; either 1.6.0 pass adds FlametalDeposit.
        // Retain this numeric form: it has no one- or two-element collision.
        private const string MappingMineralEnumDefaults = "-9, FlametalDeposit";

        // 1.6.0/1.7.0 and 5d5cc7f^ fresh defaults (module Config.cs and object JSON).
        // The 1.4.x–1.5.1 defaults plus current MigrationFor160 yield this set too.
        private const string DungeonDefaults = "BurialChambers, TrollCave, SunkenCrypts, MountainCave, InfestedMine, MorgenHole";

        // Released 1.6.0 AppendValues on the 1.4.0–1.5.1 default serialized with ", ".
        // The pre-1.4 All replacement is identical.
        private const string DungeonReleased160Defaults = "BurialChambers, \" TrollCave\", \" SunkenCrypts\", \" MountainCave\", \" InfestedMine\", MorgenHole";

        // 1.6.0/1.7.0 and 5d5cc7f^ fresh defaults (module Config.cs and object JSON).
        // The 1.4.x–1.5.1 defaults plus current MigrationFor160 yield this set too.
        private const string MiningMineralDefaults = "CopperDeposit, TinDeposit, MudPile, ObsidianDeposit, SilverVein, PetrifiedBone, SoftTissue, FlametalDeposit";

        // Released 1.6.0 AppendValues on the 1.4.0–1.5.1 default serialized with ", ".
        private const string MiningMineralReleased160Defaults = "CopperDeposit, \" TinDeposit\", \" MudPile\", \" ObsidianDeposit\", \" SilverVein\", \" PetrifiedBone\", \" SoftTissue\", FlametalDeposit";

        // 1.7.0 first binds this list; its object JSON default equals 5d5cc7f^.
        private const string CropDefaults = "Dandelion, Mushroom, Raspberries, Blueberries, Carrot, CarrotSeeds, YellowMushroom, Thistle, Turnip, TurnipSeeds, Onion, OnionSeeds, Barley, Cloudberries, Flex, JotunPuffs, Magecap, Fiddlehead, SmokePuff, Vineberry, VineberrySeeds";

        // 1.6.0/1.7.0 and 5d5cc7f^ fresh defaults (module Config.cs and object JSON).
        // The 1.4.x–1.5.1 defaults plus current MigrationFor160 yield this set too.
        private const string ProcessingContainerDefaults = "PieceChestWood, PieceChest, PieceChestBlackmetal, PieceChestCharred";

        // Released 1.6.0 AppendValues on the 1.4.1–1.5.1 container default serialized with ", ".
        private const string ProcessingContainerReleased160Defaults = "PieceChestWood, \" PieceChest\", \" PieceChestBlackmetal\", PieceChestCharred";

        // 1.7.0 first binds this list; its object JSON default equals 5d5cc7f^.
        private const string StorageContainerDefaults = "PieceChestWood, PieceChest, PieceChestBlackmetal, PieceChestCharred";

        private sealed class ObjectListRevision
        {
            public readonly string Section;
            public readonly string Key;
            public readonly string[] Additions;
            public readonly HashSet<string>[] Defaults;

            public ObjectListRevision(string section, string key, string additions, params string[] defaults)
            {
                Section = section;
                Key = key;
                Additions = ParseObjectList(additions).ToArray();
                Defaults = defaults.Select(x => new HashSet<string>(ParseObjectList(x), StringComparer.Ordinal)).ToArray();
            }
        }

        private static readonly ObjectListRevision[] ObjectListsRevision2 =
        {
            new ObjectListRevision("automatic_door", "allow_automatic_door",
                "TimberwoodGate, RoundpoleGate, Drawbridge",
                DoorDefaults, DoorReleased160Defaults),
            new ObjectListRevision("automatic_mapping", "allow_pinning_animal",
                "Moose, MooseCalf, Seal, BabySeal, RiktigFuling",
                AnimalDefaults, AnimalReleased160Defaults),
            new ObjectListRevision("automatic_mapping", "allow_pinning_monster",
                "Frysling, Gammeltroll, Krigen, Hexen, Barka, Kvastur, Elaking, EyelessOne, ShapelessPulp, TinyPulp, Shadow, Mistile, FallenWarrior",
                MonsterDefaults, MonsterReleased160Defaults),
            new ObjectListRevision("automatic_mapping", "allow_pinning_mineral",
                "PetrifiedGammeltroll, SilverDeposit, AncientArmour, AncientSword",
                MappingMineralDefaults, MappingMineralReleased160Defaults, MappingMineralEnumDefaults),
            new ObjectListRevision("automatic_mapping", "allow_pinning_dungeon",
                "BearCave, Morkhalla, WindingTunnels, TombOfLordReto",
                DungeonDefaults, DungeonReleased160Defaults),
            new ObjectListRevision("automatic_mining", "allow_mining_mineral",
                "PetrifiedGammeltroll, SilverDeposit, AncientArmour, AncientSword",
                MiningMineralDefaults, MiningMineralReleased160Defaults),
            new ObjectListRevision("automatic_farming", "allow_farming_crop",
                "Kale, KaleSeeds, Oat, Poteitr, Lingonberries, BlueMushroom",
                CropDefaults),
            new ObjectListRevision("automatic_processing", "allow_container",
                "PieceChestBarrel, PieceChestGrausten, PieceChestWardrobe",
                ProcessingContainerDefaults, ProcessingContainerReleased160Defaults),
            new ObjectListRevision("automatic_storage", "allow_container",
                "PieceChestBarrel, PieceChestGrausten, PieceChestWardrobe",
                StorageContainerDefaults),
        };

        private static List<string> ParseObjectList(string value)
        {
            return Csv.ParseLine(value ?? "", true).Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        private static bool TryReadDefaultsRevision(List<string> lines, out int revision)
        {
            var entries = ReadEntries(lines);
            var hasMarker = entries.TryGetValue((DefaultsRevisionSection, DefaultsRevisionKey), out var marker);
            revision = 0;
            if (hasMarker && (!int.TryParse(marker.Value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out revision) ||
                revision < 0 || revision > DefaultsRevision))
            {
                Automatics.Logger.Warning(
                    $"Unrecognized config defaults revision '{marker.Value}'; skipping the object defaults step and leaving the marker unchanged.");
                return false;
            }
            return true;
        }

        private static bool MigrateObjectDefaults(List<string> lines, int revision)
        {
            if (revision >= DefaultsRevision) return false;
            var entries = ReadEntries(lines);
            var hasMarker = entries.ContainsKey((DefaultsRevisionSection, DefaultsRevisionKey));

            foreach (var list in ObjectListsRevision2)
            {
                if (!entries.TryGetValue((list.Section, list.Key), out var entry)) continue;
                var values = ParseObjectList(entry.Value);
                var existing = new HashSet<string>(values, StringComparer.Ordinal);
                if (!list.Defaults.Any(x => x.SetEquals(existing))) continue;

                values = values.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                existing = new HashSet<string>(values, StringComparer.Ordinal);
                var added = list.Additions.Where(existing.Add).ToList();
                if (added.Count == 0) continue;
                values.AddRange(added);
                lines[entry.Index] = $"{list.Key} = {string.Join(", ", values.Select(Csv.Escape))}";
                Automatics.Logger.Message(
                    $"Append values: [{list.Section}] {list.Key} += [{string.Join(", ", added)}]");
            }

            // Match raw elements before repairing the spaces released 1.6.0 wrote inside quotes.
            // Edited lists need this repair too; appended lists have already been trimmed.
            foreach (var key in new[] { "allow_pinning_monster", "allow_pinning_animal", "allow_pinning_flora",
                         "allow_pinning_mineral", "allow_pinning_vehicle", "allow_pinning_spot", "allow_pinning_dungeon" })
                RepairObjectListWhitespace(lines, "automatic_mapping", key);
            RepairObjectListWhitespace(lines, "automatic_mining", "allow_mining_mineral");
            RepairObjectListWhitespace(lines, "automatic_door", "allow_automatic_door");
            RepairObjectListWhitespace(lines, "automatic_processing", "allow_container");

            RenameFlax(lines, "automatic_farming", "allow_farming_crop");
            RenameFlax(lines, "automatic_mapping", "allow_pinning_flora");

            // Update every existing marker in place, retaining its comments and section layout.
            // A file without a marker gets one at the end, after the migrated entries.
            if (hasMarker)
                RewriteEveryEntry(lines, DefaultsRevisionSection, DefaultsRevisionKey, FormatRevision());
            else
                AppendSection(lines, DefaultsRevisionSection, DefaultsRevisionKey, FormatRevision());
            Automatics.Logger.Message($"Record the config defaults revision {DefaultsRevision}");
            return true;
        }

        // Rewrites every line holding the entry, keeping its comments and section layout.
        private static void RewriteEveryEntry(List<string> lines, string section, string key, string value)
        {
            var current = string.Empty;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                    current = line.Substring(1, line.Length - 2);
                var split = line.Split(KeyValueSeparator, 2);
                if (current == section && split.Length == 2 && split[0].Trim() == key)
                    lines[i] = $"{key} = {value}";
            }
        }

        // Reads the stored Pickup values before anything binds them. A key BepInEx reads as bound,
        // under an interval that would run periodic pickup, gets the interval set to 0, so that
        // the user keeps the key-only pickup binding the key used to give. A missing key entry is
        // left alone: it takes the new default, and periodic pickup keeps running beside it.
        // The marker is recorded in the same write; if the write fails, the next start runs the
        // pass again. Returns whether the lines changed, the marker alone included.
        private static bool DecouplePickupKey(List<string> lines)
        {
            var entries = ReadEntries(lines);
            var hasMarker = entries.TryGetValue((DefaultsRevisionSection, PickupKeyDecoupledKey), out var marker);
            if (hasMarker)
            {
                if (!int.TryParse(marker.Value, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var done) || done < 0)
                {
                    Automatics.Logger.Warning(
                        $"Unrecognized Pickup All Nearby key marker '{marker.Value}'; skipping the pickup interval step and leaving the marker unchanged.");
                    return false;
                }

                // 0 is the value BepInEx saves when the write that should have recorded the pass failed.
                if (done >= PickupKeyDecoupled) return false;
            }

            if (entries.TryGetValue((PickupSection, PickupKeyKey), out var key) && IsBoundShortcut(key.Value))
            {
                var hasInterval = entries.TryGetValue((PickupSection, PickupIntervalKey), out var interval);
                if (!hasInterval || RunsPeriodicPickup(interval.Value))
                {
                    if (hasInterval)
                        lines[interval.Index] = $"{PickupIntervalKey} = 0";
                    else
                        AddEntry(lines, PickupSection, PickupIntervalKey, "0");
                    Automatics.Logger.Message(
                        $"Keep pickup on the Pickup All Nearby key only: [{PickupSection}] {PickupIntervalKey} = {(hasInterval ? interval.Value : "(missing)")} => 0");
                }
            }

            // Lines may have moved, so the entries are read again. A new marker goes right after
            // the defaults marker, or at the end of the file when there is none.
            if (hasMarker)
                RewriteEveryEntry(lines, DefaultsRevisionSection, PickupKeyDecoupledKey, FormatPickupKeyDecoupled());
            else if (ReadEntries(lines).TryGetValue((DefaultsRevisionSection, DefaultsRevisionKey), out var revision))
                lines.Insert(revision.Index + 1, $"{PickupKeyDecoupledKey} = {FormatPickupKeyDecoupled()}");
            else
                AppendSection(lines, DefaultsRevisionSection, PickupKeyDecoupledKey, FormatPickupKeyDecoupled());
            Automatics.Logger.Message("Record the Pickup All Nearby key marker");
            return true;
        }

        // The numbered settings passes, in order. Each changes stored settings once; the marker
        // records the highest pass a file has been through.
        private static readonly (int Revision, Action<List<string>> Pass)[] SettingsPasses =
        {
            (1, GroupFirePieces),
            (2, RemoveFermenterSupplyOption),
            (3, FoldFeatureSwitchesIntoModules),
            (4, KeepKeyOnlyRuns),
            (5, CopyFarmingRangeToContainerRange)
        };

        // Settings pass 5. Automatic Farming's range used to also set how far it looks for role
        // containers; that is now Container Search Range. A config with a stored farming range
        // gets the same value there once, so the containers farming uses stay the same.
        private static void CopyFarmingRangeToContainerRange(List<string> lines)
        {
            const string section = "automatic_farming";
            var entries = ReadEntries(lines);
            if (!entries.TryGetValue((section, "farming_range"), out var range)) return;
            if (entries.ContainsKey((section, "container_search_range"))) return;

            AddEntry(lines, section, "container_search_range", range.Value);
            Automatics.Logger.Message(
                $"Keep the farming containers in reach: [{section}] container_search_range = {range.Value}");
        }

        // The shortcuts that used to stop their module's periodic runs, and those intervals.
        private static readonly (string Section, string Key, string Interval)[] KeyedIntervals =
        {
            ("automatic_mining", "mining_key", "mining_interval"),
            ("automatic_farming", "farming_key", "farming_interval"),
            ("automatic_mapping", "static_object_mapping_key", "static_object_mapping_interval")
        };

        // Settings pass 4. A bound shortcut used to stop the periodic runs of automatic mining,
        // farming and static mapping; it no longer does, and an interval of 0 does instead. A
        // config whose key BepInEx reads as bound gets that interval set to 0, so it keeps
        // running on the key only. A missing interval took its default, which would run, so
        // the 0 is added.
        private static void KeepKeyOnlyRuns(List<string> lines)
        {
            foreach (var (section, key, interval) in KeyedIntervals)
            {
                var entries = ReadEntries(lines);
                if (!entries.TryGetValue((section, key), out var shortcut) || !IsBoundShortcut(shortcut.Value))
                    continue;

                if (entries.TryGetValue((section, interval), out var current))
                {
                    // BepInEx reads the interval with float.Parse in the invariant culture; a value
                    // it reads as 0 or less already stops the periodic runs.
                    if (float.TryParse(current.Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
                        seconds <= 0f)
                        continue;
                    RewriteEveryEntry(lines, section, interval, "0");
                }
                else
                {
                    AddEntry(lines, section, interval, "0");
                }

                Automatics.Logger.Message(
                    $"Keep {section} on its key only: [{section}] {interval} = {(current.Value ?? "(missing)")} => 0");
            }
        }

        // Runs every settings pass the file has not been through and records the revision in the
        // same write. If the write fails, the next start runs the passes again. Returns whether
        // the lines changed, the marker alone included.
        private static bool MigrateSettings(List<string> lines)
        {
            var entries = ReadEntries(lines);
            var hasMarker = entries.TryGetValue((DefaultsRevisionSection, SettingsRevisionKey), out var marker);
            var done = 0;
            if (hasMarker)
            {
                if (!int.TryParse(marker.Value, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out done) || done < 0)
                {
                    Automatics.Logger.Warning(
                        $"Unrecognized settings revision '{marker.Value}'; skipping the settings passes and leaving the marker unchanged.");
                    return false;
                }

                // 0 is the value BepInEx saves when the write that should have recorded the passes failed.
                if (done >= SettingsRevision) return false;
            }

            foreach (var (revision, pass) in SettingsPasses)
                if (revision > done)
                    pass(lines);

            // A new marker goes after the other markers, or at the end of the file when there is none.
            entries = ReadEntries(lines);
            if (hasMarker)
                RewriteEveryEntry(lines, DefaultsRevisionSection, SettingsRevisionKey, FormatSettingsRevision());
            else if (entries.TryGetValue((DefaultsRevisionSection, PickupKeyDecoupledKey), out var pickup))
                lines.Insert(pickup.Index + 1, $"{SettingsRevisionKey} = {FormatSettingsRevision()}");
            else if (entries.TryGetValue((DefaultsRevisionSection, DefaultsRevisionKey), out var defaults))
                lines.Insert(defaults.Index + 1, $"{SettingsRevisionKey} = {FormatSettingsRevision()}");
            else
                AppendSection(lines, DefaultsRevisionSection, SettingsRevisionKey, FormatSettingsRevision());
            Automatics.Logger.Message($"Record the settings revision {FormatSettingsRevision()}");
            return true;
        }

        // Settings pass 1. The eleven Automatic Processing fire pieces had five settings each; they
        // now share one group. The fires list holds each piece whose allowed processes included
        // Refuel. Each shared value is the one most pieces had; a tie goes to the default when it
        // is among the tied values, otherwise to the smallest. Pieces that had another value are
        // logged. The old lines are removed. A missing or unreadable old value counts as its
        // default, which is what BepInEx would have used.
        private static void GroupFirePieces(List<string> lines)
        {
            var entries = ReadEntries(lines);
            var oldKeys = FirePieces.SelectMany(x => FireKeys(x.Piece)).ToList();
            if (!oldKeys.Any(x => entries.ContainsKey((ProcessingSection, x)))) return;

            var refuelled = FirePieces
                .Where(x => !entries.TryGetValue((ProcessingSection, $"allow_processing_by_{x.Piece}"), out var allow) ||
                            AllowsRefuel(allow.Value))
                .Select(x => x.Flag)
                .ToList();
            var fires = refuelled.Count == FirePieces.Length ? "All"
                : refuelled.Count == 0 ? "None"
                : string.Join(", ", refuelled);

            var shared = new List<(string Key, string Value)>
            {
                ("refuel_fires", fires),
                ("fires_container_search_range",
                    SharedValue(entries, "container_search_range_by_{0}", "fires_container_search_range", 8, 1, 64)),
                ("fires_container_reference_limit",
                    SharedValue(entries, "container_reference_limit_by_{0}", "fires_container_reference_limit", 0, 0, 999)),
                ("fires_fuel_count_of_suppress_processing",
                    SharedValue(entries, "{0}_fuel_count_of_suppress_processing", "fires_fuel_count_of_suppress_processing", 1, 0, 999)),
                ("fires_refuel_only_when_out_of_fuel",
                    SharedFlag(entries, "{0}_refuel_only_when_out_of_fuel", "fires_refuel_only_when_out_of_fuel"))
            };

            RemoveEveryEntry(lines, ProcessingSection, new HashSet<string>(oldKeys));

            var current = ReadEntries(lines);
            foreach (var (key, value) in Enumerable.Reverse(shared))
            {
                if (current.ContainsKey((ProcessingSection, key)))
                    RewriteEveryEntry(lines, ProcessingSection, key, value);
                else
                    AddEntry(lines, ProcessingSection, key, value);
                current = ReadEntries(lines);
            }

            Automatics.Logger.Message(
                $"Merge the settings of the fire pieces: [{ProcessingSection}] {string.Join(", ", shared.Select(x => $"{x.Key} = {x.Value}"))}");
        }

        // Settings pass 2. The fermenter no longer has "Supply Only If Materials Run Out"; BepInEx
        // would keep the stored line as an entry nothing reads, so it is removed.
        private static void RemoveFermenterSupplyOption(List<string> lines)
        {
            const string key = "piece_fermenter_supply_only_when_materials_run_out";
            if (!ReadEntries(lines).ContainsKey((ProcessingSection, key))) return;

            RemoveEveryEntry(lines, ProcessingSection, new HashSet<string> { key });
            Automatics.Logger.Message($"Remove config: {ProcessingSection} {key}");
        }

        // The single-feature modules and the feature switch each used to have beside its module
        // switch.
        private static readonly (string Section, string Key)[] FeatureSwitches =
        {
            ("automatic_door", "enable_automatic_door"),
            ("automatic_feeding", "enable_automatic_feeding"),
            ("automatic_repair", "enable_automatic_repair"),
            ("automatic_storage", "enable_automatic_storage"),
            ("automatic_processing", "enable_automatic_processing")
        };

        // Settings pass 3. A module that has one feature no longer has a switch for it beside the
        // module switch. A feature switch BepInEx read as false turns its module off, which stops
        // the same work; its line is removed either way. A value BepInEx could not read took the
        // default, true, and changes nothing.
        private static void FoldFeatureSwitchesIntoModules(List<string> lines)
        {
            foreach (var (section, key) in FeatureSwitches)
            {
                var entries = ReadEntries(lines);
                if (!entries.TryGetValue((section, key), out var entry)) continue;

                if (bool.TryParse(entry.Value, out var enabled) && !enabled)
                {
                    if (entries.ContainsKey((section, ModuleKey)))
                        RewriteEveryEntry(lines, section, ModuleKey, "Disabled");
                    else
                        AddEntry(lines, section, ModuleKey, "Disabled");
                    Automatics.Logger.Message(
                        $"Turn the module off for its disabled feature: [{section}] {key} = {entry.Value} => {ModuleKey} = Disabled");
                }

                RemoveEveryEntry(lines, section, new HashSet<string> { key });
                Automatics.Logger.Message($"Remove config: {section} {key}");
            }
        }

        private static IEnumerable<string> FireKeys(string piece)
        {
            yield return $"allow_processing_by_{piece}";
            yield return $"container_search_range_by_{piece}";
            yield return $"container_reference_limit_by_{piece}";
            yield return $"{piece}_fuel_count_of_suppress_processing";
            yield return $"{piece}_refuel_only_when_out_of_fuel";
        }

        // Removes every line of the section that holds one of the keys, repeated sections included.
        private static void RemoveEveryEntry(List<string> lines, string section, ISet<string> keys)
        {
            var current = string.Empty;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    current = line.Substring(1, line.Length - 2);
                    continue;
                }

                var split = line.Split(KeyValueSeparator, 2);
                if (current != section || split.Length != 2 || !keys.Contains(split[0].Trim())) continue;
                lines.RemoveAt(i);
                i--;
            }
        }

        // Whether BepInEx ran Refuel for a fire piece with this stored Process value. It parses a
        // comma-separated list of names (any case) or a number; a value it cannot parse takes the
        // default, Refuel. The fire pieces accepted only None and Refuel, and any other parsed
        // value was clamped to the first acceptable value, None.
        private static bool AllowsRefuel(string value)
        {
            if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
                return number == 2L;

            var flags = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                { "None", 0 }, { "Craft", 1 }, { "Refuel", 2 }, { "Store", 4 }, { "Charge", 8 }, { "All", 15 }
            };
            var mask = 0L;
            foreach (var part in value.Split(','))
            {
                if (!flags.TryGetValue(part.Trim(), out var flag)) return true;
                mask |= flag;
            }

            return mask == 2L;
        }

        private static string SharedValue(Dictionary<(string Section, string Key), (int Index, string Value)> entries,
            string keyFormat, string sharedKey, int defaultValue, int min, int max)
        {
            var values = FirePieces.Select(x =>
            {
                var value = defaultValue;
                if (entries.TryGetValue((ProcessingSection, string.Format(keyFormat, x.Piece)), out var entry) &&
                    int.TryParse(entry.Value, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    value = Math.Min(max, Math.Max(min, parsed));
                return (x.Piece, value);
            }).ToList();
            var chosen = MostCommon(values.Select(x => x.value), defaultValue);
            LogDiffering(sharedKey, chosen.ToString(System.Globalization.CultureInfo.InvariantCulture),
                values.Where(x => x.value != chosen)
                    .Select(x => (x.Piece, x.value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            return chosen.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string SharedFlag(Dictionary<(string Section, string Key), (int Index, string Value)> entries,
            string keyFormat, string sharedKey)
        {
            var values = FirePieces.Select(x =>
            {
                var value = false;
                if (entries.TryGetValue((ProcessingSection, string.Format(keyFormat, x.Piece)), out var entry) &&
                    bool.TryParse(entry.Value, out var parsed))
                    value = parsed;
                return (x.Piece, value ? 1 : 0);
            }).ToList();
            var chosen = MostCommon(values.Select(x => x.Item2), 0) != 0;
            var text = chosen ? "true" : "false";
            LogDiffering(sharedKey, text,
                values.Where(x => x.Item2 != 0 != chosen).Select(x => (x.Piece, x.Item2 != 0 ? "true" : "false")));
            return text;
        }

        // The most common value; a tie goes to the default when it is among the tied values,
        // otherwise to the smallest.
        private static int MostCommon(IEnumerable<int> values, int defaultValue)
        {
            var counts = values.GroupBy(x => x).Select(x => (Value: x.Key, Count: x.Count())).ToList();
            var top = counts.Max(x => x.Count);
            var tied = counts.Where(x => x.Count == top).Select(x => x.Value).ToList();
            return tied.Contains(defaultValue) ? defaultValue : tied.Min();
        }

        private static void LogDiffering(string sharedKey, string chosen, IEnumerable<(string Piece, string Value)> differing)
        {
            foreach (var (piece, value) in differing)
                Automatics.Logger.Message(
                    $"Fire piece setting merged: [{ProcessingSection}] {sharedKey} = {chosen}; {piece} had {value}");
        }

        // Whether BepInEx reads the stored shortcut as bound, decided the way its
        // KeyboardShortcut.Deserialize reads it: the value is split on the shortcut separators
        // with empty parts dropped, and every part goes through Enum.Parse for KeyCode, case
        // sensitive. A part that fails makes the whole shortcut unbound, and so does no part at
        // all or a first key, the main key, of None.
        private static bool IsBoundShortcut(string value)
        {
            var parts = value.Split(ShortcutSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            try
            {
                var keys = parts.Select(x => (KeyCode)Enum.Parse(typeof(KeyCode), x)).ToArray();
                return keys[0] != KeyCode.None;
            }
            catch (SystemException)
            {
                return false;
            }
        }

        // Whether the stored interval runs periodic pickup. BepInEx reads it with float.Parse in
        // the invariant culture and keeps the default, 0.5, for a value it cannot read; the
        // accepted range then clamps it, so only a value above 0 runs.
        private static bool RunsPeriodicPickup(string value)
        {
            if (!float.TryParse(value,
                    System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands,
                    System.Globalization.NumberFormatInfo.InvariantInfo, out var interval))
                return true;
            return interval.CompareTo(0f) > 0;
        }

        private static void RepairObjectListWhitespace(List<string> lines, string section, string key)
        {
            if (!ReadEntries(lines).TryGetValue((section, key), out var entry)) return;
            var values = ParseObjectList(entry.Value);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var repaired = values.Select(x => x.Trim()).Where(seen.Add).ToList();
            if (values.SequenceEqual(repaired, StringComparer.Ordinal)) return;

            lines[entry.Index] = $"{key} = {string.Join(", ", repaired.Select(Csv.Escape))}";
            Automatics.Logger.Message($"Repair list whitespace: [{section}] {key}");
        }

        private static void RenameFlax(List<string> lines, string section, string key)
        {
            if (!ReadEntries(lines).TryGetValue((section, key), out var entry)) return;
            var values = ParseObjectList(entry.Value);
            if (!values.Contains("Flex", StringComparer.Ordinal)) return;

            var hasFlax = values.Contains("Flax", StringComparer.Ordinal);
            for (var i = 0; i < values.Count; i++)
            {
                if (!string.Equals(values[i], "Flex", StringComparison.Ordinal)) continue;
                if (hasFlax)
                    values.RemoveAt(i--);
                else
                {
                    values[i] = "Flax";
                    hasFlax = true;
                }
            }
            lines[entry.Index] = $"{key} = {string.Join(", ", values.Select(Csv.Escape))}";
            Automatics.Logger.Message($"Replace value: [{section}] {key} Flex => Flax");
        }

        // Whether BepInEx can read the value, asked of the converter its SetSerializedValue uses. For
        // an enum that is Enum.Parse with case ignored: a number is read, a quoted or misspelt name
        // is not. A value it cannot read is dropped with a warning, and the default applies.
        private static bool IsReadableModuleValue(string value)
        {
            try
            {
                TomlTypeConverter.ConvertToValue(value, typeof(AutomaticsModule));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Reads the file the way BepInEx's ConfigFile.Reload does: trimmed lines, '#' comments,
        // [section] headers, key = value split at the first '=' and compared ordinally, and the last
        // of repeated entries winning. Maps each entry to the line it is on and its value.
        private static Dictionary<(string Section, string Key), (int Index, string Value)> ReadEntries(
            List<string> lines)
        {
            var entries = new Dictionary<(string Section, string Key), (int Index, string Value)>();
            var section = string.Empty;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2);
                    continue;
                }

                var split = line.Split(KeyValueSeparator, 2);
                if (split.Length == 2)
                    entries[(section, split[0].Trim())] = (i, split[1].Trim());
            }

            return entries;
        }

        // Puts the entry right under the first header of its section, or appends the section.
        private static void AddEntry(List<string> lines, string section, string key, string value)
        {
            var header = lines.FindIndex(x => x.Trim() == $"[{section}]");
            if (header >= 0)
                lines.Insert(header + 1, $"{key} = {value}");
            else
                AppendSection(lines, section, key, value);
        }

        // Appends the section with the entry at the end of the file, even when the file has that
        // section already: BepInEx reads repeated sections as one.
        private static void AppendSection(List<string> lines, string section, string key, string value)
        {
            if (lines.Any() && !string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key} = {value}");
        }

        // Writes the whole content to a temporary file beside the config and swaps it in, so that an
        // interrupted write leaves the old file or the new one, never a part of either.
        private static void Replace(string path, string content, Encoding encoding)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            var temp = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temp, content, encoding);
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            finally
            {
                DeleteTemporaryFile(temp);
            }
        }

        private static void DeleteTemporaryFile(string temp)
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                Automatics.Logger.Warning($"Could not remove the temporary file {temp} ({e.Message}).");
            }
        }

        // A config file's lines, with what it takes to write them back the way the file stored
        // them: its encoding (with or without a byte order mark), its line break, and whether it
        // ended with one.
        private sealed class ConfigText
        {
            public ConfigText(List<string> lines, Encoding encoding, string newLine, bool endsWithNewLine)
            {
                Lines = lines;
                Encoding = encoding;
                NewLine = newLine;
                EndsWithNewLine = endsWithNewLine;
            }

            public List<string> Lines { get; }
            public Encoding Encoding { get; }
            private string NewLine { get; }
            private bool EndsWithNewLine { get; }

            // Splits the lines exactly as File.ReadAllLines does, which the version passes were
            // written against.
            public static ConfigText Read(string path)
            {
                string text;
                Encoding encoding;
                using (var reader = new StreamReader(path, new UTF8Encoding(false), true))
                {
                    text = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                }

                var lines = new List<string>();
                using (var reader = new StringReader(text))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        lines.Add(line);
                }

                var newLine = text.Contains("\r\n") ? "\r\n"
                    : text.Contains("\n") ? "\n"
                    : text.Contains("\r") ? "\r"
                    : Environment.NewLine;
                var last = text.Length > 0 ? text[text.Length - 1] : '\0';
                return new ConfigText(lines, encoding, newLine, last == '\n' || last == '\r');
            }

            public string Render()
            {
                var content = string.Join(NewLine, Lines);
                return EndsWithNewLine && Lines.Count > 0 ? content + NewLine : content;
            }
        }

        private static string FormatRevision()
        {
            return DefaultsRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string FormatPickupKeyDecoupled()
        {
            return PickupKeyDecoupled.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string FormatSettingsRevision()
        {
            return SettingsRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void MigrationFor130(List<string> lines)
        {
            Migration(lines, new Dictionary<string, List<Operation>>
            {
                { "[logging]", new List<Operation>
                {
                    RenameCategory("[system]"),
                    RenameConfig("logging_enabled", "enable_logging"),
                    RenameConfig("allowed_log_level", "log_level_to_allow_logging"),
                }},
                { "[automatic_door]", new List<Operation>
                {
                    RenameConfig("automatic_door_enabled", "enable_automatic_door"),
                    RenameConfig("player_search_radius_to_open", "distance_for_automatic_opening"),
                    RenameConfig("player_search_radius_to_close", "distance_for_automatic_closing"),
                    RenameConfig("toggle_automatic_door_enabled_key", "automatic_door_enable_disable_toggle"),
                }},
                { "[automatic_map_pinning]", new List<Operation>
                {
                    RenameCategory("[automatic_mapping]"),
                    RenameConfig("automatic_map_pinning_enabled", "enable_automatic_mapping"),
                    RenameConfig("allow_pinning_vein", "allow_pinning_deposit"),
                    RenameConfig("allow_pinning_vein_custom", "allow_pinning_deposit_custom"),
                    RenameConfig("ignore_tamed_animals", "not_pinning_tamed_animals"),
                    RenameConfig("in_ground_veins_need_wishbone", "need_to_equip_wishbone_for_underground_deposits"),
                }},
                { "[automatic_processing]", new List<Operation>
                {
                    RenameConfig("automatic_processing_enabled", "enable_automatic_processing"),
                    RenameConfig(@"r/^([\w_]+)_allow_automatic_processing", "allow_processing_by_$1"),
                    RenameConfig(@"r/^([\w_]+)_container_search_range", "container_search_range_by_$1"),
                    RenameConfig(@"r/^([\w_]+)_material_count_that_suppress_automatic_process", "$1_material_count_of_suppress_processing"),
                    RenameConfig(@"r/^([\w_]+)_fuel_count_that_suppress_automatic_process", "$1_fuel_count_of_suppress_processing"),
                    RenameConfig(@"r/^([\w_]+)_product_count_that_suppress_automatic_store", "$1_product_count_of_suppress_processing"),
                }},
                { "[automatic_feeding]", new List<Operation>
                {
                    RenameConfig("automatic_feeding_enabled", "enable_automatic_feeding"),
                    RenameConfig("need_close_to_eat_the_feed", "need_get_close_to_eat_the_feed"),
                    RenameConfig("automatic_repair_enabled", "enable_automatic_repair"),
                }},
                { "[automatic_repair]", new List<Operation>
                {
                    RenameConfig("automatic_repair_enabled", "enable_automatic_repair"),
                }}
            });
        }

        private static void MigrationFor140(List<string> lines)
        {
            Migration(lines, new Dictionary<string, List<Operation>>
            {
                { "[automatic_door]", new List<Operation>
                {
                    RemoveConfig("allow_automatic_door_custom"),
                    ReplaceValue("allow_automatic_door", "All", "WoodDoor, WoodGate, IronGate, DarkwoodGate, WoodShutter"),
                }},
                { "[automatic_mapping]", new List<Operation>
                {
                    RenameConfig("dynamic_object_search_range", "dynamic_object_mapping_range"),
                    RenameConfig("static_object_search_range", "static_object_mapping_range"),
                    RenameConfig("location_search_range", "location_mapping_range"),
                    RenameConfig("allow_pinning_deposit", "allow_pinning_mineral"),
                    RemoveConfig("allow_pinning_ship"),
                    RemoveConfig(@"r/^allow_pinning_[\w]+_custom$"),
                    RenameConfig("static_object_search_interval", "static_object_mapping_interval"),
                    RenameConfig("need_to_equip_wishbone_for_underground_deposits", "need_to_equip_wishbone_for_underground_minerals"),
                    RenameConfig("static_object_search_key", "static_object_mapping_key"),
                    ReplaceValue("allow_pinning_animal", "All", "Boar, Piggy, Deer, Wolf, WolfCub, Lox, LoxCalf, Hen, Chicken, Hare, Bird, Fish"),
                    ReplaceValue("allow_pinning_monster", "All", "Greyling, Neck, Ghost, Greydwarf, GreydwarfBrute, GreydwarfShaman, RancidRemains, Skeleton, Troll, Abomination, Blob, Draugr, DraugrElite, Leech, Oozer, Surtling, Wraith, Drake, Fenring, StoneGolem, Deathsquito, Fuling, FulingBerserker, FulingShaman, Growth, Serpent, Bat, FenringCultist, Ulv, DvergrRogue, DvergrMage, Tick, Seeker, SeekerBrood, Gjall, SeekerSoldier"),
                    ReplaceValue("allow_pinning_flora", "All", "Dandelion, Mushroom, Raspberries, Blueberries, Carrot, CarrotSeeds, YellowMushroom, Thistle, Turnip, TurnipSeeds, Onion, OnionSeeds, Barley, Cloudberries, Flex, JotunPuffs, Magecap"),
                    ReplaceValue("allow_pinning_mineral", "All", "CopperDeposit, TinDeposit, MudPile, ObsidianDeposit, SilverVein, PetrifiedBone, SoftTissue"),
                    ReplaceValue("allow_pinning_spawner", "All", "GreydwarfNest, EvilBonePile, BodyPile"),
                    ReplaceValue("allow_pinning_other", "All", "Vegvisir, Runestone, WildBeehive"),
                    ReplaceValue("allow_pinning_dungeon", "All", "BurialChambers, TrollCave, SunkenCrypts, MountainCave, InfestedMine"),
                    ReplaceValue("allow_pinning_spot", "All", "InfestedTree, FireHole, DrakeNest, GoblinCamp, TarPit, DvergrExcavation, DvergrGuardTower, DvergrHarbour, DvergrLighthouse, PetrifiedBone"),
                }},
                { "[automatic_processing]", new List<Operation>
                {
                    RemoveConfig(@"r/^([\w_]+)_product_count_of_suppress_processing"),
                }},
            });
        }

        private static void MigrationFor145(List<string> lines)
        {
            Migration(lines, new Dictionary<string, List<Operation>>
            {
                { "[automatic_processing]", new List<Operation>
                {
                    ReplaceValue("r/^allow_processing_by", "All", "Craft, Refuel, Store"),
                }},
            });
        }

        private static void MigrationFor160(List<string> lines)
        {
            Migration(lines, new Dictionary<string, List<Operation>>
            {
                { "[automatic_mapping]", new List<Operation>
                {
                    AppendValues("allow_pinning_monster", new[]
                    {
                        "CharredMelee", "CharredArcher", "CharredMage", "CharredTwitcher",
                        "CharredGrunt", "Morgen", "Volture", "BonemawSerpent",
                        "FallenValkyrie", "Fader", "SkeletonFire", "FrostBlob",
                        "Bear", "Vile", "LavaBlob"
                    }),
                    AppendValues("allow_pinning_animal", new[]
                    {
                        "Asksvin", "AsksvinHatchling"
                    }),
                    AppendValues("allow_pinning_flora", new[]
                    {
                        "Fiddlehead", "SmokePuff", "Vineberry", "VineberrySeeds"
                    }),
                    AppendValues("allow_pinning_mineral", new[]
                    {
                        "FlametalDeposit"
                    }),
                    AppendValues("allow_pinning_vehicle", new[]
                    {
                        "Drakkar"
                    }),
                    AppendValues("allow_pinning_spot", new[]
                    {
                        "CharredFortress", "LeviathanLava", "BogWitchCamp"
                    }),
                    AppendValues("allow_pinning_dungeon", new[]
                    {
                        "MorgenHole"
                    }),
                }},
                { "[automatic_mining]", new List<Operation>
                {
                    AppendValues("allow_mining_mineral", new[]
                    {
                        "FlametalDeposit"
                    }),
                }},
                { "[automatic_door]", new List<Operation>
                {
                    AppendValues("allow_automatic_door", new[]
                    {
                        "PieceHexagonalDoor", "AshwoodDoor", "FlametalGate", "DvergrDoor"
                    }),
                }},
                { "[automatic_processing]", new List<Operation>
                {
                    AppendValues("allow_container", new[]
                    {
                        "PieceChestCharred"
                    }),
                }},
            });
        }

        private static void Migration(List<string> lines, Dictionary<string, List<Operation>> operations)
        {
            var category = "";
            var begin = -1;
            List<Operation> list;

            var i = 0;
            for (; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.StartsWith("#")) continue;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!Regex.IsMatch(line, @"^\[[\w\d_]+\]$")) continue;

                // begin starts at -1 (no block yet); >= 0 means a section has opened.
                // Using > 0 would skip a first block that starts at line index 0.
                if (begin >= 0)
                {
                    if (operations.TryGetValue(category, out list))
                        list.RemoveAll(x => x.Invoke(category, lines, begin, i));
                }

                category = line;
                begin = i;
            }

            if (operations.TryGetValue(category, out list))
                list.RemoveAll(x => x.Invoke(category, lines, begin, i));

            foreach (var pair in operations.Where(pair => pair.Value.Any()))
                Automatics.Logger.Warning($"{pair.Value.Count} migrations for {pair.Key} were not performed.");

            ConfigCache.Clear();
        }

        private static bool TryParseVersion(List<string> lines, out Version version)
        {
            version = default;

            // The version lives in BepInEx's leading comment header
            // (e.g. "## Settings file was created by plugin Automatics v1.6.0").
            // Scan only the leading comment block so a config value further down that
            // happens to end in vX.Y.Z cannot be mistaken for the file version, and
            // return false (skip migration) rather than defaulting to 0.0.0 and
            // destructively re-running every migration on an unrecognized file.
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("#")) break;

                var match = VersionPattern.Match(line);
                if (!match.Success) continue;

                version = new Version(int.Parse(match.Groups[1].Value),
                    int.Parse(match.Groups[2].Value),
                    int.Parse(match.Groups[3].Value));
                return true;
            }

            return false;
        }

        private readonly struct Version : IComparable<Version>
        {
            private readonly int _major;
            private readonly int _minor;
            private readonly int _patch;

            public Version(int major, int minor, int patch)
            {
                _major = major;
                _minor = minor;
                _patch = patch;
            }

            public override string ToString()
            {
                return $"v{_major}.{_minor}.{_patch}";
            }

            public int CompareTo(Version other)
            {
                if (_major != other._major)
                    return _major.CompareTo(other._major);

                return _minor != other._minor
                    ? _minor.CompareTo(other._minor)
                    : _patch.CompareTo(other._patch);
            }

            public static bool operator >(Version a, Version b)
            {
                return a.CompareTo(b) > 0;
            }

            public static bool operator <(Version a, Version b)
            {
                return a.CompareTo(b) < 0;
            }
        }
    }
}
