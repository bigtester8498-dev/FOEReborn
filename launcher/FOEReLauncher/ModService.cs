using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FOEReLauncher
{
    /// <summary>Where a mod in the game's Mods folder came from.</summary>
    public enum ModSource
    {
        /// <summary>Shipped with the game (listed in installed-files.json).</summary>
        Game,

        /// <summary>Installed by the launcher from the catalogue (listed in installed-mods.json).</summary>
        Catalogue,

        /// <summary>Put there by the player.</summary>
        Hand
    }

    /// <summary>The state of a catalogue entry on this install.</summary>
    public enum CatalogState
    {
        NotInstalled,
        Installed,
        UpdateAvailable,
        WithGame,
        AddedByHand,
        NeedsGame
    }

    /// <summary>A refused mod operation; <see cref="Key"/> and <see cref="Args"/> are a <see cref="Texts"/> entry.</summary>
    public sealed class ModOperationException : Exception
    {
        public ModOperationException(string key, params object[] args)
            : base(key + ": " + string.Join(", ", args))
        {
            Key = key;
            Args = args;
        }

        public string Key { get; }

        public object[] Args { get; }

        public string Text => Texts.T(Key, Args);
    }

    /// <summary>
    /// Mod management of the launcher: the game's mod list with the game's rules (<see cref="ModRules"/>) on the
    /// game's settings.json, and install, update and removal of catalogue mods. Archives are not trusted: every entry
    /// name is checked, the unpacked size is capped and package.json must name the catalogue's id and version before
    /// anything moves into Mods/.
    /// </summary>
    public sealed class ModService
    {
        /// <summary>No archive may unpack to more than this.</summary>
        public const long MaxUnpacked = 2L * 1024 * 1024 * 1024;

        /// <summary>Nor to more than this many times its own size.</summary>
        public const long MaxRatio = 200;

        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$"
        };

        private readonly Downloader _downloader;

        public ModService(string root, string gameDir, Downloader downloader, GameSettingsFile settings)
        {
            Root = root;
            GameDir = gameDir;
            _downloader = downloader;
            Settings = settings;
        }

        public string Root { get; }

        public string GameDir { get; }

        public GameSettingsFile Settings { get; }

        public string ModsDir => Path.Combine(GameDir, "Mods");

        public string RecordFile => Path.Combine(Root, "installed-mods.json");

        public string DownloadDir => Path.Combine(Root, ".foere", "mod-downloads");

        public string StagingDir => Path.Combine(Root, ".foere", "mod-staging");

        public string BackupDir => Path.Combine(Root, ".foere", "mod-backups");

        /// <summary>Set by the window: a running game owns Mods/ and settings.json, so every change is refused.</summary>
        public Func<bool> GameRunning { get; set; } = () => false;

        public InstalledMods LoadRecord() => InstalledMods.Load(RecordFile);

        /// <summary>Mods installed from the catalogue before it had groups take the group the catalogue gives them now
        /// (their folders stay where they are). Returns whether the record changed.</summary>
        public bool AdoptGroups(ModCatalog catalog)
        {
            var record = LoadRecord();
            var changed = false;
            foreach (var mine in record.Mods)
            {
                var entry = catalog.Find(mine.Id);
                if (entry == null || entry.Group == mine.Group)
                {
                    continue;
                }

                mine.Group = entry.Group;
                record.KeepGroup(catalog.FindGroup(entry.Group));
                changed = true;
            }

            foreach (var g in record.Groups.ToList())
            {
                // Newer texts of a kept group.
                var fresh = catalog.FindGroup(g.Id);
                if (fresh != null && fresh.Name("ru") + fresh.Name("en") + fresh.Description("ru") + fresh.Description("en") + fresh.Order !=
                    g.Name("ru") + g.Name("en") + g.Description("ru") + g.Description("en") + g.Order)
                {
                    record.KeepGroup(fresh);
                    changed = true;
                }
            }

            if (changed)
            {
                record.Save(RecordFile);
            }

            return changed;
        }

        /// <summary>installed-files.json for reading only (cached, see <see cref="InstalledState.LoadCached"/>): the mod
        /// lists ask for it for every row.</summary>
        public InstalledState GameState() => InstalledState.LoadCached(Path.Combine(Root, "installed-files.json"));

        // ------------------------------------------------------------ the list

        /// <summary>Every package of the game folder with its state for the saved choices plus the defaults, as the game
        /// starts with them; also returns that selection.</summary>
        public List<ModPackage> Scan(out ModSelection selection)
        {
            var all = ModRules.Discover(Path.Combine(GameDir, "Content"), ModsDir);
            selection = ModRules.WithDefaults(Settings.Load(), all);
            ModRules.Order(all, selection);
            return all;
        }

        /// <summary>The states of the packages for a selection that is being edited.</summary>
        public List<ModPackage> Preview(ModSelection selection)
        {
            var all = ModRules.Discover(Path.Combine(GameDir, "Content"), ModsDir);
            ModRules.Order(all, selection);
            return all;
        }

        public ModSource SourceOf(ModPackage p, InstalledState? game = null, InstalledMods? record = null)
        {
            game ??= GameState();
            record ??= LoadRecord();
            if (game.Files.ContainsKey(p.RelDir + "/package.json"))
            {
                return ModSource.Game;
            }

            var mine = record.Find(p.Id);
            return mine != null && string.Equals(mine.Dir, p.RelDir, StringComparison.OrdinalIgnoreCase) ? ModSource.Catalogue : ModSource.Hand;
        }

        /// <summary>The group a mod of the list belongs to: the catalogue group it was installed from, else the group
        /// folder it sits in (Mods/&lt;group&gt;/&lt;mod&gt;, as the game scans it); empty for a mod on its own.</summary>
        public string GroupOf(ModPackage p, InstalledMods? record = null)
        {
            record ??= LoadRecord();
            var mine = record.Find(p.Id);
            if (mine != null && mine.Group.Length > 0 && string.Equals(mine.Dir, p.RelDir, StringComparison.OrdinalIgnoreCase))
            {
                return mine.Group;
            }

            var parts = p.RelDir.Split('/');
            return !p.Core && parts.Length == 3 && ModCatalog.IsSafeId(parts[1]) ? parts[1] : string.Empty;
        }

        /// <summary>Name and description of a group for the Mods tab: as kept from the catalogue, else from a group.json
        /// in its folder ({"name": {"ru", "en"}, "description": ..}), else its folder name.</summary>
        public ModGroupInfo GroupInfo(string id, InstalledMods? record = null)
        {
            record ??= LoadRecord();
            var kept = record.FindGroup(id);
            if (kept != null)
            {
                return kept;
            }

            try
            {
                var file = Path.Combine(ModsDir, id, "group.json");
                if (ModCatalog.IsSafeId(id) && File.Exists(file))
                {
                    var o = Json.Obj(Json.Parse(File.ReadAllText(file)));
                    o["id"] = id;
                    var g = ModGroupInfo.Read(o);
                    if (g != null)
                    {
                        return g;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException)
            {
                // A broken group.json: the folder name stays.
            }

            return KnownFolders.TryGetValue(id, out var known) ? known : ModGroupInfo.FromFolder(id);
        }

        /// <summary>Group folders the game's own files name: the example mods unpacked from "Mod examples.zip".</summary>
        private static readonly Dictionary<string, ModGroupInfo> KnownFolders = new Dictionary<string, ModGroupInfo>(StringComparer.Ordinal)
        {
            ["examples"] = new ModGroupInfo
            {
                Id = "examples",
                NameRu = "Примеры модов",
                NameEn = "Example mods",
                DescriptionRu = "Моды-образцы из архива «Mod examples.zip» в папке игры.",
                DescriptionEn = "Sample mods from \"Mod examples.zip\" in the game folder.",
                Order = 100
            }
        };

        /// <summary>The archive of example mods the game ships beside itself, when it is there.</summary>
        public bool HasExamplesArchive => File.Exists(Path.Combine(GameDir, ExamplesArchive));

        public const string ExamplesArchive = "Mod examples.zip";

        public CatalogState StateOf(CatalogEntry entry, IList<ModPackage> packages, InstalledState? game = null, InstalledMods? record = null)
        {
            record ??= LoadRecord();
            var p = packages.FirstOrDefault(x => x.Id == entry.Id && x.State != ModState.Duplicate && !x.Core);
            if (p == null)
            {
                return ModVersion.Satisfies(ModRules.GameVersion, entry.Game) ? CatalogState.NotInstalled : CatalogState.NeedsGame;
            }

            switch (SourceOf(p, game, record))
            {
                case ModSource.Game:
                    return CatalogState.WithGame;
                case ModSource.Hand:
                    return CatalogState.AddedByHand;
                default:
                    var mine = record.Find(entry.Id)!;
                    if (ModVersion.Compare(entry.Version, mine.Version) > 0)
                    {
                        return ModVersion.Satisfies(ModRules.GameVersion, entry.Game) ? CatalogState.UpdateAvailable : CatalogState.NeedsGame;
                    }

                    return CatalogState.Installed;
            }
        }

        // ----------------------------------------------------- settings changes

        private void EnsureGameStopped()
        {
            if (GameRunning())
            {
                throw new ModOperationException("mods.err.running");
            }
        }

        /// <summary>Saves the mods section of the game's settings (refused while the game runs).</summary>
        public void SaveSelection(ModSelection selection)
        {
            EnsureGameStopped();
            Settings.Save(selection);
        }

        // ----------------------------------------------------------- install

        /// <summary>
        /// What installing <paramref name="entry"/> takes: the catalogue mods it requires that are not installed
        /// (requirements first) and the entry itself. A requirement that is neither installed nor in the catalogue
        /// refuses the install.
        /// </summary>
        public List<CatalogEntry> PlanInstall(CatalogEntry entry, ModCatalog catalog, IList<ModPackage> packages)
        {
            var res = new List<CatalogEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Walk(CatalogEntry e)
            {
                if (!seen.Add(e.Id))
                {
                    return;
                }

                foreach (var r in e.Requires)
                {
                    if (packages.Any(p => p.Id == r.Id && p.State != ModState.Duplicate))
                    {
                        continue;
                    }

                    var dep = catalog.Find(r.Id);
                    if (dep == null)
                    {
                        throw new ModOperationException("mods.err.reqmissing", e.Id, r.ToString());
                    }

                    Walk(dep);
                }

                if (!res.Contains(e))
                {
                    res.Add(e);
                }
            }

            Walk(entry);
            return res;
        }

        /// <summary>Downloads (with resume), checks and installs or updates one catalogue mod. A folder of the mod that
        /// the launcher did not install must be moved away first (<see cref="BackupHandAdded"/>).</summary>
        public async Task<InstalledMod> InstallAsync(CatalogEntry entry, IProgress<double>? progress, CancellationToken ct, ModGroupInfo? group = null)
        {
            EnsureGameStopped();
            var record = LoadRecord();
            var packages = ModRules.Discover(Path.Combine(GameDir, "Content"), ModsDir);
            var existing = packages.FirstOrDefault(p => p.Id == entry.Id && !p.Core && p.State != ModState.Duplicate);
            var mine = record.Find(entry.Id);
            if (existing != null)
            {
                var source = SourceOf(existing, null, record);
                if (source == ModSource.Game)
                {
                    throw new ModOperationException("mods.err.withgame", entry.Id);
                }

                if (source == ModSource.Hand)
                {
                    throw new ModOperationException("mods.err.byhand", entry.Id);
                }
            }

            Directory.CreateDirectory(DownloadDir);
            var archive = Path.Combine(DownloadDir, entry.Id + "-" + entry.Version + "-" + entry.Sha256.Substring(0, 16) + ".zip");
            long done = 0;
            await _downloader.DownloadVerifiedAsync(entry.Url, archive, entry.Size, entry.Sha256, bytes =>
            {
                done += bytes;
                progress?.Report(entry.Size > 0 ? (double)done / entry.Size : 0);
            }, ct).ConfigureAwait(false);

            var staging = Path.Combine(StagingDir, entry.Id);
            List<InstalledModFile> files;
            try
            {
                files = await Task.Run(() => Extract(archive, staging, entry, ct), ct).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(staging);
                TryDeleteFile(archive);
                throw;
            }

            EnsureGameStopped();
            var relDir = mine != null && existing != null ? mine.Dir : NewModDir(entry);
            var target = PathSafety.Combine(GameDir, relDir);
            if (mine != null)
            {
                // Update: the recorded files of the old version go, files the player added stay.
                foreach (var f in mine.Files)
                {
                    var path = PathSafety.Combine(GameDir, f.Path);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }

            if (!Directory.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(staging, target);
            }
            else
            {
                foreach (var f in files)
                {
                    var from = PathSafety.Combine(staging, f.Path);
                    var to = PathSafety.Combine(target, f.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    if (File.Exists(to))
                    {
                        File.Delete(to);
                    }

                    File.Move(from, to);
                }

                RemoveEmptyDirs(target, keepRoot: true);
                TryDelete(staging);
            }

            if (mine != null)
            {
                RemoveEmptyDirs(target, keepRoot: true);
            }

            var installed = new InstalledMod
            {
                Id = entry.Id,
                Version = entry.Version,
                Sha256 = entry.Sha256,
                Dir = relDir,
                InstalledAt = InstalledMods.Now(),
                Group = entry.Group,
                Files = files.Select(f => new InstalledModFile(relDir + "/" + f.Path, f.Size, f.Sha256)).ToList()
            };
            record.Mods.RemoveAll(m => m.Id == entry.Id);
            record.Mods.Add(installed);
            record.KeepGroup(group);
            record.Save(RecordFile);
            TryDeleteFile(archive);
            return installed;
        }

        /// <summary>Where a new catalogue mod goes: into its group's folder (Mods/examples/example_home), as the game
        /// scans one level of group folders; a group folder that is itself a mod (has package.json) is not used.</summary>
        private string NewModDir(CatalogEntry entry)
        {
            if (entry.Group.Length > 0 && ModCatalog.IsSafeId(entry.Group) && !File.Exists(Path.Combine(ModsDir, entry.Group, "package.json")))
            {
                return "Mods/" + entry.Group + "/" + entry.Id;
            }

            return "Mods/" + entry.Id;
        }

        /// <summary>Unpacks an archive into <paramref name="staging"/> after checking every entry; returns the files
        /// (paths relative to the mod folder). Throws <see cref="ModOperationException"/> and leaves nothing behind
        /// when the archive is not safe or not the catalogue's mod.</summary>
        public static List<InstalledModFile> Extract(string archive, string staging, CatalogEntry entry, CancellationToken ct = default)
        {
            TryDelete(staging);
            Directory.CreateDirectory(staging);
            var archiveSize = new FileInfo(archive).Length;
            var limit = Math.Min(MaxUnpacked, Math.Max(1, archiveSize) * MaxRatio);
            var files = new List<InstalledModFile>();
            try
            {
                ZipArchive zip;
                try
                {
                    zip = ZipFile.OpenRead(archive);
                }
                catch (InvalidDataException)
                {
                    throw new ModOperationException("mods.err.archive", entry.Id);
                }

                using (zip)
                {
                    long declared = 0;
                    foreach (var e in zip.Entries)
                    {
                        var name = e.FullName.EndsWith("/", StringComparison.Ordinal) ? e.FullName.TrimEnd('/') : e.FullName;
                        if (!IsSafeEntryName(name))
                        {
                            throw new ModOperationException("mods.err.unsafe", entry.Id, e.FullName);
                        }

                        declared += e.Length;
                    }

                    if (declared > limit)
                    {
                        throw new ModOperationException("mods.err.toobig", entry.Id, LauncherService.FormatSize(declared, Texts.Language));
                    }

                    long written = 0;
                    var buffer = new byte[1 << 16];
                    foreach (var e in zip.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (e.FullName.EndsWith("/", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var target = PathSafety.Combine(staging, e.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        long size = 0;
                        using (var sha = SHA256.Create())
                        using (var input = e.Open())
                        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
                        {
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                written += read;
                                size += read;
                                if (written > limit)
                                {
                                    // The sizes in the zip directory can lie; count what really comes out.
                                    throw new ModOperationException("mods.err.toobig", entry.Id, LauncherService.FormatSize(written, Texts.Language));
                                }

                                sha.TransformBlock(buffer, 0, read, null, 0);
                                output.Write(buffer, 0, read);
                            }

                            sha.TransformFinalBlock(buffer, 0, 0);
                            files.Add(new InstalledModFile(e.FullName, size, Hashing.Hex(sha.Hash)));
                        }
                    }
                }

                var manifestPath = Path.Combine(staging, "package.json");
                ModManifest? manifest = null;
                try
                {
                    manifest = File.Exists(manifestPath) ? ModManifest.Parse(File.ReadAllText(manifestPath)) : null;
                }
                catch (FormatException)
                {
                    manifest = null;
                }

                if (manifest == null)
                {
                    throw new ModOperationException("mods.err.nomanifest", entry.Id);
                }

                if (manifest.Id != entry.Id || manifest.Version != entry.Version)
                {
                    throw new ModOperationException("mods.err.wrongmod", entry.Id, entry.Version, manifest.Id, manifest.Version);
                }

                files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
                return files;
            }
            catch
            {
                TryDelete(staging);
                throw;
            }
        }

        /// <summary>A zip entry name that is a plain relative path: no "..", no rooted path, no drive, no backslash, no
        /// device name such as CON or LPT1 (with or without an extension).</summary>
        public static bool IsSafeEntryName(string name)
        {
            if (!PathSafety.IsSafeRelativePath(name))
            {
                return false;
            }

            foreach (var segment in name.Split('/'))
            {
                var stem = segment;
                var dot = stem.IndexOf('.');
                if (dot >= 0)
                {
                    stem = stem.Substring(0, dot);
                }

                if (ReservedNames.Contains(stem.TrimEnd()))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Moves a mod folder the launcher did not install to .foere/mod-backups/&lt;id&gt;-&lt;time&gt;/ (outside Mods/,
        /// where the game would find it again); returns the new place.</summary>
        public string BackupHandAdded(string id)
        {
            EnsureGameStopped();
            var packages = ModRules.Discover(Path.Combine(GameDir, "Content"), ModsDir);
            var p = packages.FirstOrDefault(x => x.Id == id && !x.Core && x.State != ModState.Duplicate)
                    ?? throw new ModOperationException("mods.err.notfound", id);
            if (SourceOf(p) != ModSource.Hand)
            {
                throw new ModOperationException("mods.err.notbyhand", id);
            }

            Directory.CreateDirectory(BackupDir);
            var target = Path.Combine(BackupDir, id + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.Move(p.Dir, target);
            return target;
        }

        // ------------------------------------------------------------ remove

        /// <summary>Removes a catalogue mod: only the recorded files, then empty folders. The id leaves the enabled list
        /// and the order; the enabled mods that require it are switched off (the game's cascade). Returns those.</summary>
        public List<string> Remove(string id)
        {
            EnsureGameStopped();
            var record = LoadRecord();
            var mine = record.Find(id) ?? throw new ModOperationException("mods.err.notmine", id);
            var all = ModRules.Discover(Path.Combine(GameDir, "Content"), ModsDir);
            var sel = ModRules.WithDefaults(Settings.Load(), all);
            var dependents = ModRules.ToDisable(id, all, sel);

            foreach (var f in mine.Files)
            {
                var path = PathSafety.Combine(GameDir, f.Path);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            var dir = PathSafety.Combine(GameDir, mine.Dir);
            if (Directory.Exists(dir))
            {
                RemoveEmptyDirs(dir, keepRoot: false);
            }

            // A group folder the last mod left: removed when empty.
            var parent = Path.GetDirectoryName(dir.TrimEnd('/', '\\'));
            if (parent != null && !string.Equals(Path.GetFullPath(parent).TrimEnd('\\'), Path.GetFullPath(ModsDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFullPath(parent).StartsWith(Path.GetFullPath(ModsDir), StringComparison.OrdinalIgnoreCase))
            {
                RemoveEmptyDirs(parent, keepRoot: false);
            }

            record.Mods.Remove(mine);
            record.Save(RecordFile);

            foreach (var x in dependents)
            {
                sel.SetEnabled(x, false);
            }

            sel.Enabled.Remove(id);
            sel.Disabled.Remove(id);
            sel.Order.Remove(id);
            Settings.Save(sel);
            return dependents;
        }

        // ----------------------------------------------------------- helpers

        private static void RemoveEmptyDirs(string dir, bool keepRoot)
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (var sub in Directory.GetDirectories(dir))
            {
                RemoveEmptyDirs(sub, keepRoot: false);
            }

            if (!keepRoot && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                try
                {
                    Directory.Delete(dir);
                }
                catch (IOException)
                {
                    // Left for the player.
                }
            }
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Next run.
            }
        }

        private static void TryDeleteFile(string file)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Next run.
            }
        }
    }
}
