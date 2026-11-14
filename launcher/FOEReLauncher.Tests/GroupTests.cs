using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace FOEReLauncher.Tests
{
    /// <summary>Mod groups: catalogue and record parsing, list blocks, whole-group switches, one-by-one runs.</summary>
    public class GroupTests
    {
        private static readonly string Sha = new string('a', 64);

        private static string Mod(string id, string group = "") =>
            "{\"id\":\"" + id + "\",\"version\":\"1.0.0\",\"size\":10,\"sha256\":\"" + Sha + "\",\"url\":\"https://x/" + id + ".zip\"" +
            (group.Length > 0 ? ",\"group\":\"" + group + "\"" : string.Empty) + "}";

        [Fact]
        public void Catalogue_ReadsGroups()
        {
            var text = "{\"format\":1,\"groups\":[" +
                       "{\"id\":\"examples\",\"name\":{\"ru\":\"Примеры модов\",\"en\":\"Example mods\"},\"description\":{\"ru\":\"Описание\",\"en\":\"About\"},\"order\":10}," +
                       "{\"id\":\"Bad Id\",\"name\":\"x\"}," +
                       "{\"id\":\"examples\",\"name\":\"second copy\"}]," +
                       "\"mods\":[" + Mod("a", "examples") + "," + Mod("b") + "," + Mod("c", "other") + "," + Mod("d", "../up") + "]}";
            var c = ModCatalog.Parse(text);
            Assert.Equal(new[] { "a", "b", "c", "d" }, c.Entries.Select(e => e.Id).ToArray());
            Assert.Equal(new[] { "examples", string.Empty, "other", string.Empty }, c.Entries.Select(e => e.Group).ToArray());
            var g = c.FindGroup("examples")!;
            Assert.Equal("Примеры модов", g.Name("ru"));
            Assert.Equal("Example mods", g.Name("en"));
            Assert.Equal("About", g.Description("en"));
            Assert.Equal(10, g.Order);

            // A group the list does not describe still groups its mods, under its id; broken ids are dropped.
            Assert.Equal(new[] { "examples", "other" }, c.Groups.Select(x => x.Id).ToArray());
            Assert.Equal("other", c.FindGroup("other")!.Name("ru"));
            Assert.Null(c.FindGroup(string.Empty));
        }

        [Fact]
        public void Catalogue_WithoutGroups_AsBefore()
        {
            var c = ModCatalog.Parse("{\"format\":1,\"mods\":[" + Mod("a") + "]}");
            Assert.Empty(c.Groups);
            Assert.Equal(string.Empty, c.Entries[0].Group);
        }

        [Fact]
        public void InstalledRecord_KeepsGroups()
        {
            using (var dir = new TempDir())
            {
                var path = dir.Sub("installed-mods.json");
                var rec = new InstalledMods();
                rec.Mods.Add(new InstalledMod { Id = "a", Version = "1.0.0", Sha256 = Sha, Dir = "Mods/examples/a", Group = "examples" });
                rec.Mods.Add(new InstalledMod { Id = "b", Version = "1.0.0", Sha256 = Sha, Dir = "Mods/b" });
                rec.KeepGroup(new ModGroupInfo { Id = "examples", NameRu = "Примеры модов", NameEn = "Example mods", Order = 10 });
                rec.KeepGroup(new ModGroupInfo { Id = "unused", NameEn = "Not used" });
                rec.Save(path);
                var text = File.ReadAllText(path);
                Assert.DoesNotContain("unused", text);
                var back = InstalledMods.Load(path);
                Assert.Equal("examples", back.Find("a")!.Group);
                Assert.Equal(string.Empty, back.Find("b")!.Group);
                Assert.Equal("Примеры модов", back.FindGroup("examples")!.Name("ru"));
                Assert.Single(back.Groups);
            }
        }

        [Fact]
        public void GroupOf_RecordOrFolder()
        {
            using (var dir = new TempDir())
            {
                dir.Write("game/FOEReborn.exe", "fake");
                dir.Write("installed-files.json", "{\"version\":\"v0.1.1\",\"channel\":\"prod\",\"files\":[]}");
                dir.Write("game/Content/classic/package.json", "{\"id\":\"classic\",\"version\":\"1.0.2\"}");
                dir.Write("game/Mods/examples/a/package.json", "{\"id\":\"a\",\"version\":\"1.0.0\"}");
                dir.Write("game/Mods/examples/group.json", "{\"name\":{\"ru\":\"Примеры\",\"en\":\"Examples\"}}");
                dir.Write("game/Mods/b/package.json", "{\"id\":\"b\",\"version\":\"1.0.0\"}");
                dir.Write("game/Mods/c/package.json", "{\"id\":\"c\",\"version\":\"1.0.0\"}");
                var service = new LauncherService(dir.Path, "http://127.0.0.1:9/", null, dir.Sub("game-settings.json"));
                var rec = new InstalledMods();
                rec.Mods.Add(new InstalledMod { Id = "c", Version = "1.0.0", Dir = "Mods/c", Group = "tools" });
                var packages = service.Mods.Scan(out _);
                Assert.Equal("examples", service.Mods.GroupOf(packages.First(p => p.Id == "a"), rec));
                Assert.Equal(string.Empty, service.Mods.GroupOf(packages.First(p => p.Id == "b"), rec));
                Assert.Equal("tools", service.Mods.GroupOf(packages.First(p => p.Id == "c"), rec));
                Assert.Equal("Examples", service.Mods.GroupInfo("examples", rec).Name("en"));
                Assert.Equal("tools", service.Mods.GroupInfo("tools", rec).Name("en"));
            }
        }

        [Fact]
        public void Arrange_PullsMembersTogether()
        {
            var groups = new Dictionary<string, string> { ["a"] = "g", ["c"] = "g", ["d"] = "h" };
            var blocks = ModGroups.Arrange(new[] { "x", "a", "b", "c", "d" }, id => groups.TryGetValue(id, out var g) ? g : string.Empty);
            Assert.Equal(new[] { string.Empty, "g", string.Empty, "h" }, blocks.Select(b => b.GroupId).ToArray());
            Assert.Equal(new[] { "a", "c" }, blocks[1].Items);
            Assert.Equal(new[] { "x", "a", "c", "b", "d" }, blocks.SelectMany(b => b.Items).ToArray());
        }

        [Fact]
        public void Move_GroupAsAWholeAndMembersInside()
        {
            var groups = new Dictionary<string, string> { ["a"] = "g", ["c"] = "g" };
            var blocks = ModGroups.Arrange(new[] { "x", "a", "c", "b" }, id => groups.TryGetValue(id, out var g) ? g : string.Empty);
            Assert.Equal(new[] { "a", "c", "x", "b" }, ModGroups.Move(blocks, "g", null, -1));
            Assert.Equal(new[] { "x", "b", "a", "c" }, ModGroups.Move(blocks, "g", null, 1));
            Assert.Equal(new[] { "x", "c", "a", "b" }, ModGroups.Move(blocks, null, "a", 1));
            Assert.Equal(new[] { "x", "a", "c", "b" }, ModGroups.Move(blocks, null, "a", -1)); // first in its group: stays
            Assert.Equal(new[] { "x", "b", "a", "c" }, ModGroups.Move(blocks, null, "b", -1)); // a mod on its own passes the whole group
            Assert.Equal(new[] { "x", "a", "c", "b" }, ModGroups.Move(blocks, null, "b", 1));
        }

        private static List<ModPackage> Packages(TempDir dir, string settings, out ModSelection sel)
        {
            File.WriteAllText(dir.Sub("game-settings.json"), settings);
            var service = new LauncherService(dir.Path, "http://127.0.0.1:9/", null, dir.Sub("game-settings.json"));
            return service.Mods.Scan(out sel);
        }

        private static TempDir GroupGame()
        {
            var dir = new TempDir();
            dir.Write("game/FOEReborn.exe", "fake");
            dir.Write("installed-files.json", "{\"version\":\"v0.1.1\",\"channel\":\"prod\",\"files\":[]}");
            dir.Write("game/Content/classic/package.json", "{\"id\":\"classic\",\"version\":\"1.0.2\"}");
            dir.Write("game/Mods/g/g1/package.json", "{\"id\":\"g1\",\"version\":\"1.0.0\",\"requires\":[{\"id\":\"r\"}]}");
            dir.Write("game/Mods/g/g2/package.json", "{\"id\":\"g2\",\"version\":\"2.0.0\",\"assemblies\":[\"G2.dll\"]}");
            dir.Write("game/Mods/g/g2/G2.dll", "x");
            dir.Write("game/Mods/g/g3/package.json", "{\"id\":\"g3\",\"version\":\"1.0.0\",\"incompatible\":[{\"id\":\"x\"}]}");
            dir.Write("game/Mods/g/g4/package.json", "{ broken");
            dir.Write("game/Mods/r/package.json", "{\"id\":\"r\",\"version\":\"1.0.0\"}");
            dir.Write("game/Mods/x/package.json", "{\"id\":\"x\",\"version\":\"1.0.0\"}");
            dir.Write("game/Mods/d/package.json", "{\"id\":\"d\",\"version\":\"1.0.0\",\"requires\":[{\"id\":\"g1\"}]}");
            return dir;
        }

        [Fact]
        public void PlanToggle_OnTakesRequirementsSkipsConflictsAsksConsent()
        {
            using (var dir = GroupGame())
            {
                var all = Packages(dir, "{\"mods\":{\"enabled\":[\"x\"],\"order\":[\"x\"]}}", out var sel);
                var members = new List<string> { "g1", "g2", "g3", "g4" };
                // g4 is broken: its folder has no readable id, so the list shows it under its folder name.
                var broken = all.First(p => p.RelDir == "Mods/g/g4");
                members[3] = broken.Id;
                var plan = ModGroups.PlanToggle(members, true, all, sel);
                Assert.Equal(new[] { "g1", "g2" }, plan.Members);
                Assert.Equal(new[] { "r" }, plan.Outside);
                Assert.Equal(new[] { "g2" }, plan.NeedConsent);
                Assert.Equal(new[] { "g3", broken.Id }, plan.Skipped.Select(kv => kv.Key).ToArray());
                Assert.Equal("x", plan.Skipped[0].Value);

                var consented = all.Where(p => plan.NeedConsent.Contains(p.Id)).ToList();
                var after = ModGroups.Apply(sel, plan, consented);
                Assert.Equal(new[] { "g1", "g2", "r", "x" }, after.Enabled.OrderBy(x => x).ToArray());
                Assert.Equal("2.0.0", after.Consent["g2"]);

                // The rules agree: nothing the game would refuse.
                var check = ModRules.ListOrder(all, after);
                Assert.NotNull(check);
            }
        }

        [Fact]
        public void PlanToggle_OffTakesDependentsOutside()
        {
            using (var dir = GroupGame())
            {
                var all = Packages(dir, "{\"mods\":{\"enabled\":[\"g1\",\"r\",\"d\",\"g2\"],\"order\":[\"r\",\"g1\",\"d\",\"g2\"],\"consent\":{\"g2\":\"2.0.0\"}}}", out var sel);
                var plan = ModGroups.PlanToggle(new[] { "g1", "g2", "g3" }, false, all, sel);
                Assert.Equal(new[] { "g1", "g2" }, plan.Members);
                Assert.Equal(new[] { "d" }, plan.Outside);
                Assert.Empty(plan.NeedConsent);
                var after = ModGroups.Apply(sel, plan, Enumerable.Empty<ModPackage>());
                Assert.Equal(new[] { "r" }, after.Enabled);
                Assert.Equal(new[] { "r", "g1", "d", "g2" }, after.Order); // the load order is kept
            }
        }

        [Fact]
        public async Task RunOneByOne_SequentialAndFailureGoesOn()
        {
            var running = 0;
            var max = 0;
            var seen = new List<int>();
            var res = await ModGroups.RunOneByOneAsync(new[] { 1, 2, 3, 4 }, async (x, i, ct) =>
            {
                max = Math.Max(max, Interlocked.Increment(ref running));
                await Task.Delay(5, ct);
                seen.Add(i);
                Interlocked.Decrement(ref running);
                if (x == 2)
                {
                    throw new IOException("disk");
                }
            }, CancellationToken.None);
            Assert.Equal(1, max);
            Assert.Equal(new[] { 0, 1, 2, 3 }, seen);
            Assert.Equal(new[] { 1, 3, 4 }, res.Done);
            Assert.Single(res.Failed);
            Assert.Equal(2, res.Failed[0].Key);
            Assert.Equal("disk", res.Failed[0].Value.Message);
            Assert.False(res.Cancelled);
        }

        [Fact]
        public async Task RunOneByOne_CancelStopsTheRest()
        {
            using (var cts = new CancellationTokenSource())
            {
                var res = await ModGroups.RunOneByOneAsync(new[] { "a", "b", "c", "d" }, async (x, i, ct) =>
                {
                    if (x == "b")
                    {
                        cts.Cancel();
                        await Task.Delay(1000, ct); // the running download sees the cancel
                    }
                }, cts.Token);
                Assert.True(res.Cancelled);
                Assert.Equal(new[] { "a" }, res.Done);
                Assert.Equal(new[] { "b", "c", "d" }, res.NotRun);
                Assert.Empty(res.Failed);
            }
        }
    }

    /// <summary>The group row of the Mods tab switches its mods through the same rules as single boxes.</summary>
    [Collection("window")]
    public class GroupWindowTests
    {
        private static void Sta(Action action)
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null)
            {
                throw new AggregateException(error);
            }
        }

        [Fact]
        public void ModsPage_GroupRowSwitchesTheGroup()
        {
            using (var dir = new TempDir())
            {
                dir.Write("game/FOEReborn.exe", "fake");
                dir.Write("installed-files.json", "{\"version\":\"v0.1.1\",\"channel\":\"prod\",\"files\":[]}");
                dir.Write("game/Content/classic/package.json", "{\"id\":\"classic\",\"version\":\"1.0.2\"}");
                dir.Write("game/Mods/examples/e1/package.json", "{\"id\":\"e1\",\"name\":\"Example one\",\"version\":\"1.0.0\",\"requires\":[{\"id\":\"base\"}]}");
                dir.Write("game/Mods/examples/e2/package.json", "{\"id\":\"e2\",\"name\":\"Example two\",\"version\":\"1.0.0\",\"assemblies\":[\"E2.dll\"]}");
                dir.Write("game/Mods/examples/e2/E2.dll", "x");
                dir.Write("game/Mods/base/package.json", "{\"id\":\"base\",\"name\":\"Base\",\"version\":\"1.0.0\"}");
                var settings = dir.Sub("game-settings.json");
                File.WriteAllText(settings, "{\r\n  \"fpsCap\": 60\r\n}");
                var asked = new List<string>();
                try
                {
                    Sta(() =>
                    {
                        var service = new LauncherService(dir.Path, "http://127.0.0.1:9/", null, settings);
                        using (var form = new Form())
                        {
                            var page = new ModsPage(service) { Dock = DockStyle.Fill };
                            page.ApplyTexts();
                            page.Reload();
                            form.Controls.Add(page);
                            form.Show();
                            Application.DoEvents();
                            var list = (ListView)typeof(ModsPage).GetField("_list", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
                            var tags = list.Items.Cast<ListViewItem>().Select(i => (string)i.Tag).ToArray();
                            Assert.Equal(new[] { "base", "#examples", "e1", "e2" }, tags);
                            Assert.Equal("{\r\n  \"fpsCap\": 60\r\n}", File.ReadAllText(settings));

                            ConfirmForm.Answer = (title, text) =>
                            {
                                asked.Add(text);
                                return true;
                            };
                            page.ToggleGroup("examples", true);
                            Assert.Equal(2, asked.Count);
                            Assert.Contains("Base", asked[0]); // the requirement outside the group
                            Assert.Contains("Example two", asked[1]); // one consent naming the code mods
                            var saved = service.Mods.Settings.Load();
                            Assert.Equal(new[] { "base", "e1", "e2" }, saved.Enabled.OrderBy(x => x).ToArray());
                            Assert.Equal("1.0.0", saved.Consent["e2"]);

                            asked.Clear();
                            page.ToggleGroup("examples", false);
                            Assert.Empty(asked);
                            Assert.Equal(new[] { "base" }, service.Mods.Settings.Load().Enabled);
                            Assert.StartsWith("{\r\n  \"fpsCap\": 60,", File.ReadAllText(settings));
                            form.Close();
                        }
                    });
                }
                finally
                {
                    ConfirmForm.Answer = null;
                }
            }
        }
    }
}
