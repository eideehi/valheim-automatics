using System;
using System.Collections.Generic;
using System.Text;

namespace Automatics.ModUtils
{
    public static class Csv
    {
        private static readonly char[] MustQuoteChars = { '"', ',', '\r', '\n' };

        public static string Escape(string field)
        {
            return field.IndexOfAny(MustQuoteChars) == -1 ? field : $"\"{field.Replace("\"", "\"\"")}\"";
        }

        public static List<List<string>> Parse(string csv)
        {
            return new Parser(csv).Parse();
        }

        public static List<string> ParseLine(string line)
        {
            return new Parser(line).ParseLine();
        }

        public sealed class Parser
        {
            private readonly string _source;
            private readonly int _end;
            private int _offset;
            private bool _quoted;
            private readonly List<string> _recordBuffer;
            private readonly StringBuilder _fieldBuffer;

            public Parser(string source, int offset = 0)
            {
                _source = source;
                _end = source.Length - 1;
                _offset = offset;
                _quoted = false;
                _recordBuffer = new List<string>();
                _fieldBuffer = new StringBuilder();
            }

            public List<List<string>> Parse()
            {
                var result = new List<List<string>>();

                var fieldCount = -1;
                while (HasNext())
                {
                    var record = ParseLine();

                    var count = record.Count;
                    if (count == 0) continue;

                    if (fieldCount == -1)
                        fieldCount = count;
                    else if (fieldCount != count)
                        throw new Exception("Number of fields in a record is not uniform.");

                    result.Add(record);
                }

                return result;
            }

            public bool HasNext()
            {
                return _offset < _source.Length;
            }

            public List<string> ParseLine()
            {
                _recordBuffer.Clear();
                _fieldBuffer.Clear();

                var record = new List<string>();
                for (; _offset < _source.Length; _offset++)
                {
                    if (ParseChar(_source[_offset])) continue;

                    _offset++;
                    break;
                }

                if (_fieldBuffer.Length > 0)
                    FlushField();

                if (_recordBuffer.Count > 0)
                    record.AddRange(_recordBuffer);

                return record;
            }

            private bool ParseChar(char c)
            {
                while (true)
                {
                    if (c == '"')
                    {
                        if (_quoted && _offset < _end)
                        {
                            _offset++;
                            var next = _source[_offset];
                            if (next == '"')
                            {
                                _fieldBuffer.Append(next);
                            }
                            else
                            {
                                _quoted = false;
                                c = next;
                                continue;
                            }
                        }
                        else
                        {
                            _quoted = !_quoted && _offset < _end;
                        }
                    }
                    else if (c == ',')
                    {
                        if (_quoted)
                            _fieldBuffer.Append(c);
                        else
                            FlushField();
                    }
                    else if (c == '\r')
                    {
                        var nextIndex = Math.Min(_offset + 1, _end);
                        if (_source[nextIndex] == '\n')
                            _offset = nextIndex;

                        if (!_quoted)
                        {
                            if (_fieldBuffer.Length > 0)
                                FlushField();
                            return false;
                        }

                        _fieldBuffer.Append('\n');
                    }
                    else if (c == '\n')
                    {
                        if (!_quoted)
                        {
                            if (_fieldBuffer.Length > 0)
                                FlushField();
                            return false;
                        }

                        _fieldBuffer.Append('\n');
                    }
                    else
                    {
                        _fieldBuffer.Append(c);
                    }

                    return true;
                }
            }

            private void FlushField()
            {
                _recordBuffer.Add(_fieldBuffer.ToString().Trim());
                _fieldBuffer.Clear();
            }
        }
    }
}