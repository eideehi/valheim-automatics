using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Automatics
{
    public static class L10N
    {
        private static readonly Regex WordPattern;

        static L10N()
        {
            WordPattern = new Regex(@"(\$|@)((?:\w|\d|[^\s(){}[\]+\-!?/\\&%,.:=<>])+)", RegexOptions.Compiled);
        }

        internal static Localization ValheimL10N => Localization.instance;

        public static string Translate(string word)
        {
            if (string.IsNullOrEmpty(word)) return "";
            switch (word[0])
            {
                case '$':
                    return InvokeTranslate(word.Substring(1));
                case '@':
                    return InvokeTranslate($"net_eidee_{word.Substring(1)}");
                default:
                    return InvokeTranslate(word);
            }
        }

        public static string TranslateInternalNameOnly(string internalName)
        {
            switch (internalName[0])
            {
                case '$':
                    return InvokeTranslate(internalName.Substring(1));
                case '@':
                    return InvokeTranslate($"net_eidee_{internalName.Substring(1)}");
                default:
                    return internalName;
            }
        }

        public static string Localize(string text)
        {
            var sb = new StringBuilder();
            var offset = 0;
            foreach (Match match in WordPattern.Matches(text))
            {
                var groups = match.Groups;
                var word = groups[1].Value == "@" ? $"net_eidee_{groups[2].Value}" : groups[2].Value;

                sb.Append(text.Substring(offset, groups[0].Index - offset));
                sb.Append(InvokeTranslate(word));
                offset = groups[0].Index + groups[0].Value.Length;
            }

            return sb.ToString();
        }

        public static string Localize(string text, params object[] words)
        {
            return InvokeInsertWords(Localize(text), Array.ConvertAll(words, x =>
            {
                if (!(x is string s)) return x.ToString();
                return TranslateInternalNameOnly(s);
            }));
        }

        private static string InvokeTranslate(string word) =>
            Reflection.InvokeMethod<string>(ValheimL10N, "Translate", word);

        private static string InvokeInsertWords(string text, string[] words) =>
            Reflection.InvokeMethod<string>(ValheimL10N, "InsertWords", text, words);
    }

    public static class LanguageLoader
    {
        private static readonly char[] CsvSeparator;

        static LanguageLoader()
        {
            CsvSeparator = new[] { ',' };
        }

        public static void LoadFromCsv(string languagesDir, string language = "", string defaultLanguage = "English")
        {
            language = string.IsNullOrEmpty(language) ? L10N.ValheimL10N.GetSelectedLanguage() : language;

            var languageFile = Path.Combine(languagesDir, $"{defaultLanguage}.csv");
            if (language != defaultLanguage)
            {
                if (!ReadCsvFile(languageFile))
                    Automatics.ModLogger.LogError($"Failed to load default language file: {languageFile}");
            }

            languageFile = Path.Combine(languagesDir, $"{language}.csv");
            if (!ReadCsvFile(languageFile))
                Automatics.ModLogger.LogWarning($"Failed to load language file: {languageFile}");
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
            return (key.StartsWith("@") ? $"net_eidee_{key.Substring(1)}" : key, value.Replace(@"\n", "\n"));
        }
    }
}