using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Automatics.Valheim;
using BepInEx.Configuration;

namespace Automatics
{
    /// <summary>
    ///     Offline checks that custom object lists survive being saved to the config file and loaded
    ///     from it. The project compiles <c>Automatics/Valheim/ValheimObjects.cs</c> and mod-utils'
    ///     <c>Json.cs</c> against the LitJSON build the plugin ships, so a list goes through the
    ///     plugin's own value converter and JSON code. Stubs.cs stands in for BepInEx's converter
    ///     registry and for the Unity and mod-utils types the list drawer names.
    /// </summary>
    internal static class Program
    {
        private static readonly Type ListType = typeof(List<ObjectElement>);

        // Characters that the stored value has to carry: the dollar sign and the backslash, which
        // the stored value uses for its own escapes, the quote, a letter and non-ASCII text.
        private static readonly char[] ShortTextAlphabet = { '$', '\\', '"', 'a', 'é' };

        private static int _failures;

        private static int Main()
        {
            ObjectElementList.Initialize();

            Run("plain labels, exact and regex matchers and tags survive saving and loading",
                PlainListsSurvive);
            Run("backslashes and quotes in labels and regex patterns survive saving and loading",
                BackslashesAndQuotesSurvive);
            Run("non-ASCII labels survive saving and loading", NonAsciiLabelsSurvive);
            Run("a value that ends in a backslash survives saving and loading",
                TrailingBackslashSurvives);
            Run("two dollar signs in a row survive saving and loading", DoubleDollarSurvives);
            Run("a regex for a dollar sign followed by an escaped character survives saving and loading",
                DollarBeforeRegexEscapeSurvives);
            Run("a dollar sign before a quote or non-ASCII text survives saving and loading",
                DollarBeforeQuoteOrNonAsciiSurvives);
            Run("text that reads like an escape of the stored value survives saving and loading",
                EscapeLookalikesSurvive);
            Run("every short text of dollar signs, backslashes, quotes, letters and non-ASCII text survives saving and loading",
                EveryShortTextSurvives);
            Run("lists stored by earlier versions load to the same values", EarlierListsLoadTheSame);
            Run("every list that earlier versions stored and loaded correctly still loads to the same values",
                EveryCorrectlyLoadedEarlierListLoadsTheSame);
            Run("lists without a dollar sign are stored exactly as earlier versions stored them",
                ListsWithoutDollarAreStoredAsBefore);
            Run("an empty list is stored as an empty value, and an empty value loads as an empty list",
                EmptyListRoundTrips);
            Run("a stored value that is not a list loads as an empty list with a warning",
                MalformedValueLoadsEmptyWithWarning);

            return _failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            Automatics.Logger.Warnings.Clear();
            try
            {
                test();
                Console.WriteLine($"ok: {name}");
            }
            catch (Exception e)
            {
                _failures++;
                Console.Error.WriteLine($"FAIL: {name}");
                Console.Error.WriteLine(e.Message);
            }
        }

        private static void PlainListsSurvive()
        {
            ExpectSurvives(new List<ObjectElement>
            {
                Element("BlueberryBush", "Blueberries", Exact("Pickable_BlueberryBush")),
                Tagged(Element("Raspberry", "Raspberries", Exact("RaspberryBush"),
                    Regex("^Pickable_Raspberr")), "seed", "berry"),
                Tagged(Element("Thistle", "Thistle", Exact("Pickable_Thistle")))
            });
        }

        private static void BackslashesAndQuotesSurvive()
        {
            ExpectSurvives(new List<ObjectElement>
            {
                Element("BigRock", "The \"Big\" Rock", Regex(@"^Rock_\d+(_frac)?\b")),
                Element("Quoted", "\"", Exact("a\"b\\\"c"), Regex(@"^""\w+\\"""))
            });
        }

        private static void NonAsciiLabelsSurvive()
        {
            ExpectSurvives(new List<ObjectElement>
            {
                Element("Kupfer", "Kupfererz ä 銅", Exact("rock4_copper")),
                Tagged(Element("Bär", "Bär 🐻", Regex("^Bär_\\d")), "Wald")
            });
        }

        private static void TrailingBackslashSurvives()
        {
            ExpectSurvives(new List<ObjectElement>
            {
                Element("Trailing", "ends in \\", Exact("value\\"), Regex(@"^a\\"))
            });
        }

        private static void DoubleDollarSurvives()
        {
            ExpectSurvives(new List<ObjectElement>
            {
                Element("Coins", "Price $$", Exact("$$"), Regex(@"^\$\$$"))
            });
        }

        private static void DollarBeforeRegexEscapeSurvives()
        {
            ExpectSurvives(new List<ObjectElement>
            {
                Element("Enemies", "Enemies", Regex(@"^\$\w+_boar$")),
                Element("Numbers", "Numbers", Regex(@"[$]\d|^\$\d+$"))
            });
        }

        private static void DollarBeforeQuoteOrNonAsciiSurvives()
        {
            ExpectSurvives(new List<ObjectElement>
            {
                Element("Say", "say \"$\"", Exact("$\"")),
                Element("Euro", "$ä and $銅", Exact("$é"))
            });
        }

        private static void EscapeLookalikesSurvive()
        {
            ExpectSurvives(new List<ObjectElement>
            {
                Element("Lookalikes", "\\u0024 $$u0024 \\$$ $\\", Exact("\\u0024"), Exact("$$u0024"),
                    Exact("\\$$"), Exact("$\\"), Exact("$$$"), Exact("\\\\$$\\\\"))
            });
        }

        private static void EveryShortTextSurvives()
        {
            var failed = new List<string>();
            var count = 0;
            foreach (var text in ShortTexts())
            {
                count++;
                var list = ListOf(text);
                var loaded = SaveAndLoad(list, out var stored);
                if (Describe(loaded) != Describe(list))
                    failed.Add($"{Quote(text)} stored as {stored} loads as {Describe(loaded)}");
            }

            Expect(failed.Count == 0,
                $"{failed.Count} of {count} lists changed; first: {failed.FirstOrDefault()}");
            ExpectNoWarnings();
        }

        private static void EarlierListsLoadTheSame()
        {
            foreach (var earlier in EarlierLists())
                ExpectSame(earlier.Values, Load(earlier.Stored), $"loading {earlier.Stored}");
            ExpectNoWarnings();
        }

        // Earlier versions wrote each generated list with the format kept in EarlierFormat; every
        // one of them that earlier versions read back to its own values must still read back to
        // them.
        private static void EveryCorrectlyLoadedEarlierListLoadsTheSame()
        {
            var checkedLists = 0;
            foreach (var text in ShortTexts())
            {
                var list = ListOf(text);
                var stored = EarlierFormat.Store(list);
                if (Describe(EarlierFormat.Load(stored)) != Describe(list)) continue;

                checkedLists++;
                ExpectSame(list, Load(stored), $"loading {stored}");
            }

            Expect(checkedLists > 0, "no generated list loaded correctly under the earlier format");
            ExpectNoWarnings();
        }

        private static void ListsWithoutDollarAreStoredAsBefore()
        {
            foreach (var earlier in EarlierLists().Where(x => x.WrittenByEarlierVersion &&
                                                             !HasDollarSign(x.Values)))
            {
                var stored = TomlTypeConverter.ConvertToString(earlier.Values, ListType);
                Expect(stored == earlier.Stored,
                    $"stored as {stored}, earlier versions stored {earlier.Stored}");
            }

            foreach (var text in ShortTexts().Where(x => !x.Contains('$')))
            {
                var list = ListOf(text);
                var stored = TomlTypeConverter.ConvertToString(list, ListType);
                var earlierStored = EarlierFormat.Store(list);
                Expect(stored == earlierStored,
                    $"{Quote(text)} stored as {stored}, earlier versions stored {earlierStored}");
            }
        }

        private static void EmptyListRoundTrips()
        {
            var stored = TomlTypeConverter.ConvertToString(new List<ObjectElement>(), ListType);
            Expect(stored == "", $"an empty list is stored as {stored}");
            ExpectSame(new List<ObjectElement>(), Load(stored), "loading an empty value");
            ExpectNoWarnings();
        }

        private static void MalformedValueLoadsEmptyWithWarning()
        {
            ExpectSame(new List<ObjectElement>(), Load("\"not a list\""), "loading a value that is not a list");
            Expect(Automatics.Logger.Warnings.Count == 1 &&
                   Automatics.Logger.Warnings[0].StartsWith("Failed to parse custom object config"),
                $"warnings: {string.Join(" | ", Automatics.Logger.Warnings)}");
        }

        private sealed class EarlierList
        {
            public EarlierList(string stored, bool writtenByEarlierVersion, params ObjectElement[] values)
            {
                Stored = stored;
                WrittenByEarlierVersion = writtenByEarlierVersion;
                Values = values.ToList();
            }

            // The value as it stands after `key = ` in the config file.
            public string Stored { get; }

            // False for a value typed into the file by hand.
            public bool WrittenByEarlierVersion { get; }

            public List<ObjectElement> Values { get; }
        }

        private static IEnumerable<EarlierList> EarlierLists()
        {
            yield return new EarlierList(
                """
                "[{\"identifier\":\"BlueberryBush\",\"label\":\"Blueberries\",\"matches\":[{\"regex\":false,\"value\":\"Pickable_BlueberryBush\"}],\"tags\":null}]"
                """,
                true,
                Element("BlueberryBush", "Blueberries", Exact("Pickable_BlueberryBush")));
            yield return new EarlierList(
                """
                "[{\"identifier\":\"WoodHouse\",\"label\":\"Abandoned house\",\"matches\":[{\"regex\":true,\"value\":\"^WoodHouse$$$$d{1,}$\"}],\"tags\":null},{\"identifier\":\"Greyling\",\"label\":\"$enemy_greyling\",\"matches\":[{\"regex\":false,\"value\":\"Greyling\"}],\"tags\":null}]"
                """,
                true,
                Element("WoodHouse", "Abandoned house", Regex(@"^WoodHouse\d{1,}$")),
                Element("Greyling", "$enemy_greyling", Exact("Greyling")));
            yield return new EarlierList(
                """
                "[{\"identifier\":\"BigRock\",\"label\":\"The $$"Big$$" Rock\",\"matches\":[{\"regex\":true,\"value\":\"^Rock_$$$$d+(_frac)?$$$$b\"}],\"tags\":null}]"
                """,
                true,
                Element("BigRock", "The \"Big\" Rock", Regex(@"^Rock_\d+(_frac)?\b")));
            yield return new EarlierList(
                """
                "[{\"identifier\":\"Kupfer\",\"label\":\"Kupfererz $$u00E4 $$u9285\",\"matches\":[{\"regex\":false,\"value\":\"rock4_copper\"}],\"tags\":null}]"
                """,
                true,
                Element("Kupfer", "Kupfererz ä 銅", Exact("rock4_copper")));
            yield return new EarlierList(
                """
                "[{\"identifier\":\"Greydwarves\",\"label\":\"Greydwarf names\",\"matches\":[{\"regex\":true,\"value\":\"^$$$$$enemy_greydwarf\"}],\"tags\":null}]"
                """,
                true,
                Element("Greydwarves", "Greydwarf names", Regex(@"^\$enemy_greydwarf")));
            yield return new EarlierList(
                """
                "[{\"identifier\":\"Raspberry\",\"label\":\"Raspberries\",\"matches\":[{\"regex\":false,\"value\":\"RaspberryBush\"},{\"regex\":true,\"value\":\"^Pickable_Raspberr\"}],\"tags\":[\"seed\",\"berry\"]},{\"identifier\":\"Thistle\",\"label\":\"Thistle\",\"matches\":[{\"regex\":false,\"value\":\"Pickable_Thistle\"}],\"tags\":[]}]"
                """,
                true,
                Tagged(Element("Raspberry", "Raspberries", Exact("RaspberryBush"), Regex("^Pickable_Raspberr")),
                    "seed", "berry"),
                Tagged(Element("Thistle", "Thistle", Exact("Pickable_Thistle"))));
            yield return new EarlierList(
                """
                "[{\"identifier\": \"Boar\", \"label\": \"Wild boar\", \"matches\": [{\"regex\": false, \"value\": \"Boar\"}]}]"
                """,
                false,
                Element("Boar", "Wild boar", Exact("Boar")));
        }

        // Every text of one to four characters from ShortTextAlphabet.
        private static IEnumerable<string> ShortTexts()
        {
            var texts = new List<string> { "" };
            for (var length = 1; length <= 4; length++)
            {
                texts = texts.SelectMany(x => ShortTextAlphabet.Select(c => x + c)).ToList();
                foreach (var text in texts)
                    yield return text;
            }
        }

        private static List<ObjectElement> ListOf(string text)
        {
            return new List<ObjectElement>
            {
                Tagged(Element("id" + text, text, Exact(text), Regex(text)), text)
            };
        }

        private static ObjectElement Element(string identifier, string label, params ObjectMatcher[] matches)
        {
            return new ObjectElement
            {
                identifier = identifier,
                label = label,
                matches = matches.ToList()
            };
        }

        private static ObjectElement Tagged(ObjectElement element, params string[] tags)
        {
            element.tags = tags.ToList();
            return element;
        }

        private static ObjectMatcher Exact(string value)
        {
            return new ObjectMatcher { regex = false, value = value };
        }

        private static ObjectMatcher Regex(string value)
        {
            return new ObjectMatcher { regex = true, value = value };
        }

        // A save writes the converter's text after `key = ` on one line (ConfigFile.Save); a load
        // trims the line and hands the text after its first '=', trimmed, to the converter
        // (ConfigFile.Reload).
        private static List<ObjectElement> SaveAndLoad(List<ObjectElement> elements, out string stored)
        {
            stored = TomlTypeConverter.ConvertToString(elements, ListType);
            Expect(stored.All(c => c >= ' ' && c <= '~'),
                $"the stored value is not one line of printable ASCII: {stored}");
            return Load(stored);
        }

        private static List<ObjectElement> Load(string stored)
        {
            var line = "custom_flora = " + stored;
            var value = line.Trim().Split(new[] { '=' }, 2)[1].Trim();
            return (List<ObjectElement>)TomlTypeConverter.ConvertToValue(value, ListType);
        }

        private static void ExpectSurvives(List<ObjectElement> elements)
        {
            var loaded = SaveAndLoad(elements, out var stored);
            ExpectSame(elements, loaded, $"saving and loading, stored as {stored}");
            ExpectNoWarnings();
        }

        private static void ExpectSame(List<ObjectElement> expected, List<ObjectElement> actual, string context)
        {
            var want = Describe(expected);
            var got = Describe(actual);
            Expect(want == got, $"{context}: expected {want}, got {got}");
        }

        private static void ExpectNoWarnings()
        {
            Expect(Automatics.Logger.Warnings.Count == 0,
                $"warnings: {string.Join(" | ", Automatics.Logger.Warnings)}");
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static string Describe(List<ObjectElement> elements)
        {
            if (elements is null) return "null";

            var sb = new StringBuilder("[");
            foreach (var element in elements)
            {
                if (element is null)
                {
                    sb.Append("null;");
                    continue;
                }

                sb.Append("{id=").Append(Quote(element.identifier))
                    .Append(" label=").Append(Quote(element.label))
                    .Append(" matches=");
                if (element.matches is null)
                    sb.Append("null");
                else
                    sb.Append('[').Append(string.Join(",", element.matches.Select(x =>
                        x is null ? "null" : (x.regex ? "r" : "e") + Quote(x.value)))).Append(']');
                sb.Append(" tags=");
                if (element.tags is null)
                    sb.Append("null");
                else
                    sb.Append('[').Append(string.Join(",", element.tags.Select(Quote))).Append(']');
                sb.Append("};");
            }

            return sb.Append(']').ToString();
        }

        // Describe writes no '$' of its own, so a '$' in its text comes from the list.
        private static bool HasDollarSign(List<ObjectElement> elements)
        {
            return Describe(elements).Contains('$');
        }

        // Printable ASCII as it is, everything else (and the quote and backslash) as \uXXXX.
        private static string Quote(string text)
        {
            if (text is null) return "null";

            var sb = new StringBuilder("'");
            foreach (var c in text)
            {
                if (c >= ' ' && c <= '~' && c != '\'' && c != '\\')
                    sb.Append(c);
                else
                    sb.Append("\\u").Append(((int)c).ToString("X4"));
            }

            return sb.Append('\'').ToString();
        }
    }
}
