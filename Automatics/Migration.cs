using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;

namespace Automatics
{
    internal static class Migration
    {
        private static readonly Regex VersionPattern;
        private static readonly char[] KeyValueSeparator;

        static Migration()
        {
            VersionPattern = new Regex(@"v(\d+)\.(\d+)\.(\d+)$", RegexOptions.Compiled);
            KeyValueSeparator = new[] { '=' };
        }

        public static void MigrateConfig(ConfigFile config)
        {
            var path = config.ConfigFilePath;
            if (!File.Exists(path))
            {
                Automatics.ModLogger.LogError("Config file not found: " + path);
                return;
            }

            var lines = File.ReadAllLines(path).ToList();
            var dirty = false;
            var version = ParseVersion(lines[0]);

            var migrateVersion = new Version(1, 3, 0);
            if (version < migrateVersion)
            {
                Automatics.ModLogger.LogMessage($"Migrating config from {version} to {migrateVersion}");
                dirty = true;
                MigrateFor130(lines);
            }

            if (dirty)
            {
                File.WriteAllText(path, string.Join(Environment.NewLine, lines), Encoding.UTF8);
                config.Reload();
            }
        }

        private static void MigrateFor130(List<string> lines)
        {
            ReplaceAll(lines, new List<(string, string, string)>
            {
                ("", "[logging]", "[system]"),
                ("[system]", "logging_enabled", "enable_logging"),
                ("[system]", "allowed_log_level", "log_level_to_allow_logging"),
                ("[automatic_door]", "automatic_door_enabled", "enable_automatic_door"),
                ("[automatic_door]", "player_search_radius_to_open", "distance_for_automatic_opening"),
                ("[automatic_door]", "player_search_radius_to_close", "distance_for_automatic_closing"),
                ("[automatic_door]", "toggle_automatic_door_enabled_key", "automatic_door_enable_disable_toggle"),
                ("", "[automatic_map_pinning]", "[automatic_mapping]"),
                ("[automatic_mapping]", "automatic_map_pinning_enabled", "enable_automatic_mapping"),
                ("[automatic_mapping]", "allow_pinning_vein", "allow_pinning_deposit"),
                ("[automatic_mapping]", "allow_pinning_vein_custom", "allow_pinning_deposit_custom"),
                ("[automatic_mapping]", "ignore_tamed_animals", "not_pinning_tamed_animals"),
                ("[automatic_mapping]", "in_ground_veins_need_wishbone", "need_to_equip_wishbone_for_underground_deposits"),
                ("[automatic_processing]", "automatic_processing_enabled", "enable_automatic_processing"),
                ("[automatic_processing]", @"r/^([\w_]+)_allow_automatic_processing", "allow_processing_by_$1"),
                ("[automatic_processing]", @"r/^([\w_]+)_container_search_range", "container_search_range_by_$1"),
                ("[automatic_processing]", @"r/^([\w_]+)_material_count_that_suppress_automatic_process", "$1_material_count_of_suppress_processing"),
                ("[automatic_processing]", @"r/^([\w_]+)_fuel_count_that_suppress_automatic_process", "$1_fuel_count_of_suppress_processing"),
                ("[automatic_processing]", @"r/^([\w_]+)_product_count_that_suppress_automatic_store", "$1_product_count_of_suppress_processing"),
                ("[automatic_feeding]", "automatic_feeding_enabled", "enable_automatic_feeding"),
                ("[automatic_feeding]", "need_close_to_eat_the_feed", "need_get_close_to_eat_the_feed"),
                ("[automatic_feeding]", "automatic_repair_enabled", "enable_automatic_repair"),
                ("[automatic_feeding]", "automatic_repair_enabled", "enable_automatic_repair"),
            });
        }

        private static void ReplaceAll(List<string> lines, List<(string, string, string)> replacements)
        {
            var currentSection = string.Empty;
            var skipIndex = 0;
            for (var i = 0; i < replacements.Count; i++)
            {
                var (section, oldValue, newValue) = replacements[i];
                if (string.IsNullOrEmpty(section))
                {
                    for (var j = 0; j < lines.Count; j++)
                    {
                        var line = lines[j];
                        if (line != oldValue) continue;

                        lines[j] = newValue;
                        Automatics.ModLogger.LogDebug($"Migrated [{line}] -> [{lines[j]}]");
                        break;
                    }
                }
                else
                {
                    if (currentSection != section)
                    {
                        currentSection = section;
                        skipIndex = lines.FindIndex(x => x == section);
                    }

                    var isRegex = oldValue.StartsWith("r/");
                    var regex = oldValue.Substring(2);

                    for (var j = skipIndex + 1; j < lines.Count; j++)
                    {
                        var line = lines[j];
                        if (line.StartsWith("#")) continue;

                        var split = line.Split(KeyValueSeparator, 2);
                        if (split.Length != 2) continue;

                        if (isRegex)
                        {
                            if (!Regex.IsMatch(line, regex)) continue;

                            lines[j] = Regex.Replace(line, regex, newValue);
                            Automatics.ModLogger.LogDebug($"Migrated [{line}] -> [{lines[j]}]");
                        }
                        else
                        {
                            var key = split[0].Trim();
                            if (key != oldValue) continue;

                            lines[j] = line.Replace(oldValue, newValue);
                            Automatics.ModLogger.LogDebug($"Migrated [{line}] -> [{lines[j]}]");
                            break;
                        }
                    }
                }
            }
        }

        private static Version ParseVersion(string line)
        {
            var match = VersionPattern.Match(line);
            if (!match.Success)
            {
                Automatics.ModLogger.LogError("Invalid version string: " + line);
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