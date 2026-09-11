using System;
using System.Collections.Generic;
using System.Text;

namespace BepInEx.Configuration
{
    public sealed class ConfigFile
    {
        public ConfigFile(string configFilePath)
        {
            ConfigFilePath = configFilePath;
        }

        // How many of the next Reload calls throw, as BepInEx's does when it cannot read the file.
        public static int ReloadFailures;

        public string ConfigFilePath { get; }

        public void Reload()
        {
            if (ReloadFailures <= 0) return;

            ReloadFailures--;
            throw new System.IO.IOException("The config file could not be read.");
        }
    }

    // Mirrors BepInEx 5.4.23.5 TomlTypeConverter for the conversion the migrator asks for: an enum
    // goes through Enum.Parse(type, value, ignoreCase: true), and no other type is converted here.
    public static class TomlTypeConverter
    {
        public static object ConvertToValue(string value, Type valueType)
        {
            if (valueType.IsEnum)
                return Enum.Parse(valueType, value, true);

            throw new InvalidOperationException("Cannot convert to type " + valueType.Name);
        }
    }
}

namespace Automatics
{
    // Mirrors the enum in Automatics/Config.cs, whose order gives Enabled = 0 and Disabled = 1.
    public enum AutomaticsModule
    {
        Enabled,
        Disabled
    }
}

namespace UnityEngine
{
    public static class Mathf
    {
        public static int Min(int a, int b)
        {
            return Math.Min(a, b);
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}

namespace ModUtils
{
    public static class Csv
    {
        public static IEnumerable<string> ParseLine(string line)
        {
            var values = new List<string>();
            var buffer = new StringBuilder();
            var quoted = false;

            for (var i = 0; i < line.Length; i++)
            {
                var current = line[i];
                if (quoted)
                {
                    if (current == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        buffer.Append('"');
                        i++;
                        continue;
                    }

                    if (current == '"')
                    {
                        quoted = false;
                        continue;
                    }

                    buffer.Append(current);
                    continue;
                }

                if (current == '"')
                {
                    quoted = true;
                    continue;
                }

                if (current == ',')
                {
                    values.Add(buffer.ToString().Trim());
                    buffer.Clear();
                    continue;
                }

                buffer.Append(current);
            }

            values.Add(buffer.ToString().Trim());
            return values;
        }

        // Mirrors mod-utils Csv.ParseLine(line, trimUnquotedFields): trims only
        // unquoted fields when requested, leaving quoted fields verbatim.
        public static IEnumerable<string> ParseLine(string line, bool trimUnquotedFields)
        {
            var values = new List<string>();
            var buffer = new StringBuilder();
            var quoted = false;
            var fieldQuoted = false;

            for (var i = 0; i < line.Length; i++)
            {
                var current = line[i];
                if (quoted)
                {
                    if (current == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        buffer.Append('"');
                        i++;
                        continue;
                    }

                    if (current == '"')
                    {
                        quoted = false;
                        continue;
                    }

                    buffer.Append(current);
                    continue;
                }

                if (current == '"')
                {
                    quoted = true;
                    fieldQuoted = true;
                    continue;
                }

                if (current == ',')
                {
                    values.Add(FlushField(buffer, fieldQuoted, trimUnquotedFields));
                    buffer.Clear();
                    fieldQuoted = false;
                    continue;
                }

                buffer.Append(current);
            }

            values.Add(FlushField(buffer, fieldQuoted, trimUnquotedFields));
            return values;
        }

        private static string FlushField(StringBuilder buffer, bool fieldQuoted,
            bool trimUnquotedFields)
        {
            var value = buffer.ToString();
            return trimUnquotedFields && !fieldQuoted ? value.Trim() : value;
        }

        public static string Escape(string value)
        {
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
                return value;

            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }
}

namespace Automatics
{
    internal static class Automatics
    {
        public static TestLogger Logger { get; } = new TestLogger();
    }

    internal sealed class TestLogger
    {
        public void Message(string message)
        {
        }

        public void Warning(string message)
        {
        }

        public void Error(string message)
        {
        }
    }
}
