using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FOEReLauncher
{
    /// <summary>Shared look of the mod pages: dark list, dark details box, the window's buttons.</summary>
    internal static class ModUi
    {
        internal static readonly Color Panel = ColorTranslator.FromHtml("#181B23");
        internal static readonly Color Header = ColorTranslator.FromHtml("#20242E");
        internal static readonly Color Bad = ColorTranslator.FromHtml("#F0646E");
        internal static readonly Color Good = ColorTranslator.FromHtml("#7CC88A");
        internal const int ButtonHeight = 30;
        internal const int Gap = 8;

        internal static ListView MakeList(bool checkBoxes)
        {
            var list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                CheckBoxes = checkBoxes,
                BorderStyle = BorderStyle.None,
                BackColor = Panel,
                ForeColor = MainForm.BodyColor,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                OwnerDraw = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point)
            };

            // The stock header is light grey under visual styles: draw it in the window's palette, rows as usual.
            list.DrawColumnHeader += (s, e) =>
            {
                using (var back = new SolidBrush(Header))
                {
                    e.Graphics.FillRectangle(back, e.Bounds);
                }

                TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, list.Font, Rectangle.Inflate(e.Bounds, -4, 0), MainForm.StatusColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            };
            list.DrawItem += (s, e) => e.DrawDefault = true;
            list.DrawSubItem += (s, e) => e.DrawDefault = true;
            return list;
        }

        internal static RichTextBox MakeDetails() => new RichTextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Panel,
            ForeColor = MainForm.BodyColor,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            TabStop = false
        };

        internal static Label MakeLine(float size = 9f) => new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = MainForm.StatusColor,
            Font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point),
            UseMnemonic = false
        };

        internal static Button MakeButton() => MainForm.MakeButton(string.Empty, MainForm.Secondary, MainForm.SecondaryText, 9f, FontStyle.Regular);

        internal static void Append(RichTextBox box, string text, Color? color = null, bool bold = false)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor = color ?? MainForm.BodyColor;
            box.SelectionFont = new Font(box.Font, bold ? FontStyle.Bold : FontStyle.Regular);
            box.AppendText(text + Environment.NewLine);
        }

        /// <summary>Lays out a row of buttons over the full width.</summary>
        internal static void Row(int y, int width, params Button[] buttons)
        {
            // Every button of a row is laid out: Control.Visible reads false while the page's tab is not selected.
            var shown = buttons;
            if (shown.Length == 0)
            {
                return;
            }

            var w = (width - (Gap * (shown.Length - 1))) / shown.Length;
            for (var i = 0; i < shown.Length; i++)
            {
                var x = i * (w + Gap);
                shown[i].Bounds = new Rectangle(x, y, i == shown.Length - 1 ? width - x : w, ButtonHeight);
            }
        }

        /// <summary>The state word of a package as the game's manager shows it.</summary>
        internal static string StateWord(ModPackage p, ModSelection selection)
        {
            switch (p.State)
            {
                case ModState.Active:
                    // the game loads the package but runs no code of a version the player has not allowed (ModHost.cs:63)
                    return p.HasCode && !selection.HasConsent(p.Id, p.Manifest.Version) ? Texts.T("mods.st.noconsent") : Texts.T("mods.st.on");
                case ModState.Disabled:
                    return Texts.T("mods.st.off");
                case ModState.Invalid:
                    return Texts.T("mods.st.broken");
                case ModState.Duplicate:
                    return Texts.T("mods.st.duplicate");
                case ModState.MissingDependency:
                    return Texts.T("mods.st.missing");
                case ModState.DependencyInactive:
                    return Texts.T("mods.st.needs");
                case ModState.WrongVersion:
                    return Texts.T("mods.st.version");
                case ModState.Incompatible:
                    return Texts.T("mods.st.incompatible");
                default:
                    return Texts.T("mods.st.cycle");
            }
        }

        internal static string ErrorText(Exception ex) =>
            ex is ModOperationException m ? m.Text : ex is InvalidDataException ? Texts.T("mods.err.hash") : ex.Message;
    }

    /// <summary>
    /// The Mods tab: the game's mod manager on the same settings.json. Changes are saved at once, as the game
    /// does; while the game runs the list is read-only.
    /// </summary>
    public sealed class ModsPage : Panel
    {
        private readonly LauncherService _service;
        private readonly Label _hint;
        private readonly ListView _list;
        private readonly RichTextBox _details;
        private readonly Button _upButton;
        private readonly Button _downButton;
        private readonly Button _refreshButton;
        private readonly Button _folderButton;
        private readonly Label _status;

        private List<ModPackage> _packages = new List<ModPackage>();
        private ModSelection _selection = new ModSelection();
        private bool _filling;
        private bool _running;
        private Func<string> _statusText = () => string.Empty;

        public ModsPage(LauncherService service)
        {
            _service = service;
            BackColor = MainForm.Background;
            _hint = ModUi.MakeLine(8.25f);
            _list = ModUi.MakeList(checkBoxes: true);
            _list.Columns.Add(string.Empty, 150);
            _list.Columns.Add(string.Empty, 58);
            _list.Columns.Add(string.Empty, 46);
            _list.Columns.Add(string.Empty, 104);
            _list.Columns.Add(string.Empty, 96);
            _list.ItemCheck += OnItemCheck;
            _list.SelectedIndexChanged += (s, e) => ShowDetails();
            _details = ModUi.MakeDetails();
            _upButton = ModUi.MakeButton();
            _upButton.Click += (s, e) => MoveSelected(-1);
            _downButton = ModUi.MakeButton();
            _downButton.Click += (s, e) => MoveSelected(1);
            _refreshButton = ModUi.MakeButton();
            _refreshButton.Click += (s, e) => Reload();
            _folderButton = ModUi.MakeButton();
            _folderButton.Click += (s, e) => OpenFolder();
            _status = ModUi.MakeLine();
            Controls.AddRange(new Control[] { _hint, _list, _details, _upButton, _downButton, _refreshButton, _folderButton, _status });
            Resize += (s, e) => LayoutPage();
        }

        /// <summary>Raised after the mod list changed on disk (the catalogue page refreshes its states).</summary>
        public event EventHandler? Changed;

        public void ApplyTexts()
        {
            _hint.Text = Texts.T("mods.hint");
            _list.Columns[0].Text = Texts.T("mods.col.name");
            _list.Columns[1].Text = Texts.T("mods.col.version");
            _list.Columns[2].Text = Texts.T("cat.col.kind");
            _list.Columns[3].Text = Texts.T("mods.col.source");
            _list.Columns[4].Text = Texts.T("mods.col.state");
            _upButton.Text = Texts.T("mods.btn.up");
            _downButton.Text = Texts.T("mods.btn.down");
            _refreshButton.Text = Texts.T("mods.btn.refresh");
            _folderButton.Text = Texts.T("mods.btn.folder");
            Fill(SelectedId());
            ShowStatus();
        }

        public void LayoutPage()
        {
            var width = ClientSize.Width;
            var height = ClientSize.Height;
            if (width < 100)
            {
                return;
            }

            _hint.Bounds = new Rectangle(0, 4, width, (_hint.Font.Height * 3) + 2);
            var statusHeight = (_status.Font.Height * 2) + 2;
            _status.Bounds = new Rectangle(0, height - statusHeight, width, statusHeight);
            var rowY = _status.Top - 6 - ModUi.ButtonHeight;
            ModUi.Row(rowY, width, _upButton, _downButton, _refreshButton, _folderButton);
            var detailsHeight = 130;
            _details.Bounds = new Rectangle(0, rowY - 8 - detailsHeight, width, detailsHeight);
            _list.Bounds = new Rectangle(0, _hint.Bottom + 4, width, _details.Top - 8 - _hint.Bottom - 4);
            var fixedWidth = 58 + 46 + 104 + 96 + SystemInformation.VerticalScrollBarWidth + 4;
            _list.Columns[0].Width = Math.Max(120, width - fixedWidth);
        }

        public void SetGameRunning(bool running)
        {
            if (_running == running)
            {
                return;
            }

            _running = running;
            if (!running)
            {
                // The game may have changed the list from its own menu.
                Reload();
                return;
            }

            ApplyEnabled();
            ShowStatus();
        }

        /// <summary>Reads the folder and the settings again, as the game does at its start.</summary>
        public void Reload()
        {
            var id = SelectedId();
            _packages = _service.IsGameInstalled() ? _service.Mods.Scan(out _selection) : new List<ModPackage>();
            if (!_service.IsGameInstalled())
            {
                _selection = new ModSelection();
            }

            Fill(id);
            SetStatus(null);
        }

        private string? SelectedId() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

        private IList<ModPackage> Listed() => ModRules.ListOrder(_packages, _selection);

        private void Fill(string? select)
        {
            _filling = true;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                var game = _service.Mods.GameState();
                var record = _service.Mods.LoadRecord();
                foreach (var p in Listed())
                {
                    var source = _service.Mods.SourceOf(p, game, record);
                    var item = new ListViewItem(p.Manifest.DisplayName)
                    {
                        Tag = p.Id,
                        Checked = _selection.IsEnabled(p.Id),
                        ForeColor = p.Active ? MainForm.TitleColor : _selection.IsEnabled(p.Id) || p.State == ModState.Invalid ? ModUi.Bad : MainForm.StatusColor
                    };
                    item.UseItemStyleForSubItems = true;
                    item.SubItems.Add(p.Manifest.Version);
                    item.SubItems.Add(p.HasCode ? Texts.T("mods.codetag") : string.Empty);
                    item.SubItems.Add(Texts.T(source == ModSource.Game ? "mods.src.game" : source == ModSource.Catalogue ? "mods.src.catalogue" : "mods.src.hand"));
                    item.SubItems.Add(ModUi.StateWord(p, _selection));
                    _list.Items.Add(item);
                    if (p.Id == select)
                    {
                        item.Selected = true;
                        item.Focused = true;
                    }
                }

                if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
                {
                    _list.Items[0].Selected = true;
                }

                _list.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.EnsureVisible();
            }
            finally
            {
                _list.EndUpdate();
                _filling = false;
            }

            ApplyEnabled();
            ShowDetails();
        }

        private void ApplyEnabled()
        {
            var any = _list.SelectedItems.Count > 0 && !_running;
            _upButton.Enabled = any && _list.SelectedIndices[0] > 0;
            _downButton.Enabled = any && _list.SelectedIndices[0] < _list.Items.Count - 1;
            _folderButton.Enabled = _service.IsGameInstalled();
        }

        private void ShowDetails()
        {
            ApplyEnabled();
            _details.Clear();
            var id = SelectedId();
            var p = _packages.FirstOrDefault(x => x.Id == id && !x.Core && x.State != ModState.Duplicate);
            if (p == null)
            {
                return;
            }

            var m = p.Manifest;
            ModUi.Append(_details, m.DisplayName + "  " + m.Version, MainForm.TitleColor, bold: true);
            if (m.Author.Length > 0)
            {
                ModUi.Append(_details, Texts.T("mods.d.by", m.Author), MainForm.StatusColor);
            }

            if (m.Description.Length > 0)
            {
                ModUi.Append(_details, m.Description);
            }

            ModUi.Append(_details, Texts.T("mods.d.state", ModUi.StateWord(p, _selection)), p.Active ? ModUi.Good : MainForm.BodyColor);
            if (!p.Active && p.State != ModState.Disabled)
            {
                ModUi.Append(_details, ModRules.ReasonText(p, Texts.Language), ModUi.Bad);
            }

            ModUi.Append(_details, _selection.IsEnabled(p.Id) && p.LoadIndex >= 0
                ? Texts.T("mods.d.enabled", p.LoadIndex + 1)
                : Texts.T("mods.d.disabled"));
            if (m.Requires.Count > 0)
            {
                ModUi.Append(_details, Texts.T("mods.d.requires", string.Join(", ", m.Requires.Select(r =>
                    r + (_packages.Any(x => x.Id == r.Id) ? string.Empty : " " + Texts.T("mods.d.notinstalled"))))));
            }

            if (m.LoadAfter.Count > 0)
            {
                ModUi.Append(_details, Texts.T("mods.d.loadafter", string.Join(", ", m.LoadAfter)));
            }

            if (m.LoadBefore.Count > 0)
            {
                ModUi.Append(_details, Texts.T("mods.d.loadbefore", string.Join(", ", m.LoadBefore)));
            }

            if (m.Incompatible.Count > 0)
            {
                ModUi.Append(_details, Texts.T("mods.d.incompatible", string.Join(", ", m.Incompatible)));
            }

            if (p.HasCode)
            {
                ModUi.Append(_details, Texts.T("mods.d.code", string.Join(", ", m.Assemblies)) +
                                       Texts.T(_selection.HasConsent(p.Id, m.Version) ? "mods.d.allowed" : "mods.d.notallowed"));
            }

            ModUi.Append(_details, Texts.T("mods.d.folder", p.RelDir), MainForm.StatusColor);
            if (p.Issues.Count > 0)
            {
                ModUi.Append(_details, Texts.T("mods.d.issues", string.Join("; ", p.Issues)), MainForm.StatusColor);
            }

            _details.SelectionStart = 0;
            _details.ScrollToCaret();
        }

        private void OnItemCheck(object? sender, ItemCheckEventArgs e)
        {
            if (_filling)
            {
                return;
            }

            // The list re-applies its ticks when its window handle is created: a tick that already matches the saved
            // list is not a change.
            var on = e.NewValue == CheckState.Checked;
            var tagged = _list.Items[e.Index].Tag as string;
            if (tagged == null || on == _selection.IsEnabled(tagged))
            {
                return;
            }

            // The tick follows the saved list, not the click: the change goes through the rules first.
            e.NewValue = e.CurrentValue;
            if (_running)
            {
                SetStatus(() => Texts.T("mods.readonly"));
                return;
            }

            var id = _list.Items[e.Index].Tag as string;
            if (id != null)
            {
                BeginInvoke(new Action(() => Toggle(id, on)));
            }
        }

        private ModPackage? Package(string id) => _packages.FirstOrDefault(x => x.Id == id && !x.Core && x.State != ModState.Duplicate);

        private string NameOf(string id) => Package(id)?.Manifest.DisplayName ?? id;

        /// <summary>The game's SetModEnabled + ApplyModEnabled: consent for code, the cascade, saved at once. The
        /// launcher asks before the change (one confirm listing the mods), the game reports it after.</summary>
        internal void Toggle(string id, bool on)
        {
            var p = Package(id);
            if (p == null || _running || on == _selection.IsEnabled(id))
            {
                return;
            }

            var with = on ? ModRules.ToEnable(id, _packages, _selection) : ModRules.ToDisable(id, _packages, _selection);
            if (with.Count > 0)
            {
                var names = string.Join(", ", with.Select(NameOf));
                if (!ConfirmForm.Ask(FindForm(), Texts.T("mods.confirm.title"), Texts.T(on ? "mods.confirm.on" : "mods.confirm.off", p.Manifest.DisplayName, names), Texts.T("btn.ok")))
                {
                    return;
                }
            }

            var consent = new List<ModPackage>();
            if (on)
            {
                foreach (var x in new[] { id }.Concat(with).Select(Package))
                {
                    if (x != null && x.HasCode && !_selection.HasConsent(x.Id, x.Manifest.Version))
                    {
                        if (!ConfirmForm.Ask(FindForm(), Texts.T("mods.c.title"), Texts.T("mods.c.text", x.Manifest.DisplayName, string.Join(", ", x.Manifest.Assemblies)), Texts.T("mods.c.allow"), warning: true))
                        {
                            return;
                        }

                        consent.Add(x);
                    }
                }
            }

            var sel = _selection.Clone();
            foreach (var x in consent)
            {
                sel.Consent[x.Id] = x.Manifest.Version;
            }

            sel.SetEnabled(id, on);
            if (!sel.Order.Contains(id))
            {
                sel.Order.Add(id);
            }

            foreach (var x in with)
            {
                sel.SetEnabled(x, on);
                if (!sel.Order.Contains(x))
                {
                    sel.Order.Add(x);
                }
            }

            if (Save(sel))
            {
                var also = with.Count == 0 ? string.Empty : " " + Texts.T(on ? "mods.alsoon" : "mods.alsooff", p.Manifest.DisplayName, string.Join(", ", with.Select(NameOf)));
                Reload();
                SetStatus(() => Texts.T("mods.saved") + also);
            }
        }

        private void MoveSelected(int delta)
        {
            var id = SelectedId();
            if (id == null || _running)
            {
                return;
            }

            var sel = _selection.Clone();
            sel.Move(id, delta, Listed().Select(p => p.Id));
            if (Save(sel))
            {
                Reload();
                SetStatus(() => Texts.T("mods.saved"));
            }
        }

        private bool Save(ModSelection sel)
        {
            try
            {
                _service.Mods.SaveSelection(sel);
                _selection = sel;
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex) when (ex is ModOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                var text = ModUi.ErrorText(ex);
                SetStatus(() => text);
                return false;
            }
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(_service.Mods.ModsDir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _service.Mods.ModsDir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus(() => ex.Message);
            }
        }

        private void SetStatus(Func<string>? text)
        {
            _statusText = text ?? (() => string.Empty);
            ShowStatus();
        }

        private void ShowStatus()
        {
            if (_running)
            {
                _status.ForeColor = ModUi.Bad;
                _status.Text = Texts.T("mods.readonly");
                return;
            }

            _status.ForeColor = MainForm.StatusColor;
            var text = _statusText();
            if (text.Length == 0)
            {
                text = !_service.IsGameInstalled() ? Texts.T("mods.nogame") : _list.Items.Count == 0 ? Texts.T("mods.none") : string.Empty;
            }

            _status.Text = text;
        }
    }

    /// <summary>The Catalogue tab: mods from downloads/mods/catalog.json; install, update and remove.</summary>
    public sealed class CataloguePage : Panel
    {
        private readonly LauncherService _service;
        private readonly Label _searchLabel;
        private readonly TextBox _search;
        private readonly Label _count;
        private readonly ListView _list;
        private readonly RichTextBox _details;
        private readonly Button _installButton;
        private readonly Button _removeButton;
        private readonly Button _updateAllButton;
        private readonly Button _refreshButton;
        private readonly ProgressStripe _progress;
        private bool _progressShown;
        private readonly Label _status;

        private ModCatalog? _catalog;
        private bool _loaded;
        private bool _busy;
        private bool _running;
        private string? _loadError;
        private List<ModPackage> _packages = new List<ModPackage>();
        private ModSelection _selection = new ModSelection();
        private Func<string> _statusText = () => string.Empty;

        public CataloguePage(LauncherService service)
        {
            _service = service;
            BackColor = MainForm.Background;
            _searchLabel = ModUi.MakeLine();
            _searchLabel.TextAlign = ContentAlignment.MiddleLeft;
            _search = new TextBox
            {
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = ModUi.Panel,
                ForeColor = MainForm.TitleColor,
                Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point)
            };
            _search.TextChanged += (s, e) => Fill(SelectedId());
            _count = ModUi.MakeLine();
            _count.TextAlign = ContentAlignment.MiddleRight;
            _list = ModUi.MakeList(checkBoxes: false);
            _list.Columns.Add(string.Empty, 150);
            _list.Columns.Add(string.Empty, 58);
            _list.Columns.Add(string.Empty, 54);
            _list.Columns.Add(string.Empty, 70);
            _list.Columns.Add(string.Empty, 120);
            _list.SelectedIndexChanged += (s, e) => ShowDetails();
            _details = ModUi.MakeDetails();
            _installButton = ModUi.MakeButton();
            _installButton.BackColor = MainForm.Accent;
            _installButton.ForeColor = Color.White;
            _installButton.FlatAppearance.MouseOverBackColor = ControlPaint.Light(MainForm.Accent, 0.12f);
            // The accent colour says "press me": a disabled button (already installed, game running) takes the plain one.
            _installButton.EnabledChanged += (s, e) => _installButton.BackColor = _installButton.Enabled ? MainForm.Accent : MainForm.Secondary;
            _installButton.Click += async (s, e) => await InstallSelectedAsync();
            _removeButton = ModUi.MakeButton();
            _removeButton.Click += (s, e) => RemoveSelected();
            _updateAllButton = ModUi.MakeButton();
            _updateAllButton.Click += async (s, e) => await UpdateAllAsync();
            _refreshButton = ModUi.MakeButton();
            _refreshButton.Click += async (s, e) => await RefreshAsync();
            _progress = new ProgressStripe { Visible = false, TrackColor = MainForm.ProgressTrack, FillColor = MainForm.Accent };
            _status = ModUi.MakeLine();
            Controls.AddRange(new Control[] { _searchLabel, _search, _count, _list, _details, _installButton, _removeButton, _updateAllButton, _refreshButton, _progress, _status });
            Resize += (s, e) => LayoutPage();
        }

        /// <summary>Raised after a mod was installed, updated or removed (the Mods tab reloads).</summary>
        public event EventHandler? Changed;

        public void ApplyTexts()
        {
            _searchLabel.Text = Texts.T("cat.search");
            _list.Columns[0].Text = Texts.T("mods.col.name");
            _list.Columns[1].Text = Texts.T("mods.col.version");
            _list.Columns[2].Text = Texts.T("cat.col.kind");
            _list.Columns[3].Text = Texts.T("cat.col.size");
            _list.Columns[4].Text = Texts.T("mods.col.state");
            _removeButton.Text = Texts.T("cat.btn.remove");
            _refreshButton.Text = Texts.T("cat.btn.refresh");
            Fill(SelectedId());
            ShowStatus();
        }

        public void LayoutPage()
        {
            var width = ClientSize.Width;
            var height = ClientSize.Height;
            if (width < 100)
            {
                return;
            }

            var labelWidth = TextRenderer.MeasureText(_searchLabel.Text, _searchLabel.Font).Width + 4;
            _searchLabel.Bounds = new Rectangle(0, 6, labelWidth, _search.PreferredHeight);
            _count.Bounds = new Rectangle(width - 150, 6, 150, _search.PreferredHeight);
            _search.Bounds = new Rectangle(labelWidth + 4, 6, _count.Left - 12 - labelWidth, _search.PreferredHeight);

            var statusHeight = (_status.Font.Height * 2) + 2;
            _status.Bounds = new Rectangle(0, height - statusHeight, width, statusHeight);
            var bottom = _status.Top - 4;
            if (_progressShown)
            {
                _progress.Bounds = new Rectangle(0, bottom - 8, width, 8);
                bottom = _progress.Top - 6;
            }

            var row2 = bottom - ModUi.ButtonHeight;
            ModUi.Row(row2, width, _updateAllButton, _refreshButton);
            var row1 = row2 - ModUi.Gap - ModUi.ButtonHeight;
            ModUi.Row(row1, width, _installButton, _removeButton);
            var detailsHeight = 120;
            _details.Bounds = new Rectangle(0, row1 - 8 - detailsHeight, width, detailsHeight);
            var top = _search.Bottom + 8;
            _list.Bounds = new Rectangle(0, top, width, _details.Top - 8 - top);
            var fixedWidth = 58 + 54 + 70 + 120 + SystemInformation.VerticalScrollBarWidth + 4;
            _list.Columns[0].Width = Math.Max(110, width - fixedWidth);
        }

        public void SetGameRunning(bool running)
        {
            if (_running == running)
            {
                return;
            }

            _running = running;
            ScanLocal();
            Fill(SelectedId());
            ShowStatus();
        }

        /// <summary>The catalogue is fetched when the tab is first opened, then only on Refresh.</summary>
        public async Task EnsureLoadedAsync()
        {
            if (!_loaded && !_busy)
            {
                await RefreshAsync();
            }
        }

        /// <summary>The mod folder changed (Mods tab): states only, no network.</summary>
        public void LocalChanged()
        {
            ScanLocal();
            Fill(SelectedId());
        }

        public async Task RefreshAsync()
        {
            if (_busy)
            {
                return;
            }

            SetBusy(true);
            SetStatus(() => Texts.T("cat.loading"));
            try
            {
                _catalog = await _service.FetchCatalogAsync();
                _loadError = null;
            }
            catch (FileNotFoundException)
            {
                _catalog = null;
                _loadError = string.Empty;
            }
            catch (Exception ex)
            {
                _catalog = null;
                _loadError = ex.Message;
            }
            finally
            {
                _loaded = true;
                SetBusy(false);
            }

            ScanLocal();
            Fill(SelectedId());
            SetStatus(null);
        }

        private void ScanLocal()
        {
            _packages = _service.IsGameInstalled() ? _service.Mods.Scan(out _selection) : new List<ModPackage>();
        }

        private string? SelectedId() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

        private CatalogEntry? Selected() => _catalog?.Find(SelectedId() ?? string.Empty);

        private CatalogState StateOf(CatalogEntry e) => _service.Mods.StateOf(e, _packages);

        private string StateText(CatalogEntry e, CatalogState state)
        {
            switch (state)
            {
                case CatalogState.Installed:
                    return Texts.T("cat.st.installed");
                case CatalogState.UpdateAvailable:
                    return Texts.T("cat.st.update", e.Version);
                case CatalogState.WithGame:
                    return Texts.T("cat.st.withgame");
                case CatalogState.AddedByHand:
                    return Texts.T("cat.st.byhand");
                case CatalogState.NeedsGame:
                    return Texts.T("cat.st.needsgame", e.Game);
                default:
                    return Texts.T("cat.st.notinstalled");
            }
        }

        private bool Matches(CatalogEntry e)
        {
            var q = _search.Text.Trim();
            if (q.Length == 0)
            {
                return true;
            }

            return new[] { e.Id, e.NameRu, e.NameEn, e.DescriptionRu, e.DescriptionEn, e.Author }
                .Any(t => t.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }

        private void Fill(string? select)
        {
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var e in _catalog?.Entries ?? new List<CatalogEntry>())
                {
                    if (!Matches(e))
                    {
                        continue;
                    }

                    var state = StateOf(e);
                    var item = new ListViewItem(e.Name(Texts.Language))
                    {
                        Tag = e.Id,
                        ForeColor = state == CatalogState.UpdateAvailable ? MainForm.Accent : state == CatalogState.NotInstalled ? MainForm.TitleColor : MainForm.StatusColor
                    };
                    item.SubItems.Add(e.Version);
                    item.SubItems.Add(Texts.T(e.IsCode ? "cat.kind.code" : "cat.kind.data"));
                    item.SubItems.Add(LauncherService.FormatSize(e.Size, Texts.Language));
                    item.SubItems.Add(StateText(e, state));
                    _list.Items.Add(item);
                    if (e.Id == select)
                    {
                        item.Selected = true;
                    }
                }

                if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
                {
                    _list.Items[0].Selected = true;
                }

                _list.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.EnsureVisible();
            }
            finally
            {
                _list.EndUpdate();
            }

            _count.Text = _catalog == null ? string.Empty : Texts.T("cat.count", _catalog.Entries.Count);
            ShowDetails();
        }

        private void ShowDetails()
        {
            _details.Clear();
            var e = Selected();
            ApplyButtons(e);
            if (e == null)
            {
                return;
            }

            ModUi.Append(_details, e.Name(Texts.Language) + "  " + e.Version, MainForm.TitleColor, bold: true);
            if (e.Author.Length > 0)
            {
                ModUi.Append(_details, Texts.T("cat.d.by", e.Author), MainForm.StatusColor);
            }

            var description = e.Description(Texts.Language);
            if (description.Length > 0)
            {
                ModUi.Append(_details, description);
            }

            if (e.IsCode)
            {
                ModUi.Append(_details, Texts.T("mods.d.code", Texts.T("cat.kind.code")), ModUi.Bad);
            }

            if (e.Requires.Count > 0)
            {
                ModUi.Append(_details, Texts.T("cat.d.requires", string.Join(", ", e.Requires)));
            }

            if (e.Game.Length > 0)
            {
                ModUi.Append(_details, Texts.T("cat.d.game", e.Game));
            }

            var mine = _service.Mods.LoadRecord().Find(e.Id);
            if (mine != null)
            {
                ModUi.Append(_details, Texts.T("cat.d.installed", mine.Version), MainForm.StatusColor);
            }

            if (e.Homepage.Length > 0)
            {
                ModUi.Append(_details, Texts.T("cat.d.homepage", e.Homepage), MainForm.StatusColor);
            }

            _details.SelectionStart = 0;
            _details.ScrollToCaret();
        }

        private void ApplyButtons(CatalogEntry? e)
        {
            var can = !_busy && !_running && _service.IsGameInstalled();
            var state = e == null ? CatalogState.NotInstalled : StateOf(e);
            _installButton.Text = Texts.T(state == CatalogState.UpdateAvailable ? "cat.btn.update" : state == CatalogState.AddedByHand ? "cat.btn.replace" : "cat.btn.install");
            _installButton.Enabled = can && e != null && (state == CatalogState.NotInstalled || state == CatalogState.UpdateAvailable || state == CatalogState.AddedByHand);
            _removeButton.Enabled = can && e != null && (state == CatalogState.Installed || state == CatalogState.UpdateAvailable);
            var updates = (_catalog?.Entries ?? new List<CatalogEntry>()).Count(x => StateOf(x) == CatalogState.UpdateAvailable);
            _updateAllButton.Text = Texts.T("cat.btn.updateall", updates);
            _updateAllButton.Enabled = can && updates > 0;
            _refreshButton.Enabled = !_busy;
            _search.Enabled = true;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            ApplyButtons(Selected());
        }

        private Form? Owner => FindForm();

        private async Task InstallSelectedAsync()
        {
            var e = Selected();
            if (e == null || _busy)
            {
                return;
            }

            await InstallAsync(new[] { e });
        }

        private async Task UpdateAllAsync()
        {
            if (_catalog == null || _busy)
            {
                return;
            }

            await InstallAsync(_catalog.Entries.Where(x => StateOf(x) == CatalogState.UpdateAvailable).ToList());
        }

        /// <summary>Installs or updates the entries with the catalogue mods they require. One confirm lists the
        /// requirements, a code mod is warned about, a folder added by hand is moved to the backups first.</summary>
        private async Task InstallAsync(IList<CatalogEntry> targets)
        {
            if (_catalog == null || targets.Count == 0)
            {
                return;
            }

            if (_running)
            {
                SetStatus(() => Texts.T("st.blocked.running"));
                return;
            }

            var plan = new List<CatalogEntry>();
            try
            {
                foreach (var t in targets)
                {
                    foreach (var x in _service.Mods.PlanInstall(t, _catalog, _packages))
                    {
                        if (!plan.Contains(x))
                        {
                            plan.Add(x);
                        }
                    }
                }
            }
            catch (ModOperationException ex)
            {
                SetStatus(() => Texts.T("cat.st.failed", ex.Text));
                return;
            }

            var extra = plan.Where(x => !targets.Contains(x)).ToList();
            if (extra.Count > 0 && targets.Count == 1 &&
                !ConfirmForm.Ask(Owner, Texts.T("mods.confirm.title"), Texts.T("cat.confirm.deps", targets[0].Name(Texts.Language), string.Join(", ", extra.Select(x => x.Name(Texts.Language)))), Texts.T("btn.ok")))
            {
                return;
            }

            foreach (var x in plan.Where(x => x.IsCode && StateOf(x) == CatalogState.NotInstalled))
            {
                if (!ConfirmForm.Ask(Owner, Texts.T("mods.c.title"), Texts.T("cat.confirm.code", x.Name(Texts.Language), x.Author.Length > 0 ? x.Author : "?"), Texts.T("cat.btn.install"), warning: true))
                {
                    return;
                }
            }

            var backups = new List<string>();
            foreach (var x in plan.Where(x => StateOf(x) == CatalogState.AddedByHand))
            {
                if (!ConfirmForm.Ask(Owner, Texts.T("mods.confirm.title"), Texts.T("cat.confirm.replace", x.Id, _service.Mods.BackupDir), Texts.T("btn.ok")))
                {
                    return;
                }
            }

            var updating = plan.Where(x => StateOf(x) == CatalogState.UpdateAvailable).Select(x => x.Id).ToList();
            SetBusy(true);
            SetProgress(true);
            Func<string>? done = null;
            var installed = new List<string>();
            try
            {
                foreach (var x in plan)
                {
                    if (StateOf(x) == CatalogState.AddedByHand)
                    {
                        backups.Add(_service.Mods.BackupHandAdded(x.Id));
                    }

                    var name = x.Name(Texts.Language);
                    var progress = new Progress<double>(f =>
                    {
                        _progress.Value = f * 100;
                        var percent = (int)Math.Round(f * 100);
                        SetStatus(() => Texts.T("cat.st.downloading", name, percent));
                    });
                    await _service.Mods.InstallAsync(x, progress, CancellationToken.None);
                    installed.Add(name);
                    ScanLocal();
                }

                // An update of an enabled code mod: the consent belongs to the old version, so ask for the new one now
                // (the game asks in its own manager otherwise).
                foreach (var x in plan.Where(x => x.IsCode && updating.Contains(x.Id)))
                {
                    var selection = _service.Mods.Settings.Load();
                    if (selection.IsEnabled(x.Id) && !selection.HasConsent(x.Id, x.Version) &&
                        ConfirmForm.Ask(Owner, Texts.T("mods.c.title"), Texts.T("cat.confirm.reconsent", x.Name(Texts.Language), x.Version, x.Author.Length > 0 ? x.Author : "?"), Texts.T("cat.btn.allow"), warning: true))
                    {
                        selection.Consent[x.Id] = x.Version;
                        _service.Mods.SaveSelection(selection);
                    }
                }

                var names = string.Join(", ", installed);
                var key = updating.Count == plan.Count ? "cat.st.updated.done" : "cat.st.installed.done";
                var backupText = backups.Count > 0 ? " " + Texts.T("cat.st.backup", string.Join(", ", backups)) : string.Empty;
                done = () => Texts.T(key, names) + backupText;
            }
            catch (Exception ex)
            {
                var text = ModUi.ErrorText(ex);
                done = () => Texts.T("cat.st.failed", text);
            }
            finally
            {
                SetProgress(false);
                SetBusy(false);
                ScanLocal();
                Fill(SelectedId());
                Changed?.Invoke(this, EventArgs.Empty);
            }

            SetStatus(done);
        }

        private void RemoveSelected()
        {
            var e = Selected();
            if (e == null || _busy)
            {
                return;
            }

            if (_running)
            {
                SetStatus(() => Texts.T("st.blocked.running"));
                return;
            }

            var name = e.Name(Texts.Language);
            var dependents = ModRules.ToDisable(e.Id, _packages, _selection);
            var depNames = string.Join(", ", dependents.Select(id => _packages.FirstOrDefault(p => p.Id == id)?.Manifest.DisplayName ?? id));
            var question = dependents.Count > 0 ? Texts.T("cat.confirm.remove.deps", name, depNames) : Texts.T("cat.confirm.remove", name);
            if (!ConfirmForm.Ask(Owner, Texts.T("mods.confirm.title"), question, Texts.T("cat.btn.remove"), warning: dependents.Count > 0))
            {
                return;
            }

            Func<string> done;
            try
            {
                var off = _service.Mods.Remove(e.Id);
                var offNames = string.Join(", ", off.Select(id => _packages.FirstOrDefault(p => p.Id == id)?.Manifest.DisplayName ?? id));
                done = off.Count > 0 ? (Func<string>)(() => Texts.T("cat.st.removed.deps", name, offNames)) : () => Texts.T("cat.st.removed.done", name);
            }
            catch (Exception ex) when (ex is ModOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                var text = ModUi.ErrorText(ex);
                done = () => Texts.T("cat.st.failed", text);
            }

            ScanLocal();
            Fill(e.Id);
            Changed?.Invoke(this, EventArgs.Empty);
            SetStatus(done);
        }

        private void SetProgress(bool visible)
        {
            _progress.Value = 0;
            _progressShown = visible; // not _progress.Visible: it reads false while this tab is hidden
            _progress.Visible = visible;
            LayoutPage();
        }

        private void SetStatus(Func<string>? text)
        {
            _statusText = text ?? (() => string.Empty);
            ShowStatus();
        }

        private void ShowStatus()
        {
            var text = _statusText();
            _status.ForeColor = MainForm.StatusColor;
            if (text.Length == 0)
            {
                if (_running)
                {
                    _status.ForeColor = ModUi.Bad;
                    text = Texts.T("st.blocked.running");
                }
                else if (!_service.IsGameInstalled())
                {
                    text = Texts.T("mods.nogame");
                }
                else if (_loaded && _catalog == null)
                {
                    var error = _loadError ?? string.Empty;
                    text = error.Length == 0 ? Texts.T("cat.empty") : Texts.T("cat.failed", error);
                }
            }

            _status.Text = text;
        }
    }

    /// <summary>A question in the launcher's palette: the text, a confirm button and Cancel.</summary>
    public sealed class ConfirmForm : Form
    {
        private const int Pad = 20;

        private ConfirmForm(string title, string text, string confirm, bool warning)
        {
            Text = title;
            BackColor = MainForm.Background;
            ForeColor = MainForm.BodyColor;
            Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            var width = 420;
            var label = new Label
            {
                Text = text,
                AutoSize = false,
                ForeColor = warning ? MainForm.TitleColor : MainForm.BodyColor,
                BackColor = Color.Transparent,
                UseMnemonic = false
            };
            var size = TextRenderer.MeasureText(text, Font, new Size(width - (Pad * 2), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            label.Bounds = new Rectangle(Pad, Pad, width - (Pad * 2), size.Height + 4);
            var ok = MainForm.MakeButton(confirm, warning ? ColorTranslator.FromHtml("#A83A44") : MainForm.Accent, Color.White, 9.75f, FontStyle.Regular);
            ok.DialogResult = DialogResult.OK;
            var cancel = MainForm.MakeButton(Texts.T("btn.cancel"), MainForm.Secondary, MainForm.SecondaryText, 9.75f, FontStyle.Regular);
            cancel.DialogResult = DialogResult.Cancel;
            var okWidth = Math.Max(120, TextRenderer.MeasureText(confirm, ok.Font).Width + 24);
            var y = label.Bottom + 16;
            cancel.Bounds = new Rectangle(width - Pad - 110, y, 110, 32);
            ok.Bounds = new Rectangle(cancel.Left - 10 - okWidth, y, okWidth, 32);
            ClientSize = new Size(width, y + 32 + Pad);
            Controls.AddRange(new Control[] { label, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (s, e) => cancel.Focus();
        }

        /// <summary>Hook for tests: answers instead of a window when set.</summary>
        internal static Func<string, string, bool>? Answer { get; set; }

        public static bool Ask(IWin32Window? owner, string title, string text, string confirm, bool warning = false)
        {
            if (Answer != null)
            {
                return Answer(title, text);
            }

            using (var form = new ConfirmForm(title, text, confirm, warning))
            {
                return form.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }
}
