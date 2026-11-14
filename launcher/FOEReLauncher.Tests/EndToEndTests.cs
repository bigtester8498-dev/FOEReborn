using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace FOEReLauncher.Tests
{
    /// <summary>
    /// A local download site: tools/publish.py packs a fake game into work/vN/, the latest site is
    /// mirrored to work/site/, and tools/test_server.py (127.0.0.1 only) serves work/.
    /// </summary>
    public sealed class LocalSite : IDisposable
    {
        private readonly ITestOutputHelper _log;
        private Process? _server;
        private string? _prev;
        private int _rate;

        public LocalSite(ITestOutputHelper log)
        {
            _log = log;
            Work = new TempDir();
            Port = FreePort();
            RepoRoot = FindRepoRoot();
            Directory.CreateDirectory(Work.Sub("site"));
        }

        public TempDir Work { get; }

        public int Port { get; }

        public string RepoRoot { get; }

        public string BaseUrl => "http://127.0.0.1:" + Port + "/";

        public string SiteUrl => BaseUrl + "site/";

        public string ServerLog => Work.Sub("server.log");

        // The server appends a request's line after it has sent the response, so the line of the last request can
        // still be missing (or the file open for writing) when the client already has its answer: read with sharing
        // and wait until two reads 150 ms apart agree.
        public string[] ReadServerLog()
        {
            var last = ReadServerLogOnce();
            for (var i = 0; i < 30; i++)
            {
                Thread.Sleep(150);
                var next = ReadServerLogOnce();
                if (next.Length == last.Length)
                {
                    return next;
                }

                last = next;
            }

            return last;
        }

        private string[] ReadServerLogOnce()
        {
            if (!File.Exists(ServerLog))
            {
                return new string[0];
            }

            using (var stream = new FileStream(ServerLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        private static string FindRepoRoot()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "tools", "publish.py")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            return dir ?? throw new InvalidOperationException("tools/publish.py not found above the test folder");
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public static int Run(string exe, string args, ITestOutputHelper? log, string? cwd = null)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = cwd ?? Environment.CurrentDirectory
            };
            using (var p = Process.Start(psi)!)
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                p.WaitForExit();
                log?.WriteLine(stdout.Result + stderr.Result);
                return p.ExitCode;
            }
        }

        /// <summary>--catalogue-mods of the next packs (null: the script's default, every mod left out).</summary>
        public string? CatalogueMods { get; set; }

        /// <summary>Packs <paramref name="build"/> as <paramref name="version"/> and makes it the live site.</summary>
        public string Publish(string build, string version, string channel = "prod", string? launcher = null)
        {
            var name = version.Replace('.', '_');
            var outDir = Work.Sub(name);
            var notesRu = Work.Sub(name + "-ru.txt");
            var notesEn = Work.Sub(name + "-en.txt");
            File.WriteAllText(notesRu, "Тестовая сборка " + version + "\n");
            File.WriteAllText(notesEn, "Test build " + version + "\n");
            var args = "-I \"" + Path.Combine(RepoRoot, "tools", "publish.py") + "\" pack --build \"" + build + "\" --out \"" + outDir +
                       "\" --version " + version + " --channel " + channel + " --base-url " + BaseUrl + name + "/" +
                       " --notes-ru \"" + notesRu + "\" --notes-en \"" + notesEn + "\"" +
                       (_prev != null ? " --prev \"" + _prev + "\"" : string.Empty) +
                       (launcher != null ? " --launcher \"" + launcher + "\"" : string.Empty) +
                       (CatalogueMods != null ? " --catalogue-mods \"" + CatalogueMods + "\"" : string.Empty);
            Assert.Equal(0, Run("python", args, _log));
            _prev = outDir;

            // The launcher always reads work/site/; the packs stay where each version put them.
            var site = Work.Sub("site");
            Directory.Delete(site, true);
            CopyTree(Path.Combine(outDir, "site"), site);
            CopyCatalogue();
            return outDir;
        }

        private string? _prevMods;
        private int _modRuns;

        /// <summary>Runs publish.py mods on the given mod folders (comma list) and puts the catalogue on the site.</summary>
        public string PublishMods(string mods, string? texts = null)
        {
            var name = "mods" + (++_modRuns);
            var outDir = Work.Sub(name);
            var args = "-I \"" + Path.Combine(RepoRoot, "tools", "publish.py") + "\" mods --mods \"" + mods + "\" --out \"" + outDir +
                       "\" --base-url " + BaseUrl + name + "/" +
                       (_prevMods != null ? " --prev \"" + _prevMods + "\"" : string.Empty) +
                       (texts != null ? " --texts \"" + texts + "\"" : string.Empty);
            Assert.Equal(0, Run("python", args, _log));
            _prevMods = outDir;
            CopyCatalogue();
            return outDir;
        }

        private void CopyCatalogue()
        {
            if (_prevMods == null)
            {
                return;
            }

            var target = Path.Combine(Work.Sub("site"), "downloads", "mods");
            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }

            CopyTree(Path.Combine(_prevMods, "site", "downloads", "mods"), target);
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            }

            foreach (var dir in Directory.GetDirectories(from))
            {
                CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
            }
        }

        public void Start(int rate = 0)
        {
            Stop();
            _rate = rate;
            var args = "-I \"" + Path.Combine(RepoRoot, "tools", "test_server.py") + "\" --root \"" + Work.Path + "\" --port " + Port +
                       " --log \"" + ServerLog + "\"" + (rate > 0 ? " --rate " + rate : string.Empty);
            _server = Process.Start(new ProcessStartInfo("python", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            })!;
            _server.StandardOutput.ReadLine(); // "serving ..." once it listens
        }

        public void Stop()
        {
            if (_server == null)
            {
                return;
            }

            try
            {
                if (!_server.HasExited)
                {
                    _server.Kill();
                    _server.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                // Already gone.
            }

            _server.Dispose();
            _server = null;
        }

        public void Dispose()
        {
            Stop();
            Work.Dispose();
        }
    }

    public class EndToEndTests : IDisposable
    {
        private readonly ITestOutputHelper _log;
        private readonly LocalSite _site;
        private readonly TempDir _install = new TempDir();
        private readonly TempDir _build = new TempDir();

        public EndToEndTests(ITestOutputHelper log)
        {
            _log = log;
            _site = new LocalSite(log);
        }

        public void Dispose()
        {
            _site.Dispose();
            _install.Dispose();
            _build.Dispose();
        }

        /// <summary>A fake game with the real folder layout, a few files per pack group.</summary>
        private void MakeFakeGame(int bigBytes = 0)
        {
            _build.Write("FOEReborn.exe", "fake exe v1");
            _build.Write("UnityPlayer.dll", "player");
            _build.Write("FOEReborn_Data/Managed/FOE.Game.dll", "game code v1");
            _build.Write("FOEReborn_Data/Managed/UnityEngine.dll", "engine");
            _build.Write("FOEReborn_Data/globalgamemanagers", "ggm");
            _build.Write("Content/classic/sprites/player/pip/run_0.png", "pip frame");
            _build.Write("Content/classic/sprites/units/raider.png", "raider");
            _build.Write("Content/classic/music/menu.mp3", "music");
            _build.Write("Content/classic/defs/units.json", "{}");
            _build.Write("Content/reborn/defs/padlayout.json", "{}");
            _build.Write("Mod examples.zip", "examples archive");
            if (bigBytes > 0)
            {
                var data = new byte[bigBytes];
                new Random(3).NextBytes(data);
                Directory.CreateDirectory(_build.Sub("Content/classic/sounds"));
                File.WriteAllBytes(_build.Sub("Content/classic/sounds/big.mp3"), data);
            }
        }

        private LauncherService Service() => new LauncherService(_install.Path, _site.SiteUrl);

        private async Task<UpdateResult> Update(bool verify = false)
        {
            var service = Service();
            var manifest = await service.FetchManifestAsync();
            return await service.InstallOrUpdateAsync(manifest, verify, null, CancellationToken.None);
        }

        private string Game(string relative) => _install.Sub("game/" + relative);

        private void AssertGameEqualsBuild()
        {
            var build = Directory.GetFiles(_build.Path, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(_build.Path.Length + 1)).OrderBy(f => f).ToList();
            foreach (var rel in build)
            {
                Assert.True(File.Exists(Game(rel)), "missing " + rel);
                Assert.Equal(File.ReadAllBytes(_build.Sub(rel)), File.ReadAllBytes(Game(rel)));
            }
        }

        [Fact]
        public async Task Install_Update_Remove_UserFiles_Verify()
        {
            // This build may ship files under Mods/ (step 4), as a build packed with --catalogue-mods "" does.
            _site.CatalogueMods = string.Empty;
            MakeFakeGame();
            _site.Publish(_build.Path, "v0.1.1");
            _site.Start();

            // 1. Fresh install.
            var r1 = await Update();
            _log.WriteLine("install: files {0}, packs {1}, network {2}", r1.InstalledFiles, r1.Packs, r1.NetworkBytes);
            Assert.Equal(11, r1.InstalledFiles);
            AssertGameEqualsBuild();
            Assert.Equal("v0.1.1", Service().GetInstalled().Version);
            Assert.True(Service().IsGameInstalled());
            Assert.True(File.Exists(_install.Sub("installed-files.json")));
            // JSON comes gzip-compressed (as Pages sends it), packs as raw bytes.
            var requests = _site.ReadServerLog();
            Assert.Contains(requests, l => l.Contains("manifest.json") && l.Contains("enc=gzip"));
            Assert.Contains(requests, l => l.Contains(".zip") && l.Contains("status=200"));
            Assert.DoesNotContain(requests, l => l.Contains(".zip") && l.Contains("enc=gzip"));

            // A channel without a published build answers 404: reported as "nothing published", not a network error.
            var test = Service();
            test.Channel = UpdateChannel.Test;
            await Assert.ThrowsAsync<FileNotFoundException>(() => test.FetchManifestAsync());

            // 2. User files: a mod of their own and a file that a later build will ship under the same name.
            _install.Write("game/Mods/my_mod/mod.json", "user mod");
            _install.Write("game/Mods/reborn_extra/mod.json", "user copy");

            // 3. Code-only change: one file differs, one pack is downloaded.
            _build.Write("FOEReborn_Data/Managed/FOE.Game.dll", "game code v2");
            _site.Publish(_build.Path, "v0.1.2");
            var r2 = await Update();
            Assert.Equal(1, r2.InstalledFiles);
            Assert.Equal(1, r2.Packs);
            Assert.True(r2.NetworkBytes < 1000, "code pack only: " + r2.NetworkBytes);
            AssertGameEqualsBuild();

            // 4. A removed file goes; user files stay; a shipped file with a user file's name wins, the user's is kept as .bak.
            File.Delete(_build.Sub("Content/classic/sprites/units/raider.png"));
            _build.Write("Mods/reborn_extra/mod.json", "shipped");
            _site.Publish(_build.Path, "v0.1.3");
            var r3 = await Update();
            Assert.Equal(1, r3.RemovedFiles);
            Assert.False(File.Exists(Game("Content/classic/sprites/units/raider.png")));
            Assert.False(Directory.Exists(Game("Content/classic/sprites/units")));
            Assert.Equal("user mod", File.ReadAllText(Game("Mods/my_mod/mod.json")));
            Assert.Equal("shipped", File.ReadAllText(Game("Mods/reborn_extra/mod.json")));
            Assert.Equal("user copy", File.ReadAllText(Game("Mods/reborn_extra/mod.json.bak")));
            Assert.Equal(new[] { "Mods/reborn_extra/mod.json" }, r3.BackedUp.ToArray());
            AssertGameEqualsBuild();

            // 5. A damaged file: the quick check misses it (same size and time), Verify repairs it.
            var victim = Game("Content/classic/defs/units.json");
            var time = File.GetLastWriteTimeUtc(victim);
            File.WriteAllText(victim, "[]");
            File.SetLastWriteTimeUtc(victim, time);
            var quick = await Update();
            Assert.Equal(0, quick.InstalledFiles);
            var verify = await Update(verify: true);
            Assert.Equal(1, verify.InstalledFiles);
            Assert.Equal("{}", File.ReadAllText(victim));
            Assert.Equal("user mod", File.ReadAllText(Game("Mods/my_mod/mod.json")));

            // 6. A deleted file is noticed without Verify.
            File.Delete(Game("UnityPlayer.dll"));
            Assert.Equal(1, (await Update()).InstalledFiles);
            AssertGameEqualsBuild();
        }

        [Fact]
        public async Task InterruptedDownload_ResumesAndOldInstallStaysPlayable()
        {
            MakeFakeGame();
            _site.Publish(_build.Path, "v0.1.1");
            _site.Start();
            await Update();

            const int Big = 6 * 1024 * 1024;
            MakeFakeGame(Big);
            _site.Publish(_build.Path, "v0.1.2");
            _site.Start(rate: 1024 * 1024);

            var service = Service();
            var manifest = await service.FetchManifestAsync();
            var task = service.InstallOrUpdateAsync(manifest, false, null, CancellationToken.None);
            var clock = Stopwatch.StartNew();
            string? partFile = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                partFile = Directory.Exists(_install.Sub(".foere/downloads"))
                    ? Directory.GetFiles(_install.Sub(".foere/downloads"), "*.part").FirstOrDefault()
                    : null;
                if (partFile != null && new FileInfo(partFile).Length > 2 * 1024 * 1024)
                {
                    break;
                }

                await Task.Delay(100);
            }

            Assert.NotNull(partFile);
            _site.Stop(); // the connection breaks mid-file
            await Assert.ThrowsAnyAsync<Exception>(() => task);
            var partBytes = new FileInfo(partFile!).Length;
            _log.WriteLine("part after the break: {0} of {1}", partBytes, Big);
            Assert.True(partBytes > 0 && partBytes < Big);

            // The previous version is untouched and still complete.
            Assert.Equal("v0.1.1", Service().GetInstalled().Version);
            Assert.Equal("fake exe v1", File.ReadAllText(Game("FOEReborn.exe")));
            Assert.False(File.Exists(Game("Content/classic/sounds/big.mp3")));

            _site.Start();
            var r = await Update();
            _log.WriteLine("resume: network {0}, resumed requests {1}", r.NetworkBytes, r.ResumedRequests);
            Assert.Equal(1, r.ResumedRequests);
            Assert.True(r.NetworkBytes < Big - partBytes + 64 * 1024, "only the rest is downloaded: " + r.NetworkBytes);
            AssertGameEqualsBuild();
            Assert.Contains(_site.ReadServerLog(), l => l.Contains("range=bytes=" + partBytes + "-") && l.Contains("status=206"));
        }

        [Fact]
        public async Task FailedSwap_RollsBackToPreviousInstall()
        {
            MakeFakeGame();
            _site.Publish(_build.Path, "v0.1.1");
            _site.Start();
            await Update();

            _build.Write("FOEReborn.exe", "fake exe v2");
            _build.Write("Content/classic/defs/units.json", "{\"v\":2}");
            _build.Write("Content/classic/defs/new.json", "new");
            _site.Publish(_build.Path, "v0.1.2");

            // A file held open without sharing (as a running game holds its files) makes the swap fail midway.
            using (new FileStream(Game("FOEReborn.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Assert.ThrowsAnyAsync<IOException>(() => Update());
            }

            Assert.Equal("v0.1.1", Service().GetInstalled().Version);
            Assert.Equal("fake exe v1", File.ReadAllText(Game("FOEReborn.exe")));
            Assert.Equal("{}", File.ReadAllText(Game("Content/classic/defs/units.json")));
            Assert.False(File.Exists(Game("Content/classic/defs/new.json")));
            Assert.False(File.Exists(_install.Sub(".foere/swap.json")));

            var r = await Update();
            Assert.Equal(3, r.InstalledFiles);
            AssertGameEqualsBuild();
        }

        [Fact]
        public async Task InterruptedSwapJournal_IsRolledBackOnNextRun()
        {
            MakeFakeGame();
            _site.Publish(_build.Path, "v0.1.1");
            _site.Start();
            await Update();

            // State a killed process leaves behind: one file moved to rollback/, a new file created, journal present.
            var u = new Updater(_install.Path, new Downloader(new System.Net.Http.HttpClient()));
            Directory.CreateDirectory(_install.Sub(".foere/rollback"));
            File.Move(Game("FOEReborn.exe"), _install.Sub(".foere/rollback/FOEReborn.exe"));
            File.WriteAllText(Game("FOEReborn.exe"), "half-updated");
            File.WriteAllText(Game("created.txt"), "new");
            File.WriteAllText(u.JournalFile, Json.Write(new Dictionary<string, object?> { ["created"] = new List<object?> { "created.txt" }, ["backups"] = new List<object?>() }));

            u.RecoverInterruptedSwap();
            Assert.Equal("fake exe v1", File.ReadAllText(Game("FOEReborn.exe")));
            Assert.False(File.Exists(Game("created.txt")));
            Assert.False(File.Exists(u.JournalFile));
        }

        [Fact]
        public void DefaultPack_LeavesEveryModOut()
        {
            MakeFakeGame();
            _build.Write("Mods/reborn_home/package.json", "{\"id\":\"reborn_home\",\"version\":\"1.0.0\"}");
            _build.Write("Mods/examples/ex/package.json", "{\"id\":\"ex\",\"version\":\"1.0.0\"}");
            _site.Publish(_build.Path, "v0.1.1");
            var manifest = File.ReadAllText(_site.Work.Sub("site/downloads/manifest.json"));
            Assert.DoesNotContain("\"Mods/", manifest);
            Assert.Contains("\"Mod examples.zip\"", manifest);
        }

        [Fact]
        public async Task RedirectedPackUrl_IsFollowed()
        {
            MakeFakeGame();
            var outDir = _site.Publish(_build.Path, "v0.1.1");
            _site.Start();

            // Point every pack at /cdn/..., which answers 302 like a release asset.
            var manifestPath = _site.Work.Sub("site/downloads/manifest.json");
            var text = File.ReadAllText(manifestPath).Replace(_site.BaseUrl + "v0_1_1/", _site.BaseUrl + "cdn/v0_1_1/");
            File.WriteAllText(manifestPath, text);

            var r = await Update();
            Assert.Equal(11, r.InstalledFiles);
            AssertGameEqualsBuild();
            Assert.Contains(_site.ReadServerLog(), l => l.Contains("/cdn/") && l.Contains("status=302"));
        }

        [Fact]
        public async Task SelfUpdate_DownloadsVerifiesAndSwapsTheExe()
        {
            MakeFakeGame();
            var launcher = typeof(LauncherService).Assembly.Location;
            _site.Publish(_build.Path, "v0.1.1", launcher: launcher);
            _site.Start();

            // Claim a newer version for the same exe, so the swap can be checked by the hash of the file.
            var versionFile = _site.Work.Sub("site/downloads/launcher-version.json");
            var published = LauncherVersionInfo.Parse(File.ReadAllText(versionFile));
            File.WriteAllText(versionFile, File.ReadAllText(versionFile).Replace("\"version\": \"" + published.Version + "\"", "\"version\": \"9.9.9\""));

            var service = Service();
            var info = await service.FetchLauncherVersionAsync();
            Assert.Equal("9.9.9", info.Version);
            Assert.True(LauncherService.IsNewer(info.Version, LauncherService.CurrentLauncherVersion));

            var dir = new TempDir();
            try
            {
                var target = dir.Sub("FOEReLauncher.exe");
                File.WriteAllText(target, "old launcher");
                var versionOut = dir.Sub("started.txt");
                var script = await service.PrepareSelfUpdateAsync(info, target, "--write-version \"" + versionOut + "\"", null, CancellationToken.None);

                // The script waits for this stand-in of the old launcher process to exit.
                var standIn = Process.Start(new ProcessStartInfo("ping", "-n 3 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
                var swap = LauncherService.StartSwap(script, standIn.Id);
                Assert.True(swap.WaitForExit(60000));
                var clock = Stopwatch.StartNew();
                while (!File.Exists(versionOut) && clock.Elapsed < TimeSpan.FromSeconds(30))
                {
                    await Task.Delay(200);
                }

                Assert.True(standIn.HasExited, "stand-in still running; swap exit " + swap.ExitCode + ", version file " + File.Exists(versionOut) + ", script: " + File.ReadAllText(script));
                Assert.Equal(Hashing.Sha256OfFile(launcher), Hashing.Sha256OfFile(target));
                Assert.Equal(published.Version, File.ReadAllText(versionOut).Trim());
            }
            finally
            {
                dir.Dispose();
            }
        }
    }
}
