using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FOEReLauncher
{
    /// <summary>
    /// A small JSON reader and writer, enough for the files the launcher reads and writes (manifest,
    /// notes, settings, installed state). Objects become Dictionary&lt;string, object?&gt;, arrays
    /// List&lt;object?&gt;, numbers long or double. Keeps the launcher free of package dependencies.
    /// </summary>
    public static class Json
    {
        public static object? Parse(string text)
        {
            var i = 0;
            var value = ReadValue(text, ref i);
            SkipWhitespace(text, ref i);
            if (i != text.Length)
            {
                throw new FormatException("JSON: unexpected text at " + i);
            }

            return value;
        }

        private static void SkipWhitespace(string t, ref int i)
        {
            while (i < t.Length && char.IsWhiteSpace(t[i]))
            {
                i++;
            }
        }

        private static object? ReadValue(string t, ref int i)
        {
            SkipWhitespace(t, ref i);
            if (i >= t.Length)
            {
                throw new FormatException("JSON: unexpected end");
            }

            var c = t[i];
            switch (c)
            {
                case '{': return ReadObject(t, ref i);
                case '[': return ReadArray(t, ref i);
                case '"': return ReadString(t, ref i);
                case 't': Expect(t, ref i, "true"); return true;
                case 'f': Expect(t, ref i, "false"); return false;
                case 'n': Expect(t, ref i, "null"); return null;
                default: return ReadNumber(t, ref i);
            }
        }

        private static void Expect(string t, ref int i, string word)
        {
            if (string.CompareOrdinal(t, i, word, 0, word.Length) != 0)
            {
                throw new FormatException("JSON: expected " + word + " at " + i);
            }

            i += word.Length;
        }

        private static Dictionary<string, object?> ReadObject(string t, ref int i)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            i++;
            SkipWhitespace(t, ref i);
            if (i < t.Length && t[i] == '}')
            {
                i++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(t, ref i);
                if (i >= t.Length || t[i] != '"')
                {
                    throw new FormatException("JSON: expected a key at " + i);
                }

                var key = ReadString(t, ref i);
                SkipWhitespace(t, ref i);
                if (i >= t.Length || t[i] != ':')
                {
                    throw new FormatException("JSON: expected ':' at " + i);
                }

                i++;
                result[key] = ReadValue(t, ref i);
                SkipWhitespace(t, ref i);
                if (i < t.Length && t[i] == ',')
                {
                    i++;
                    continue;
                }

                if (i < t.Length && t[i] == '}')
                {
                    i++;
                    return result;
                }

                throw new FormatException("JSON: expected ',' or '}' at " + i);
            }
        }

        private static List<object?> ReadArray(string t, ref int i)
        {
            var result = new List<object?>();
            i++;
            SkipWhitespace(t, ref i);
            if (i < t.Length && t[i] == ']')
            {
                i++;
                return result;
            }

            while (true)
            {
                result.Add(ReadValue(t, ref i));
                SkipWhitespace(t, ref i);
                if (i < t.Length && t[i] == ',')
                {
                    i++;
                    continue;
                }

                if (i < t.Length && t[i] == ']')
                {
                    i++;
                    return result;
                }

                throw new FormatException("JSON: expected ',' or ']' at " + i);
            }
        }

        private static string ReadString(string t, ref int i)
        {
            i++;
            var sb = new StringBuilder();
            while (i < t.Length)
            {
                var c = t[i++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i >= t.Length)
                {
                    break;
                }

                var e = t[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > t.Length)
                        {
                            throw new FormatException("JSON: bad \\u escape");
                        }

                        sb.Append((char)int.Parse(t.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }

            throw new FormatException("JSON: unterminated string");
        }

        private static object ReadNumber(string t, ref int i)
        {
            var start = i;
            while (i < t.Length && "+-0123456789.eE".IndexOf(t[i]) >= 0)
            {
                i++;
            }

            var s = t.Substring(start, i - start);
            if (s.Length == 0)
            {
                throw new FormatException("JSON: unexpected character at " + start);
            }

            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                return l;
            }

            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------ accessors

        public static Dictionary<string, object?> Obj(object? value) =>
            value as Dictionary<string, object?> ?? throw new FormatException("JSON: object expected");

        public static List<object?> Arr(object? value) =>
            value as List<object?> ?? throw new FormatException("JSON: array expected");

        public static string? Str(Dictionary<string, object?> o, string key) =>
            o.TryGetValue(key, out var v) ? v as string : null;

        public static long Long(Dictionary<string, object?> o, string key)
        {
            if (!o.TryGetValue(key, out var v))
            {
                throw new FormatException("JSON: missing " + key);
            }

            switch (v)
            {
                case long l: return l;
                case double d: return (long)d;
                default: throw new FormatException("JSON: " + key + " is not a number");
            }
        }

        public static List<string> Strings(Dictionary<string, object?> o, string key)
        {
            var result = new List<string>();
            if (o.TryGetValue(key, out var v) && v is List<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is string s)
                    {
                        result.Add(s);
                    }
                }
            }

            return result;
        }

        // --------------------------------------------------------------- writer

        public static string Quote(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.Append('"').ToString();
        }

        /// <summary>Writes dictionaries, lists, strings, numbers, bools and null with two-space indents.</summary>
        public static string Write(object? value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.Append('\n').ToString();
        }

        private static void WriteValue(StringBuilder sb, object? value, int indent)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case string s: sb.Append(Quote(s)); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object?> o:
                    if (o.Count == 0)
                    {
                        sb.Append("{}");
                        break;
                    }

                    sb.Append("{\n");
                    var first = true;
                    foreach (var pair in o)
                    {
                        if (!first)
                        {
                            sb.Append(",\n");
                        }

                        first = false;
                        sb.Append(' ', indent + 2).Append(Quote(pair.Key)).Append(": ");
                        WriteValue(sb, pair.Value, indent + 2);
                    }

                    sb.Append('\n').Append(' ', indent).Append('}');
                    break;
                case System.Collections.IEnumerable list:
                    var items = new List<object?>();
                    foreach (var item in list)
                    {
                        items.Add(item);
                    }

                    if (items.Count == 0)
                    {
                        sb.Append("[]");
                        break;
                    }

                    sb.Append("[\n");
                    for (var k = 0; k < items.Count; k++)
                    {
                        if (k > 0)
                        {
                            sb.Append(",\n");
                        }

                        sb.Append(' ', indent + 2);
                        WriteValue(sb, items[k], indent + 2);
                    }

                    sb.Append('\n').Append(' ', indent).Append(']');
                    break;
                default:
                    throw new ArgumentException("JSON: cannot write " + value.GetType().Name);
            }
        }
    }
}
