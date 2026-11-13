using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FOEReLauncher
{
    public enum UpdatePhase
    {
        Checking,
        Downloading,
        Extracting,
        Applying
    }

    /// <summary>Progress of an install, update or verify run.</summary>
    public sealed class UpdateProgress
    {
        public UpdatePhase Phase { get; set; }

        /// <summary>Downloading: bytes on disk / bytes of all needed packs. Other phases: files done / files total.</summary>
        public long Done { get; set; }

        public long Total { get; set; }

        /// <summary>Bytes per second over the download so far; 0 outside the download.</summary>
        public double Speed { get; set; }

        public double Fraction => Total <= 0 ? 0 : Math.Min(1d, (double)Done / Total);
    }

    /// <summary>What an update has to do, computed from the manifest and the files on disk.</summary>
    public sealed class UpdatePlan
    {
        /// <summary>Manifest files that are missing or differ on disk.</summary>
        public List<FileEntry> ToInstall { get; } = new List<FileEntry>();

        /// <summary>Files the launcher installed earlier that the new manifest no longer has.</summary>
        public List<string> ToRemove { get; } = new List<string>();

        /// <summary>Packs that hold at least one file of <see cref="ToInstall"/>.</summary>
        public List<PackInfo> Packs { get; } = new List<PackInfo>();

        /// <summary>Files on disk the launcher did not install that a shipped file replaces (kept as .bak).</summary>
        public List<string> UserFilesReplaced { get; } = new List<string>();

        /// <summary>Manifest files that are already right, with their hash record.</summary>
        public Dictionary<string, InstalledFile> Unchanged { get; } = new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase);

        public long DownloadBytes
        {
            get
            {
                long total = 0;
                foreach (var p in Packs)
                {
                    total += p.Size;
                }

                return total;
            }
        }

        public bool IsEmpty => ToInstall.Count == 0 && ToRemove.Count == 0;
    }

    /// <summary>What a finished run did, for the status line and the tests.</summary>
    public sealed class UpdateResult
    {
        public string Version { get; set; } = string.Empty;

        public int InstalledFiles { get; set; }

        public int RemovedFiles { get; set; }

        public int Packs { get; set; }

        public long PackBytes { get; set; }

        /// <summary>Bytes that came over the network in this run (a resumed part is not counted twice).</summary>
        public long NetworkBytes { get; set; }

        public int ResumedRequests { get; set; }

        public List<string> BackedUp { get; } = new List<string>();

        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// Pack-based install, update and repair of game/.
    ///
    /// Layout under the launcher folder (root):
    ///   game/                  the game
    ///   installed-files.json   the version and every file the launcher installed (path, size, sha256, mtime)
    ///   .foere/downloads/      packs and their .part files (kept between runs, so downloads resume)
    ///   .foere/staging/        files extracted and verified, not yet in game/
    ///   .foere/rollback/       files replaced or removed during the swap, until the swap is committed
    ///   .foere/swap.json       the swap journal; present only while a swap is in progress
    ///
    /// Nothing in game/ changes before every needed file is downloaded, extracted and verified in
    /// staging. The swap then moves files (same volume, no copying); if it fails or the process dies,
    /// the journal lets the next run move the old files back, so the previous install stays playable.
    /// </summary>
    public sealed class Updater
    {
        private readonly Downloader _downloader;

        public Updater(string root, Downloader downloader)
        {
            Root = root;
            _downloader = downloader;
        }

        public string Root { get; }

        public string GameDir => Path.Combine(Root, "game");

        public string StateFile => Path.Combine(Root, "installed-files.json");

        public string WorkDir => Path.Combine(Root, ".foere");

        public string DownloadDir => Path.Combine(WorkDir, "downloads");

        public string StagingDir => Path.Combine(WorkDir, "staging");

        public string RollbackDir => Path.Combine(WorkDir, "rollback");

        public string JournalFile => Path.Combine(WorkDir, "swap.json");

        public InstalledState LoadState() => InstalledState.Load(StateFile);

        // ----------------------------------------------------------------- plan

        /// <summary>
        /// Compares game/ with the manifest. A file counts as right when its size matches and its SHA-256
        /// equals the manifest's. With <paramref name="fullHash"/> false, a file whose size and last write
        /// time equal the record in installed-files.json reuses the recorded hash instead of reading it
        /// again (a 1 GB game is otherwise rehashed on every start); Verify passes true.
        /// </summary>
        public UpdatePlan Plan(Manifest manifest, InstalledState state, bool fullHash, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
        {
            var plan = new UpdatePlan();
            var report = new UpdateProgress { Phase = UpdatePhase.Checking, Total = manifest.Files.Count };
            var lastReport = Stopwatch.StartNew();
            var needed = new HashSet<string>(StringComparer.Ordinal);
            var inManifest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                inManifest.Add(file.Path);
                var full = PathSafety.Combine(GameDir, file.Path);
                var info = new FileInfo(full);
                var ok = false;
                if (info.Exists && info.Length == file.Size)
                {
                    var mtime = info.LastWriteTimeUtc.Ticks;
                    string sha;
                    if (!fullHash && state.Files.TryGetValue(file.Path, out var known) && known.Size == file.Size && known.Mtime == mtime &&
                        known.Sha256.Length > 0)
                    {
                        sha = known.Sha256;
                    }
                    else
                    {
                        sha = Hashing.Sha256OfFile(full);
                    }

                    if (string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        ok = true;
                        plan.Unchanged[file.Path] = new InstalledFile(file.Path, file.Size, file.Sha256, mtime);
                    }
                }

                if (!ok)
                {
                    plan.ToInstall.Add(file);
                    needed.Add(file.Pack);
                    if (info.Exists && !state.Files.ContainsKey(file.Path))
                    {
                        plan.UserFilesReplaced.Add(file.Path);
                    }
                }

                report.Done++;
                if (progress != null && lastReport.ElapsedMilliseconds > 100)
                {
                    progress.Report(report);
                    lastReport.Restart();
                }
            }

            foreach (var path in state.Files.Keys)
            {
                if (!inManifest.Contains(path) && File.Exists(PathSafety.Combine(GameDir, path)))
                {
                    plan.ToRemove.Add(path);
                }
            }

            plan.ToRemove.Sort(StringComparer.Ordinal);
            foreach (var pack in manifest.Packs)
            {
                if (needed.Contains(pack.Name))
                {
                    plan.Packs.Add(pack);
                }
            }

            progress?.Report(report);
            return plan;
        }

        // ---------------------------------------------------------------- apply

        /// <summary>Plans and applies in one go: install, update, or (with fullHash) verify and repair.</summary>
        public async Task<UpdateResult> RunAsync(
            Manifest manifest,
            string channel,
            bool fullHash,
            IProgress<UpdateProgress>? progress,
            CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            RecoverInterruptedSwap();
            var state = LoadState();
            var plan = await Task.Run(() => Plan(manifest, state, fullHash, progress, ct), ct).ConfigureAwait(false);
            var result = await ApplyAsync(manifest, state, plan, channel, progress, ct).ConfigureAwait(false);
            result.Elapsed = clock.Elapsed;
            return result;
        }

        public async Task<UpdateResult> ApplyAsync(
            Manifest manifest,
            InstalledState state,
            UpdatePlan plan,
            string channel,
            IProgress<UpdateProgress>? progress,
            CancellationToken ct)
        {
            var result = new UpdateResult { Version = manifest.Version, Packs = plan.Packs.Count, PackBytes = plan.DownloadBytes };
            var networkBefore = _downloader.NetworkBytes;
            var resumedBefore = _downloader.ResumedRequests;

            if (!plan.IsEmpty)
            {
                Directory.CreateDirectory(DownloadDir);
                ResetDirectory(StagingDir);

                await DownloadPacksAsync(plan, progress, ct).ConfigureAwait(false);
                await Task.Run(() => ExtractToStaging(plan, progress, ct), ct).ConfigureAwait(false);
                await Task.Run(() => Swap(plan, state, result, progress), CancellationToken.None).ConfigureAwait(false);
            }

            // Every manifest file is right now: record all of them, so the next start can skip rehashing.
            var files = new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in plan.Unchanged)
            {
                files[pair.Key] = pair.Value;
            }

            foreach (var file in plan.ToInstall)
            {
                var full = PathSafety.Combine(GameDir, file.Path);
                files[file.Path] = new InstalledFile(file.Path, file.Size, file.Sha256, File.GetLastWriteTimeUtc(full).Ticks);
            }

            WriteState(new InstalledState(manifest.Version, channel, files));
            TryDeleteDirectory(StagingDir);
            DeleteUnusedDownloads(manifest);

            result.InstalledFiles = plan.ToInstall.Count;
            result.RemovedFiles = plan.ToRemove.Count;
            result.NetworkBytes = _downloader.NetworkBytes - networkBefore;
            result.ResumedRequests = _downloader.ResumedRequests - resumedBefore;
            return result;
        }

        private async Task DownloadPacksAsync(UpdatePlan plan, IProgress<UpdateProgress>? progress, CancellationToken ct)
        {
            var report = new UpdateProgress { Phase = UpdatePhase.Downloading, Total = plan.DownloadBytes };
            var clock = Stopwatch.StartNew();
            var lastReport = Stopwatch.StartNew();
            long network = 0;

            foreach (var pack in plan.Packs)
            {
                var target = Path.Combine(DownloadDir, pack.Name);
                if (File.Exists(target) && new FileInfo(target).Length == pack.Size &&
                    string.Equals(Hashing.Sha256OfFile(target), pack.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // Downloaded by an earlier run that failed later on: no need to fetch it again.
                    report.Done += pack.Size;
                    continue;
                }

                await _downloader.DownloadVerifiedAsync(
                    pack.Url,
                    target,
                    pack.Size,
                    pack.Sha256,
                    bytes =>
                    {
                        report.Done += bytes;
                        if (bytes > 0)
                        {
                            network += bytes;
                        }

                        if (progress != null && lastReport.ElapsedMilliseconds >= 200)
                        {
                            report.Speed = clock.Elapsed.TotalSeconds > 0 ? network / clock.Elapsed.TotalSeconds : 0;
                            progress.Report(report);
                            lastReport.Restart();
                        }
                    },
                    ct).ConfigureAwait(false);
            }

            report.Speed = clock.Elapsed.TotalSeconds > 0 ? network / clock.Elapsed.TotalSeconds : 0;
            progress?.Report(report);
        }

        private void ExtractToStaging(UpdatePlan plan, IProgress<UpdateProgress>? progress, CancellationToken ct)
        {
            var report = new UpdateProgress { Phase = UpdatePhase.Extracting, Total = plan.ToInstall.Count };
            var lastReport = Stopwatch.StartNew();
            var byPack = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);
            foreach (var file in plan.ToInstall)
            {
                if (!byPack.TryGetValue(file.Pack, out var list))
                {
                    byPack[file.Pack] = list = new List<FileEntry>();
                }

                list.Add(file);
            }

            var buffer = new byte[1 << 16];
            foreach (var pack in plan.Packs)
            {
                var zipPath = Path.Combine(DownloadDir, pack.Name);
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var file in byPack[pack.Name])
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = zip.GetEntry(file.Path) ?? throw new InvalidDataException(pack.Name + " has no " + file.Path);
                        var target = PathSafety.Combine(StagingDir, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        long size = 0;
                        string sha;
                        using (var hash = SHA256.Create())
                        using (var input = entry.Open())
                        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        {
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                hash.TransformBlock(buffer, 0, read, null, 0);
                                output.Write(buffer, 0, read);
                                size += read;
                            }

                            hash.TransformFinalBlock(buffer, 0, 0);
                            sha = Hashing.Hex(hash.Hash);
                        }

                        if (size != file.Size || !string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("extracted file does not match the manifest: " + file.Path);
                        }

                        report.Done++;
                        if (progress != null && lastReport.ElapsedMilliseconds >= 200)
                        {
                            progress.Report(report);
                            lastReport.Restart();
                        }
                    }
                }
            }

            progress?.Report(report);
        }

        /// <summary>
        /// Moves the staged files into game/. Old files go to rollback/ (or to &lt;name&gt;.bak for a file the
        /// launcher did not install), removed files go to rollback/ as well. The journal written first
        /// lists what a rollback has to undo; deleting it commits the swap.
        /// </summary>
        private void Swap(UpdatePlan plan, InstalledState state, UpdateResult result, IProgress<UpdateProgress>? progress)
        {
            ResetDirectory(RollbackDir);
            var created = new List<object?>();
            var backups = new List<object?>();
            var backupSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in plan.ToInstall)
            {
                var target = PathSafety.Combine(GameDir, file.Path);
                if (!File.Exists(target))
                {
                    created.Add(file.Path);
                }
                else if (!state.Files.ContainsKey(file.Path) && !File.Exists(target + ".bak"))
                {
                    backups.Add(file.Path);
                    backupSet.Add(file.Path);
                }
            }

            File.WriteAllText(JournalFile, Json.Write(new Dictionary<string, object?>
            {
                ["created"] = created,
                ["backups"] = backups
            }));

            var report = new UpdateProgress { Phase = UpdatePhase.Applying, Total = plan.ToInstall.Count + plan.ToRemove.Count };
            try
            {
                foreach (var file in plan.ToInstall)
                {
                    var target = PathSafety.Combine(GameDir, file.Path);
                    if (File.Exists(target))
                    {
                        if (backupSet.Contains(file.Path))
                        {
                            File.Move(target, target + ".bak");
                            result.BackedUp.Add(file.Path);
                        }
                        else
                        {
                            MoveReplacing(target, PathSafety.Combine(RollbackDir, file.Path));
                        }
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(PathSafety.Combine(StagingDir, file.Path), target);
                    report.Done++;
                }

                foreach (var path in plan.ToRemove)
                {
                    var target = PathSafety.Combine(GameDir, path);
                    if (File.Exists(target))
                    {
                        MoveReplacing(target, PathSafety.Combine(RollbackDir, path));
                    }

                    report.Done++;
                }
            }
            catch (Exception)
            {
                RecoverInterruptedSwap();
                throw;
            }

            // Commit point: from here on the new files stay.
            File.Delete(JournalFile);
            progress?.Report(report);
            TryDeleteDirectory(RollbackDir);
            foreach (var path in plan.ToRemove)
            {
                RemoveEmptyParents(PathSafety.Combine(GameDir, path));
            }
        }

        /// <summary>
        /// Undoes a swap that did not reach its commit: files created by it are deleted, the files in
        /// rollback/ and the .bak copies go back to their places. Safe to call when there is nothing to do.
        /// </summary>
        public void RecoverInterruptedSwap()
        {
            if (!File.Exists(JournalFile))
            {
                return;
            }

            var journal = Json.Obj(Json.Parse(File.ReadAllText(JournalFile)));
            foreach (var path in Json.Strings(journal, "created"))
            {
                var target = PathSafety.Combine(GameDir, path);
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }

            foreach (var path in Json.Strings(journal, "backups"))
            {
                var target = PathSafety.Combine(GameDir, path);
                if (File.Exists(target + ".bak"))
                {
                    MoveReplacing(target + ".bak", target);
                }
            }

            if (Directory.Exists(RollbackDir))
            {
                var prefix = Path.GetFullPath(RollbackDir).TrimEnd('\\') + "\\";
                foreach (var saved in Directory.GetFiles(RollbackDir, "*", SearchOption.AllDirectories))
                {
                    var relative = saved.Substring(prefix.Length).Replace('\\', '/');
                    MoveReplacing(saved, PathSafety.Combine(GameDir, relative));
                }
            }

            File.Delete(JournalFile);
            TryDeleteDirectory(RollbackDir);
        }

        private void WriteState(InstalledState state)
        {
            var temp = StateFile + ".tmp";
            File.WriteAllText(temp, state.Serialize());
            if (File.Exists(StateFile))
            {
                File.Replace(temp, StateFile, null);
            }
            else
            {
                File.Move(temp, StateFile);
            }
        }

        /// <summary>Packs and parts the current manifest no longer lists are dead weight.</summary>
        private void DeleteUnusedDownloads(Manifest manifest)
        {
            if (!Directory.Exists(DownloadDir))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(DownloadDir))
            {
                var name = Path.GetFileName(file);
                try
                {
                    // A fully used pack is extracted already; only a part of a still listed pack is kept.
                    if (!name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                        manifest.FindPack(name.Substring(0, name.Length - 5)) == null)
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                    // Left for the next run.
                }
            }
        }

        private static void MoveReplacing(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to))
            {
                File.Delete(to);
            }

            File.Move(from, to);
        }

        private void RemoveEmptyParents(string file)
        {
            var gameRoot = Path.GetFullPath(GameDir).TrimEnd('\\');
            var dir = Path.GetDirectoryName(file);
            while (!string.IsNullOrEmpty(dir) && dir!.Length > gameRoot.Length &&
                   dir.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(dir).GetEnumerator().MoveNext())
                    {
                        return;
                    }

                    Directory.Delete(dir);
                }
                catch (IOException)
                {
                    return;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }

        private static void ResetDirectory(string dir)
        {
            TryDeleteDirectory(dir);
            Directory.CreateDirectory(dir);
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (IOException)
            {
                // Next run cleans it.
            }
            catch (UnauthorizedAccessException)
            {
                // Next run cleans it.
            }
        }
    }
}
