using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace FOEReLauncher.Tests
{
    /// <summary>
    /// cases/modrules-cases.json against <see cref="ModRules"/>. The same file runs in the game as the EditMode test
    /// L1bModRulesCases (a byte-identical copy in unity/FOEReborn/Assets/Tests/EditMode/Data/), so the launcher's mod
    /// list shows what the game will load.
    /// </summary>
    public class ModRulesCasesTests
    {
        private readonly ITestOutputHelper _log;

        public ModRulesCasesTests(ITestOutputHelper log) => _log = log;

        internal static string RepoRoot
        {
            get
            {
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "tools", "publish.py")))
                {
                    dir = Path.GetDirectoryName(dir);
                }

                return dir ?? throw new InvalidOperationException("repository root not found");
            }
        }

        internal static string CasesFile => Path.Combine(RepoRoot, "launcher", "FOEReLauncher.Tests", "cases", "modrules-cases.json");

        private static Dictionary<string, object?> LoadCases() => Json.Obj(Json.Parse(File.ReadAllText(CasesFile, Encoding.UTF8)));

        /// <summary>Writes one case's game folder: an object value is a manifest, a string the raw file text.</summary>
        internal static void WriteFiles(string root, Dictionary<string, object?> files)
        {
            foreach (var pair in files)
            {
                var full = Path.Combine(root, pair.Key.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, pair.Value is string s ? s : Json.Write(pair.Value), new UTF8Encoding(false));
            }
        }

        /// <summary>The launcher's answers for a case, in the "expect" layout.</summary>
        internal static Dictionary<string, object?> Answer(Dictionary<string, object?> c)
        {
            using (var dir = new TempDir())
            {
                WriteFiles(dir.Path, Json.Obj(c["files"]));
                var all = ModRules.Discover(Path.Combine(dir.Path, "Content"), Path.Combine(dir.Path, "Mods"));
                ModSelection? sel = null;
                if (c["settings"] != null)
                {
                    sel = ModSelection.FromJson(c["settings"]);
                    if (c["defaults"] is bool d && d)
                    {
                        sel = ModRules.WithDefaults(sel, all);
                    }
                }

                var order = ModRules.Order(all, sel);
                var packages = new List<object?>();
                foreach (var p in all)
                {
                    var o = new Dictionary<string, object?>
                    {
                        ["dir"] = p.RelDir,
                        ["id"] = p.Id,
                        ["state"] = p.State.ToString(),
                        ["reasonKey"] = p.ReasonKey,
                        ["code"] = p.HasCode
                    };
                    // Exception texts of an unreadable file differ between JSON libraries: only the key is shared.
                    if (p.ReasonKey != "unreadable")
                    {
                        o["reason"] = p.Reason;
                        o["reasonRu"] = ModRules.ReasonText(p, "ru");
                    }

                    o["issues"] = p.Issues.Cast<object?>().ToList();
                    packages.Add(o);
                }

                var effective = sel ?? new ModSelection();
                var toEnable = new Dictionary<string, object?>();
                foreach (var id in Json.Strings(c, "toEnable"))
                {
                    toEnable[id] = ModRules.ToEnable(id, all, effective).Cast<object?>().ToList();
                }

                var toDisable = new Dictionary<string, object?>();
                foreach (var id in Json.Strings(c, "toDisable"))
                {
                    toDisable[id] = ModRules.ToDisable(id, all, effective).Cast<object?>().ToList();
                }

                return new Dictionary<string, object?>
                {
                    ["packages"] = packages,
                    ["order"] = order.Select(p => (object?)p.Id).ToList(),
                    ["enabled"] = sel == null ? null : sel.Enabled.Cast<object?>().ToList(),
                    ["toEnable"] = toEnable,
                    ["toDisable"] = toDisable
                };
            }
        }

        [Fact]
        public void Cases_MatchTheExpectedAnswers()
        {
            var data = LoadCases();
            var cases = Json.Arr(data["cases"]);
            Assert.True(cases.Count >= 40, "at least 40 cases: " + cases.Count);
            var failures = new List<string>();
            foreach (var item in cases)
            {
                var c = Json.Obj(item);
                var name = Json.Str(c, "name");
                Assert.True(c.ContainsKey("expect"), name + " has no expected answer (run with FOERE_FILL_CASES=1)");
                var expected = Json.Write(c["expect"]);
                var actual = Json.Write(Answer(c));
                if (expected != actual)
                {
                    failures.Add(name + "\n expected " + expected.Replace("\n", " ") + "\n actual   " + actual.Replace("\n", " "));
                }
            }

            foreach (var f in failures)
            {
                _log.WriteLine(f);
            }

            Assert.True(failures.Count == 0, failures.Count + " cases differ:\n" + string.Join("\n", failures));
            _log.WriteLine("{0} cases ok", cases.Count);
        }

        [Fact]
        public void Cases_CoverEveryState()
        {
            var states = new HashSet<string>();
            foreach (var item in Json.Arr(LoadCases()["cases"]))
            {
                var expect = Json.Obj(Json.Obj(item)["expect"]);
                foreach (var p in Json.Arr(expect["packages"]))
                {
                    states.Add(Json.Str(Json.Obj(p), "state")!);
                }
            }

            foreach (var s in Enum.GetNames(typeof(ModState)))
            {
                Assert.Contains(s, states);
            }
        }

        [Fact]
        public void Versions_MatchTheExpectedAnswers()
        {
            var data = LoadCases();
            var n = 0;
            foreach (var item in Json.Arr(data["versions"]))
            {
                var o = Json.Obj(item);
                Assert.True((bool)o["satisfies"]! == ModVersion.Satisfies(Json.Str(o, "version"), Json.Str(o, "range")),
                    Json.Str(o, "version") + " in '" + Json.Str(o, "range") + "'");
                n++;
            }

            foreach (var item in Json.Arr(data["ranges"]))
            {
                var o = Json.Obj(item);
                Assert.True((bool)o["valid"]! == ModVersion.IsRange(Json.Str(o, "range")), "range '" + Json.Str(o, "range") + "'");
                n++;
            }

            foreach (var item in Json.Arr(data["isVersion"]))
            {
                var o = Json.Obj(item);
                Assert.True((bool)o["valid"]! == ModVersion.IsVersion(Json.Str(o, "version")), "version '" + Json.Str(o, "version") + "'");
                n++;
            }

            foreach (var item in Json.Arr(data["compare"]))
            {
                var o = Json.Obj(item);
                Assert.Equal(Json.Long(o, "result"), Math.Sign(ModVersion.Compare(Json.Str(o, "a"), Json.Str(o, "b"))));
                n++;
            }

            _log.WriteLine("{0} version checks ok", n);
        }

        /// <summary>The game repository keeps a byte-identical copy for its EditMode test.</summary>
        [Fact]
        public void GameRepoCopy_IsIdentical()
        {
            var gameRepo = Environment.GetEnvironmentVariable("FOERE_GAME_REPO") ?? "";
            var copy = Path.Combine(gameRepo, "unity", "FOEReborn", "Assets", "Tests", "EditMode", "Data", "modrules-cases.json");
            if (!Directory.Exists(gameRepo))
            {
                _log.WriteLine("game repository not here, copy not compared: " + gameRepo);
                return;
            }

            Assert.True(File.Exists(copy), "missing " + copy);
            Assert.Equal(File.ReadAllBytes(CasesFile), File.ReadAllBytes(copy));
        }

        /// <summary>FOERE_FILL_CASES=1: writes the launcher's answers into cases without one. The game's EditMode test
        /// then has to agree with them.</summary>
        [Fact]
        public void FillExpected()
        {
            if (Environment.GetEnvironmentVariable("FOERE_FILL_CASES") != "1")
            {
                return;
            }

            var data = LoadCases();
            var filled = 0;
            foreach (var item in Json.Arr(data["cases"]))
            {
                var c = Json.Obj(item);
                if (!c.ContainsKey("expect") || Environment.GetEnvironmentVariable("FOERE_FILL_ALL") == "1")
                {
                    c["expect"] = Answer(c);
                    filled++;
                }
            }

            File.WriteAllText(CasesFile, Json.Write(data) + "\n", new UTF8Encoding(false));
            _log.WriteLine("filled {0} cases", filled);
        }
    }
}
