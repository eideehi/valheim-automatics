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

        // The sections of the nine modules. Each binds a `module` entry whose default is Disabled.
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
        internal const int DefaultsRevision = 1;

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

            var versionsChanged = MigrateVersions(file.Lines);
            var keptRunning = KeepModulesRunning(file.Lines);
            if (!versionsChanged && keptRunning == null)
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
                // Only the module defaults were at stake, and without the marker the next start
                // tries again. While the version passes have changes, a failure still stops the
                // start, as it always has: once BepInEx rewrote the header, they would not run again.
                Automatics.Logger.Warning(
                    $"Could not update the config file {path} ({e.Message}). " +
                    DescribeSessionDefaults(keptRunning) + " The next start tries again.");
                return;
            }

            ReloadOrFail(config);
        }

        // A new install. Every module takes its default, Disabled, when it is bound. Only the marker
        // is written, so that the next start does not take this file for one written before the
        // change. Should that fail, the defaults apply all the same.
        private static void CreateForNewInstall(ConfigFile config, string path)
        {
            Automatics.Logger.Message("New config file: every module starts disabled.");
            var lines = new List<string>();
            AppendSection(lines, DefaultsRevisionSection, DefaultsRevisionKey, FormatRevision());
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
            return sections.Count == 0
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

            // The marker goes last, after every line it vouches for.
            AppendSection(lines, DefaultsRevisionSection, DefaultsRevisionKey, FormatRevision());
            Automatics.Logger.Message($"Record the config defaults revision {DefaultsRevision}");
            return keptRunning;
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
