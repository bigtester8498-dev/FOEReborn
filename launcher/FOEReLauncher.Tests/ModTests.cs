using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace FOEReLauncher.Tests
{
    /// <summary>The game's settings file, catalogue parsing and archive checks (no network).</summary>
    public class ModUnitTests
    {
        private const string GameSettings =
            "{\r\n  \"vsync\": false,\r\n  \"fpsCap\": 60,\r\n  \"stepVolume\": 0.5,\r\n  \"pickup\": {\r\n    \"ammo\": true\r\n  },\r\n" +
            "  \"language\": \"ru\",\r\n  \"mods\": {\r\n    \"enabled\": [\r\n      \"a\"\r\n    ],\r\n    \"disabled\": [],\r\n" +
            "    \"order\": [\r\n      \"a\"\r\n    ],\r\n    \"consent\": {}\r\n  },\r\n  \"masterVolume\": 1.0,\r\n  \"display\": \"windowed\"\r\n}";

        [Fact]
        public void Settings_SpliceKeepsEveryOtherByte()
        {
            using (var dir = new TempDir())
            {
                var path = dir.Sub("settings.json");
                File.WriteAllText(path, GameSettings);
                var file = new GameSettingsFile(path);
                var sel = file.Load();
                Assert.Equal(new[] { "a" }, sel.Enabled);
                sel.SetEnabled("b", true);
                sel.SetEnabled("a", false);
                sel.Consent["b"] = "1.0.0";
                file.Save(sel);
                var text = File.ReadAllText(path);
                Assert.StartsWith(GameSettings.Substring(0, GameSettings.IndexOf("\"mods\"", StringComparison.Ordinal)), text);
                Assert.EndsWith(",\r\n  \"masterVolume\": 1.0,\r\n  \"display\": \"windowed\"\r\n}", text);
                var again = file.Load();
                Assert.Equal(new[] { "b" }, again.Enabled);
                Assert.Equal(new[] { "a" }, again.Disabled);
                Assert.Equal(new[] { "a", "b" }, again.Order);
                Assert.Equal("1.0.0", again.Consent["b"]);
                Assert.False(File.Exists(path + ".launcher.tmp"));

                // The same selection written twice gives the same bytes.
                file.Save(again);
                Assert.Equal(text, File.ReadAllText(path));
            }
        }

        [Fact]
        public void Settings_MissingSectionOrFile()
        {
            Assert.Equal("{\r\n  \"x\": 1,\r\n  \"mods\": {\r\n    \"enabled\": [],\r\n    \"disabled\": [],\r\n    \"order\": [],\r\n    \"consent\": {}\r\n  }\r\n}",
                GameSettingsFile.Splice("{\r\n  \"x\": 1\r\n}", new ModSelection()));
            Assert.StartsWith("{\r\n  \"mods\": {", GameSettingsFile.Splice("{}", new ModSelection()));
            Assert.StartsWith("{\r\n  \"mods\": {", GameSettingsFile.Splice(null, new ModSelection()));
            Assert.StartsWith("{\r\n  \"mods\": {", GameSettingsFile.Splice("not json", new ModSelection()));
            using (var dir = new TempDir())
            {
                var file = new GameSettingsFile(dir.Sub("sub/settings.json"));
                Assert.Empty(file.Load().Enabled);
                var sel = new ModSelection();
                sel.SetEnabled("a", true);
                file.Save(sel);
                Assert.Equal(new[] { "a" }, file.Load().Enabled);
            }
        }

        [Theory]
        [InlineData("package.json", true)]
        [InlineData("Assets/x.dll", true)]
        [InlineData("../x", false)]
        [InlineData("a/../../x", false)]
        [InlineData("/etc/x", false)]
        [InlineData("C:/x", false)]
        [InlineData("a\\b", false)]
        [InlineData("CON", false)]
        [InlineData("a/nul.txt", false)]
        [InlineData("LPT1.json", false)]
        [InlineData("console.txt", true)]
        [InlineData("a//b", false)]
        public void EntryNames(string name, bool ok) => Assert.Equal(ok, ModService.IsSafeEntryName(name));

        [Fact]
        public void Catalogue_ParsesAndSkipsBrokenEntries()
        {
            var sha = new string('a', 64);
            var text = "{\"format\":1,\"mods\":[" +
                       "{\"id\":\"good\",\"name\":{\"ru\":\"Хороший\",\"en\":\"Good\"},\"description\":\"plain\",\"version\":\"1.2.0\",\"kind\":\"code\",\"size\":10,\"sha256\":\"" + sha + "\",\"url\":\"https://x/good.zip\",\"requires\":[{\"id\":\"classic\",\"version\":\">=1.0.2\"}]}," +
                       "{\"id\":\"Bad\",\"version\":\"1\",\"size\":10,\"sha256\":\"" + sha + "\",\"url\":\"https://x/a.zip\"}," +
                       "{\"id\":\"rel\",\"version\":\"1\",\"size\":10,\"sha256\":\"" + sha + "\",\"url\":\"a.zip\"}," +
                       "{\"id\":\"ftp\",\"version\":\"1\",\"size\":10,\"sha256\":\"" + sha + "\",\"url\":\"ftp://x/a.zip\"}," +
                       "{\"id\":\"nosha\",\"version\":\"1\",\"size\":10,\"url\":\"https://x/a.zip\"}," +
                       "{\"id\":\"good\",\"version\":\"2\",\"size\":10,\"sha256\":\"" + sha + "\",\"url\":\"https://x/b.zip\"}]}";
            var c = ModCatalog.Parse(text);
            Assert.Single(c.Entries);
            var e = c.Entries[0];
            Assert.Equal("Хороший", e.Name("ru"));
            Assert.Equal("Good", e.Name("en"));
            Assert.Equal("plain", e.Description("ru"));
            Assert.True(e.IsCode);
            Assert.Equal("classic >=1.0.2", e.Requires[0].ToString());
            Assert.Throws<FormatException>(() => ModCatalog.Parse("{\"format\":2,\"mods\":[]}"));
        }

        internal static string Zip(string path, params (string Name, byte[] Data)[] entries)
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var (name, data) in entries)
                {
                    using (var s = zip.CreateEntry(name).Open())
                    {
                        s.Write(data, 0, data.Length);
                    }
                }
            }

            return path;
        }

        internal static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

        private static CatalogEntry Entry(string id = "m", string version = "1.0.0") => new CatalogEntry { Id = id, Version = version };

        [Fact]
        public void Extract_RefusesUnsafeArchivesAndLeavesNothing()
        {
            using (var dir = new TempDir())
            {
                var stage = dir.Sub("stage");
                var manifest = B("{\"id\":\"m\",\"version\":\"1.0.0\"}");
                var good = Zip(dir.Sub("good.zip"), ("package.json", manifest), ("data/a.json", B("{}")));
                var files = ModService.Extract(good, stage, Entry());
                Assert.Equal(new[] { "data/a.json", "package.json" }, files.Select(f => f.Path).ToArray());
                Assert.True(File.Exists(Path.Combine(stage, "data", "a.json")));

                void Refused(string zip, string key, CatalogEntry? entry = null)
                {
                    var ex = Assert.Throws<ModOperationException>(() => ModService.Extract(zip, stage, entry ?? Entry()));
                    Assert.Equal(key, ex.Key);
                    Assert.False(Directory.Exists(stage), "staging removed after " + key);
                }

                Refused(Zip(dir.Sub("escape.zip"), ("package.json", manifest), ("../evil.txt", B("x"))), "mods.err.unsafe");
                Refused(Zip(dir.Sub("rooted.zip"), ("package.json", manifest), ("/evil.txt", B("x"))), "mods.err.unsafe");
                Refused(Zip(dir.Sub("drive.zip"), ("package.json", manifest), ("C:/evil.txt", B("x"))), "mods.err.unsafe");
                Refused(Zip(dir.Sub("device.zip"), ("package.json", manifest), ("aux.json", B("x"))), "mods.err.unsafe");
                Refused(Zip(dir.Sub("noman.zip"), ("sub/package.json", manifest)), "mods.err.nomanifest");
                Refused(good, "mods.err.wrongmod", Entry("other"));
                Refused(good, "mods.err.wrongmod", Entry("m", "1.0.1"));
                // 20 MB of zeros pack into a few KB: over 200 times the archive.
                Refused(Zip(dir.Sub("bomb.zip"), ("package.json", manifest), ("zeros.bin", new byte[20 * 1024 * 1024])), "mods.err.toobig");
                File.WriteAllText(dir.Sub("notzip.zip"), "this is not a zip");
                Refused(dir.Sub("notzip.zip"), "mods.err.archive");
                Assert.False(File.Exists(dir.Sub("evil.txt")));
            }
        }

        [Fact]
        public void Selection_MoveAndDefaultsLikeTheGame()
        {
            var sel = new ModSelection();
            sel.SetEnabled("b", true);
            sel.Move("b", 1, new[] { "a", "b", "c" });
            Assert.Equal(new[] { "a", "b", "c" }, sel.Order); // enabling put b first; known ids are appended, then +1
            sel.Move("c", -5, new[] { "a", "b", "c" });
            Assert.Equal(new[] { "c", "a", "b" }, sel.Order);
            Assert.Equal(new[] { "d" }, sel.ApplyDefaults(new[] { "b", "d" }));
            sel.SetEnabled("d", false);
            Assert.Empty(sel.ApplyDefaults(new[] { "d" }));
        }

        [Fact]
        public void GameRunning_RefusesEveryChange()
        {
            using (var dir = new TempDir())
            {
                var service = new ModService(dir.Path, dir.Sub("game"), new Downloader(new System.Net.Http.HttpClient()), new GameSettingsFile(dir.Sub("s.json")))
                {
                    GameRunning = () => true
                };
                Assert.Equal("mods.err.running", Assert.Throws<ModOperationException>(() => service.SaveSelection(new ModSelection())).Key);
                Assert.Equal("mods.err.running", Assert.Throws<ModOperationException>(() => service.Remove("x")).Key);
                Assert.Equal("mods.err.running", Assert.Throws<ModOperationException>(() => service.BackupHandAdded("x")).Key);
                Assert.Equal("mods.err.running",
                    Assert.ThrowsAsync<ModOperationException>(() => service.InstallAsync(Entry(), null, CancellationToken.None)).Result.Key);
                Assert.False(File.Exists(dir.Sub("s.json")));
            }
        }
    }

    /// <summary>The catalogue end to end: publish.py mods + test_server.py on 127.0.0.1 + a fake game.</summary>
    public class ModCatalogueEndToEndTests : IDisposable
    {
        private readonly ITestOutputHelper _log;
        private readonly LocalSite _site;
        private readonly TempDir _install = new TempDir();
        private readonly TempDir _build = new TempDir();
        private readonly TempDir _mods = new TempDir();

        public ModCatalogueEndToEndTests(ITestOutputHelper log)
        {
            _log = log;
            _site = new LocalSite(log);
        }

        public void Dispose()
        {
            _site.Dispose();
            _install.Dispose();
            _build.Dispose();
            _mods.Dispose();
        }

        private string GameSettings => _install.Sub("game-settings.json");

        private LauncherService Service() => new LauncherService(_install.Path, _site.SiteUrl, null, GameSettings);

        private string Game(string relative) => _install.Sub("game/" + relative);

        private static string Manifest(string id, string version, string requires = "", string extra = "") =>
            "{\"id\":\"" + id + "\",\"name\":\"Mod " + id + "\",\"version\":\"" + version + "\"" +
            (requires.Length > 0 ? ",\"requires\":[" + requires + "]" : string.Empty) + extra + "}";

        /// <summary>A fake game with the real package layout: classic and reborn in Content/, reborn_home shipped in Mods/,
        /// and an examples group that --catalogue-mods leaves out of the packs.</summary>
        private void MakeGame()
        {
            _build.Write("FOEReborn.exe", "fake exe");
            _build.Write("FOEReborn_Data/Managed/FOE.Game.dll", "code v1");
            _build.Write("Content/classic/package.json", Manifest("classic", "1.0.2"));
            _build.Write("Content/reborn/package.json", Manifest("reborn", "0.1.0"));
            _build.Write("Mods/reborn_home/package.json", Manifest("reborn_home", "1.0.0", "{\"id\":\"classic\",\"version\":\">=1.0.2\"}", ",\"defaultEnabled\":true"));
            _build.Write("Mods/examples/ex_data/package.json", Manifest("ex_data", "1.0.0"));
            _build.Write("Mods/reforged_balance/package.json", Manifest("reforged_balance", "1.0.0"));
        }

        /// <summary>Catalogue mods in their own folder (as Mods/examples in the game repository).</summary>
        private void MakeMods()
        {
            Write("ex_home/package.json", Manifest("ex_home", "1.0.0", "{\"id\":\"classic\",\"version\":\">=1.0.2\"},{\"id\":\"reborn_home\",\"version\":\">=1.0.0 <2.0.0\"}"));
            Write("ex_home/rooms/cabin.json", "{\"room\":1}");
            Write("ex_home/catalog.ru.json", "{\"name\":\"Пример: домик\",\"description\":\"Второй дом\"}");
            Write("ex_code/package.json", Manifest("ex_code", "1.0.0", "", ",\"assemblies\":[\"Assemblies/ExCode.dll\"]"));
            Write("ex_code/Assemblies/ExCode.dll", "MZ code 1");
            Write("ex_code/old.txt", "only in 1.0.0");
            Write("ex_addon/package.json", Manifest("ex_addon", "1.0.0", "{\"id\":\"ex_code\",\"version\":\"^1.0.0\"}", ",\"assemblies\":[\"Assemblies/ExAddon.dll\"]"));
            Write("ex_addon/Assemblies/ExAddon.dll", "MZ addon");
            Write("ex_data/package.json", Manifest("ex_data", "1.0.0"));
            Write("ex_data/defs/x.json", "{}");
            Write("ex_orphan/package.json", Manifest("ex_orphan", "1.0.0", "{\"id\":\"nowhere\"}"));
        }

        private void Write(string rel, string text) => _mods.Write(rel, text);

        private async Task InstallGame()
        {
            var service = Service();
            await service.InstallOrUpdateAsync(await service.FetchManifestAsync(), false, null, CancellationToken.None);
        }

        [Fact]
        public async Task Catalogue_Install_Update_Remove_HandAdded_GameUpdate()
        {
            // A build that still ships one mod (reborn_home) inside the game, so a requirement "with the game" is covered.
            _site.CatalogueMods = "Mods/examples/*,Mods/reforged_balance";
            MakeGame();
            MakeMods();
            _site.Publish(_build.Path, "v0.1.1");
            _site.PublishMods(_mods.Path);
            _site.Start();
            await InstallGame();

            // --catalogue-mods left the examples and reforged_balance out of the game.
            Assert.True(File.Exists(Game("Mods/reborn_home/package.json")));
            Assert.False(Directory.Exists(Game("Mods/examples")));
            Assert.False(Directory.Exists(Game("Mods/reforged_balance")));

            var service = Service();
            var catalog = await service.FetchCatalogAsync();
            Assert.Equal(5, catalog.Entries.Count);
            Assert.Equal("Пример: домик", catalog.Find("ex_home")!.Name("ru"));
            Assert.Equal("Mod ex_home", catalog.Find("ex_home")!.Name("en"));
            Assert.True(catalog.Find("ex_code")!.IsCode);
            var packages = service.Mods.Scan(out var sel);
            Assert.Equal(new[] { "reborn_home" }, sel.Enabled); // defaultEnabled, as the game starts
            Assert.Equal(CatalogState.NotInstalled, service.Mods.StateOf(catalog.Find("ex_home")!, packages));

            // A requirement shipped with the game: one archive.
            Assert.Equal(new[] { "ex_home" }, service.Mods.PlanInstall(catalog.Find("ex_home")!, catalog, packages).Select(e => e.Id).ToArray());
            // A catalogue requirement comes first; a requirement found nowhere refuses.
            var plan = service.Mods.PlanInstall(catalog.Find("ex_addon")!, catalog, packages);
            Assert.Equal(new[] { "ex_code", "ex_addon" }, plan.Select(e => e.Id).ToArray());
            Assert.Equal("mods.err.reqmissing", Assert.Throws<ModOperationException>(() => service.Mods.PlanInstall(catalog.Find("ex_orphan")!, catalog, packages)).Key);

            await service.Mods.InstallAsync(catalog.Find("ex_home")!, null, CancellationToken.None);
            foreach (var e in plan)
            {
                await service.Mods.InstallAsync(e, null, CancellationToken.None);
            }

            Assert.Equal("{\"room\":1}", File.ReadAllText(Game("Mods/ex_home/rooms/cabin.json")));
            Assert.False(File.Exists(Game("Mods/ex_home/catalog.ru.json")), "catalogue metadata is not part of the mod");
            Assert.Equal("MZ code 1", File.ReadAllText(Game("Mods/ex_code/Assemblies/ExCode.dll")));
            var record = service.Mods.LoadRecord();
            Assert.Equal(new[] { "ex_addon", "ex_code", "ex_home" }, record.Mods.Select(m => m.Id).OrderBy(x => x).ToArray());
            Assert.Contains(record.Find("ex_code")!.Files, f => f.Path == "Mods/ex_code/Assemblies/ExCode.dll");
            Assert.True(File.Exists(_install.Sub("installed-mods.json")));
            packages = service.Mods.Scan(out sel);
            Assert.Equal(CatalogState.Installed, service.Mods.StateOf(catalog.Find("ex_code")!, packages));
            Assert.Equal(ModSource.Catalogue, service.Mods.SourceOf(packages.First(p => p.Id == "ex_code")));
            Assert.Equal(ModSource.Game, service.Mods.SourceOf(packages.First(p => p.Id == "reborn_home")));
            Assert.Equal(CatalogState.WithGame, service.Mods.StateOf(new CatalogEntry { Id = "reborn_home", Version = "1.0.0" }, packages));

            // Enable the addon with the game's cascade (ex_code comes with it) and save.
            var with = ModRules.ToEnable("ex_addon", packages, sel);
            Assert.Equal(new[] { "ex_code" }, with);
            sel.SetEnabled("ex_addon", true);
            foreach (var x in with)
            {
                sel.SetEnabled(x, true);
            }

            sel.Consent["ex_addon"] = "1.0.0";
            sel.Consent["ex_code"] = "1.0.0";
            service.Mods.SaveSelection(sel);
            Assert.True(service.Mods.Preview(service.Mods.Settings.Load()).First(p => p.Id == "ex_addon").Active);

            // Game update and Verify with catalogue mods present: they are not the game's files and stay.
            _build.Write("FOEReborn_Data/Managed/FOE.Game.dll", "code v2");
            _site.Publish(_build.Path, "v0.1.2");
            var upd = Service();
            var r = await upd.InstallOrUpdateAsync(await upd.FetchManifestAsync(), false, null, CancellationToken.None);
            Assert.Equal(1, r.InstalledFiles);
            Assert.Equal(0, r.RemovedFiles);
            var v = await upd.InstallOrUpdateAsync(await upd.FetchManifestAsync(), true, null, CancellationToken.None);
            Assert.Equal(0, v.InstalledFiles);
            Assert.Equal(0, v.RemovedFiles);
            Assert.Equal("MZ code 1", File.ReadAllText(Game("Mods/ex_code/Assemblies/ExCode.dll")));
            Assert.True(File.Exists(Game("Mods/ex_home/rooms/cabin.json")));

            // Update: ex_code 1.1.0 drops old.txt; a file the player put into the folder stays.
            _install.Write("game/Mods/ex_code/my-notes.txt", "player's");
            Write("ex_code/package.json", Manifest("ex_code", "1.1.0", "", ",\"assemblies\":[\"Assemblies/ExCode.dll\"]"));
            Write("ex_code/Assemblies/ExCode.dll", "MZ code 2");
            File.Delete(_mods.Sub("ex_code/old.txt"));
            _site.PublishMods(_mods.Sub("ex_code"));
            catalog = await service.FetchCatalogAsync();
            Assert.Equal(5, catalog.Entries.Count); // the other entries are kept from --prev
            packages = service.Mods.Scan(out sel);
            Assert.Equal(CatalogState.UpdateAvailable, service.Mods.StateOf(catalog.Find("ex_code")!, packages));
            Assert.Equal(CatalogState.Installed, service.Mods.StateOf(catalog.Find("ex_home")!, packages));
            await service.Mods.InstallAsync(catalog.Find("ex_code")!, null, CancellationToken.None);
            Assert.Equal("MZ code 2", File.ReadAllText(Game("Mods/ex_code/Assemblies/ExCode.dll")));
            Assert.False(File.Exists(Game("Mods/ex_code/old.txt")));
            Assert.Equal("player's", File.ReadAllText(Game("Mods/ex_code/my-notes.txt")));
            Assert.Equal("1.1.0", service.Mods.LoadRecord().Find("ex_code")!.Version);

            // Remove with a dependent: ex_addon is switched off, ex_code leaves enabled and order.
            var off = service.Mods.Remove("ex_code");
            Assert.Equal(new[] { "ex_addon" }, off);
            var after = service.Mods.Settings.Load();
            Assert.DoesNotContain("ex_code", after.Enabled);
            Assert.DoesNotContain("ex_code", after.Order);
            Assert.Contains("ex_addon", after.Disabled);
            Assert.False(File.Exists(Game("Mods/ex_code/Assemblies/ExCode.dll")));
            Assert.False(Directory.Exists(Game("Mods/ex_code/Assemblies")));
            Assert.Equal("player's", File.ReadAllText(Game("Mods/ex_code/my-notes.txt")));
            Assert.Null(service.Mods.LoadRecord().Find("ex_code"));

            // A folder added by hand with a catalogue id.
            _install.Write("game/Mods/my_copy/package.json", Manifest("ex_data", "0.9.0"));
            packages = service.Mods.Scan(out sel);
            Assert.Equal(CatalogState.AddedByHand, service.Mods.StateOf(catalog.Find("ex_data")!, packages));
            Assert.Equal("mods.err.byhand", (await Assert.ThrowsAsync<ModOperationException>(() => service.Mods.InstallAsync(catalog.Find("ex_data")!, null, CancellationToken.None))).Key);
            var backup = service.Mods.BackupHandAdded("ex_data");
            Assert.StartsWith(_install.Sub(".foere\\mod-backups\\ex_data-"), backup);
            Assert.True(File.Exists(Path.Combine(backup, "package.json")));
            Assert.False(Directory.Exists(Game("Mods/my_copy")));
            await service.Mods.InstallAsync(catalog.Find("ex_data")!, null, CancellationToken.None);
            Assert.True(File.Exists(Game("Mods/ex_data/defs/x.json")));

            // The cheap check: version.json names the installed version and the manifest is cached.
            var lines = _site.ReadServerLog().Length;
            var check = Service();
            var m = await check.CheckAsync(forceManifest: false);
            Assert.True(check.LastCheckUsedCache);
            Assert.Equal("v0.1.2", m.Version);
            var newLines = _site.ReadServerLog().Skip(lines).ToList();
            foreach (var l in newLines)
            {
                _log.WriteLine("ordinary check: " + l);
            }

            Assert.Single(newLines);
            Assert.Contains("version.json", newLines[0]);
            // Verify downloads the manifest again.
            await check.CheckAsync(forceManifest: true);
            Assert.False(check.LastCheckUsedCache);
        }

        [Fact]
        public async Task Catalogue_BadArchivesInstallNothing()
        {
            MakeGame();
            _site.Publish(_build.Path, "v0.1.1");
            Write("ok/package.json", Manifest("ok", "1.0.0"));
            var outDir = _site.PublishMods(_mods.Path);
            _site.Start();
            await InstallGame();
            var service = Service();
            var good = (await service.FetchCatalogAsync()).Find("ok")!;

            // Bad archives served by the site, each described by a catalogue entry with its true size and hash
            // (except the wrong-hash case).
            var assets = Path.Combine(outDir, "assets");
            CatalogEntry Bad(string id, string file, string version = "1.0.0", string? sha = null)
            {
                var path = Path.Combine(assets, file);
                return new CatalogEntry
                {
                    Id = id,
                    Version = version,
                    Size = new FileInfo(path).Length,
                    Sha256 = sha ?? Hashing.Sha256OfFile(path),
                    Url = _site.BaseUrl + Path.GetFileName(outDir) + "/assets/" + file
                };
            }

            var manifest = ModUnitTests.B(Manifest("evil", "1.0.0"));
            ModUnitTests.Zip(Path.Combine(assets, "escape.zip"), ("package.json", manifest), ("../../escaped.txt", ModUnitTests.B("x")));
            ModUnitTests.Zip(Path.Combine(assets, "wrongid.zip"), ("package.json", ModUnitTests.B(Manifest("other", "1.0.0"))));
            ModUnitTests.Zip(Path.Combine(assets, "bomb.zip"), ("package.json", manifest), ("zeros.bin", new byte[30 * 1024 * 1024]));

            var cases = new (CatalogEntry Entry, string Expected)[]
            {
                (Bad("evil", "escape.zip"), "mods.err.unsafe"),
                (Bad("evil", "wrongid.zip"), "mods.err.wrongmod"),
                (Bad("evil", "bomb.zip"), "mods.err.toobig"),
                (Bad("ok", good.Url.Substring(good.Url.LastIndexOf('/') + 1), sha: new string('0', 64)), "hash")
            };
            foreach (var (entry, expected) in cases)
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.Mods.InstallAsync(entry, null, CancellationToken.None));
                _log.WriteLine("{0}: {1}", expected, ex.Message);
                if (expected == "hash")
                {
                    Assert.IsType<InvalidDataException>(ex);
                }
                else
                {
                    Assert.Equal(expected, ((ModOperationException)ex).Key);
                }

                Assert.False(Directory.Exists(Game("Mods/" + entry.Id)) && entry.Id == "evil");
                Assert.Empty(Directory.Exists(service.Mods.StagingDir) ? Directory.GetFileSystemEntries(service.Mods.StagingDir) : new string[0]);
            }

            Assert.False(File.Exists(Game("escaped.txt")));
            Assert.False(File.Exists(_install.Sub("escaped.txt")));
            Assert.False(Directory.Exists(Game("Mods/ok")));
            Assert.Empty(service.Mods.LoadRecord().Mods);

            // The real archive still installs.
            await service.Mods.InstallAsync(good, null, CancellationToken.None);
            Assert.True(File.Exists(Game("Mods/ok/package.json")));
        }

        [Fact]
        public async Task Catalogue_InterruptedDownloadResumes()
        {
            MakeGame();
            _site.Publish(_build.Path, "v0.1.1");
            var data = new byte[6 * 1024 * 1024];
            new Random(5).NextBytes(data);
            Directory.CreateDirectory(_mods.Sub("big/data"));
            File.WriteAllBytes(_mods.Sub("big/data/blob.bin"), data);
            Write("big/package.json", Manifest("big", "1.0.0"));
            _site.PublishMods(_mods.Path);
            _site.Start();
            await InstallGame();

            _site.Start(rate: 1024 * 1024);
            var service = Service();
            var entry = (await service.FetchCatalogAsync()).Find("big")!;
            var task = service.Mods.InstallAsync(entry, null, CancellationToken.None);
            var clock = Stopwatch.StartNew();
            string? part = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                part = Directory.Exists(service.Mods.DownloadDir) ? Directory.GetFiles(service.Mods.DownloadDir, "*.part").FirstOrDefault() : null;
                if (part != null && new FileInfo(part).Length > 2 * 1024 * 1024)
                {
                    break;
                }

                await Task.Delay(100);
            }

            Assert.NotNull(part);
            _site.Stop();
            await Assert.ThrowsAnyAsync<Exception>(() => task);
            var partBytes = new FileInfo(part!).Length;
            Assert.False(Directory.Exists(Game("Mods/big")));

            _site.Start();
            var again = Service();
            await again.Mods.InstallAsync(entry, null, CancellationToken.None);
            Assert.Equal(data, File.ReadAllBytes(Game("Mods/big/data/blob.bin")));
            Assert.Contains(_site.ReadServerLog(), l => l.Contains("range=bytes=" + partBytes + "-") && l.Contains("status=206"));
            _log.WriteLine("resumed at {0} of {1}", partBytes, entry.Size);
        }
    }
}
