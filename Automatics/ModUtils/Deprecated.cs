using System;
using System.IO;
using System.Linq;

namespace Automatics.ModUtils
{
    internal static class Deprecated
    {
        internal static class LanguageLoader
        {
            private static readonly char[] CsvSeparator;

            static LanguageLoader()
            {
                CsvSeparator = new[] { ',' };
            }

            public static void LoadFromCsv(string languagesDir, string language = "",
                string defaultLanguage = "English")
            {
                language = string.IsNullOrEmpty(language) ? L10N.ValheimL10N.GetSelectedLanguage() : language;

                var languageFile = Path.Combine(languagesDir, $"{defaultLanguage}.csv");
                if (language != defaultLanguage)
                    ReadCsvFile(languageFile);

                languageFile = Path.Combine(languagesDir, $"{language}.csv");
                ReadCsvFile(languageFile);
            }

            private static bool ReadCsvFile(string filePath)
            {
                if (!File.Exists(filePath)) return false;

                Automatics.ModLogger.LogDebug($"Try to load language file: {filePath}");
                try
                {
                    foreach (var (key, value) in from x in File.ReadLines(filePath)
                             let line = x.Trim()
                             where !line.StartsWith("//")
                             select ReadCsvLine(x))
                        Reflection.InvokeMethod(L10N.ValheimL10N, "AddWord", key, value);
                }
                catch (Exception e)
                {
                    Automatics.ModLogger.LogError($"File read error: {e.Message}");
                    return false;
                }

                return true;
            }

            private static (string, string) ReadCsvLine(string line)
            {
                var strings = line.Split(CsvSeparator, 2);
                if (strings.Length != 2) return ("", "");

                var key = strings[0];
                var value = strings[1];
                return (key.StartsWith("@") ? $"automatics_{key.Substring(1)}" : key, value.Replace(@"\n", "\n"));
            }
        }
    }
}