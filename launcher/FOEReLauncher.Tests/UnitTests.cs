using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FOEReLauncher.Tests
{
    public sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foere-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Sub(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', '\\'));

        public void Write(string relative, string content)
        {
            var full = Sub(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (Exception)
            {
                // Temp leftovers are harmless.
            }
        }
    }

    internal static class Make
    {
        public static string Sha(string text)
        {
            using (var sha = SHA256.Create())
            {
                return Hashing.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
            }
        }

        /// <summary>A manifest whose files have the given contents, all in one pack "p.zip".</summary>
        public static Manifest Manifest(string version, params (string path, string content)[] files)
        {
            var packs = new List<PackInfo> { new PackInfo("p.zip", "http://127.0.0.1/p.zip", 1, new string('0', 64)) };
            var list = files.Select(f => new FileEntry(f.path, Encoding.UTF8.GetByteCount(f.content), Sha(f.content), "p.zip")).ToList();
            return new Manifest(version, string.Empty, "FOEReborn.exe", packs, list);
        }

        public static string ManifestJson(string path, string url = "http://127.0.0.1:1/a/p-1.zip") =>
            "{\"version\":\"v0.1.5\",\"packs\":[{\"name\":\"p-1.zip\",\"url\":\"" + url + "\",\"size\":10,\"sha256\":\"AB\"}]," +
            "\"files\":[{\"path\":" + Json.Quote(path) + ",\"size\":3,\"sha256\":\"CD\",\"pack\":\"p-1.zip\"}]}";
    }

    public class ManifestTests
    {
        [Fact]
        public void Parse_ReadsPacksAndFiles()
        {
            var m = Manifest.Parse(Make.ManifestJson("Content/classic/a b.png"));
            Assert.Equal("v0.1.5", m.Version);
            Assert.Single(m.Packs);
            Assert.Equal("ab", m.Packs[0].Sha256);
            Assert.Equal(10, m.Packs[0].Size);
            Assert.Equal("Content/classic/a b.png", m.Files[0].Path);
            Assert.Equal("cd", m.Files[0].Sha256);
            Assert.Equal("p-1.zip", m.Files[0].Pack);
            Assert.Equal("FOEReborn.exe", m.Game);
        }

        [Theory]
        [InlineData("../evil.dll")]
        [InlineData("Content/../../evil.dll")]
        [InlineData("/abs/evil.dll")]
        [InlineData("C:/Windows/evil.dll")]
        [InlineData("C:evil.dll")]
        [InlineData("Content\\evil.dll")]
        [InlineData("Content//evil.dll")]
        [InlineData("./evil.dll")]
        [InlineData("Content/evil.dll.")]
        [InlineData("")]
        public void Parse_RefusesUnsafePaths(string path)
        {
            Assert.Throws<FormatException>(() => Manifest.Parse(Make.ManifestJson(path)));
        }

        [Fact]
        public void Parse_RefusesRelativeOrNonHttpUrls()
        {
            Assert.Throws<FormatException>(() => Manifest.Parse(Make.ManifestJson("a.txt", "assets/p-1.zip")));
            Assert.Throws<FormatException>(() => Manifest.Parse(Make.ManifestJson("a.txt", "file:///C:/p-1.zip")));
        }

        [Fact]
        public void Parse_RefusesUnknownPackAndDuplicatePaths()
        {
            var unknown = "{\"version\":\"v1\",\"packs\":[],\"files\":[{\"path\":\"a\",\"size\":1,\"sha256\":\"x\",\"pack\":\"q.zip\"}]}";
            Assert.Throws<FormatException>(() => Manifest.Parse(unknown));
            var dup = "{\"version\":\"v1\",\"packs\":[{\"name\":\"p.zip\",\"url\":\"http://h/p.zip\",\"size\":1,\"sha256\":\"x\"}]," +
                      "\"files\":[{\"path\":\"a\",\"size\":1,\"sha256\":\"x\",\"pack\":\"p.zip\"},{\"path\":\"A\",\"size\":1,\"sha256\":\"x\",\"pack\":\"p.zip\"}]}";
            Assert.Throws<FormatException>(() => Manifest.Parse(dup));
        }

        [Fact]
        public void PathSafety_CombineStaysInsideRoot()
        {
            using (var dir = new TempDir())
            {
                var full = PathSafety.Combine(dir.Path, "Mods/x/mod.json");
                Assert.StartsWith(dir.Path, full, StringComparison.OrdinalIgnoreCase);
                Assert.Throws<InvalidDataException>(() => PathSafety.Combine(dir.Path, "../x"));
                Assert.Throws<InvalidDataException>(() => PathSafety.Combine(dir.Path, "C:/x"));
            }
        }
    }

    public class VersionAndNotesTests
    {
        [Theory]
        [InlineData("1.0.1", "1.0.0", true)]
        [InlineData("1.0.10", "1.0.9", true)]
        [InlineData("v1.2", "1.1.9", true)]
        [InlineData("1.0.0", "1.0.0", false)]
        [InlineData("1.0.0", "1.0.1", false)]
        [InlineData("garbage", "1.0.0", false)]
        [InlineData("1.0.0", "", true)]
        [InlineData("", "1.0.0", false)]
        public void IsNewer(string candidate, string current, bool expected)
        {
            Assert.Equal(expected, LauncherService.IsNewer(candidate, current));
        }

        [Fact]
        public void Notes_ParseBothLanguagesWithFallback()
        {
            var json = "{\"entries\":[{\"version\":\"v0.1.9\",\"date\":\"2026-11-12T20:00:00Z\",\"ru\":[\"Исправлено\"],\"en\":[\"Fixed\",\"More\"]}," +
                       "{\"version\":\"v0.1.8\",\"date\":\"x\",\"ru\":[],\"en\":[\"Only English\"]},{\"nope\":1}]}";
            var notes = PatchNote.ParseList(json);
            Assert.Equal(2, notes.Count);
            Assert.Equal("v0.1.9", notes[0].Version);
            Assert.Equal(new[] { "Исправлено" }, notes[0].Lines("ru"));
            Assert.Equal(new[] { "Fixed", "More" }, notes[0].Lines("en"));
            Assert.Equal(new[] { "Only English" }, notes[1].Lines("ru"));
            Assert.Equal("2026-11-12", notes[0].DisplayDate);
        }

        [Fact]
        public void Notes_AcceptPlainArray()
        {
            var notes = PatchNote.ParseList("[{\"version\":\"v1\",\"en\":[\"a\"]}]");
            Assert.Single(notes);
        }

        [Fact]
        public void LauncherVersion_Parse()
        {
            var info = LauncherVersionInfo.Parse("{\"version\":\"1.0.4\",\"file\":\"FOEReLauncher.exe\",\"sha256\":\"AA\",\"size\":5}");
            Assert.Equal("1.0.4", info.Version);
            Assert.Equal("aa", info.Sha256);
            Assert.Throws<FormatException>(() => LauncherVersionInfo.Parse("{\"version\":\"1\",\"file\":\"../x.exe\"}"));
        }

        [Fact]
        public void Texts_EveryKeyHasRussianAndEnglish()
        {
            foreach (var key in Texts.Keys)
            {
                Assert.True(Texts.HasBoth(key), key);
            }
        }

        [Fact]
        public void Json_RoundTrip()
        {
            var state = new InstalledState("v0.1.2", "test", new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase)
            {
                ["a/б c.txt"] = new InstalledFile("a/б c.txt", 5, "ff", 637000000000000000)
            });
            var back = InstalledState.Parse(state.Serialize());
            Assert.Equal("v0.1.2", back.Version);
            Assert.Equal("test", back.Channel);
            Assert.Equal(637000000000000000, back.Files["A/Б C.txt"].Mtime);
        }
    }

    public class PlanTests
    {
        private static Updater MakeUpdater(TempDir dir) => new Updater(dir.Path, new Downloader(new HttpClient()));

        private static InstalledState StateOf(Updater u, params string[] paths)
        {
            var files = new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                var full = PathSafety.Combine(u.GameDir, p);
                files[p] = new InstalledFile(p, new FileInfo(full).Length, Hashing.Sha256OfFile(full), File.GetLastWriteTimeUtc(full).Ticks);
            }

            return new InstalledState("v1", "prod", files);
        }

        [Fact]
        public void Plan_FindsChangedMissingAndExtraFiles()
        {
            using (var dir = new TempDir())
            {
                dir.Write("game/same.txt", "same");
                dir.Write("game/changed.txt", "old");
                dir.Write("game/gone.txt", "gone");
                var u = MakeUpdater(dir);
                var state = StateOf(u, "same.txt", "changed.txt", "gone.txt");
                var m = Make.Manifest("v2", ("same.txt", "same"), ("changed.txt", "new!"), ("missing.txt", "m"));
                var plan = u.Plan(m, state, fullHash: false);
                Assert.Equal(new[] { "changed.txt", "missing.txt" }, plan.ToInstall.Select(f => f.Path).ToArray());
                Assert.Equal(new[] { "gone.txt" }, plan.ToRemove.ToArray());
                Assert.True(plan.Unchanged.ContainsKey("same.txt"));
                Assert.Single(plan.Packs);
                Assert.Empty(plan.UserFilesReplaced);
            }
        }

        [Fact]
        public void Plan_NeverRemovesUserFiles_AndMarksShippedNameClashes()
        {
            using (var dir = new TempDir())
            {
                dir.Write("game/FOEReborn.exe", "exe");
                dir.Write("game/Mods/my_mod/mod.json", "mine");
                dir.Write("game/Mods/clash/mod.json", "user version");
                var u = MakeUpdater(dir);
                var state = StateOf(u, "FOEReborn.exe");
                var m = Make.Manifest("v2", ("FOEReborn.exe", "exe"), ("Mods/clash/mod.json", "shipped"));
                var plan = u.Plan(m, state, fullHash: false);
                Assert.Empty(plan.ToRemove);
                Assert.Equal(new[] { "Mods/clash/mod.json" }, plan.UserFilesReplaced.ToArray());
                Assert.Equal(new[] { "Mods/clash/mod.json" }, plan.ToInstall.Select(f => f.Path).ToArray());
            }
        }

        [Fact]
        public void Plan_QuickCheckTrustsRecord_FullHashDoesNot()
        {
            using (var dir = new TempDir())
            {
                dir.Write("game/a.txt", "abcd");
                var u = MakeUpdater(dir);
                var state = StateOf(u, "a.txt");
                var full = dir.Sub("game/a.txt");
                var mtime = File.GetLastWriteTimeUtc(full);

                // Same size, same mtime, different bytes: only a full hash sees it.
                File.WriteAllText(full, "abce");
                File.SetLastWriteTimeUtc(full, mtime);
                var m = Make.Manifest("v1", ("a.txt", "abcd"));
                Assert.Empty(u.Plan(m, state, fullHash: false).ToInstall);
                Assert.Single(u.Plan(m, state, fullHash: true).ToInstall);
            }
        }

        [Fact]
        public void Plan_ChangedMtimeRehashes()
        {
            using (var dir = new TempDir())
            {
                dir.Write("game/a.txt", "abcd");
                var u = MakeUpdater(dir);
                var state = StateOf(u, "a.txt");
                var full = dir.Sub("game/a.txt");
                File.WriteAllText(full, "abce");
                File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(5));
                Assert.Single(u.Plan(Make.Manifest("v1", ("a.txt", "abcd")), state, fullHash: false).ToInstall);
            }
        }
    }

    /// <summary>In-process HTTP handler that serves one byte array and honours Range requests.</summary>
    internal sealed class RangeHandler : HttpMessageHandler
    {
        private readonly byte[] _data;

        public RangeHandler(byte[] data) => _data = data;

        public List<string> Ranges { get; } = new List<string>();

        public bool IgnoreRange { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range;
            Ranges.Add(range?.ToString() ?? "-");
            long from = 0;
            var status = HttpStatusCode.OK;
            if (range != null && !IgnoreRange)
            {
                from = range.Ranges.First().From ?? 0;
                status = HttpStatusCode.PartialContent;
            }

            var body = new byte[_data.Length - from];
            Array.Copy(_data, from, body, 0, body.Length);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    public class DownloaderTests
    {
        private static byte[] Data(int n)
        {
            var data = new byte[n];
            new Random(7).NextBytes(data);
            return data;
        }

        [Fact]
        public async Task Resume_ContinuesPartialFileWithRange()
        {
            using (var dir = new TempDir())
            {
                var data = Data(100000);
                var target = dir.Sub("p.zip");
                File.WriteAllBytes(target + ".part", data.Take(40000).ToArray());
                var handler = new RangeHandler(data);
                var d = new Downloader(new HttpClient(handler));
                await d.DownloadVerifiedAsync("http://127.0.0.1/p.zip", target, data.Length, Hashing.Hex(SHA256.Create().ComputeHash(data)), null, CancellationToken.None);
                Assert.Equal(new[] { "bytes=40000-" }, handler.Ranges.ToArray());
                Assert.Equal(60000, d.NetworkBytes);
                Assert.Equal(1, d.ResumedRequests);
                Assert.Equal(data, File.ReadAllBytes(target));
                Assert.False(File.Exists(target + ".part"));
            }
        }

        [Fact]
        public async Task Resume_WrongPartIsDownloadedAgainInFull()
        {
            using (var dir = new TempDir())
            {
                var data = Data(50000);
                var target = dir.Sub("p.zip");
                File.WriteAllBytes(target + ".part", Data(20000).Select(b => (byte)(b ^ 0xFF)).ToArray());
                var handler = new RangeHandler(data);
                var d = new Downloader(new HttpClient(handler));
                await d.DownloadVerifiedAsync("http://127.0.0.1/p.zip", target, data.Length, Hashing.Hex(SHA256.Create().ComputeHash(data)), null, CancellationToken.None);
                Assert.Equal(new[] { "bytes=20000-", "-" }, handler.Ranges.ToArray());
                Assert.Equal(data, File.ReadAllBytes(target));
            }
        }

        [Fact]
        public async Task Resume_ServerWithoutRangeRestartsFromZero()
        {
            using (var dir = new TempDir())
            {
                var data = Data(30000);
                var target = dir.Sub("p.zip");
                File.WriteAllBytes(target + ".part", data.Take(10000).ToArray());
                var handler = new RangeHandler(data) { IgnoreRange = true };
                long reported = 0;
                var d = new Downloader(new HttpClient(handler));
                await d.DownloadVerifiedAsync("http://127.0.0.1/p.zip", target, data.Length, Hashing.Hex(SHA256.Create().ComputeHash(data)), b => reported += b, CancellationToken.None);
                Assert.Equal(data, File.ReadAllBytes(target));
                Assert.Equal(data.Length, reported);
            }
        }
    }
}
