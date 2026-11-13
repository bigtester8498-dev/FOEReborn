using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Xunit;

namespace FOEReLauncher.Tests
{
    /// <summary>The window's controls without a network or a person: run on an STA thread, texts read from the controls.</summary>
    [Collection("window")]
    public class WindowTests
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

        private static T Field<T>(object owner, string name) =>
            (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

        private static void Pump(int ms = 300)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        /// <summary>Bug: after choosing a channel with the keyboard the refresh disabled the picker, Windows moved the
        /// focus to the language button and the next Space or Enter switched the language.</summary>
        [Fact]
        public void LanguageButton_NeverTakesTheFocus()
        {
            using (var dir = new TempDir())
            {
                Sta(() =>
                {
                    var service = new LauncherService(dir.Path, "http://127.0.0.1:9/", null, dir.Sub("game-settings.json"));
                    service.Language = "en";
                    using (var form = new MainForm(service, new string[0]))
                    {
                        form.Show();
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        while (Field<bool>(form, "_busy") && clock.Elapsed < TimeSpan.FromSeconds(30))
                        {
                            Pump(50); // the first check fails against a closed port and frees the picker
                        }

                        Assert.False(Field<bool>(form, "_busy"));
                        var language = Field<Button>(form, "_languageButton");
                        var combo = Field<ComboBox>(form, "_channelCombo");
                        Assert.False(language.CanSelect);
                        Assert.False(language.TabStop);

                        // Walk the whole focus order forwards and backwards from the channel picker, as Tab, the
                        // arrow keys and a disabled picker do: the language button is never reached.
                        var visited = new List<Control>();
                        foreach (var forward in new[] { true, false })
                        {
                            form.ActiveControl = combo;
                            for (var i = 0; i < 40; i++)
                            {
                                form.SelectNextControl(form.ActiveControl ?? combo, forward, tabStopOnly: false, nested: true, wrap: true);
                                visited.Add(form.ActiveControl!);
                            }
                        }

                        Assert.Contains(combo, visited);
                        Assert.DoesNotContain(language, visited);
                        combo.Enabled = false; // what the refresh does
                        Assert.NotSame(language, form.ActiveControl);
                        combo.Enabled = true;
                        form.Close();
                    }
                });
            }
        }

        /// <summary>Found in a live run: the Mods and Catalogue pages are laid out while their tab is hidden; their buttons
        /// stayed at the default 75x23 in the top left corner because a hidden button reads Visible = false.</summary>
        [Fact]
        public void HiddenTabPages_LayOutTheirButtons()
        {
            using (var dir = new TempDir())
            {
                MakeInstalledGame(dir);
                Sta(() =>
                {
                    var service = new LauncherService(dir.Path, "http://127.0.0.1:9/", null, dir.Sub("game-settings.json"));
                    service.Language = "en";
                    using (var form = new MainForm(service, new string[0]))
                    {
                        form.Show();
                        Pump(300);
                        var pages = new Control[] { Field<ModsPage>(form, "_modsPage"), Field<CataloguePage>(form, "_cataloguePage") };
                        foreach (var page in pages)
                        {
                            Assert.False(page.Visible); // the Game tab is selected
                            foreach (var name in page is ModsPage ? new[] { "_upButton", "_refreshButton" } : new[] { "_installButton", "_refreshButton" })
                            {
                                var button = Field<Button>(page, name);
                                Assert.True(button.Top > page.ClientSize.Height / 2, name + " at " + button.Bounds);
                                Assert.True(button.Width > 100, name + " at " + button.Bounds);
                            }
                        }

                        form.Close();
                    }
                });
            }
        }

        /// <summary>Found in a live run: a code mod updated to a new version keeps the consent of the old one; the game loads it
        /// but runs no code (ModHost.cs:63), so the list must not say plain "on".</summary>
        [Fact]
        public void ModsPage_CodeOfANewVersion_IsNotAllowed()
        {
            using (var dir = new TempDir())
            {
                MakeInstalledGame(dir);
                dir.Write("game/Mods/b/B.dll", "not a real assembly; the launcher only checks that it is there");
                var settings = dir.Sub("game-settings.json");
                File.WriteAllText(settings, "{\"mods\":{\"enabled\":[\"b\"],\"order\":[\"b\"],\"consent\":{\"b\":\"1.0.0\"}}}");
                Sta(() =>
                {
                    Texts.Language = "en";
                    var service = new LauncherService(dir.Path, "http://127.0.0.1:9/", null, settings);
                    using (var form = new Form())
                    {
                        var page = new ModsPage(service) { Dock = DockStyle.Fill };
                        page.ApplyTexts();
                        page.Reload();
                        form.Controls.Add(page);
                        form.Show();
                        var list = Field<ListView>(page, "_list");
                        var row = list.Items.Cast<ListViewItem>().First(i => (string)i.Tag == "b");
                        Assert.Equal(Texts.T("mods.st.noconsent"), row.SubItems[row.SubItems.Count - 1].Text);
                        File.WriteAllText(settings, "{\"mods\":{\"enabled\":[\"b\"],\"order\":[\"b\"],\"consent\":{\"b\":\"2.0.0\"}}}");
                        page.Reload();
                        row = list.Items.Cast<ListViewItem>().First(i => (string)i.Tag == "b");
                        Assert.Equal(Texts.T("mods.st.on"), row.SubItems[row.SubItems.Count - 1].Text);
                        form.Close();
                    }
                });
            }
        }

        private static void MakeInstalledGame(TempDir dir)
        {
            dir.Write("game/FOEReborn.exe", "fake");
            dir.Write("installed-files.json", "{\"version\":\"v0.1.1\",\"channel\":\"prod\",\"files\":[]}");
            dir.Write("game/Content/classic/package.json", "{\"id\":\"classic\",\"version\":\"1.0.2\"}");
            dir.Write("game/Mods/a/package.json", "{\"id\":\"a\",\"name\":\"Mod A\",\"version\":\"1.0.0\",\"requires\":[{\"id\":\"b\"}]}");
            dir.Write("game/Mods/b/package.json", "{\"id\":\"b\",\"name\":\"Mod B\",\"version\":\"2.0.0\",\"assemblies\":[\"B.dll\"]}");
            dir.Write("game/Mods/c/package.json", "{\"id\":\"c\",\"name\":\"Mod C\",\"version\":\"1.0.0\"}");
        }

        [Fact]
        public void ModsPage_CascadeConsentAndOrder_SavedAtOnce()
        {
            using (var dir = new TempDir())
            {
                MakeInstalledGame(dir);
                var settings = dir.Sub("game-settings.json");
                // c is on: the list must not save again when its window handle re-applies the tick.
                var original = "{\r\n  \"fpsCap\": 60,\r\n  \"mods\": { \"enabled\": [\"c\"], \"disabled\": [], \"order\": [], \"consent\": {} }\r\n}";
                File.WriteAllText(settings, original);
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
                            page.Reload(); // filled before the list has a window handle, as in the launcher
                            form.Controls.Add(page);
                            form.Show();
                            var list = Field<ListView>(page, "_list");
                            Application.DoEvents();
                            // Showing and filling the list writes nothing.
                            Assert.Equal(original, File.ReadAllText(settings));
                            Assert.Equal(new[] { "a", "b", "c" }, list.Items.Cast<ListViewItem>().Select(i => (string)i.Tag).ToArray());

                            // A refused confirm changes nothing.
                            ConfirmForm.Answer = (title, text) =>
                            {
                                asked.Add(text);
                                return false;
                            };
                            page.Toggle("a", true);
                            Assert.Equal(new[] { "c" }, service.Mods.Settings.Load().Enabled);

                            // Accepted: one cascade confirm, then the consent for the code of b; both enabled and saved.
                            asked.Clear();
                            ConfirmForm.Answer = (title, text) =>
                            {
                                asked.Add(text);
                                return true;
                            };
                            page.Toggle("a", true);
                            Assert.Equal(2, asked.Count);
                            Assert.Contains("Mod B", asked[0]);
                            Assert.Contains("B.dll", asked[1]);
                            var saved = service.Mods.Settings.Load();
                            Assert.Equal(new[] { "a", "b", "c" }, saved.Enabled.OrderBy(x => x).ToArray());
                            Assert.Equal("2.0.0", saved.Consent["b"]);
                            Assert.StartsWith("{\r\n  \"fpsCap\": 60,", File.ReadAllText(settings));
                            Assert.True(list.Items.Cast<ListViewItem>().First(i => (string)i.Tag == "a").Checked);

                            // Off with the dependent: b off takes a with it, one confirm.
                            asked.Clear();
                            page.Toggle("b", false);
                            Assert.Single(asked);
                            Assert.Equal(new[] { "c" }, service.Mods.Settings.Load().Enabled);

                            // Order: c moved up twice is first.
                            list.Items.Cast<ListViewItem>().First(i => (string)i.Tag == "c").Selected = true;
                            Field<Button>(page, "_upButton").PerformClick();
                            Field<Button>(page, "_upButton").PerformClick();
                            Assert.Equal("c", service.Mods.Settings.Load().Order[0]);

                            // The game running: read-only.
                            page.SetGameRunning(true);
                            Assert.Equal(Texts.T("mods.readonly"), Field<Label>(page, "_status").Text);
                            Assert.False(Field<Button>(page, "_upButton").Enabled);
                            page.Toggle("c", false);
                            Assert.Equal(new[] { "c" }, service.Mods.Settings.Load().Enabled);
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
