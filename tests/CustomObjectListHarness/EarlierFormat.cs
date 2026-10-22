using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Automatics.Valheim;
using ModUtils;

namespace Automatics
{
    /// <summary>
    ///     The custom object list value as earlier versions wrote and read it, kept verbatim from
    ///     <c>ObjectElementList</c> in <c>Automatics/Valheim/ValheimObjects.cs</c> as the reference
    ///     for config files that already exist. It writes the JSON text with each backslash as "$$"
    ///     and each other quote as \", and reads "$$" back as a backslash, drops every other
    ///     backslash and keeps a single '$'.
    /// </summary>
    internal static class EarlierFormat
    {
        public static string Store(List<ObjectElement> elements)
        {
            return elements.Any() ? $"\"{EscapeForToml(Json.ToString(elements))}\"" : "";
        }

        public static List<ObjectElement> Load(string str)
        {
            if (string.IsNullOrEmpty(str))
                return new List<ObjectElement>();

            try
            {
                return Json.Parse<List<ObjectElement>>(UnescapeToml(str.Trim('"'))) ??
                       new List<ObjectElement>();
            }
            catch (Exception)
            {
                return new List<ObjectElement>();
            }
        }

        private static string EscapeForToml(string json)
        {
            var sb = new StringBuilder();
            var backslash = false;
            foreach (var c in json.ToCharArray())
            {
                switch (c)
                {
                    case '\\':
                        backslash = true;
                        sb.Append("$$");
                        continue;
                    case '"':
                        if (!backslash)
                            sb.Append('\\');
                        backslash = false;
                        break;
                    default:
                        backslash = false;
                        break;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static string UnescapeToml(string toml)
        {
            var sb = new StringBuilder();
            var dollar = false;
            foreach (var c in toml.ToCharArray())
            {
                switch (c)
                {
                    case '\\':
                        if (dollar)
                            sb.Append('$');
                        dollar = false;
                        continue;
                    case '$':
                        if (dollar)
                            sb.Append('\\');
                        dollar = !dollar;
                        continue;
                    default:
                        if (dollar)
                            sb.Append('$');
                        dollar = false;
                        break;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
