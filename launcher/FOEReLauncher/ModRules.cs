using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FOEReLauncher
{
    /// <summary>One "requires" or "incompatible" entry of a mod manifest: a package id and a version range.</summary>
    public sealed class ModRequirement
    {
        public ModRequirement(string? id, string? range)
        {
            Id = id ?? string.Empty;
            Range = range;
        }

        public string Id { get; }

        /// <summary>A version range (<see cref="ModVersion.Satisfies"/>); null or empty means any version.</summary>
        public string? Range { get; }

        public override string ToString() => string.IsNullOrWhiteSpace(Range) ? Id : Id + " " + Range;
    }

    /// <summary>A mod's package.json, read the way the game reads it (field names match case-insensitively,
    /// numbers given for text fields count as their text).</summary>
    public sealed class ModManifest
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;

        /// <summary>The range of game versions the mod is made for; a mismatch is only a warning.</summary>
        public string Game { get; set; } = string.Empty;

        public List<ModRequirement> Requires { get; set; } = new List<ModRequirement>();
        public List<string> LoadAfter { get; set; } = new List<string>();
        public List<string> LoadBefore { get; set; } = new List<string>();
        public List<ModRequirement> Incompatible { get; set; } = new List<ModRequirement>();

        /// <summary>Program code of the mod (DLLs relative to its folder): a "code mod".</summary>
        public List<string> Assemblies { get; set; } = new List<string>();

        public bool DefaultEnabled { get; set; }

        /// <summary>Keys the manifest has that are not read into a field above.</summary>
        public List<string> OtherKeys { get; } = new List<string>();

        public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;

        /// <summary>Reads a manifest; throws <see cref="FormatException"/> when the text is not a JSON object of
        /// the right shape, and returns null for an empty file or a JSON null (the game calls both "empty").</summary>
        public static ModManifest? Parse(string text)
        {
            text = text.TrimStart('﻿');
            if (text.Trim().Length == 0)
            {
                return null;
            }

            var value = Json.Parse(text);
            if (value == null)
            {
                return null;
            }

            if (!(value is Dictionary<string, object?> o))
            {
                throw new FormatException("the manifest is not a JSON object");
            }

            var m = new ModManifest();
            foreach (var pair in o)
            {
                switch (pair.Key.ToLowerInvariant())
                {
                    case "id": m.Id = Text(pair.Value, pair.Key); break;
                    case "name": m.Name = Text(pair.Value, pair.Key); break;
                    case "version": m.Version = Text(pair.Value, pair.Key); break;
                    case "description": m.Description = Text(pair.Value, pair.Key); break;
                    case "author": m.Author = Text(pair.Value, pair.Key); break;
                    case "game": m.Game = Text(pair.Value, pair.Key); break;
                    case "requires": m.Requires = Requirements(pair.Value, pair.Key); break;
                    case "incompatible": m.Incompatible = Requirements(pair.Value, pair.Key); break;
                    case "loadafter": m.LoadAfter = TextList(pair.Value, pair.Key); break;
                    case "loadbefore": m.LoadBefore = TextList(pair.Value, pair.Key); break;
                    case "assemblies": m.Assemblies = TextList(pair.Value, pair.Key); break;
                    case "defaultenabled": m.DefaultEnabled = Flag(pair.Value, pair.Key); break;
                    default: m.OtherKeys.Add(pair.Key); break;
                }
            }

            return m;
        }

        private static string Text(object? v, string key)
        {
            switch (v)
            {
                case null: return string.Empty;
                case string s: return s;
                case bool b: return b ? "True" : "False";
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                default: throw new FormatException("\"" + key + "\" must be text");
            }
        }

        private static bool Flag(object? v, string key)
        {
            switch (v)
            {
                case null: return false;
                case bool b: return b;
                case string s when bool.TryParse(s, out var parsed): return parsed;
                case long l: return l != 0;
                default: throw new FormatException("\"" + key + "\" must be true or false");
            }
        }

        private static List<string> TextList(object? v, string key)
        {
            var res = new List<string>();
            if (v == null)
            {
                return res;
            }

            if (!(v is List<object?> list))
            {
                throw new FormatException("\"" + key + "\" must be a list");
            }

            foreach (var item in list)
            {
                var s = item == null ? null : Text(item, key);
                if (!string.IsNullOrEmpty(s))
                {
                    res.Add(s!);
                }
            }

            return res;
        }

        private static List<ModRequirement> Requirements(object? v, string key)
        {
            var res = new List<ModRequirement>();
            if (v == null)
            {
                return res;
            }

            if (!(v is List<object?> list))
            {
                throw new FormatException("\"" + key + "\" must be a list");
            }

            foreach (var item in list)
            {
                if (item == null)
                {
                    continue;
                }

                if (!(item is Dictionary<string, object?> o))
                {
                    throw new FormatException("\"" + key + "\" entries must be objects with \"id\" and \"version\"");
                }

                string? id = null, range = null;
                foreach (var pair in o)
                {
                    var k = pair.Key.ToLowerInvariant();
                    if (k == "id")
                    {
                        id = pair.Value == null ? null : Text(pair.Value, key);
                    }
                    else if (k == "version")
                    {
                        range = pair.Value == null ? null : Text(pair.Value, key);
                    }
                }

                res.Add(new ModRequirement(id, range));
            }

            return res;
        }
    }

    /// <summary>What happened to a package, with the same names and meaning as the game's mod manager.</summary>
    public enum ModState
    {
        Active,
        Disabled,
        Invalid,
        Duplicate,
        MissingDependency,
        DependencyInactive,
        WrongVersion,
        Incompatible,
        Cycle
    }

    /// <summary>A package found in Content/ or Mods/ and what the rules decided for it.</summary>
    public sealed class ModPackage
    {
        public ModPackage(ModManifest manifest, string dir, string origin, string relDir, int discovery)
        {
            Manifest = manifest;
            Dir = dir;
            Origin = origin;
            RelDir = relDir;
            Discovery = discovery;
        }

        public ModManifest Manifest { get; }

        public string Dir { get; }

        /// <summary>"Content" or "Mods".</summary>
        public string Origin { get; }

        /// <summary>"Mods/examples/example_data": the folder below the game folder.</summary>
        public string RelDir { get; }

        public int Discovery { get; }

        /// <summary>A package of the game's Content/ folder: always on, never listed as a mod.</summary>
        public bool Core => Origin == "Content";

        public bool Enabled { get; set; }

        public ModState State { get; set; } = ModState.Active;

        /// <summary>Why the package is not active, in English, as the game's log and console word it.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>The reason as a key of <see cref="ModRules.ReasonPatterns"/> plus its arguments; an argument that
        /// is a package stands for that package's own reason.</summary>
        public string ReasonKey { get; set; } = string.Empty;

        public object[] ReasonArgs { get; set; } = new object[0];

        /// <summary>Manifest warnings and errors, each starting with "warning: " or "error: ".</summary>
        public List<string> Issues { get; } = new List<string>();

        /// <summary>Position among the loaded packages, -1 when not loaded.</summary>
        public int LoadIndex { get; set; } = -1;

        public string Id => Manifest.Id;

        public bool Active => State == ModState.Active;

        public bool HasCode => Manifest.Assemblies.Count > 0;

        public override string ToString() => Id + " " + Manifest.Version + " (" + Origin + ")";
    }

    /// <summary>The "mods" section of the game's settings.json: the player's choices.</summary>
    public sealed class ModSelection
    {
        public List<string> Enabled { get; set; } = new List<string>();

        /// <summary>Mods the player switched off (keeps a defaultEnabled mod off).</summary>
        public List<string> Disabled { get; set; } = new List<string>();

        /// <summary>The player's load order of mods, first loads first.</summary>
        public List<string> Order { get; set; } = new List<string>();

        /// <summary>Code mods the player allowed: id -> the version allowed.</summary>
        public Dictionary<string, string> Consent { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool IsEnabled(string id) => Enabled.Contains(id);

        public bool HasChoice(string id) => Enabled.Contains(id) || Disabled.Contains(id);

        public bool HasConsent(string id, string version) => Consent.TryGetValue(id, out var v) && v == (version ?? string.Empty);

        public void SetEnabled(string id, bool on)
        {
            Enabled.Remove(id);
            Disabled.Remove(id);
            (on ? Enabled : Disabled).Add(id);
            if (!Order.Contains(id))
            {
                Order.Add(id);
            }
        }

        /// <summary>Turns on the defaultEnabled mods the player made no choice for, as the game does at start.</summary>
        public List<string> ApplyDefaults(IEnumerable<string> defaultOn)
        {
            var added = new List<string>();
            foreach (var id in defaultOn)
            {
                if (string.IsNullOrEmpty(id) || HasChoice(id))
                {
                    continue;
                }

                Enabled.Add(id);
                if (!Order.Contains(id))
                {
                    Order.Add(id);
                }

                added.Add(id);
            }

            return added;
        }

        /// <summary>Moves a mod in the order by <paramref name="delta"/> places; ids of <paramref name="known"/> not yet in
        /// the order are appended first, so the list on screen and the saved order agree.</summary>
        public void Move(string id, int delta, IEnumerable<string> known)
        {
            foreach (var k in known)
            {
                if (!Order.Contains(k))
                {
                    Order.Add(k);
                }
            }

            var i = Order.IndexOf(id);
            if (i < 0)
            {
                return;
            }

            var j = Math.Max(0, Math.Min(Order.Count - 1, i + delta));
            Order.RemoveAt(i);
            Order.Insert(j, id);
        }

        /// <summary>Drops empty and repeated ids; an id both enabled and disabled counts as enabled.</summary>
        public void Normalize()
        {
            Enabled = Enabled.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            Disabled = Disabled.Where(s => !string.IsNullOrEmpty(s) && !Enabled.Contains(s)).Distinct().ToList();
            Order = Order.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        }

        public ModSelection Clone() => new ModSelection
        {
            Enabled = new List<string>(Enabled),
            Disabled = new List<string>(Disabled),
            Order = new List<string>(Order),
            Consent = new Dictionary<string, string>(Consent, StringComparer.Ordinal)
        };

        public static ModSelection FromJson(object? value)
        {
            var sel = new ModSelection();
            if (value is Dictionary<string, object?> o)
            {
                sel.Enabled = Strings(o, "enabled");
                sel.Disabled = Strings(o, "disabled");
                sel.Order = Strings(o, "order");
                if (Find(o, "consent") is Dictionary<string, object?> c)
                {
                    foreach (var pair in c)
                    {
                        if (pair.Value is string v)
                        {
                            sel.Consent[pair.Key] = v;
                        }
                    }
                }
            }

            sel.Normalize();
            return sel;
        }

        private static object? Find(Dictionary<string, object?> o, string key)
        {
            foreach (var pair in o)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        private static List<string> Strings(Dictionary<string, object?> o, string key)
        {
            var res = new List<string>();
            if (Find(o, key) is List<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is string s)
                    {
                        res.Add(s);
                    }
                }
            }

            return res;
        }

        /// <summary>The section as the game writes it (enabled, disabled, order, consent).</summary>
        public Dictionary<string, object?> ToJson() => new Dictionary<string, object?>
        {
            ["enabled"] = Enabled.Cast<object?>().ToList(),
            ["disabled"] = Disabled.Cast<object?>().ToList(),
            ["order"] = Order.Cast<object?>().ToList(),
            ["consent"] = Consent.ToDictionary(p => p.Key, p => (object?)p.Value)
        };
    }

    /// <summary>
    /// The game's mod rules, repeated for the launcher: discovery of packages in Content/ and Mods/ (one level of
    /// group folders inside Mods/), manifest checks, version ranges, which packages load and in which order, and the
    /// enable/disable cascades of the mod manager. modrules-cases.json is run against this class and against the
    /// game, so both answer the same.
    /// </summary>
    public static class ModRules
    {
        /// <summary>The game version that manifests' "game" ranges are checked against.</summary>
        public const string GameVersion = "1.0.2";

        private static readonly Regex IdPattern = new Regex(@"^[a-z0-9][a-z0-9_.\-]*$");

        /// <summary>Manifest keys the game knows besides the fields it reads.</summary>
        private static readonly HashSet<string> KnownExtra = new HashSet<string>(StringComparer.Ordinal)
        {
            "generated", "url", "tags", "$schema"
        };

        /// <summary>Reason texts by key: English (the game's log) and Russian (the game's ru texts).</summary>
        public static readonly Dictionary<string, string[]> ReasonPatterns = new Dictionary<string, string[]>
        {
            ["unreadable"] = new[] { "package.json cannot be read: {0}", "package.json не читается: {0}" },
            ["manifest"] = new[] { "{0}", "{0}" },
            ["duplicate"] = new[] { "duplicate package id '{0}' (also in {1}), this copy skipped", "повтор id пакета «{0}» (есть и в {1}), эта копия пропущена" },
            ["disabled"] = new[] { "disabled", "выключен" },
            ["missing"] = new[] { "requires '{0}', which is not installed", "требует «{0}», а он не установлен" },
            ["version"] = new[] { "requires '{0}' {1}, found {2}", "требует «{0}» {1}, найдена версия {2}" },
            ["depoff"] = new[] { "requires '{0}' ({1}), which is disabled: enable it too", "требует «{0}» ({1}), а он выключен: включите и его" },
            ["depfailed"] = new[] { "requires '{0}', which failed to load ({1})", "требует «{0}», а он не загрузился ({1})" },
            ["incompatible"] = new[] { "incompatible with '{0}' ({1} {2})", "несовместим с «{0}» ({1} {2})" },
            ["depnotloaded"] = new[] { "requires '{0}', which is not loaded ({1})", "требует «{0}», а он не загружен ({1})" },
            ["cycle"] = new[] { "load order cycle with {0}", "цикл порядка загрузки с {0}" }
        };

        // ------------------------------------------------------------- discovery

        /// <summary>Finds packages under the given roots (the root folder's name is the origin: "Content", "Mods").</summary>
        public static List<ModPackage> Discover(params string[] roots)
        {
            var found = new List<ModPackage>();
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    continue;
                }

                var origin = Path.GetFileName(root.TrimEnd('/', '\\'));
                Scan(root, origin, origin, 0, found);
            }

            return found;
        }

        private static void Scan(string dir, string origin, string rel, int depth, List<ModPackage> found)
        {
            var subs = Directory.GetDirectories(dir).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal);
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                var relSub = rel + "/" + name;
                var manifestPath = Path.Combine(sub, "package.json");
                if (!File.Exists(manifestPath))
                {
                    // A group folder such as Mods/examples/: one level deep, never inside Content/.
                    if (depth == 0 && origin != "Content")
                    {
                        Scan(sub, origin, relSub, depth + 1, found);
                    }

                    continue;
                }

                ModManifest? m = null;
                string? error = null;
                try
                {
                    m = ModManifest.Parse(File.ReadAllText(manifestPath));
                }
                catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    error = ex.Message;
                }

                var pkg = new ModPackage(m ?? new ModManifest { Id = name }, sub, origin, relSub, found.Count);
                if (m == null || error != null)
                {
                    Fail(pkg, ModState.Invalid, "unreadable", error ?? "empty");
                    if (string.IsNullOrEmpty(pkg.Manifest.Id))
                    {
                        pkg.Manifest.Id = name;
                    }
                }
                else
                {
                    foreach (var issue in Validate(m, pkg.Core, sub))
                    {
                        pkg.Issues.Add(issue);
                        if (issue.StartsWith("error: ", StringComparison.Ordinal) && pkg.State == ModState.Active)
                        {
                            Fail(pkg, ModState.Invalid, "manifest", issue.Substring(7));
                        }
                    }

                    if (string.IsNullOrEmpty(m.Id))
                    {
                        m.Id = name;
                    }
                }

                var dup = found.FirstOrDefault(p => p.Id == pkg.Id);
                if (dup != null)
                {
                    Fail(pkg, ModState.Duplicate, "duplicate", pkg.Id, dup.RelDir);
                }

                found.Add(pkg);
            }
        }

        /// <summary>Checks a manifest; every line starts with "error: " or "warning: ".</summary>
        public static List<string> Validate(ModManifest m, bool core, string? dir = null)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(m.Id))
            {
                res.Add("error: the manifest has no \"id\"");
            }
            else if (!IdPattern.IsMatch(m.Id))
            {
                res.Add("error: id '" + m.Id + "' may only have lower-case letters, digits, '_', '.' and '-'");
            }

            if (string.IsNullOrEmpty(m.Version))
            {
                res.Add("error: the manifest has no \"version\"");
            }
            else if (!ModVersion.IsVersion(m.Version))
            {
                res.Add("error: version '" + m.Version + "' is not numbers separated by dots (1.0.2)");
            }

            foreach (var r in m.Requires)
            {
                if (string.IsNullOrEmpty(r.Id))
                {
                    res.Add("error: a \"requires\" entry has no id");
                }
                else if (r.Id == m.Id)
                {
                    res.Add("error: the package requires itself");
                }

                if (!ModVersion.IsRange(r.Range))
                {
                    res.Add("error: version range '" + r.Range + "' of '" + r.Id + "' cannot be read");
                }
            }

            foreach (var r in m.Incompatible)
            {
                if (!ModVersion.IsRange(r.Range))
                {
                    res.Add("error: version range '" + r.Range + "' of incompatible '" + r.Id + "' cannot be read");
                }
            }

            if (core)
            {
                return res;
            }

            foreach (var k in m.OtherKeys)
            {
                if (!KnownExtra.Contains(k))
                {
                    res.Add("warning: unknown manifest field \"" + k + "\" ignored");
                }
            }

            if (!string.IsNullOrWhiteSpace(m.Game) && !ModVersion.IsRange(m.Game))
            {
                res.Add("error: game version range '" + m.Game + "' cannot be read");
            }
            else if (!string.IsNullOrWhiteSpace(m.Game) && !ModVersion.Satisfies(GameVersion, m.Game))
            {
                res.Add("warning: made for game version " + m.Game + ", this is " + GameVersion);
            }

            if (dir != null)
            {
                foreach (var a in m.Assemblies)
                {
                    if (string.IsNullOrEmpty(a) || Path.IsPathRooted(a) || a.Contains(".."))
                    {
                        res.Add("error: assembly path '" + a + "' must be relative to the package folder");
                    }
                    else if (!File.Exists(Path.Combine(dir, a)))
                    {
                        res.Add("error: assembly '" + a + "' not found in the package (is the mod built?)");
                    }
                }
            }

            return res;
        }

        private static void Fail(ModPackage p, ModState state, string key, params object[] args)
        {
            p.State = state;
            p.ReasonKey = key;
            p.ReasonArgs = args;
            p.Reason = Format(ReasonPatterns[key][0], args.Select(a => a is ModPackage dep ? (object)dep.Reason : a).ToArray());
        }

        private static string Format(string pattern, object[] args) => string.Format(CultureInfo.InvariantCulture, pattern, args);

        /// <summary>The reason of a package in "en" or "ru"; nested reasons of required packages in the same language.
        /// The details of a broken manifest stay as written, for the mod's author.</summary>
        public static string ReasonText(ModPackage p, string language)
        {
            if (string.IsNullOrEmpty(p.ReasonKey) || !ReasonPatterns.TryGetValue(p.ReasonKey, out var patterns))
            {
                return p.Reason;
            }

            var args = p.ReasonArgs.Select(a => a is ModPackage dep ? (object)ReasonText(dep, language) : a).ToArray();
            return Format(language == "ru" ? patterns[1] : patterns[0], args);
        }

        // ---------------------------------------------------------------- order

        /// <summary>
        /// Decides which packages load and in which order, as the game does at start. <paramref name="sel"/> null
        /// enables everything; otherwise Content packages are always on and mods only when enabled. Ties of the
        /// topological order follow the player's order.
        /// </summary>
        public static List<ModPackage> Order(List<ModPackage> found, ModSelection? sel)
        {
            foreach (var p in found)
            {
                p.LoadIndex = -1;
                p.Enabled = p.Core || sel == null || sel.IsEnabled(p.Id);
                if (p.State == ModState.Active && !p.Enabled)
                {
                    Fail(p, ModState.Disabled, "disabled");
                }
            }

            var byId = new Dictionary<string, ModPackage>(StringComparer.Ordinal);
            foreach (var p in found)
            {
                if (p.State != ModState.Duplicate && !byId.ContainsKey(p.Id))
                {
                    byId[p.Id] = p;
                }
            }

            // Requirements, until nothing changes, so users of a skipped package are skipped too.
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var p in found)
                {
                    if (p.State != ModState.Active)
                    {
                        continue;
                    }

                    foreach (var r in p.Manifest.Requires)
                    {
                        if (string.IsNullOrEmpty(r.Id))
                        {
                            continue;
                        }

                        if (!byId.TryGetValue(r.Id, out var dep))
                        {
                            Fail(p, ModState.MissingDependency, "missing", r.ToString());
                        }
                        else if (!ModVersion.Satisfies(dep.Manifest.Version, r.Range))
                        {
                            Fail(p, ModState.WrongVersion, "version", r.Id, r.Range ?? string.Empty, dep.Manifest.Version);
                        }
                        else if (dep.State == ModState.Disabled)
                        {
                            Fail(p, ModState.DependencyInactive, "depoff", dep.Manifest.DisplayName, r.Id);
                        }
                        else if (dep.State != ModState.Active)
                        {
                            Fail(p, ModState.DependencyInactive, "depfailed", r.Id, dep);
                        }

                        if (p.State != ModState.Active)
                        {
                            changed = true;
                            break;
                        }
                    }
                }
            }

            // Incompatible pairs: of two active packages the one later in the player's order is dropped.
            var rank = Ranks(found, sel);
            foreach (var p in found.Where(x => x.State == ModState.Active).OrderBy(x => rank[x]).ToList())
            {
                if (p.State != ModState.Active)
                {
                    continue;
                }

                foreach (var r in p.Manifest.Incompatible)
                {
                    if (!byId.TryGetValue(r.Id, out var other) || other == p || other.State != ModState.Active)
                    {
                        continue;
                    }

                    if (!ModVersion.Satisfies(other.Manifest.Version, r.Range))
                    {
                        continue;
                    }

                    var loser = rank[other] > rank[p] && !other.Core ? other : p;
                    var winner = loser == p ? other : p;
                    Fail(loser, ModState.Incompatible, "incompatible", winner.Manifest.DisplayName, winner.Id, winner.Manifest.Version);
                    if (loser == p)
                    {
                        break;
                    }
                }
            }

            // A dropped incompatible package can leave users behind.
            changed = true;
            while (changed)
            {
                changed = false;
                foreach (var p in found.Where(x => x.State == ModState.Active))
                {
                    foreach (var r in p.Manifest.Requires)
                    {
                        if (byId.TryGetValue(r.Id, out var dep) && dep.State != ModState.Active)
                        {
                            Fail(p, ModState.DependencyInactive, "depnotloaded", r.Id, dep);
                            changed = true;
                            break;
                        }
                    }
                }
            }

            var nodes = found.Where(p => p.State == ModState.Active).ToList();
            var ok = new HashSet<string>(nodes.Select(p => p.Id), StringComparer.Ordinal);
            var deps = nodes.ToDictionary(p => p.Id, p => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var p in nodes)
            {
                foreach (var r in p.Manifest.Requires)
                {
                    if (ok.Contains(r.Id))
                    {
                        deps[p.Id].Add(r.Id);
                    }
                }

                foreach (var a in p.Manifest.LoadAfter)
                {
                    if (ok.Contains(a))
                    {
                        deps[p.Id].Add(a);
                    }
                }

                foreach (var b in p.Manifest.LoadBefore)
                {
                    if (ok.Contains(b) && b != p.Id)
                    {
                        deps[b].Add(p.Id);
                    }
                }

                deps[p.Id].Remove(p.Id);
            }

            var order = new List<ModPackage>();
            var done = new HashSet<string>(StringComparer.Ordinal);
            while (order.Count < nodes.Count)
            {
                var next = nodes.Where(p => !done.Contains(p.Id) && deps[p.Id].All(done.Contains)).OrderBy(p => rank[p]).FirstOrDefault();
                if (next == null)
                {
                    var rest = nodes.Where(p => !done.Contains(p.Id)).ToList();
                    foreach (var p in rest)
                    {
                        Fail(p, ModState.Cycle, "cycle", string.Join(", ", rest.Where(x => x != p).Select(x => x.Id)));
                    }

                    break;
                }

                next.LoadIndex = order.Count;
                order.Add(next);
                done.Add(next.Id);
            }

            return order;
        }

        /// <summary>Tie-break rank: Content packages in discovery order, then mods in the player's order, then the
        /// other mods in discovery order.</summary>
        private static Dictionary<ModPackage, int> Ranks(List<ModPackage> found, ModSelection? sel)
        {
            var r = new Dictionary<ModPackage, int>();
            foreach (var p in found)
            {
                var user = sel != null ? sel.Order.IndexOf(p.Id) : -1;
                r[p] = p.Core ? p.Discovery : user >= 0 ? 100000 + user : 200000 + p.Discovery;
            }

            return r;
        }

        /// <summary>The packages of a game folder with their states for a selection, the way the game's manager shows
        /// them. The selection is used as given (call <see cref="WithDefaults"/> first to see what the game starts with).</summary>
        public static List<ModPackage> Preview(string gameDir, ModSelection sel)
        {
            var all = Discover(Path.Combine(gameDir, "Content"), Path.Combine(gameDir, "Mods"));
            Order(all, sel);
            return all;
        }

        /// <summary>The selection the game starts with: the saved one plus the defaultEnabled mods without a choice.</summary>
        public static ModSelection WithDefaults(ModSelection saved, IEnumerable<ModPackage> found)
        {
            var sel = saved.Clone();
            sel.ApplyDefaults(found.Where(p => !p.Core && p.Manifest.DefaultEnabled).Select(p => p.Id));
            return sel;
        }

        /// <summary>The mods in the manager's list order: the player's order first, then discovery order; duplicates
        /// and Content packages are left out.</summary>
        public static List<ModPackage> ListOrder(IEnumerable<ModPackage> all, ModSelection sel) =>
            all.Where(p => !p.Core && p.State != ModState.Duplicate)
               .OrderBy(p =>
               {
                   var i = sel.Order.IndexOf(p.Id);
                   return i >= 0 ? i : 100000 + p.Discovery;
               })
               .ToList();

        // ------------------------------------------------------------- cascades

        /// <summary>The mods <paramref name="id"/> requires, directly or through others, that are not enabled, in the
        /// order to enable them (requirements first). Missing ones and Content packages are left out.</summary>
        public static List<string> ToEnable(string id, IEnumerable<ModPackage> all, ModSelection sel)
        {
            var byId = new Dictionary<string, ModPackage>(StringComparer.Ordinal);
            foreach (var p in all)
            {
                if (!byId.ContainsKey(p.Id))
                {
                    byId[p.Id] = p;
                }
            }

            var res = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Walk(string x)
            {
                if (!seen.Add(x) || !byId.TryGetValue(x, out var p))
                {
                    return;
                }

                foreach (var r in p.Manifest.Requires)
                {
                    if (string.IsNullOrEmpty(r.Id) || !byId.TryGetValue(r.Id, out var dep) || dep.Core)
                    {
                        continue;
                    }

                    Walk(r.Id);
                    if (!sel.IsEnabled(r.Id) && !res.Contains(r.Id))
                    {
                        res.Add(r.Id);
                    }
                }
            }

            Walk(id);
            res.Remove(id);
            return res;
        }

        /// <summary>The enabled mods that require <paramref name="id"/>, directly or through others.</summary>
        public static List<string> ToDisable(string id, IEnumerable<ModPackage> all, ModSelection sel)
        {
            var list = all.Where(p => !p.Core).ToList();
            var res = new List<string>();
            var queue = new Queue<string>();
            queue.Enqueue(id);
            while (queue.Count > 0)
            {
                var x = queue.Dequeue();
                foreach (var p in list)
                {
                    if (p.Id == id || res.Contains(p.Id) || !sel.IsEnabled(p.Id))
                    {
                        continue;
                    }

                    if (p.Manifest.Requires.Any(r => r.Id == x))
                    {
                        res.Add(p.Id);
                        queue.Enqueue(p.Id);
                    }
                }
            }

            return res;
        }
    }

    /// <summary>Mod versions: numeric dot parts (missing parts are 0, a "-suffix" is ignored) and the ranges of the
    /// manifests: "", "*", "1.0.2", "=1.0.2", "&gt;=1.0.2", "&gt;1.0", "&lt;=2", "&lt;2", "^1.2", "~1.2", "1.x",
    /// parts joined by spaces (all must hold) or "||" (any may hold).</summary>
    public static class ModVersion
    {
        private static readonly Regex VersionRx = new Regex(@"^\d+(\.\d+)*([-+][0-9A-Za-z.\-]+)?$");
        private static readonly Regex PartRx = new Regex(@"^(>=|<=|>|<|=|\^|~)?\s*(\d+(\.(\d+|x|\*))*([-+][0-9A-Za-z.\-]+)?)$");
        private static readonly string[] Operators = { ">=", "<=", ">", "<", "=", "^", "~" };

        public static int Compare(string? a, string? b)
        {
            var pa = Parts(a);
            var pb = Parts(b);
            for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                var x = i < pa.Length ? pa[i] : 0;
                var y = i < pb.Length ? pb[i] : 0;
                if (x != y)
                {
                    return x.CompareTo(y);
                }
            }

            return 0;
        }

        private static int[] Parts(string? v)
        {
            if (string.IsNullOrWhiteSpace(v))
            {
                return new int[0];
            }

            v = v!.Trim();
            var cut = v.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0)
            {
                v = v.Substring(0, cut);
            }

            return v.Split('.').Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        }

        public static bool IsVersion(string? v) => v != null && VersionRx.IsMatch(v.Trim());

        public static bool IsRange(string? range)
        {
            if (string.IsNullOrWhiteSpace(range) || range!.Trim() == "*")
            {
                return true;
            }

            foreach (var alt in range.Split(new[] { "||" }, StringSplitOptions.None))
            {
                var parts = Tokens(alt);
                if (parts.Count == 0 || parts.Any(p => !PartRx.IsMatch(p)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>"&gt;= 1.0 &lt;2" -> "&gt;=1.0", "&lt;2": an operator may stand apart from its number.</summary>
        private static List<string> Tokens(string s)
        {
            var raw = s.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var res = new List<string>();
            for (var i = 0; i < raw.Length; i++)
            {
                if (Operators.Contains(raw[i]) && i + 1 < raw.Length)
                {
                    res.Add(raw[i] + raw[i + 1]);
                    i++;
                }
                else
                {
                    res.Add(raw[i]);
                }
            }

            return res;
        }

        public static bool Satisfies(string? version, string? range)
        {
            if (string.IsNullOrWhiteSpace(range) || range!.Trim() == "*")
            {
                return true;
            }

            foreach (var alt in range.Split(new[] { "||" }, StringSplitOptions.None))
            {
                var parts = Tokens(alt);
                if (parts.Count > 0 && parts.All(p => One(version, p)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool One(string? version, string c)
        {
            var op = "=";
            foreach (var o in Operators)
            {
                if (c.StartsWith(o, StringComparison.Ordinal))
                {
                    op = o;
                    c = c.Substring(o.Length).Trim();
                    break;
                }
            }

            // Wildcards: 1.x and 1.2.* cover the range of their fixed parts.
            var segs = c.Split('.');
            var wild = Array.FindIndex(segs, s => s == "x" || s == "*" || s == "X");
            if (wild >= 0)
            {
                if (wild == 0)
                {
                    return true;
                }

                var low = string.Join(".", segs.Take(wild));
                return Compare(version, low) >= 0 && Compare(version, Bump(segs.Take(wild).ToArray(), wild - 1)) < 0;
            }

            var cmp = Compare(version, c);
            switch (op)
            {
                case ">=": return cmp >= 0;
                case "<=": return cmp <= 0;
                case ">": return cmp > 0;
                case "<": return cmp < 0;
                case "^":
                {
                    // ^1.2.3 = >=1.2.3 <2; ^0.2.3 = >=0.2.3 <0.3
                    var p = Parts(c);
                    var k = 0;
                    while (k < p.Length - 1 && p[k] == 0)
                    {
                        k++;
                    }

                    return cmp >= 0 && Compare(version, Bump(p.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(), k)) < 0;
                }

                case "~":
                {
                    // ~1.2.3 = >=1.2.3 <1.3; ~1 = >=1 <2
                    var p = Parts(c);
                    return cmp >= 0 && Compare(version, Bump(p.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(), p.Length > 1 ? 1 : 0)) < 0;
                }

                default: return cmp == 0;
            }
        }

        private static string Bump(string[] segs, int at)
        {
            var res = new List<string>();
            for (var i = 0; i <= at && i < segs.Length; i++)
            {
                res.Add(i == at ? ((int.TryParse(segs[i], out var n) ? n : 0) + 1).ToString(CultureInfo.InvariantCulture) : segs[i]);
            }

            return string.Join(".", res);
        }
    }
}
