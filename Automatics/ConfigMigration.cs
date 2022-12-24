using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
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
                        ? Regex.Replace(key, key, newKey)
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
                    currentCategory = line;
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
            if (config.Offset < 0 && config.Offset >= lines.Count) return;
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

        public static void Migration(ConfigFile config)
        {
            var path = config.ConfigFilePath;
            if (!File.Exists(path))
            {
                Automatics.Logger.Error($"Config file not found: {path}");
                return;
            }

            var lines = File.ReadAllLines(path).ToList();
            var dirty = false;
            var version = ParseVersion(lines[0]);

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

            if (dirty)
            {
                File.WriteAllText(path, string.Join(Environment.NewLine, lines), Encoding.UTF8);
                config.Reload();
            }
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

                if (begin > 0)
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

        private static Version ParseVersion(string line)
        {
            var match = VersionPattern.Match(line);
            if (!match.Success)
            {
                Automatics.Logger.Error($"Invalid version string: {line}");
                return new Version(0, 0, 0);
            }

            return new Version(int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value));
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