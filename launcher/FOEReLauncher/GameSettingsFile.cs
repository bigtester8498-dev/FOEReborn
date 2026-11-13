using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FOEReLauncher
{
    /// <summary>
    /// The game's settings.json (Unity persistentDataPath, %USERPROFILE%\AppData\LocalLow\FOEReborn\FOE Reborn\).
    /// The launcher reads and writes only its "mods" section; the rest of the file stays byte for byte, because the
    /// new section is spliced into the original text in place of the old one. Writes go through a temp file.
    /// </summary>
    public sealed class GameSettingsFile
    {
        public GameSettingsFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static string DefaultPath =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "FOEReborn", "FOE Reborn", "settings.json");

        public bool Exists => File.Exists(Path);

        /// <summary>The saved mod choices; an empty selection when the file or the section is missing or unreadable
        /// (the game then starts with its defaults too).</summary>
        public ModSelection Load()
        {
            try
            {
                if (!File.Exists(Path))
                {
                    return new ModSelection();
                }

                var root = Json.Parse(File.ReadAllText(Path).TrimStart('﻿')) as Dictionary<string, object?>;
                if (root == null)
                {
                    return new ModSelection();
                }

                foreach (var pair in root)
                {
                    // Newtonsoft matches property names case-insensitively; so does this.
                    if (string.Equals(pair.Key, "mods", StringComparison.OrdinalIgnoreCase))
                    {
                        return ModSelection.FromJson(pair.Value);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException)
            {
                // The game uses defaults for an unreadable file.
            }

            return new ModSelection();
        }

        /// <summary>Writes the "mods" section. Other keys, their values and their order are kept as text.</summary>
        public void Save(ModSelection sel)
        {
            var original = File.Exists(Path) ? File.ReadAllText(Path) : null;
            var text = Splice(original, sel);
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = Path + ".launcher.tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(Path))
            {
                File.Replace(temp, Path, null);
            }
            else
            {
                File.Move(temp, Path);
            }
        }

        /// <summary>The settings text with the "mods" value replaced (or added before the closing brace). A missing or
        /// unreadable file becomes an object with only "mods", which the game reads with defaults for the rest.</summary>
        public static string Splice(string? original, ModSelection sel)
        {
            var value = WriteMods(sel, "  ");
            if (original != null)
            {
                var bom = original.StartsWith("﻿", StringComparison.Ordinal) ? "﻿" : string.Empty;
                var body = original.Substring(bom.Length);
                try
                {
                    Json.Parse(body);
                    if (FindTopLevelValue(body, "mods", out var start, out var end))
                    {
                        return bom + body.Substring(0, start) + value + body.Substring(end);
                    }

                    var close = body.LastIndexOf('}');
                    if (close > 0 && body.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    {
                        var before = body.Substring(0, close).TrimEnd();
                        var empty = before.EndsWith("{", StringComparison.Ordinal);
                        return bom + before + (empty ? string.Empty : ",") + "\r\n  \"mods\": " + value + "\r\n" + body.Substring(close);
                    }
                }
                catch (FormatException)
                {
                    // Unreadable: the game would use defaults; write a file it can read.
                }
            }

            return "{\r\n  \"mods\": " + value + "\r\n}";
        }

        /// <summary>The section in the game's own layout (Newtonsoft, indented by two spaces).</summary>
        private static string WriteMods(ModSelection sel, string indent)
        {
            var inner = indent + "  ";
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append(inner).Append("\"enabled\": ").Append(WriteList(sel.Enabled, inner)).Append(",\r\n");
            sb.Append(inner).Append("\"disabled\": ").Append(WriteList(sel.Disabled, inner)).Append(",\r\n");
            sb.Append(inner).Append("\"order\": ").Append(WriteList(sel.Order, inner)).Append(",\r\n");
            sb.Append(inner).Append("\"consent\": ");
            if (sel.Consent.Count == 0)
            {
                sb.Append("{}");
            }
            else
            {
                sb.Append("{\r\n");
                var first = true;
                foreach (var pair in sel.Consent)
                {
                    if (!first)
                    {
                        sb.Append(",\r\n");
                    }

                    first = false;
                    sb.Append(inner).Append("  ").Append(Json.Quote(pair.Key)).Append(": ").Append(Json.Quote(pair.Value));
                }

                sb.Append("\r\n").Append(inner).Append('}');
            }

            sb.Append("\r\n").Append(indent).Append('}');
            return sb.ToString();
        }

        private static string WriteList(List<string> items, string indent)
        {
            if (items.Count == 0)
            {
                return "[]";
            }

            var sb = new StringBuilder("[\r\n");
            for (var i = 0; i < items.Count; i++)
            {
                sb.Append(indent).Append("  ").Append(Json.Quote(items[i]));
                sb.Append(i < items.Count - 1 ? ",\r\n" : "\r\n");
            }

            return sb.Append(indent).Append(']').ToString();
        }

        /// <summary>Finds the text span of a top-level key's value in a JSON object (the key compared without case).</summary>
        internal static bool FindTopLevelValue(string t, string key, out int start, out int end)
        {
            start = end = -1;
            var i = 0;
            Skip(t, ref i);
            if (i >= t.Length || t[i] != '{')
            {
                return false;
            }

            i++;
            while (true)
            {
                Skip(t, ref i);
                if (i >= t.Length || t[i] == '}')
                {
                    return false;
                }

                if (t[i] == ',')
                {
                    i++;
                    continue;
                }

                var keyStart = i;
                SkipString(t, ref i);
                var name = (string)Json.Parse(t.Substring(keyStart, i - keyStart))!;
                Skip(t, ref i);
                i++; // ':'
                Skip(t, ref i);
                var valueStart = i;
                SkipValue(t, ref i);
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                {
                    start = valueStart;
                    end = i;
                    return true;
                }
            }
        }

        private static void Skip(string t, ref int i)
        {
            while (i < t.Length && char.IsWhiteSpace(t[i]))
            {
                i++;
            }
        }

        private static void SkipString(string t, ref int i)
        {
            i++;
            while (i < t.Length && t[i] != '"')
            {
                i += t[i] == '\\' ? 2 : 1;
            }

            i++;
        }

        private static void SkipValue(string t, ref int i)
        {
            if (t[i] == '"')
            {
                SkipString(t, ref i);
                return;
            }

            if (t[i] == '{' || t[i] == '[')
            {
                var depth = 0;
                while (i < t.Length)
                {
                    var c = t[i];
                    if (c == '"')
                    {
                        SkipString(t, ref i);
                        continue;
                    }

                    if (c == '{' || c == '[')
                    {
                        depth++;
                    }
                    else if (c == '}' || c == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            i++;
                            return;
                        }
                    }

                    i++;
                }

                return;
            }

            while (i < t.Length && t[i] != ',' && t[i] != '}' && !char.IsWhiteSpace(t[i]))
            {
                i++;
            }
        }
    }
}
