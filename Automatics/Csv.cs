using System;
using System.Collections.Generic;
using System.Text;

namespace Automatics
{
    public static class Csv
    {
        public static List<string> ParseLine(string line)
        {
            var result = new List<string>();

            var endIndex = line.Length - 1;
            var isInQuote = false;

            var sb = new StringBuilder();
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                switch (c)
                {
                    case '"':
                    {
                        if (isInQuote)
                        {
                            var next = Math.Min(i + 1, endIndex);
                            if (line[next] == '"')
                            {
                                sb.Append(c);
                                i = next;
                            }
                            else
                            {
                                isInQuote = false;
                            }
                        }
                        else
                        {
                            isInQuote = true;
                        }
                        break;
                    }

                    case ',':
                    {
                        if (isInQuote)
                        {
                            sb.Append(c);
                        }
                        else
                        {
                            result.Add(sb.ToString().Trim());
                            sb.Clear();
                        }
                        break;
                    }

                    default:
                    {
                        sb.Append(c);
                        break;
                    }
                }
            }

            if (sb.Length > 0)
            {
                result.Add(sb.ToString().Trim());
            }

            return result;
        }
    }
}