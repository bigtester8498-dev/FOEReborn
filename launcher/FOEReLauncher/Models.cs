using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FOEReLauncher
{
    /// <summary>Which published stream of game builds the launcher follows: downloads/ or downloads/test/.</summary>
    public enum UpdateChannel
    {
        Prod,
        Test
    }

    /// <summary>One zip pack of a published build (a release asset).</summary>
    public sealed class PackInfo
    {
        public PackInfo(string name, string url, long size, string sha256)
        {
            Name = name;
            Url = url;
            Size = size;
            Sha256 = sha256;
        }

        /// <summary>File name, &lt;group&gt;-&lt;sha256 first 16&gt;.zip.</summary>
        public string Name { get; }

        /// <summary>Absolute download url; may point at an asset of an earlier release.</summary>
        public string Url { get; }

        public long Size { get; }

        public string Sha256 { get; }
    }

    /// <summary>One game file of a published build.</summary>
    public sealed class FileEntry
    {
        public FileEntry(string path, long size, string sha256, string pack)
        {
            Path = path;
            Size = size;
            Sha256 = sha256;
            Pack = pack;
        }

        /// <summary>Path inside game/, forward slashes; also the entry name inside the pack.</summary>
        public string Path { get; }

        public long Size { get; }

        public string Sha256 { get; }

        /// <summary>Name of the pack that holds the file.</summary>
        public string Pack { get; }
    }

    /// <summary>downloads/&lt;channel&gt;/manifest.json: every pack and every file of one build.</summary>
    public sealed class Manifest
    {
        public Manifest(string version, string builtAt, string game, IList<PackInfo> packs, IList<FileEntry> files)
        {
            Version = version;
            BuiltAt = builtAt;
            Game = game;
            Packs = packs;
            Files = files;
        }

        public string Version { get; }

        public string BuiltAt { get; }

        /// <summary>Game executable, relative to game/.</summary>
        public string Game { get; }

        public IList<PackInfo> Packs { get; }

        public IList<FileEntry> Files { get; }

        public long TotalSize
        {
            get
            {
                long total = 0;
                foreach (var f in Files)
                {
                    total += f.Size;
                }

                return total;
            }
        }

        public PackInfo? FindPack(string name)
        {
            foreach (var p in Packs)
            {
                if (string.Equals(p.Name, name, StringComparison.Ordinal))
                {
                    return p;
                }
            }

            return null;
        }

        /// <summary>
        /// Parses and validates a manifest: every path must be a safe relative path, every file must
        /// name a listed pack, no path may appear twice. Anything else throws FormatException.
        /// </summary>
        public static Manifest Parse(string text)
        {
            var root = Json.Obj(Json.Parse(text));
            var version = Json.Str(root, "version");
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new FormatException("manifest: no version");
            }

            var packs = new List<PackInfo>();
            var packNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in Json.Arr(root.TryGetValue("packs", out var p) ? p : null))
            {
                var o = Json.Obj(item);
                var name = Json.Str(o, "name") ?? throw new FormatException("manifest: pack without name");
                var url = Json.Str(o, "url") ?? throw new FormatException("manifest: pack without url");
                var sha = Json.Str(o, "sha256") ?? throw new FormatException("manifest: pack without sha256");
                if (!PathSafety.IsSafeRelativePath(name) || name.IndexOf('/') >= 0)
                {
                    throw new FormatException("manifest: bad pack name " + name);
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new FormatException("manifest: pack url must be absolute http(s): " + url);
                }

                if (!packNames.Add(name))
                {
                    throw new FormatException("manifest: pack listed twice: " + name);
                }

                packs.Add(new PackInfo(name, url, Json.Long(o, "size"), sha.ToLowerInvariant()));
            }

            var files = new List<FileEntry>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Json.Arr(root.TryGetValue("files", out var f) ? f : null))
            {
                var o = Json.Obj(item);
                var path = Json.Str(o, "path");
                if (!PathSafety.IsSafeRelativePath(path))
                {
                    throw new FormatException("manifest: unsafe path " + (path ?? "(none)"));
                }

                var pack = Json.Str(o, "pack") ?? string.Empty;
                if (!packNames.Contains(pack))
                {
                    throw new FormatException("manifest: " + path + " names an unknown pack " + pack);
                }

                if (!paths.Add(path!))
                {
                    throw new FormatException("manifest: path listed twice: " + path);
                }

                var sha = Json.Str(o, "sha256") ?? throw new FormatException("manifest: no sha256 for " + path);
                files.Add(new FileEntry(path!, Json.Long(o, "size"), sha.ToLowerInvariant(), pack));
            }

            var game = Json.Str(root, "game") ?? "FOEReborn.exe";
            if (!PathSafety.IsSafeRelativePath(game))
            {
                throw new FormatException("manifest: unsafe game path " + game);
            }

            return new Manifest(version!, Json.Str(root, "builtAt") ?? string.Empty, game, packs, files);
        }
    }

    /// <summary>One entry of notes.json: a version with its changes in Russian and English.</summary>
    public sealed class PatchNote
    {
        public PatchNote(string version, string date, IList<string> ru, IList<string> en)
        {
            Version = version;
            Date = date;
            Ru = ru;
            En = en;
        }

        public string Version { get; }

        public string Date { get; }

        public IList<string> Ru { get; }

        public IList<string> En { get; }

        /// <summary>Lines in the wanted language, falling back to the other one when it has none.</summary>
        public IList<string> Lines(string language)
        {
            var primary = language == "ru" ? Ru : En;
            return primary.Count > 0 ? primary : (language == "ru" ? En : Ru);
        }

        public string DisplayDate
        {
            get
            {
                if (DateTimeOffset.TryParse(Date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                return Date.Length >= 10 ? Date.Substring(0, 10) : Date;
            }
        }

        /// <summary>notes.json: {"entries":[{"version","date","ru":[...],"en":[...]}]}, newest first.</summary>
        public static IList<PatchNote> ParseList(string text)
        {
            var result = new List<PatchNote>();
            var root = Json.Parse(text);
            List<object?> entries;
            if (root is List<object?> list)
            {
                entries = list;
            }
            else
            {
                var o = Json.Obj(root);
                entries = o.TryGetValue("entries", out var e) && e is List<object?> l ? l : new List<object?>();
            }

            foreach (var item in entries)
            {
                if (!(item is Dictionary<string, object?> o))
                {
                    continue;
                }

                var version = Json.Str(o, "version");
                if (string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                result.Add(new PatchNote(version!, Json.Str(o, "date") ?? string.Empty, Json.Strings(o, "ru"), Json.Strings(o, "en")));
            }

            return result;
        }
    }

    /// <summary>
    /// The change history cut at the installed version: <see cref="New"/> holds the versions the player has not
    /// installed yet, <see cref="Old"/> the installed one and everything before it. Both keep the order of the list.
    /// </summary>
    public sealed class NotesSplit
    {
        private NotesSplit(string installed, IList<PatchNote> newer, IList<PatchNote> older)
        {
            Installed = installed;
            New = newer;
            Old = older;
        }

        /// <summary>The installed version as given; empty when the game is not installed.</summary>
        public string Installed { get; }

        public bool HasInstalled => Installed.Length > 0;

        public IList<PatchNote> New { get; }

        public IList<PatchNote> Old { get; }

        /// <summary>
        /// Versions are compared as numbers (0.1.592 is newer than 0.1.589), so the cut works even when the
        /// installed version is missing from the list or came from the other channel. With nothing installed every
        /// entry is new. An entry whose version is not a number is new only when it stands above the installed
        /// version in the list.
        /// </summary>
        public static NotesSplit Split(IEnumerable<PatchNote> notes, string? installed)
        {
            var current = installed?.Trim() ?? string.Empty;
            var list = new List<PatchNote>(notes);
            var newer = new List<PatchNote>();
            var older = new List<PatchNote>();
            if (current.Length == 0)
            {
                return new NotesSplit(string.Empty, list, older);
            }

            var currentParsed = LauncherService.TryParseVersion(current, out var currentVersion);
            var installedIndex = list.FindIndex(n => SameVersion(n.Version, current));
            for (var i = 0; i < list.Count; i++)
            {
                var note = list[i];
                bool isNew;
                if (currentParsed && LauncherService.TryParseVersion(note.Version, out var noteVersion))
                {
                    isNew = noteVersion > currentVersion;
                }
                else
                {
                    isNew = installedIndex >= 0 && i < installedIndex;
                }

                (isNew ? newer : older).Add(note);
            }

            return new NotesSplit(current, newer, older);
        }

        private static bool SameVersion(string a, string b) =>
            string.Equals(TrimV(a), TrimV(b), StringComparison.OrdinalIgnoreCase);

        private static string TrimV(string v)
        {
            var t = v.Trim();
            return t.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? t.Substring(1) : t;
        }
    }

    /// <summary>downloads/launcher-version.json: the newest launcher build on the site.</summary>
    public sealed class LauncherVersionInfo
    {
        public LauncherVersionInfo(string version, string builtAt, string file, string sha256, long size)
        {
            Version = version;
            BuiltAt = builtAt;
            File = file;
            Sha256 = sha256;
            Size = size;
        }

        public string Version { get; }

        public string BuiltAt { get; }

        /// <summary>Exe file name next to launcher-version.json.</summary>
        public string File { get; }

        public string Sha256 { get; }

        public long Size { get; }

        public static LauncherVersionInfo Parse(string text)
        {
            var o = Json.Obj(Json.Parse(text));
            var version = Json.Str(o, "version");
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new FormatException("launcher-version.json: no version");
            }

            var file = Json.Str(o, "file") ?? "FOEReLauncher.exe";
            if (!PathSafety.IsSafeRelativePath(file) || file.IndexOf('/') >= 0)
            {
                throw new FormatException("launcher-version.json: bad file name");
            }

            return new LauncherVersionInfo(
                version!,
                Json.Str(o, "builtAt") ?? string.Empty,
                file,
                (Json.Str(o, "sha256") ?? string.Empty).ToLowerInvariant(),
                o.ContainsKey("size") ? Json.Long(o, "size") : 0);
        }
    }

    /// <summary>One file the launcher put into game/, as recorded in installed-files.json.</summary>
    public sealed class InstalledFile
    {
        public InstalledFile(string path, long size, string sha256, long mtime)
        {
            Path = path;
            Size = size;
            Sha256 = sha256;
            Mtime = mtime;
        }

        public string Path { get; }

        public long Size { get; }

        public string Sha256 { get; }

        /// <summary>Last write time (UTC ticks) when the hash was taken; a quick check skips rehashing on a match.</summary>
        public long Mtime { get; }
    }

    /// <summary>installed-files.json next to game/: the installed version and every file the launcher installed.</summary>
    public sealed class InstalledState
    {
        public InstalledState(string version, string channel, IDictionary<string, InstalledFile> files)
        {
            Version = version;
            Channel = channel;
            Files = files;
        }

        public string Version { get; }

        public string Channel { get; }

        /// <summary>Keyed by path, case-insensitive like the file system.</summary>
        public IDictionary<string, InstalledFile> Files { get; }

        public static InstalledState Empty() =>
            new InstalledState(string.Empty, string.Empty, new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase));

        public static InstalledState Parse(string text)
        {
            var o = Json.Obj(Json.Parse(text));
            var files = new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase);
            if (o.TryGetValue("files", out var list) && list is List<object?> items)
            {
                foreach (var item in items)
                {
                    if (!(item is Dictionary<string, object?> f))
                    {
                        continue;
                    }

                    var path = Json.Str(f, "path");
                    if (!PathSafety.IsSafeRelativePath(path))
                    {
                        // A damaged record never leads the launcher outside game/.
                        continue;
                    }

                    files[path!] = new InstalledFile(
                        path!,
                        Json.Long(f, "size"),
                        Json.Str(f, "sha256") ?? string.Empty,
                        f.ContainsKey("mtime") ? Json.Long(f, "mtime") : 0);
                }
            }

            return new InstalledState(Json.Str(o, "version") ?? string.Empty, Json.Str(o, "channel") ?? string.Empty, files);
        }

        public string Serialize()
        {
            var list = new List<object?>();
            var keys = new List<string>(Files.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var f = Files[key];
                list.Add(new Dictionary<string, object?>
                {
                    ["path"] = f.Path,
                    ["size"] = f.Size,
                    ["sha256"] = f.Sha256,
                    ["mtime"] = f.Mtime
                });
            }

            return Json.Write(new Dictionary<string, object?>
            {
                ["version"] = Version,
                ["channel"] = Channel,
                ["files"] = list
            });
        }

        public static InstalledState Load(string path)
        {
            try
            {
                return File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty();
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException)
            {
                return Empty();
            }
        }

        private static readonly object CacheLock = new object();
        private static string? _cachedPath;
        private static DateTime _cachedTime;
        private static long _cachedSize;
        private static InstalledState? _cached;

        /// <summary>The record for reading only, parsed once per change of the file (by its time and size). The record
        /// lists every file of the game (about 12 MB of JSON, 0.7 s to parse) and the window asks for it on every
        /// button update and list row; never change the returned object, <see cref="Load"/> gives a private copy.</summary>
        public static InstalledState LoadCached(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Empty();
            }

            lock (CacheLock)
            {
                if (_cached != null && _cachedPath == info.FullName && _cachedTime == info.LastWriteTimeUtc && _cachedSize == info.Length)
                {
                    return _cached;
                }

                var state = Load(info.FullName);
                if (state.Version.Length > 0 || state.Files.Count > 0)
                {
                    // An unreadable file (still being written) gives an empty record: not kept, the next call reads again.
                    _cached = state;
                    _cachedPath = info.FullName;
                    _cachedTime = info.LastWriteTimeUtc;
                    _cachedSize = info.Length;
                }

                return state;
            }
        }
    }

    public static class PathSafety
    {
        /// <summary>
        /// True for a relative path with forward slashes that cannot leave its root: no "..", no ".",
        /// no empty segment, no drive, no backslash, no rooted path, no invalid characters.
        /// </summary>
        public static bool IsSafeRelativePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path!.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0)
            {
                return false;
            }

            if (path.StartsWith("/", StringComparison.Ordinal) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return false;
            }

            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.Trim().Length == 0)
                {
                    return false;
                }

                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith(".", StringComparison.Ordinal) ||
                    segment.EndsWith(" ", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Full path of a manifest path under root; throws when it would land outside root.</summary>
        public static string Combine(string root, string relative)
        {
            if (!IsSafeRelativePath(relative))
            {
                throw new InvalidDataException("unsafe path: " + relative);
            }

            var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', '\\')));
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("path leaves the game folder: " + relative);
            }

            return full;
        }
    }
}
