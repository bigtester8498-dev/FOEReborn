using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FOEReLauncher
{
    /// <summary>One mod of the catalogue (downloads/mods/catalog.json on the download site).</summary>
    public sealed class CatalogEntry
    {
        public string Id { get; set; } = string.Empty;
        public string NameRu { get; set; } = string.Empty;
        public string NameEn { get; set; } = string.Empty;
        public string DescriptionRu { get; set; } = string.Empty;
        public string DescriptionEn { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;

        /// <summary>"data" or "code" (a code mod runs program code inside the game).</summary>
        public string Kind { get; set; } = "data";

        /// <summary>The game version range the mod is made for (empty: any).</summary>
        public string Game { get; set; } = string.Empty;

        public List<ModRequirement> Requires { get; set; } = new List<ModRequirement>();
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;

        /// <summary>Absolute url of the archive (any release asset).</summary>
        public string Url { get; set; } = string.Empty;

        public string Homepage { get; set; } = string.Empty;

        /// <summary>The id of the group the mod is listed under (empty: on its own).</summary>
        public string Group { get; set; } = string.Empty;

        public bool IsCode => Kind == "code";

        public string Name(string language) => language == "ru" && NameRu.Length > 0 ? NameRu : NameEn.Length > 0 ? NameEn : Id;

        public string Description(string language) => language == "ru" && DescriptionRu.Length > 0 ? DescriptionRu : DescriptionEn;
    }

    /// <summary>A group of mods (all the example mods, say): listed together, installed and enabled together.</summary>
    public sealed class ModGroupInfo
    {
        public string Id { get; set; } = string.Empty;
        public string NameRu { get; set; } = string.Empty;
        public string NameEn { get; set; } = string.Empty;
        public string DescriptionRu { get; set; } = string.Empty;
        public string DescriptionEn { get; set; } = string.Empty;

        /// <summary>Lower first; groups with the same order go by name.</summary>
        public int Order { get; set; }

        public string Name(string language) => language == "ru" && NameRu.Length > 0 ? NameRu : NameEn.Length > 0 ? NameEn : Id;

        public string Description(string language) => language == "ru" && DescriptionRu.Length > 0 ? DescriptionRu : DescriptionEn;

        /// <summary>A group known only by its folder: its folder name is its name.</summary>
        public static ModGroupInfo FromFolder(string id) => new ModGroupInfo { Id = id, NameEn = id, NameRu = id, Order = int.MaxValue / 2 };

        /// <summary>Reads one group object; null when the id is missing or unsafe.</summary>
        internal static ModGroupInfo? Read(Dictionary<string, object?> m)
        {
            var g = new ModGroupInfo { Id = Json.Str(m, "id") ?? string.Empty };
            if (!ModCatalog.IsSafeId(g.Id))
            {
                return null;
            }

            ModCatalog.ReadTexts(m, "name", out var nameRu, out var nameEn);
            ModCatalog.ReadTexts(m, "description", out var descRu, out var descEn);
            g.NameRu = nameRu;
            g.NameEn = nameEn;
            g.DescriptionRu = descRu;
            g.DescriptionEn = descEn;
            g.Order = m.TryGetValue("order", out var order) && order is long l ? (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, l)) : 0;
            return g;
        }

        internal Dictionary<string, object?> ToJson() => new Dictionary<string, object?>
        {
            ["id"] = Id,
            ["name"] = new Dictionary<string, object?> { ["ru"] = NameRu, ["en"] = NameEn },
            ["description"] = new Dictionary<string, object?> { ["ru"] = DescriptionRu, ["en"] = DescriptionEn },
            ["order"] = (long)Order
        };
    }

    /// <summary>The mod catalogue: format version, groups and entries.</summary>
    public sealed class ModCatalog
    {
        public const int Format = 1;

        private static readonly Regex IdPattern = new Regex(@"^[a-z0-9][a-z0-9_.\-]*$");
        private static readonly Regex ShaPattern = new Regex("^[0-9a-fA-F]{64}$");

        public List<CatalogEntry> Entries { get; } = new List<CatalogEntry>();

        /// <summary>The groups the entries refer to ("groups" of catalog.json; launchers before groups ignore it).</summary>
        public List<ModGroupInfo> Groups { get; } = new List<ModGroupInfo>();

        public CatalogEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);

        public ModGroupInfo? FindGroup(string id) => id.Length == 0 ? null : Groups.FirstOrDefault(g => g.Id == id);

        internal static bool IsSafeId(string id) => IdPattern.IsMatch(id);

        /// <summary>Reads the catalogue. Entries with an unsafe id, a relative or non-http url, a missing hash or size
        /// are left out (a broken entry must not break the list); a newer format or a non-object throws.</summary>
        public static ModCatalog Parse(string text)
        {
            var o = Json.Obj(Json.Parse(text.TrimStart('﻿')));
            var format = o.ContainsKey("format") ? Json.Long(o, "format") : Format;
            if (format > Format)
            {
                throw new FormatException("catalogue format " + format + " is newer than this launcher reads (" + Format + ")");
            }

            var catalog = new ModCatalog();
            foreach (var item in o.TryGetValue("groups", out var groups) && groups is List<object?> gl ? gl : new List<object?>())
            {
                if (item is Dictionary<string, object?> go && ModGroupInfo.Read(go) is ModGroupInfo g && catalog.FindGroup(g.Id) == null)
                {
                    catalog.Groups.Add(g);
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in o.TryGetValue("mods", out var mods) && mods is List<object?> list ? list : new List<object?>())
            {
                if (!(item is Dictionary<string, object?> m))
                {
                    continue;
                }

                var e = new CatalogEntry
                {
                    Id = Json.Str(m, "id") ?? string.Empty,
                    Author = Json.Str(m, "author") ?? string.Empty,
                    Version = Json.Str(m, "version") ?? string.Empty,
                    Kind = Json.Str(m, "kind") == "code" ? "code" : "data",
                    Game = Json.Str(m, "game") ?? string.Empty,
                    Sha256 = (Json.Str(m, "sha256") ?? string.Empty).ToLowerInvariant(),
                    Url = Json.Str(m, "url") ?? string.Empty,
                    Homepage = Json.Str(m, "homepage") ?? string.Empty,
                    Group = Json.Str(m, "group") ?? string.Empty
                };
                if (!IdPattern.IsMatch(e.Group))
                {
                    e.Group = string.Empty;
                }
                ReadTexts(m, "name", out var nameRu, out var nameEn);
                ReadTexts(m, "description", out var descRu, out var descEn);
                e.NameRu = nameRu;
                e.NameEn = nameEn;
                e.DescriptionRu = descRu;
                e.DescriptionEn = descEn;
                e.Size = m.TryGetValue("size", out var size) && size is long l ? l : 0;
                if (m.TryGetValue("requires", out var reqs) && reqs is List<object?> rl)
                {
                    foreach (var r in rl)
                    {
                        if (r is Dictionary<string, object?> ro && !string.IsNullOrEmpty(Json.Str(ro, "id")))
                        {
                            e.Requires.Add(new ModRequirement(Json.Str(ro, "id"), Json.Str(ro, "version")));
                        }
                    }
                }

                if (!IdPattern.IsMatch(e.Id) || !ModVersion.IsVersion(e.Version) || !ShaPattern.IsMatch(e.Sha256) || e.Size <= 0 ||
                    !Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                    !seen.Add(e.Id))
                {
                    continue;
                }

                catalog.Entries.Add(e);
            }

            // A group the list does not describe still groups its mods, under its id.
            foreach (var id in catalog.Entries.Select(x => x.Group).Where(x => x.Length > 0).Distinct().ToList())
            {
                if (catalog.FindGroup(id) == null)
                {
                    catalog.Groups.Add(ModGroupInfo.FromFolder(id));
                }
            }

            return catalog;
        }

        /// <summary>A text field as {"ru": .., "en": ..} or a plain string (English).</summary>
        internal static void ReadTexts(Dictionary<string, object?> m, string key, out string ru, out string en)
        {
            ru = en = string.Empty;
            if (!m.TryGetValue(key, out var v))
            {
                return;
            }

            if (v is string s)
            {
                en = s;
            }
            else if (v is Dictionary<string, object?> t)
            {
                ru = Json.Str(t, "ru") ?? string.Empty;
                en = Json.Str(t, "en") ?? string.Empty;
            }
        }
    }

    /// <summary>A file of a mod the launcher installed.</summary>
    public sealed class InstalledModFile
    {
        public InstalledModFile(string path, long size, string sha256)
        {
            Path = path;
            Size = size;
            Sha256 = sha256;
        }

        /// <summary>Relative to the game folder, forward slashes ("Mods/example_home/package.json").</summary>
        public string Path { get; }

        public long Size { get; }

        public string Sha256 { get; }
    }

    /// <summary>A mod the launcher installed from the catalogue.</summary>
    public sealed class InstalledMod
    {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;

        /// <summary>The mod folder relative to the game folder ("Mods/example_home").</summary>
        public string Dir { get; set; } = string.Empty;

        public string InstalledAt { get; set; } = string.Empty;

        /// <summary>The catalogue group the mod was installed from (empty: none).</summary>
        public string Group { get; set; } = string.Empty;

        public List<InstalledModFile> Files { get; set; } = new List<InstalledModFile>();
    }

    /// <summary>installed-mods.json beside installed-files.json: what the launcher installed from the catalogue.
    /// Game updates never touch these files (they are not in installed-files.json).</summary>
    public sealed class InstalledMods
    {
        public List<InstalledMod> Mods { get; } = new List<InstalledMod>();

        /// <summary>Names and descriptions of the groups of the installed mods, kept from the catalogue so the Mods tab
        /// can show them without a network.</summary>
        public List<ModGroupInfo> Groups { get; } = new List<ModGroupInfo>();

        public InstalledMod? Find(string id) => Mods.FirstOrDefault(m => m.Id == id);

        public ModGroupInfo? FindGroup(string id) => id.Length == 0 ? null : Groups.FirstOrDefault(g => g.Id == id);

        /// <summary>Keeps the texts of a group (replaces older ones).</summary>
        public void KeepGroup(ModGroupInfo? group)
        {
            if (group == null)
            {
                return;
            }

            Groups.RemoveAll(g => g.Id == group.Id);
            Groups.Add(group);
        }

        public static InstalledMods Load(string path)
        {
            var res = new InstalledMods();
            try
            {
                if (!File.Exists(path))
                {
                    return res;
                }

                var o = Json.Obj(Json.Parse(File.ReadAllText(path)));
                foreach (var item in o.TryGetValue("mods", out var v) && v is List<object?> list ? list : new List<object?>())
                {
                    if (!(item is Dictionary<string, object?> m))
                    {
                        continue;
                    }

                    var mod = new InstalledMod
                    {
                        Id = Json.Str(m, "id") ?? string.Empty,
                        Version = Json.Str(m, "version") ?? string.Empty,
                        Sha256 = Json.Str(m, "sha256") ?? string.Empty,
                        Dir = Json.Str(m, "dir") ?? string.Empty,
                        InstalledAt = Json.Str(m, "installedAt") ?? string.Empty,
                        Group = Json.Str(m, "group") ?? string.Empty
                    };
                    if (!ModCatalog.IsSafeId(mod.Group))
                    {
                        mod.Group = string.Empty;
                    }

                    if (m.TryGetValue("files", out var files) && files is List<object?> fl)
                    {
                        foreach (var f in fl)
                        {
                            if (f is Dictionary<string, object?> fo && PathSafety.IsSafeRelativePath(Json.Str(fo, "path")))
                            {
                                mod.Files.Add(new InstalledModFile(Json.Str(fo, "path")!, fo.ContainsKey("size") ? Json.Long(fo, "size") : 0, Json.Str(fo, "sha256") ?? string.Empty));
                            }
                        }
                    }

                    if (mod.Id.Length > 0 && PathSafety.IsSafeRelativePath(mod.Dir))
                    {
                        res.Mods.Add(mod);
                    }
                }

                foreach (var item in o.TryGetValue("groups", out var gv) && gv is List<object?> gl ? gl : new List<object?>())
                {
                    if (item is Dictionary<string, object?> go && ModGroupInfo.Read(go) is ModGroupInfo g && res.FindGroup(g.Id) == null)
                    {
                        res.Groups.Add(g);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException)
            {
                // An unreadable record: the mods count as added by hand, nothing is removed blindly.
            }

            return res;
        }

        public void Save(string path)
        {
            var mods = Mods.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m =>
            {
                var o = new Dictionary<string, object?>
                {
                    ["id"] = m.Id,
                    ["version"] = m.Version,
                    ["sha256"] = m.Sha256,
                    ["dir"] = m.Dir,
                    ["installedAt"] = m.InstalledAt
                };
                if (m.Group.Length > 0)
                {
                    o["group"] = m.Group;
                }

                o["files"] = m.Files.Select(f => (object?)new Dictionary<string, object?> { ["path"] = f.Path, ["size"] = f.Size, ["sha256"] = f.Sha256 }).ToList();
                return (object?)o;
            }).ToList();
            var root = new Dictionary<string, object?> { ["format"] = 1, ["mods"] = mods };
            var used = Groups.Where(g => Mods.Any(m => m.Group == g.Id)).OrderBy(g => g.Id, StringComparer.Ordinal).Select(g => (object?)g.ToJson()).ToList();
            if (used.Count > 0)
            {
                root["groups"] = used;
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, Json.Write(root));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}
