using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FOEReLauncher
{
    /// <summary>Shared look of the mod pages: framed lists and details boxes, the window's buttons.</summary>
    internal static class ModUi
    {
        internal static readonly Color Panel = Theme.Panel;
        internal static readonly Color Bad = Theme.Bad;
        internal static readonly Color Good = Theme.Bright;

        internal static int ButtonHeight => Theme.S(32);

        internal static int Gap => Theme.S(8);

        /// <summary>The tag of a group's header row (mod ids never start with '#').</summary>
        internal static string GroupTag(string groupId) => "#" + groupId;

        internal static string? GroupOfTag(object? tag) => tag is string s && s.StartsWith("#", StringComparison.Ordinal) ? s.Substring(1) : null;

        internal static RichTextBox MakeDetails()
        {
            var box = new RichTextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Panel,
                ForeColor = Theme.Body,
                Font = Theme.Font(9.75f),
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = false,
                TabStop = false
            };
            Theme.DarkScrollBars(box);
            return box;
        }

        internal static Label MakeLine(float size = 9.75f) => new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = Theme.Dim,
            Font = Theme.Font(size),
            UseMnemonic = false
        };

        internal static PipButton MakeButton(PipButtonKind kind = PipButtonKind.Normal) => MainForm.MakeButton(string.Empty, kind, 9.75f, FontStyle.Regular);

        internal static void Append(RichTextBox box, string text, Color? color = null, bool bold = false)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor = color ?? Theme.Body;
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

        /// <summary>A column wide enough for its caption and the widest of the given cell texts.</summary>
        internal static int ColumnWidth(Control list, int min, params string[] texts) =>
            Math.Max(Theme.S(min), texts.Select(t => TextRenderer.MeasureText(t, list.Font).Width).DefaultIfEmpty(0).Max() + Theme.S(16));

        internal static string ErrorText(Exception ex) =>
            ex is ModOperationException m ? m.Text : ex is InvalidDataException ? Texts.T("mods.err.hash") : Texts.ErrorText(ex);

        /// <summary>Up to <paramref name="max"/> names joined by commas, then "and N more".</summary>
        internal static string Names(IEnumerable<string> names, int max = 6)
        {
            var list = names.ToList();
            return list.Count <= max ? string.Join(", ", list) : string.Join(", ", list.Take(max)) + " " + Texts.T("names.more", list.Count - max);
        }
    }

    /// <summary>How one row of a <see cref="PipList"/> is drawn.</summary>
    internal sealed class RowLook
    {
        public bool IsGroup { get; set; }

        public bool Open { get; set; } = true;

        public bool Indent { get; set; }

        public CheckState Check { get; set; }

        public bool CheckEnabled { get; set; } = true;

        public bool Code { get; set; }

        public Color Color { get; set; } = Theme.Body;

        /// <summary>Colour of the last column (state), when it differs.</summary>
        public Color? StateColor { get; set; }
    }

    /// <summary>
    /// The mod list in the PipBuck's look: framed rows, yellow column captions, group header rows with a triangle that
    /// opens and closes them. Group rows open and close with a click on the triangle, a double click, Enter, or the
    /// Left and Right keys.
    /// </summary>
    internal sealed class PipList : ListView
    {
        private const int LVM_GETHEADER = 0x101F;

        private readonly Dictionary<string, RowLook> _looks = new Dictionary<string, RowLook>(StringComparer.Ordinal);

        public PipList(bool checkBoxes)
        {
            View = View.Details;
            FullRowSelect = true;
            MultiSelect = false;
            HideSelection = false;
            CheckBoxes = checkBoxes;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Panel;
            ForeColor = Theme.Body;
            HeaderStyle = ColumnHeaderStyle.Nonclickable;
            OwnerDraw = true;
            DoubleBuffered = true;
            Font = Theme.Font(9.75f);
            // The row height comes from the small image list; the images themselves are not used.
            SmallImageList = new ImageList { ImageSize = new Size(1, Math.Max(16, Theme.S(26))) };
            Theme.DarkScrollBars(this);
            HandleCreated += (s, e) =>
            {
                var header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero)
                {
                    SetHeaderTheme(header);
                }
            };
        }

        /// <summary>A group header asks to open or close (its id).</summary>
        public event Action<string>? GroupToggle;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

        private static void SetHeaderTheme(IntPtr header)
        {
            try
            {
                SetWindowTheme(header, "DarkMode_ItemsView", null);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                // No themes.
            }
        }

        public void SetLook(string tag, RowLook look) => _looks[tag] = look;

        public void ClearLooks() => _looks.Clear();

        private RowLook LookOf(ListViewItem item) => item.Tag is string t && _looks.TryGetValue(t, out var l) ? l : new RowLook();

        private Rectangle CheckRect(ListViewItem item, Rectangle row)
        {
            var label = item.GetBounds(ItemBoundsPortion.Label);
            var size = Math.Max(10, Math.Min(Theme.S(15), row.Height - Theme.S(8)));
            var areaRight = Math.Max(row.X + size + 4, label.X - 2);
            var x = row.X + Math.Max(2, ((areaRight - row.X) - size) / 2);
            return new Rectangle(x, row.Y + ((row.Height - size) / 2), size, size);
        }

        private Rectangle ChevronRect(ListViewItem item, Rectangle row)
        {
            var label = item.GetBounds(ItemBoundsPortion.Label);
            var start = CheckBoxes ? label.X : row.X + Theme.S(4);
            return new Rectangle(start, row.Y, Theme.S(18), row.Height);
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using (var back = new SolidBrush(Theme.Background))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
            }

            using (var line = new SolidBrush(Theme.Frame))
            {
                e.Graphics.FillRectangle(line, new Rectangle(e.Bounds.X, e.Bounds.Bottom - 1, e.Bounds.Width, 1));
            }

            var r = Rectangle.Inflate(e.Bounds, -Theme.S(6), 0);
            if (e.ColumnIndex == 0)
            {
                r.X += CheckBoxes ? Theme.S(20) : Theme.S(4);
                r.Width -= CheckBoxes ? Theme.S(20) : Theme.S(4);
            }

            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, Font, r, Theme.Amber,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            // The row is composed off screen and shown in one copy: drawn straight on screen, the row showed empty
            // for a moment between its background and its text whenever Windows repainted it (mouse moves, clicks).
            var row = e.Bounds;
            if (row.Width <= 0 || row.Height <= 0)
            {
                return;
            }

            // The buffer covers the client area from (0, 0), so GDI text (which ignores a Graphics offset) lands where it
            // should; only the row is copied to the screen.
            var area = new Rectangle(0, 0, Math.Max(ClientSize.Width, row.Right), Math.Max(ClientSize.Height, row.Bottom));
            using (var buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, area))
            {
                DrawRow(buffer.Graphics, e.Item, row);
                var src = buffer.Graphics.GetHdc();
                var dst = e.Graphics.GetHdc();
                try
                {
                    BitBlt(dst, row.X, row.Y, row.Width, row.Height, src, row.X, row.Y, 0x00CC0020);
                }
                finally
                {
                    e.Graphics.ReleaseHdc(dst);
                    buffer.Graphics.ReleaseHdc(src);
                }
            }
        }

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

        private void DrawRow(Graphics g, ListViewItem item, Rectangle row)
        {
            var look = LookOf(item);
            var selected = item.Selected;
            using (var back = new SolidBrush(selected ? Theme.FillSelected : look.IsGroup ? Theme.GroupFill : Theme.Panel))
            {
                g.FillRectangle(back, row);
            }

            // The PipBuck's rows: each in a thin frame, the chosen one in a bright one.
            var frame = new Rectangle(row.X + 1, row.Y + 1, row.Width - 2, row.Height - 2);
            Theme.DrawFrame(g, frame, selected ? (Focused ? Theme.Bright : Theme.FrameBright) : look.IsGroup ? Theme.Hex("#0E7A50") : Theme.Hex("#063A26"));
            if (CheckBoxes)
            {
                Theme.DrawCheck(g, CheckRect(item, row), look.IsGroup ? look.Check : item.Checked ? CheckState.Checked : CheckState.Unchecked, look.CheckEnabled);
            }

            // The whole row is drawn here, cells included: on mouse hover Windows repaints a row with DrawItem alone,
            // without DrawSubItem, so cells drawn only there vanished under the row background.
            for (var i = 0; i < item.SubItems.Count && i < Columns.Count; i++)
            {
                var cell = i == 0 ? new Rectangle(row.X, row.Y, Columns[0].Width, row.Height) : item.SubItems[i].Bounds;
                DrawCell(g, item, item.SubItems[i], i, cell, look);
            }
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            // Cells are drawn with their row in OnDrawItem.
        }

        private void DrawCell(Graphics g, ListViewItem item, ListViewItem.ListViewSubItem sub, int column, Rectangle cell, RowLook look)
        {
            const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            if (column == 0)
            {
                var label = item.GetBounds(ItemBoundsPortion.Label);
                var x = CheckBoxes ? label.X : cell.X + Theme.S(4);
                if (look.IsGroup)
                {
                    var chevron = ChevronRect(item, cell);
                    Theme.DrawChevron(g, chevron, look.Open, Theme.Bright);
                    x = chevron.Right + Theme.S(2);
                }
                else if (look.Indent)
                {
                    x += Theme.S(20);
                }

                var right = cell.Right - Theme.S(4);
                if (look.Code)
                {
                    var tag = Texts.T("cat.kind.code");
                    var size = TextRenderer.MeasureText(tag, Font, new Size(int.MaxValue, cell.Height), TextFormatFlags.NoPrefix);
                    var tagRect = new Rectangle(right - size.Width - Theme.S(6), cell.Y + ((cell.Height - size.Height) / 2) - 1, size.Width + Theme.S(6), size.Height + 2);
                    Theme.DrawFrame(g, tagRect, Color.FromArgb(150, Theme.Amber));
                    TextRenderer.DrawText(g, tag, Font, tagRect, Theme.Amber, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    right = tagRect.Left - Theme.S(4);
                }

                using (var font = look.IsGroup ? new Font(Font, FontStyle.Bold) : null)
                {
                    var color = look.IsGroup ? Theme.Bright : look.Color;
                    var r = new Rectangle(x, cell.Y, Math.Max(0, right - x), cell.Height);
                    if (look.IsGroup || item.Selected)
                    {
                        Theme.DrawGlowText(g, sub.Text, font ?? Font, r, color, Theme.Bright, Flags, look.IsGroup ? 1 : 0);
                    }
                    else
                    {
                        TextRenderer.DrawText(g, sub.Text, font ?? Font, r, color, Flags);
                    }
                }

                return;
            }

            var last = column == item.SubItems.Count - 1;
            var c = last && look.StateColor.HasValue ? look.StateColor.Value : look.IsGroup ? Theme.Dim : look.Color;
            TextRenderer.DrawText(g, sub.Text, Font, Rectangle.Inflate(cell, -Theme.S(6), 0), c, Flags);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var hit = HitTest(e.Location);
            var group = ModUi.GroupOfTag(hit.Item?.Tag);
            if (hit.Item != null && group != null && e.Button == MouseButtons.Left && ChevronRect(hit.Item, hit.Item.Bounds).Contains(e.Location))
            {
                hit.Item.Selected = true;
                hit.Item.Focused = true;
                GroupToggle?.Invoke(group);
                return;
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            var hit = HitTest(e.Location);
            var group = ModUi.GroupOfTag(hit.Item?.Tag);
            if (group != null && !(CheckBoxes && hit.Item != null && CheckRect(hit.Item, hit.Item.Bounds).Contains(e.Location)))
            {
                GroupToggle?.Invoke(group);
                return;
            }

            base.OnMouseDoubleClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            var item = SelectedItems.Count > 0 ? SelectedItems[0] : null;
            var group = ModUi.GroupOfTag(item?.Tag);
            if (item != null && group != null)
            {
                var look = LookOf(item);
                if ((e.KeyCode == Keys.Left && look.Open) || (e.KeyCode == Keys.Right && !look.Open) || e.KeyCode == Keys.Enter)
                {
                    GroupToggle?.Invoke(group);
                    e.Handled = true;
                    return;
                }
            }
            else if (item != null && e.KeyCode == Keys.Left && LookOf(item).Indent)
            {
                // Left on a member goes to its group's header.
                for (var i = item.Index - 1; i >= 0; i--)
                {
                    if (ModUi.GroupOfTag(Items[i].Tag) != null)
                    {
                        Items[i].Selected = true;
                        Items[i].Focused = true;
                        Items[i].EnsureVisible();
                        e.Handled = true;
                        return;
                    }
                }
            }

            base.OnKeyDown(e);
        }

        protected override void WndProc(ref Message m)
        {
            // The dark Explorer theme (for the scroll bars) also brings the stock hover highlight: on every mouse move
            // Windows erased the row under the mouse and repainted it in its own blue, so rows blinked. The lists have
            // no hover look of their own, so hot tracking is refused (LVN_HOTTRACK, reflected WM_NOTIFY).
            const int WmReflectNotify = 0x204E;
            const int LvnHotTrack = -121;
            if (m.Msg == WmReflectNotify && Marshal.ReadInt32(m.LParam, IntPtr.Size * 2) == LvnHotTrack)
            {
                m.Result = (IntPtr)1;
                return;
            }

            // The list is double buffered and every row paints its whole background, so the separate erase only
            // showed an empty row for a moment before the paint.
            const int WmEraseBkgnd = 0x0014;
            if (m.Msg == WmEraseBkgnd)
            {
                m.Result = (IntPtr)1;
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }
    }

    /// <summary>
    /// The Mods tab: the game's mod manager on the same settings.json. Changes are saved at once, as the game
    /// does; while the game runs the list is read-only. Mods of a group are listed under the group's row, whose box
    /// switches the whole group.
    /// </summary>
    public sealed class ModsPage : ThemedPanel
    {
        private readonly LauncherService _service;
        private readonly Label _hint;
        private readonly Label _examples;
        private readonly PipList _list;
        private readonly FrameBox _listBox;
        private readonly RichTextBox _details;
        private readonly FrameBox _detailsBox;
        private readonly Button _upButton;
        private readonly Button _downButton;
        private readonly Button _refreshButton;
        private readonly Button _folderButton;
        private readonly Label _status;

        private List<ModPackage> _packages = new List<ModPackage>();
        private ModSelection _selection = new ModSelection();
        private List<ModBlock<string>> _blocks = new List<ModBlock<string>>();
        private readonly Dictionary<string, ModGroupInfo> _groups = new Dictionary<string, ModGroupInfo>(StringComparer.Ordinal);
        private readonly HashSet<string> _closed = new HashSet<string>(StringComparer.Ordinal);
        private bool _filling;
        private bool _running;
        private Func<string> _statusText = () => string.Empty;

        public ModsPage(LauncherService service)
        {
            _service = service;
            _hint = ModUi.MakeLine(9f);
            _examples = ModUi.MakeLine(9f);
            _list = new PipList(checkBoxes: true);
            _list.Columns.Add(string.Empty, 150);
            _list.Columns.Add(string.Empty, 60);
            _list.Columns.Add(string.Empty, 110);
            _list.Columns.Add(string.Empty, 110);
            _list.ItemCheck += OnItemCheck;
            _list.SelectedIndexChanged += (s, e) => ShowDetails();
            _list.GroupToggle += ToggleOpen;
            _listBox = new FrameBox(_list, 1);
            _details = ModUi.MakeDetails();
            _detailsBox = new FrameBox(_details, 8);
            _upButton = ModUi.MakeButton();
            _upButton.Click += (s, e) => MoveSelected(-1);
            _downButton = ModUi.MakeButton();
            _downButton.Click += (s, e) => MoveSelected(1);
            _refreshButton = ModUi.MakeButton();
            _refreshButton.Click += (s, e) => Reload();
            _folderButton = ModUi.MakeButton();
            _folderButton.Click += (s, e) => OpenFolder();
            _status = ModUi.MakeLine();
            Controls.AddRange(new Control[] { _hint, _examples, _listBox, _detailsBox, _upButton, _downButton, _refreshButton, _folderButton, _status });
            Resize += (s, e) => LayoutPage();
        }

        /// <summary>Raised after the mod list changed on disk (the catalogue page refreshes its states).</summary>
        public event EventHandler? Changed;

        public void ApplyTexts()
        {
            _hint.Text = Texts.T("mods.hint");
            _examples.Text = Texts.T("mods.examples", ModService.ExamplesArchive);
            _list.Columns[0].Text = Texts.T("mods.col.name");
            _list.Columns[1].Text = Texts.T("mods.col.version");
            _list.Columns[2].Text = Texts.T("mods.col.source");
            _list.Columns[3].Text = Texts.T("mods.col.state");
            _upButton.Text = Texts.T("mods.btn.up");
            _downButton.Text = Texts.T("mods.btn.down");
            _refreshButton.Text = Texts.T("mods.btn.refresh");
            _folderButton.Text = Texts.T("mods.btn.folder");
            Fill(SelectedTag());
            ShowStatus();
            LayoutPage();
        }

        public void LayoutPage()
        {
            var width = ClientSize.Width;
            var height = ClientSize.Height;
            if (width < 100)
            {
                return;
            }

            var hintHeight = TextRenderer.MeasureText(_hint.Text, _hint.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            _hint.Bounds = new Rectangle(0, 0, width, hintHeight + 2);
            var examples = _service.IsGameInstalled() && _service.Mods.HasExamplesArchive;
            _examples.Visible = examples;
            _examples.Bounds = new Rectangle(0, _hint.Bottom, width, examples ? TextRenderer.MeasureText(_examples.Text, _examples.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 2 : 0);
            var statusHeight = (_status.Font.Height * 2) + 2;
            _status.Bounds = new Rectangle(0, height - statusHeight, width, statusHeight);
            var rowY = _status.Top - Theme.S(6) - ModUi.ButtonHeight;
            ModUi.Row(rowY, width, _upButton, _downButton, _refreshButton, _folderButton);
            // The list keeps at least two thirds of the room above the buttons; the details box takes the rest.
            var detailsHeight = Math.Max(Theme.S(64), Math.Min(Theme.S(140), (rowY - _examples.Bottom) / 3));
            _detailsBox.Bounds = new Rectangle(0, rowY - Theme.S(8) - detailsHeight, width, detailsHeight);
            _detailsBox.LayoutChild();
            var top = _examples.Bottom + Theme.S(6);
            _listBox.Bounds = new Rectangle(0, top, width, Math.Max(Theme.S(60), _detailsBox.Top - Theme.S(8) - top));
            _listBox.LayoutChild();
            _list.Columns[1].Width = ModUi.ColumnWidth(_list, 64, _list.Columns[1].Text, "10.10.10");
            _list.Columns[2].Width = ModUi.ColumnWidth(_list, 110, _list.Columns[2].Text, Texts.T("mods.src.game"), Texts.T("mods.src.catalogue"), Texts.T("mods.src.hand"));
            _list.Columns[3].Width = ModUi.ColumnWidth(_list, 110, _list.Columns[3].Text, Texts.T("mods.st.on"), Texts.T("mods.st.off"), Texts.T("mods.st.noconsent"));
            var fixedWidth = _list.Columns[1].Width + _list.Columns[2].Width + _list.Columns[3].Width + SystemInformation.VerticalScrollBarWidth + Theme.S(2);
            _list.Columns[0].Width = Math.Max(Theme.S(140), _list.Width - fixedWidth);
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

            Fill(SelectedTag());
            ShowStatus();
        }

        /// <summary>Reads the folder and the settings again, as the game does at its start.</summary>
        public void Reload()
        {
            var tag = SelectedTag();
            _packages = _service.IsGameInstalled() ? _service.Mods.Scan(out _selection) : new List<ModPackage>();
            if (!_service.IsGameInstalled())
            {
                _selection = new ModSelection();
            }

            Fill(tag);
            SetStatus(null);
        }

        private string? SelectedTag() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

        private string? SelectedId() => SelectedTag() is string t && ModUi.GroupOfTag(t) == null ? t : null;

        private string? SelectedGroup() => ModUi.GroupOfTag(SelectedTag());

        private IList<ModPackage> Listed() => ModRules.ListOrder(_packages, _selection);

        private void ToggleOpen(string group)
        {
            if (!_closed.Remove(group))
            {
                _closed.Add(group);
            }

            Fill(ModUi.GroupTag(group));
        }

        /// <summary>The members of a group that the list shows (no duplicates or Content packages).</summary>
        private List<string> Members(string group) => _blocks.FirstOrDefault(b => b.GroupId == group)?.Items ?? new List<string>();

        private CheckState GroupCheck(string group)
        {
            var members = Members(group);
            var on = members.Count(id => _selection.IsEnabled(id));
            return on == 0 ? CheckState.Unchecked : on == members.Count ? CheckState.Checked : CheckState.Indeterminate;
        }

        private void Fill(string? select)
        {
            _filling = true;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                _list.ClearLooks();
                var game = _service.Mods.GameState();
                var record = _service.Mods.LoadRecord();
                var listed = Listed();
                _blocks = ModGroups.Arrange(listed.Select(p => p.Id), id => _service.Mods.GroupOf(listed.First(p => p.Id == id), record));
                _groups.Clear();
                foreach (var block in _blocks)
                {
                    if (block.IsGroup)
                    {
                        var info = _service.Mods.GroupInfo(block.GroupId, record);
                        _groups[block.GroupId] = info;
                        var check = GroupCheck(block.GroupId);
                        var on = block.Items.Count(id => _selection.IsEnabled(id));
                        var header = new ListViewItem(info.Name(Texts.Language)) { Tag = ModUi.GroupTag(block.GroupId), Checked = check == CheckState.Checked };
                        header.SubItems.Add(string.Empty);
                        header.SubItems.Add(Texts.T("group.mods", block.Items.Count));
                        header.SubItems.Add(Texts.T("group.on", on, block.Items.Count));
                        _list.SetLook((string)header.Tag, new RowLook { IsGroup = true, Open = !_closed.Contains(block.GroupId), Check = check, CheckEnabled = !_running });
                        _list.Items.Add(header);
                        if (_closed.Contains(block.GroupId))
                        {
                            continue;
                        }
                    }

                    foreach (var id in block.Items)
                    {
                        var p = listed.First(x => x.Id == id);
                        var source = _service.Mods.SourceOf(p, game, record);
                        var item = new ListViewItem(p.Manifest.DisplayName)
                        {
                            Tag = p.Id,
                            Checked = _selection.IsEnabled(p.Id)
                        };
                        item.SubItems.Add(p.Manifest.Version);
                        item.SubItems.Add(Texts.T(source == ModSource.Game ? "mods.src.game" : source == ModSource.Catalogue ? "mods.src.catalogue" : "mods.src.hand"));
                        item.SubItems.Add(ModUi.StateWord(p, _selection));
                        var problem = !p.Active && (_selection.IsEnabled(p.Id) || p.State == ModState.Invalid);
                        _list.SetLook(p.Id, new RowLook
                        {
                            Indent = block.IsGroup,
                            Code = p.HasCode,
                            CheckEnabled = !_running,
                            Color = p.Active ? Theme.Bright : problem ? Theme.Bad : Theme.Dim,
                            StateColor = problem ? Theme.Bad : p.Active ? Theme.Bright : Theme.Dim
                        });
                        _list.Items.Add(item);
                    }
                }

                foreach (ListViewItem item in _list.Items)
                {
                    if ((string)item.Tag == select)
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
            var item = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0] : null;
            var can = item != null && !_running;
            var tag = item?.Tag as string;
            var group = ModUi.GroupOfTag(tag);
            var up = false;
            var down = false;
            if (can && tag != null)
            {
                var gi = _blocks.FindIndex(b => group != null ? b.GroupId == group : !b.IsGroup && b.Items.Contains(tag));
                if (gi >= 0)
                {
                    up = gi > 0;
                    down = gi < _blocks.Count - 1;
                }
                else
                {
                    var block = _blocks.FirstOrDefault(b => b.Items.Contains(tag));
                    var i = block?.Items.IndexOf(tag) ?? -1;
                    up = i > 0;
                    down = block != null && i >= 0 && i < block.Items.Count - 1;
                }
            }

            _upButton.Enabled = up;
            _downButton.Enabled = down;
            _folderButton.Enabled = _service.IsGameInstalled();
        }

        private void ShowDetails()
        {
            ApplyEnabled();
            _details.Clear();
            var group = SelectedGroup();
            if (group != null && _groups.TryGetValue(group, out var info))
            {
                var members = Members(group);
                ModUi.Append(_details, info.Name(Texts.Language), Theme.Bright, bold: true);
                var description = info.Description(Texts.Language);
                if (description.Length > 0)
                {
                    ModUi.Append(_details, description);
                }

                ModUi.Append(_details, Texts.T("group.d.on", members.Count(id => _selection.IsEnabled(id)), members.Count), Theme.Bright);
                ModUi.Append(_details, Texts.T("group.d.hint"), Theme.Dim);
                _details.SelectionStart = 0;
                _details.ScrollToCaret();
                return;
            }

            var id = SelectedId();
            var p = _packages.FirstOrDefault(x => x.Id == id && !x.Core && x.State != ModState.Duplicate);
            if (p == null)
            {
                return;
            }

            var m = p.Manifest;
            ModUi.Append(_details, m.DisplayName + "  " + m.Version, Theme.Bright, bold: true);
            if (m.Author.Length > 0)
            {
                ModUi.Append(_details, Texts.T("mods.d.by", m.Author), Theme.Dim);
            }

            if (m.Description.Length > 0)
            {
                ModUi.Append(_details, m.Description);
            }

            ModUi.Append(_details, Texts.T("mods.d.state", ModUi.StateWord(p, _selection)), p.Active ? Theme.Bright : Theme.Body);
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
                                       Texts.T(_selection.HasConsent(p.Id, m.Version) ? "mods.d.allowed" : "mods.d.notallowed"), Theme.Amber);
            }

            ModUi.Append(_details, Texts.T("mods.d.folder", p.RelDir), Theme.Dim);
            if (p.Issues.Count > 0)
            {
                ModUi.Append(_details, Texts.T("mods.d.issues", string.Join("; ", p.Issues)), Theme.Dim);
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

            var tag = _list.Items[e.Index].Tag as string;
            var group = ModUi.GroupOfTag(tag);
            var on = e.NewValue == CheckState.Checked;
            if (group != null)
            {
                // The group's box: on when not all of its mods are on, off when they all are.
                e.NewValue = e.CurrentValue;
                var wanted = GroupCheck(group) != CheckState.Checked;
                if ((on && GroupCheck(group) == CheckState.Checked) || (!on && GroupCheck(group) == CheckState.Unchecked && e.CurrentValue == CheckState.Unchecked))
                {
                    // The list re-applies its ticks when its window handle is created: not a change.
                    return;
                }

                if (_running)
                {
                    SetStatus(() => Texts.T("mods.readonly"));
                    return;
                }

                BeginInvoke(new Action(() => ToggleGroup(group, wanted)));
                return;
            }

            // The list re-applies its ticks when its window handle is created: a tick that already matches the saved
            // list is not a change.
            if (tag == null || on == _selection.IsEnabled(tag))
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

            BeginInvoke(new Action(() => Toggle(tag, on)));
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

        /// <summary>Switches every mod of a group on or off with the same rules as single switches: one confirm for the
        /// mods outside the group that change too, one for program code; mods that conflict are left off and named.</summary>
        internal void ToggleGroup(string group, bool on)
        {
            if (_running || !_groups.TryGetValue(group, out var info))
            {
                return;
            }

            var members = Members(group);
            var plan = ModGroups.PlanToggle(members, on, _packages, _selection);
            var groupName = info.Name(Texts.Language);
            if (plan.Outside.Count > 0 &&
                !ConfirmForm.Ask(FindForm(), Texts.T("mods.confirm.title"), Texts.T(on ? "group.confirm.on" : "group.confirm.off", groupName, string.Join(", ", plan.Outside.Select(NameOf))), Texts.T("btn.ok")))
            {
                return;
            }

            var consent = plan.NeedConsent.Select(Package).Where(x => x != null).Cast<ModPackage>().ToList();
            if (consent.Count > 0 &&
                !ConfirmForm.Ask(FindForm(), Texts.T("mods.c.title"), Texts.T("group.c.text", string.Join(", ", consent.Select(x => x.Manifest.DisplayName))), Texts.T("mods.c.allow"), warning: true))
            {
                return;
            }

            var skipped = plan.Skipped.Select(kv => kv.Value.Length > 0 ? Texts.T("group.skip.conflict", NameOf(kv.Key), NameOf(kv.Value)) : Texts.T("group.skip.broken", NameOf(kv.Key))).ToList();
            if (plan.Members.Count == 0 && plan.Outside.Count == 0)
            {
                SetStatus(() => skipped.Count > 0 ? string.Join(" ", skipped) : Texts.T("mods.saved"));
                return;
            }

            if (Save(ModGroups.Apply(_selection, plan, consent)))
            {
                var count = plan.Members.Count;
                var also = plan.Outside.Count == 0 ? string.Empty : " " + Texts.T(on ? "group.alsoon" : "group.alsooff", string.Join(", ", plan.Outside.Select(NameOf)));
                var left = skipped.Count == 0 ? string.Empty : " " + string.Join(" ", skipped);
                Reload();
                SetStatus(() => Texts.T(on ? "group.saved.on" : "group.saved.off", groupName, count) + also + left);
            }
        }

        private void MoveSelected(int delta)
        {
            var tag = SelectedTag();
            if (tag == null || _running)
            {
                return;
            }

            var sel = _selection.Clone();
            var group = ModUi.GroupOfTag(tag);
            sel.Order = ModGroups.Move(_blocks, group, group == null ? tag : null, delta);
            foreach (var id in _selection.Order.Where(x => !sel.Order.Contains(x)))
            {
                // Ids of mods that are not in the folder now keep their place at the end.
                sel.Order.Add(id);
            }

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

            _status.ForeColor = Theme.Dim;
            var text = _statusText();
            if (text.Length == 0)
            {
                text = !_service.IsGameInstalled() ? Texts.T("mods.nogame") : _list.Items.Count == 0 ? Texts.T("mods.none") : string.Empty;
            }

            _status.Text = text;
        }
    }

    /// <summary>The Catalogue tab: mods from downloads/mods/catalog.json under their groups; install, update, remove,
    /// and a whole group installed and switched on one mod after another.</summary>
    public sealed class CataloguePage : ThemedPanel
    {
        private readonly LauncherService _service;
        private readonly Label _searchLabel;
        private readonly TextBox _search;
        private readonly FrameBox _searchBox;
        private readonly Label _count;
        private readonly Label _examples;
        private readonly PipList _list;
        private readonly FrameBox _listBox;
        private readonly RichTextBox _details;
        private readonly FrameBox _detailsBox;
        private readonly PipButton _installButton;
        private readonly PipButton _removeButton;
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
        private CancellationTokenSource? _cancel;
        private List<ModPackage> _packages = new List<ModPackage>();
        private ModSelection _selection = new ModSelection();
        private readonly HashSet<string> _closed = new HashSet<string>(StringComparer.Ordinal);
        private Func<string> _statusText = () => string.Empty;

        public CataloguePage(LauncherService service)
        {
            _service = service;
            _searchLabel = ModUi.MakeLine();
            _searchLabel.TextAlign = ContentAlignment.MiddleLeft;
            _search = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = ModUi.Panel,
                ForeColor = Theme.Bright,
                Font = Theme.Font(10f)
            };
            _search.TextChanged += (s, e) => Fill(SelectedTag());
            _searchBox = new FrameBox(_search, 5);
            _count = ModUi.MakeLine();
            _count.TextAlign = ContentAlignment.MiddleRight;
            _examples = ModUi.MakeLine(9f);
            _list = new PipList(checkBoxes: false);
            _list.Columns.Add(string.Empty, 150);
            _list.Columns.Add(string.Empty, 60);
            _list.Columns.Add(string.Empty, 70);
            _list.Columns.Add(string.Empty, 150);
            _list.SelectedIndexChanged += (s, e) => ShowDetails();
            _list.GroupToggle += ToggleOpen;
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !e.Handled && _installButton?.Enabled == true)
                {
                    _installButton!.PerformClick();
                    e.Handled = true;
                }
            };
            _listBox = new FrameBox(_list, 1);
            _details = ModUi.MakeDetails();
            _detailsBox = new FrameBox(_details, 8);
            _installButton = ModUi.MakeButton(PipButtonKind.Primary);
            _installButton.Click += async (s, e) => await OnInstallClickAsync();
            _removeButton = ModUi.MakeButton(PipButtonKind.Danger);
            _removeButton.Click += (s, e) => RemoveSelected();
            _updateAllButton = ModUi.MakeButton();
            _updateAllButton.Click += async (s, e) => await UpdateAllAsync();
            _refreshButton = ModUi.MakeButton();
            _refreshButton.Click += async (s, e) => await RefreshAsync();
            _progress = new ProgressStripe { Visible = false };
            _status = ModUi.MakeLine();
            Controls.AddRange(new Control[] { _searchLabel, _searchBox, _count, _examples, _listBox, _detailsBox, _installButton, _removeButton, _updateAllButton, _refreshButton, _progress, _status });
            Resize += (s, e) => LayoutPage();
        }

        /// <summary>Raised after a mod was installed, updated or removed (the Mods tab reloads).</summary>
        public event EventHandler? Changed;

        public void ApplyTexts()
        {
            _searchLabel.Text = Texts.T("cat.search");
            _examples.Text = Texts.T("mods.examples", ModService.ExamplesArchive);
            _list.Columns[0].Text = Texts.T("mods.col.name");
            _list.Columns[1].Text = Texts.T("mods.col.version");
            _list.Columns[2].Text = Texts.T("cat.col.size");
            _list.Columns[3].Text = Texts.T("mods.col.state");
            _refreshButton.Text = Texts.T("cat.btn.refresh");
            Fill(SelectedTag());
            ShowStatus();
            LayoutPage();
        }

        public void LayoutPage()
        {
            var width = ClientSize.Width;
            var height = ClientSize.Height;
            if (width < 100)
            {
                return;
            }

            var searchHeight = _search.PreferredHeight + (2 * Theme.S(_searchBox.Inner));
            var labelWidth = TextRenderer.MeasureText(_searchLabel.Text, _searchLabel.Font).Width + Theme.S(4);
            var countWidth = Math.Max(Theme.S(150), TextRenderer.MeasureText(_count.Text, _count.Font).Width + Theme.S(8));
            _searchLabel.Bounds = new Rectangle(0, 0, labelWidth, searchHeight);
            _count.Bounds = new Rectangle(width - countWidth, 0, countWidth, searchHeight);
            _searchBox.Bounds = new Rectangle(labelWidth + Theme.S(4), 0, Math.Max(Theme.S(80), _count.Left - Theme.S(12) - labelWidth), searchHeight);
            _searchBox.LayoutChild();

            var statusHeight = (_status.Font.Height * 2) + 2;
            _status.Bounds = new Rectangle(0, height - statusHeight, width, statusHeight);
            var bottom = _status.Top - Theme.S(4);
            if (_progressShown)
            {
                _progress.Bounds = new Rectangle(0, bottom - Theme.S(12), width, Theme.S(12));
                bottom = _progress.Top - Theme.S(6);
            }

            var row2 = bottom - ModUi.ButtonHeight;
            ModUi.Row(row2, width, _updateAllButton, _refreshButton);
            var row1 = row2 - ModUi.Gap - ModUi.ButtonHeight;
            ModUi.Row(row1, width, _installButton, _removeButton);
            var detailsHeight = Math.Max(Theme.S(64), Math.Min(Theme.S(130), (row1 - _searchBox.Bottom) / 3));
            _detailsBox.Bounds = new Rectangle(0, row1 - Theme.S(8) - detailsHeight, width, detailsHeight);
            _detailsBox.LayoutChild();
            var examples = _service.IsGameInstalled() && _service.Mods.HasExamplesArchive;
            _examples.Visible = examples;
            _examples.Bounds = new Rectangle(0, _searchBox.Bottom + Theme.S(4), width, examples ? TextRenderer.MeasureText(_examples.Text, _examples.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 2 : 0);
            var top = (examples ? _examples.Bottom : _searchBox.Bottom) + Theme.S(6);
            _listBox.Bounds = new Rectangle(0, top, width, Math.Max(Theme.S(60), _detailsBox.Top - Theme.S(8) - top));
            _listBox.LayoutChild();
            _list.Columns[1].Width = ModUi.ColumnWidth(_list, 64, _list.Columns[1].Text, "10.10.10");
            _list.Columns[2].Width = ModUi.ColumnWidth(_list, 70, _list.Columns[2].Text, "999 KB", Texts.T("group.mods", 99));
            _list.Columns[3].Width = ModUi.ColumnWidth(_list, 150, _list.Columns[3].Text, Texts.T("cat.st.installed.on"), Texts.T("cat.st.notinstalled"), Texts.T("group.installed", 10, 10));
            var fixedWidth = _list.Columns[1].Width + _list.Columns[2].Width + _list.Columns[3].Width + SystemInformation.VerticalScrollBarWidth + Theme.S(2);
            _list.Columns[0].Width = Math.Max(Theme.S(140), _list.Width - fixedWidth);
        }

        public void SetGameRunning(bool running)
        {
            if (_running == running)
            {
                return;
            }

            _running = running;
            ScanLocal();
            Fill(SelectedTag());
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
            Fill(SelectedTag());
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
                if (_service.IsGameInstalled() && _service.Mods.AdoptGroups(_catalog))
                {
                    // Mods installed before the catalogue had groups now list under theirs.
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (FileNotFoundException)
            {
                _catalog = null;
                _loadError = string.Empty;
            }
            catch (Exception ex)
            {
                _catalog = null;
                _loadError = Texts.ErrorText(ex);
            }
            finally
            {
                _loaded = true;
                SetBusy(false);
            }

            ScanLocal();
            Fill(SelectedTag());
            SetStatus(null);
        }

        private void ScanLocal()
        {
            _packages = _service.IsGameInstalled() ? _service.Mods.Scan(out _selection) : new List<ModPackage>();
        }

        private string? SelectedTag() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

        private string? SelectedId() => SelectedTag() is string t && ModUi.GroupOfTag(t) == null ? t : null;

        private string? SelectedGroup() => ModUi.GroupOfTag(SelectedTag());

        private CatalogEntry? Selected() => _catalog?.Find(SelectedId() ?? string.Empty);

        private CatalogState StateOf(CatalogEntry e) => _service.Mods.StateOf(e, _packages);

        private List<CatalogEntry> GroupEntries(string group) =>
            (_catalog?.Entries ?? new List<CatalogEntry>()).Where(e => e.Group == group).ToList();

        private string StateText(CatalogEntry e, CatalogState state)
        {
            switch (state)
            {
                case CatalogState.Installed:
                    return _selection.IsEnabled(e.Id) ? Texts.T("cat.st.installed.on") : Texts.T("cat.st.installed");
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

            var group = _catalog?.FindGroup(e.Group);
            return new[] { e.Id, e.NameRu, e.NameEn, e.DescriptionRu, e.DescriptionEn, e.Author, group?.NameRu ?? string.Empty, group?.NameEn ?? string.Empty }
                .Any(t => t.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }

        private void ToggleOpen(string group)
        {
            if (!_closed.Remove(group))
            {
                _closed.Add(group);
            }

            Fill(ModUi.GroupTag(group));
        }

        /// <summary>Entries in list order: groups by their order, then the mods on their own, each by name.</summary>
        private List<ModBlock<CatalogEntry>> Blocks()
        {
            var entries = (_catalog?.Entries ?? new List<CatalogEntry>()).Where(Matches).ToList();
            var groups = _catalog?.Groups ?? new List<ModGroupInfo>();
            var ordered = entries
                .OrderBy(e => e.Group.Length == 0 ? 1 : 0)
                .ThenBy(e => groups.FirstOrDefault(g => g.Id == e.Group)?.Order ?? 0)
                .ThenBy(e => groups.FirstOrDefault(g => g.Id == e.Group)?.Name(Texts.Language) ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Name(Texts.Language), StringComparer.CurrentCultureIgnoreCase);
            return ModGroups.Arrange(ordered, e => e.Group);
        }

        private void Fill(string? select)
        {
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                _list.ClearLooks();
                var searching = _search.Text.Trim().Length > 0;
                foreach (var block in Blocks())
                {
                    if (block.IsGroup)
                    {
                        var info = _catalog?.FindGroup(block.GroupId) ?? ModGroupInfo.FromFolder(block.GroupId);
                        var all = GroupEntries(block.GroupId);
                        var installed = all.Count(e => StateOf(e) == CatalogState.Installed || StateOf(e) == CatalogState.UpdateAvailable);
                        var updates = all.Count(e => StateOf(e) == CatalogState.UpdateAvailable);
                        var open = searching || !_closed.Contains(block.GroupId);
                        var header = new ListViewItem(info.Name(Texts.Language)) { Tag = ModUi.GroupTag(block.GroupId) };
                        header.SubItems.Add(string.Empty);
                        header.SubItems.Add(Texts.T("group.mods", all.Count));
                        header.SubItems.Add(updates > 0 ? Texts.T("group.updates", updates) : Texts.T("group.installed", installed, all.Count));
                        _list.SetLook((string)header.Tag, new RowLook { IsGroup = true, Open = open, StateColor = updates > 0 ? Theme.Amber : installed == all.Count ? Theme.Bright : Theme.Dim });
                        _list.Items.Add(header);
                        if (!open)
                        {
                            continue;
                        }
                    }

                    foreach (var e in block.Items)
                    {
                        var state = StateOf(e);
                        var item = new ListViewItem(e.Name(Texts.Language)) { Tag = e.Id };
                        item.SubItems.Add(e.Version);
                        item.SubItems.Add(LauncherService.FormatSize(e.Size, Texts.Language));
                        item.SubItems.Add(StateText(e, state));
                        _list.SetLook(e.Id, new RowLook
                        {
                            Indent = block.IsGroup,
                            Code = e.IsCode,
                            Color = state == CatalogState.NotInstalled ? Theme.Bright : Theme.Body,
                            StateColor = state == CatalogState.UpdateAvailable ? Theme.Amber : state == CatalogState.NotInstalled ? Theme.Dim : state == CatalogState.NeedsGame ? Theme.Bad : Theme.Bright
                        });
                        _list.Items.Add(item);
                    }
                }

                foreach (ListViewItem item in _list.Items)
                {
                    if ((string)item.Tag == select)
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
            }

            _count.Text = _catalog == null ? string.Empty : Texts.T("cat.count", _catalog.Entries.Count);
            LayoutPage();
            ShowDetails();
        }

        private void ShowDetails()
        {
            _details.Clear();
            ApplyButtons();
            var group = SelectedGroup();
            if (group != null)
            {
                var info = _catalog?.FindGroup(group) ?? ModGroupInfo.FromFolder(group);
                var all = GroupEntries(group);
                ModUi.Append(_details, info.Name(Texts.Language), Theme.Bright, bold: true);
                var description = info.Description(Texts.Language);
                if (description.Length > 0)
                {
                    ModUi.Append(_details, description);
                }

                var installed = all.Count(e => StateOf(e) == CatalogState.Installed || StateOf(e) == CatalogState.UpdateAvailable);
                ModUi.Append(_details, Texts.T("group.d.installed", installed, all.Count, all.Count(e => _selection.IsEnabled(e.Id) && _packages.Any(p => p.Id == e.Id))), Theme.Bright);
                var code = all.Where(e => e.IsCode).Select(e => e.Name(Texts.Language)).ToList();
                if (code.Count > 0)
                {
                    ModUi.Append(_details, Texts.T("group.d.code", string.Join(", ", code)), Theme.Amber);
                }

                ModUi.Append(_details, Texts.T("group.d.list", string.Join(", ", all.Select(e => e.Name(Texts.Language)))), Theme.Dim);
                _details.SelectionStart = 0;
                _details.ScrollToCaret();
                return;
            }

            var e = Selected();
            if (e == null)
            {
                return;
            }

            ModUi.Append(_details, e.Name(Texts.Language) + "  " + e.Version, Theme.Bright, bold: true);
            if (e.Author.Length > 0)
            {
                ModUi.Append(_details, Texts.T("cat.d.by", e.Author), Theme.Dim);
            }

            var text = e.Description(Texts.Language);
            if (text.Length > 0)
            {
                ModUi.Append(_details, text);
            }

            if (e.IsCode)
            {
                ModUi.Append(_details, Texts.T("mods.d.code", Texts.T("cat.kind.code")), Theme.Amber);
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
                ModUi.Append(_details, Texts.T("cat.d.installed", mine.Version), Theme.Dim);
            }

            if (e.Homepage.Length > 0)
            {
                ModUi.Append(_details, Texts.T("cat.d.homepage", e.Homepage), Theme.Dim);
            }

            _details.SelectionStart = 0;
            _details.ScrollToCaret();
        }

        /// <summary>What the main button does for the selected group: install (and switch on) the missing ones, update,
        /// switch on the installed ones that are off, or nothing.</summary>
        private void GroupCounts(string group, out int toInstall, out int toUpdate, out int toEnable, out int removable)
        {
            var all = GroupEntries(group);
            toInstall = all.Count(e => StateOf(e) == CatalogState.NotInstalled);
            toUpdate = all.Count(e => StateOf(e) == CatalogState.UpdateAvailable);
            toEnable = all.Count(e => StateOf(e) != CatalogState.NotInstalled && StateOf(e) != CatalogState.NeedsGame && _packages.Any(p => p.Id == e.Id) && !_selection.IsEnabled(e.Id));
            removable = all.Count(e => StateOf(e) == CatalogState.Installed || StateOf(e) == CatalogState.UpdateAvailable);
        }

        private void ApplyButtons()
        {
            var can = !_busy && !_running && _service.IsGameInstalled();
            if (_cancel != null)
            {
                // A run is going: the main button stops it, everything else waits.
                _installButton.Text = Texts.T("cat.btn.stop");
                _installButton.Enabled = !_cancel.IsCancellationRequested;
                _removeButton.Enabled = false;
                _updateAllButton.Enabled = false;
                _refreshButton.Enabled = false;
                return;
            }

            var group = SelectedGroup();
            if (group != null)
            {
                GroupCounts(group, out var toInstall, out var toUpdate, out var toEnable, out var removable);
                _installButton.Text = toInstall > 0 ? Texts.T("cat.btn.group.install", toInstall)
                    : toUpdate > 0 ? Texts.T("cat.btn.group.update", toUpdate)
                    : toEnable > 0 ? Texts.T("cat.btn.group.enable", toEnable)
                    : Texts.T("cat.btn.group.done");
                _installButton.Enabled = can && (toInstall > 0 || toUpdate > 0 || toEnable > 0);
                _removeButton.Text = Texts.T("cat.btn.group.remove", removable);
                _removeButton.Enabled = can && removable > 0;
            }
            else
            {
                var e = Selected();
                var state = e == null ? CatalogState.NotInstalled : StateOf(e);
                var off = e != null && (state == CatalogState.Installed) && !_selection.IsEnabled(e.Id);
                _installButton.Text = Texts.T(state == CatalogState.UpdateAvailable ? "cat.btn.update" : state == CatalogState.AddedByHand ? "cat.btn.replace" : off ? "cat.btn.enable" : state == CatalogState.Installed ? "cat.btn.installed" : "cat.btn.install");
                _installButton.Enabled = can && e != null && (state == CatalogState.NotInstalled || state == CatalogState.UpdateAvailable || state == CatalogState.AddedByHand || off);
                _removeButton.Text = Texts.T("cat.btn.remove");
                _removeButton.Enabled = can && e != null && (state == CatalogState.Installed || state == CatalogState.UpdateAvailable);
            }

            var updates = (_catalog?.Entries ?? new List<CatalogEntry>()).Count(x => StateOf(x) == CatalogState.UpdateAvailable);
            _updateAllButton.Text = Texts.T("cat.btn.updateall", updates);
            _updateAllButton.Enabled = can && updates > 0;
            _refreshButton.Enabled = !_busy;
            _search.Enabled = true;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            ApplyButtons();
        }

        private Form? Owner => FindForm();

        private async Task OnInstallClickAsync()
        {
            if (_cancel != null)
            {
                _cancel.Cancel();
                SetStatus(() => Texts.T("cat.st.stopping"));
                ApplyButtons();
                return;
            }

            if (_busy || _catalog == null)
            {
                return;
            }

            var group = SelectedGroup();
            if (group != null)
            {
                await InstallGroupAsync(group);
                return;
            }

            var e = Selected();
            if (e == null)
            {
                return;
            }

            if (StateOf(e) == CatalogState.Installed)
            {
                // Installed and off: the button only switches it on.
                EnableAfter(new[] { e.Id }, new List<string>(), e.Name(Texts.Language), out var enabledText);
                ScanLocal();
                Fill(e.Id);
                Changed?.Invoke(this, EventArgs.Empty);
                SetStatus(() => enabledText);
                return;
            }

            await InstallAsync(new[] { e }, enable: StateOf(e) != CatalogState.UpdateAvailable, groupName: null);
        }

        private async Task InstallGroupAsync(string group)
        {
            var info = _catalog?.FindGroup(group) ?? ModGroupInfo.FromFolder(group);
            var all = GroupEntries(group);
            var targets = all.Where(e => StateOf(e) == CatalogState.NotInstalled || StateOf(e) == CatalogState.UpdateAvailable).ToList();
            if (targets.Count == 0)
            {
                EnableAfter(all.Select(e => e.Id).ToList(), new List<string>(), info.Name(Texts.Language), out var text);
                ScanLocal();
                Fill(ModUi.GroupTag(group));
                Changed?.Invoke(this, EventArgs.Empty);
                SetStatus(() => text);
                return;
            }

            await InstallAsync(targets, enable: true, groupName: info.Name(Texts.Language), groupMembers: all.Select(e => e.Id).ToList());
        }

        private async Task UpdateAllAsync()
        {
            if (_catalog == null || _busy)
            {
                return;
            }

            await InstallAsync(_catalog.Entries.Where(x => StateOf(x) == CatalogState.UpdateAvailable).ToList(), enable: false, groupName: null);
        }

        /// <summary>
        /// Installs or updates the entries with the catalogue mods they require, strictly one after another with a
        /// "3 of 11" progress and Stop; a mod that fails is reported and the rest go on. One confirm lists the
        /// requirements of a single mod, one names every code mod, a folder added by hand is moved to the backups
        /// first. With <paramref name="enable"/> the installed mods (or the whole group) are switched on afterwards
        /// with the game's rules.
        /// </summary>
        private async Task InstallAsync(IList<CatalogEntry> targets, bool enable, string? groupName, IList<string>? groupMembers = null)
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
            var refused = new List<string>();
            foreach (var t in targets)
            {
                try
                {
                    foreach (var x in _service.Mods.PlanInstall(t, _catalog, _packages))
                    {
                        if (!plan.Contains(x))
                        {
                            plan.Add(x);
                        }
                    }
                }
                catch (ModOperationException ex)
                {
                    if (targets.Count == 1)
                    {
                        SetStatus(() => Texts.T("cat.st.failed", ex.Text));
                        return;
                    }

                    refused.Add(Texts.T("cat.st.failed.one", t.Name(Texts.Language), ex.Text));
                }
            }

            if (plan.Count == 0)
            {
                SetStatus(() => string.Join(" ", refused));
                return;
            }

            var extra = plan.Where(x => !targets.Contains(x)).ToList();
            if (extra.Count > 0 && targets.Count == 1 &&
                !ConfirmForm.Ask(Owner, Texts.T("mods.confirm.title"), Texts.T("cat.confirm.deps", targets[0].Name(Texts.Language), string.Join(", ", extra.Select(x => x.Name(Texts.Language)))), Texts.T("btn.ok")))
            {
                return;
            }

            // Program code: one question for the whole run; it also allows the code of the versions being installed.
            var codeNew = plan.Where(x => x.IsCode && StateOf(x) == CatalogState.NotInstalled).ToList();
            var allowed = new List<string>();
            if (codeNew.Count == 1 && targets.Count == 1)
            {
                var x = codeNew[0];
                if (!ConfirmForm.Ask(Owner, Texts.T("mods.c.title"), Texts.T("cat.confirm.code", x.Name(Texts.Language), x.Author.Length > 0 ? x.Author : "?"), Texts.T("cat.btn.install"), warning: true))
                {
                    return;
                }

                allowed.Add(x.Id);
            }
            else if (codeNew.Count > 0)
            {
                if (!ConfirmForm.Ask(Owner, Texts.T("mods.c.title"), Texts.T("cat.confirm.code.many", string.Join(", ", codeNew.Select(x => x.Name(Texts.Language)))), Texts.T("cat.btn.install"), warning: true))
                {
                    return;
                }

                allowed.AddRange(codeNew.Select(x => x.Id));
            }

            foreach (var x in plan.Where(x => StateOf(x) == CatalogState.AddedByHand))
            {
                if (!ConfirmForm.Ask(Owner, Texts.T("mods.confirm.title"), Texts.T("cat.confirm.replace", x.Id, _service.Mods.BackupDir), Texts.T("btn.ok")))
                {
                    return;
                }
            }

            var updating = plan.Where(x => StateOf(x) == CatalogState.UpdateAvailable).Select(x => x.Id).ToList();
            var backups = new List<string>();
            _cancel = new CancellationTokenSource();
            SetBusy(true);
            SetProgress(true);
            BatchResult<CatalogEntry> result;
            try
            {
                result = await ModGroups.RunOneByOneAsync(plan, async (x, index, ct) =>
                {
                    if (StateOf(x) == CatalogState.AddedByHand)
                    {
                        backups.Add(_service.Mods.BackupHandAdded(x.Id));
                    }

                    var name = x.Name(Texts.Language);
                    var progress = new Progress<double>(f =>
                    {
                        _progress.Value = ((index + f) / plan.Count) * 100;
                        var percent = (int)Math.Round(f * 100);
                        SetStatus(() => plan.Count > 1 ? Texts.T("cat.st.downloading.n", index + 1, plan.Count, name, percent) : Texts.T("cat.st.downloading", name, percent));
                    });
                    await _service.Mods.InstallAsync(x, progress, ct, _catalog.FindGroup(x.Group));
                    ScanLocal();
                }, _cancel.Token);
            }
            finally
            {
                _cancel.Dispose();
                _cancel = null;
                SetProgress(false);
                SetBusy(false);
                ScanLocal();
            }

            // An update of an enabled code mod: the consent belongs to the old version, so ask for the new one now
            // (the game asks in its own manager otherwise).
            foreach (var x in result.Done.Where(x => x.IsCode && updating.Contains(x.Id)))
            {
                var selection = _service.Mods.Settings.Load();
                if (selection.IsEnabled(x.Id) && !selection.HasConsent(x.Id, x.Version) &&
                    ConfirmForm.Ask(Owner, Texts.T("mods.c.title"), Texts.T("cat.confirm.reconsent", x.Name(Texts.Language), x.Version, x.Author.Length > 0 ? x.Author : "?"), Texts.T("cat.btn.allow"), warning: true))
                {
                    selection.Consent[x.Id] = x.Version;
                    TrySave(selection);
                }
            }

            var parts = new List<string>();
            var installedNames = result.Done.Select(x => x.Name(Texts.Language)).ToList();
            if (enable && !result.Cancelled)
            {
                var ids = groupMembers != null ? groupMembers.ToList() : targets.Where(t => result.Done.Contains(t)).Select(t => t.Id).ToList();
                EnableAfter(ids.Where(id => _packages.Any(p => p.Id == id)).ToList(), allowed, groupName ?? ModUi.Names(installedNames), out var enabledText);
                if (result.Done.Count > 0)
                {
                    parts.Add(groupName != null ? Texts.T("cat.st.group.installed", groupName, result.Done.Count, plan.Count) : string.Empty);
                }

                parts.Add(enabledText);
            }
            else if (result.Done.Count > 0)
            {
                var key = updating.Count == result.Done.Count ? "cat.st.updated.done" : "cat.st.installed.done";
                parts.Add(Texts.T(key, ModUi.Names(installedNames)));
            }

            if (result.Cancelled)
            {
                parts.Add(Texts.T("cat.st.stopped", result.Done.Count, plan.Count));
            }

            parts.AddRange(refused);
            parts.AddRange(result.Failed.Select(f => Texts.T("cat.st.failed.one", f.Key.Name(Texts.Language), ModUi.ErrorText(f.Value))));
            if (backups.Count > 0)
            {
                parts.Add(Texts.T("cat.st.backup", string.Join(", ", backups)));
            }

            var done = string.Join(" ", parts.Where(x => x.Length > 0));
            Fill(SelectedTag());
            Changed?.Invoke(this, EventArgs.Empty);
            SetStatus(() => done);
        }

        /// <summary>Switches the given installed mods on with the game's rules; code of <paramref name="allowed"/> counts as
        /// allowed, other code asks once. Returns the status text.</summary>
        private void EnableAfter(IList<string> ids, IList<string> allowed, string what, out string text)
        {
            ScanLocal();
            var plan = ModGroups.PlanToggle(ids, true, _packages, _selection);
            var name = new Func<string, string>(id => _packages.FirstOrDefault(p => p.Id == id)?.Manifest.DisplayName ?? id);
            var ask = plan.NeedConsent.Where(id => !allowed.Contains(id)).ToList();
            if (ask.Count > 0 &&
                !ConfirmForm.Ask(Owner, Texts.T("mods.c.title"), Texts.T("group.c.text", string.Join(", ", ask.Select(name))), Texts.T("mods.c.allow"), warning: true))
            {
                text = Texts.T("cat.st.notenabled");
                return;
            }

            var consented = plan.NeedConsent.Select(id => _packages.First(p => p.Id == id)).ToList();
            var skipped = plan.Skipped.Select(kv => kv.Value.Length > 0 ? Texts.T("group.skip.conflict", name(kv.Key), name(kv.Value)) : Texts.T("group.skip.broken", name(kv.Key))).ToList();
            if (plan.Members.Count == 0 && plan.Outside.Count == 0)
            {
                text = string.Join(" ", new[] { Texts.T("cat.st.enabled.none", what) }.Concat(skipped));
                return;
            }

            if (!TrySave(ModGroups.Apply(_selection, plan, consented)))
            {
                text = Texts.T("cat.st.notenabled");
                return;
            }

            ScanLocal();
            var also = plan.Outside.Count == 0 ? string.Empty : " " + Texts.T("group.alsoon", string.Join(", ", plan.Outside.Select(name)));
            text = Texts.T("cat.st.enabled", ModUi.Names(plan.Members.Select(name))) + also + (skipped.Count > 0 ? " " + string.Join(" ", skipped) : string.Empty);
        }

        private bool TrySave(ModSelection selection)
        {
            try
            {
                _service.Mods.SaveSelection(selection);
                _selection = selection;
                return true;
            }
            catch (Exception ex) when (ex is ModOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void RemoveSelected()
        {
            if (_busy)
            {
                return;
            }

            if (_running)
            {
                SetStatus(() => Texts.T("st.blocked.running"));
                return;
            }

            var group = SelectedGroup();
            var entries = group != null
                ? GroupEntries(group).Where(x => StateOf(x) == CatalogState.Installed || StateOf(x) == CatalogState.UpdateAvailable).ToList()
                : Selected() is CatalogEntry one ? new List<CatalogEntry> { one } : new List<CatalogEntry>();
            if (entries.Count == 0)
            {
                return;
            }

            var ids = entries.Select(x => x.Id).ToList();
            var dependents = ids.SelectMany(id => ModRules.ToDisable(id, _packages, _selection)).Where(x => !ids.Contains(x)).Distinct().ToList();
            var depNames = string.Join(", ", dependents.Select(id => _packages.FirstOrDefault(p => p.Id == id)?.Manifest.DisplayName ?? id));
            string question;
            if (group != null)
            {
                var info = _catalog?.FindGroup(group) ?? ModGroupInfo.FromFolder(group);
                question = Texts.T("cat.confirm.remove.group", info.Name(Texts.Language), entries.Count, ModUi.Names(entries.Select(x => x.Name(Texts.Language)), 12));
                if (dependents.Count > 0)
                {
                    question += " " + Texts.T("cat.confirm.remove.also", depNames);
                }
            }
            else
            {
                question = dependents.Count > 0 ? Texts.T("cat.confirm.remove.deps", entries[0].Name(Texts.Language), depNames) : Texts.T("cat.confirm.remove", entries[0].Name(Texts.Language));
            }

            if (!ConfirmForm.Ask(Owner, Texts.T("mods.confirm.title"), question, group != null ? Texts.T("cat.btn.group.remove", entries.Count) : Texts.T("cat.btn.remove"), warning: true))
            {
                return;
            }

            var removed = new List<string>();
            var off = new List<string>();
            var failed = new List<string>();
            foreach (var e in entries)
            {
                try
                {
                    off.AddRange(_service.Mods.Remove(e.Id).Where(x => !ids.Contains(x) && !off.Contains(x)));
                    removed.Add(e.Name(Texts.Language));
                }
                catch (Exception ex) when (ex is ModOperationException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    failed.Add(Texts.T("cat.st.removefailed.one", e.Name(Texts.Language), ModUi.ErrorText(ex)));
                }
            }

            var offNames = string.Join(", ", off.Select(id => _packages.FirstOrDefault(p => p.Id == id)?.Manifest.DisplayName ?? id));
            var parts = new List<string>();
            if (removed.Count > 0)
            {
                parts.Add(off.Count > 0 ? Texts.T("cat.st.removed.deps", ModUi.Names(removed), offNames) : Texts.T("cat.st.removed.done", ModUi.Names(removed)));
            }

            parts.AddRange(failed);
            var done = string.Join(" ", parts);
            ScanLocal();
            Fill(SelectedTag());
            Changed?.Invoke(this, EventArgs.Empty);
            SetStatus(() => done);
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
            _status.ForeColor = Theme.Dim;
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

    /// <summary>A question in the launcher's look: the text, a confirm button and Cancel.</summary>
    public sealed class ConfirmForm : Form
    {
        private static int Pad => Theme.S(20);

        private ConfirmForm(string title, string text, string confirm, bool warning)
        {
            Text = title;
            Icon = Theme.AppIcon ?? Icon;
            BackColor = MainForm.Background;
            ForeColor = MainForm.BodyColor;
            Font = Theme.Font(10f);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Theme.DarkTitleBar(this);

            var width = Theme.S(460);
            var label = new Label
            {
                Text = text,
                AutoSize = false,
                ForeColor = warning ? Theme.Amber : Theme.Bright,
                BackColor = Color.Transparent,
                UseMnemonic = false
            };
            var size = TextRenderer.MeasureText(text, Font, new Size(width - (Pad * 2), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            label.Bounds = new Rectangle(Pad, Pad, width - (Pad * 2), size.Height + 4);
            var ok = MainForm.MakeButton(confirm, warning ? PipButtonKind.Danger : PipButtonKind.Primary, 9.75f, FontStyle.Bold);
            ok.DialogResult = DialogResult.OK;
            var cancel = MainForm.MakeButton(Texts.T("btn.cancel"), PipButtonKind.Normal, 9.75f, FontStyle.Regular);
            cancel.DialogResult = DialogResult.Cancel;
            var buttonHeight = Theme.S(34);
            var okWidth = Math.Max(Theme.S(130), TextRenderer.MeasureText(confirm, ok.Font).Width + Theme.S(32));
            var cancelWidth = Math.Max(Theme.S(110), TextRenderer.MeasureText(cancel.Text, cancel.Font).Width + Theme.S(32));
            var y = label.Bottom + Theme.S(16);
            cancel.Bounds = new Rectangle(width - Pad - cancelWidth, y, cancelWidth, buttonHeight);
            ok.Bounds = new Rectangle(cancel.Left - Theme.S(10) - okWidth, y, okWidth, buttonHeight);
            ClientSize = new Size(width, y + buttonHeight + Pad);
            Controls.AddRange(new Control[] { label, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (s, e) => cancel.Focus();
        }

        protected override void OnPaintBackground(PaintEventArgs e) => Theme.PaintBackground(e.Graphics, ClientRectangle, BackColor, Point.Empty);

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
