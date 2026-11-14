using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FOEReLauncher
{
    /// <summary>The whole launcher window; laid out in code, no designer file.</summary>
    public sealed class MainForm : Form
    {
        private enum ActionKind
        {
            None,
            Retry,
            Install,
            Update,
            Play
        }

        // The game's PipBuck palette (Theme).
        internal static readonly Color Background = Theme.Background;
        internal static readonly Color TitleColor = Theme.Bright;
        internal static readonly Color BodyColor = Theme.Body;
        internal static readonly Color StatusColor = Theme.Dim;
        internal static readonly Color VersionColor = Theme.Faint;
        internal static readonly Color Accent = Theme.Bright;

        private static int Pad => Theme.S(20);
        private static int Gap => Theme.S(8);
        private static int ActionHeight => Theme.S(50);
        private static int SmallButtonHeight => Theme.S(32);
        private static int ProgressHeight => Theme.S(12);
        private static int TabStripHeight => Theme.S(32);

        /// <summary>Bullets shown in the "what's new" teaser; the full list lives in the dialog.</summary>
        private const int TeaserNoteCount = 3;

        /// <summary>How often foreign game launches are noticed.</summary>
        private const int GameWatchIntervalMs = 2000;

        private readonly LauncherService _service;
        private readonly string[] _args;

        private readonly Control _titleLabel;
        private readonly Button _languageButton;
        private readonly TabStrip _tabs;
        private readonly Panel _gamePage;
        private readonly ModsPage _modsPage;
        private readonly CataloguePage _cataloguePage;
        private readonly Label _installedLabel;
        private readonly Label _availableLabel;
        private readonly Label _whatsNewTitle;
        private readonly Label _whatsNewBody;
        private readonly Button _actionButton;
        private readonly Button _gameUpdateButton;
        private readonly Button _launcherUpdateButton;
        private readonly Button _checkUpdatesButton;
        private readonly Button _verifyButton;
        private readonly Button _folderButton;
        private readonly Button _patchNotesButton;
        private readonly Label _channelLabel;
        private readonly ComboBox _channelCombo;
        private readonly FrameBox _channelBox;
        private readonly ProgressStripe _progress;
        private readonly Label _statusLabel;
        private readonly Label _versionLabel;
        private readonly System.Windows.Forms.Timer _gameWatchTimer;

        private Manifest? _latest;
        private string? _checkError;
        private LauncherVersionInfo? _launcherUpdate;
        private IList<PatchNote> _patchNotes = new List<PatchNote>();
        private readonly List<string> _whatsNewLines = new List<string>();

        /// <summary>The installed version the notes were last split at; null before the first split.</summary>
        private string? _notesInstalled;

        private ActionKind _action = ActionKind.None;
        private string _actionKey = "btn.checking";
        private bool _actionEnabled;
        private bool _busy;
        private bool _gameUpdateShown; // not Button.Visible: that reads false while the Game tab is hidden

        /// <summary>The last status line as a key and its arguments, so a language switch can redraw it.</summary>
        private Func<string> _status = () => string.Empty;

        private Process? _gameProcess;
        private bool _gameRunning;

        /// <summary>True while the channel picker is being filled in code, so it raises no reload.</summary>
        private bool _settingChannel;

        public MainForm(LauncherService service, string[] args)
        {
            _service = service;
            _args = args;
            Texts.Language = _service.Language.Length > 0 ? _service.Language : Texts.SystemLanguage();

            BackColor = Background;
            ForeColor = BodyColor;
            Font = Theme.Font(9.75f);
            AutoScaleMode = AutoScaleMode.None; // sizes go through Theme.S, fonts follow the DPI by themselves
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            ClientSize = FitToScreen(new Size(Theme.S(640), Theme.S(660)));
            MinimumSize = new Size(Width - ClientSize.Width + Math.Min(ClientSize.Width, Theme.S(540)), Height - ClientSize.Height + Math.Min(ClientSize.Height, Theme.S(560)));
            DoubleBuffered = true;
            Theme.DarkTitleBar(this);
            KeyPreview = true;

            _titleLabel = new GlowLabel { Font = Theme.TitleFont(20f), ForeColor = TitleColor };

            // Never takes the keyboard focus: when the channel picker is disabled for a refresh, Windows moves the focus to
            // the next control, and a following Space or Enter must not switch the language.
            _languageButton = MakeButton(string.Empty, PipButtonKind.Normal, 9f, FontStyle.Bold, selectable: false);
            _languageButton.Click += OnLanguageClick;

            _tabs = new TabStrip { BackColor = Background, AccessibleName = "tabs" };
            _gamePage = new ThemedPanel();
            _gamePage.Paint += OnGamePagePaint;
            _modsPage = new ModsPage(_service);
            _cataloguePage = new CataloguePage(_service);
            _tabs.AddTab("tab.game", _gamePage);
            _tabs.AddTab("tab.mods", _modsPage);
            _tabs.AddTab("tab.catalogue", _cataloguePage);
            _tabs.SelectedChanged += OnTabChanged;
            _modsPage.Changed += (s, e) => _cataloguePage.LocalChanged();
            _cataloguePage.Changed += (s, e) => _modsPage.Reload();

            _installedLabel = MakeLabel(string.Empty, 11.25f, FontStyle.Regular, BodyColor);
            _availableLabel = MakeLabel(string.Empty, 11.25f, FontStyle.Regular, BodyColor);

            _whatsNewTitle = MakeLabel(string.Empty, 9.75f, FontStyle.Bold, Theme.Amber);
            _whatsNewTitle.AutoSize = false;
            _whatsNewTitle.Visible = false;

            _whatsNewBody = MakeLabel(string.Empty, 9f, FontStyle.Regular, BodyColor);
            _whatsNewBody.AutoSize = false;
            _whatsNewBody.Visible = false;

            _actionButton = MakeButton(string.Empty, PipButtonKind.Primary, 14f, FontStyle.Bold);
            _actionButton.Enabled = false;
            _actionButton.Click += OnActionClick;

            // An update never takes the Play button away: the installed version stays playable.
            _gameUpdateButton = MakeButton(string.Empty, PipButtonKind.Normal, 9.75f, FontStyle.Regular);
            _gameUpdateButton.Visible = false;
            _gameUpdateButton.Click += OnGameUpdateClick;

            _launcherUpdateButton = MakeButton(string.Empty, PipButtonKind.Normal, 9.75f, FontStyle.Regular);
            _launcherUpdateButton.Visible = false;
            _launcherUpdateButton.Click += OnLauncherUpdateClick;

            _checkUpdatesButton = MakeButton(string.Empty, PipButtonKind.Normal, 9.75f, FontStyle.Regular);
            _checkUpdatesButton.Click += OnCheckUpdatesClick;

            _verifyButton = MakeButton(string.Empty, PipButtonKind.Normal, 9.75f, FontStyle.Regular);
            _verifyButton.Enabled = false;
            _verifyButton.Click += OnVerifyClick;

            _folderButton = MakeButton(string.Empty, PipButtonKind.Normal, 9.75f, FontStyle.Regular);
            _folderButton.Click += OnFolderClick;

            _patchNotesButton = MakeButton(string.Empty, PipButtonKind.Normal, 9.75f, FontStyle.Regular);
            _patchNotesButton.Enabled = false;
            _patchNotesButton.Click += OnPatchNotesClick;

            _channelLabel = MakeLabel(string.Empty, 9.75f, FontStyle.Regular, StatusColor);
            _channelLabel.AutoSize = false;
            _channelLabel.TextAlign = ContentAlignment.MiddleLeft;

            _channelCombo = MakeChannelCombo();
            _channelCombo.SelectedIndexChanged += OnChannelChanged;

            _progress = new ProgressStripe { Visible = false };

            _statusLabel = MakeLabel(string.Empty, 9.75f, FontStyle.Regular, StatusColor);
            _statusLabel.AutoSize = false;

            _versionLabel = MakeLabel(string.Empty, 8.25f, FontStyle.Regular, VersionColor);
            _versionLabel.AutoSize = false;
            _versionLabel.TextAlign = ContentAlignment.MiddleRight;

            _gameWatchTimer = new System.Windows.Forms.Timer { Interval = GameWatchIntervalMs };
            _gameWatchTimer.Tick += (s, e) => PollGameRunning();

            _gamePage.Controls.AddRange(new Control[]
            {
                _installedLabel, _availableLabel, _whatsNewTitle, _whatsNewBody,
                _actionButton, _gameUpdateButton, _launcherUpdateButton, _checkUpdatesButton, _verifyButton, _folderButton,
                _patchNotesButton, _channelLabel, _channelCombo, _progress, _statusLabel
            });
            // The picker sits in a box that hides its system-coloured border.
            _gamePage.Controls.Remove(_channelCombo);
            _channelBox = new FrameBox(_channelCombo, 0) { ClipChild = true };
            Theme.DarkCombo(_channelCombo);
            _gamePage.Controls.Add(_channelBox);
            Controls.AddRange(new Control[] { _titleLabel, _languageButton, _tabs, _gamePage, _modsPage, _cataloguePage, _versionLabel });

            ApplyTexts();
            Shown += OnShown;
            FormClosed += OnFormClosed;
            Resize += (s, e) => LayoutUi();
            LayoutUi();
        }

        // ------------------------------------------------------------- controls

        private static Color FromHex(string hex) => ColorTranslator.FromHtml(hex);

        private static Label MakeLabel(string text, float size, FontStyle style, Color color) => new Label
        {
            Text = text,
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = color,
            Font = Theme.Font(size, style),
            UseMnemonic = false
        };

        internal static PipButton MakeButton(string text, PipButtonKind kind, float size, FontStyle style, bool selectable = true)
        {
            var button = selectable ? new PipButton() : new NoFocusButton();
            button.Kind = kind;
            button.Text = text;
            button.Font = Theme.Font(size, style);
            button.TabStop = selectable;
            return button;
        }

        /// <summary>The wanted client size, made smaller when the screen's work area cannot hold it.</summary>
        private Size FitToScreen(Size wanted)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            var chrome = Size - ClientSize;
            return new Size(Math.Min(wanted.Width, area.Width - chrome.Width), Math.Min(wanted.Height, area.Height - chrome.Height));
        }

        /// <summary>
        /// The channel picker. A flat combo box still paints its list rows in system colours, so the
        /// items are drawn by hand to keep the dropdown as dark as the rest of the window.
        /// </summary>
        private ComboBox MakeChannelCombo()
        {
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Fill,
                ForeColor = Theme.Bright,
                Font = Theme.Font(9.75f),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = Theme.S(22),
                IntegralHeight = false,
                TabStop = true
            };
            combo.Items.Add(string.Empty);
            combo.Items.Add(string.Empty);
            combo.DrawItem += OnChannelDrawItem;
            return combo;
        }

        private void OnChannelDrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0)
            {
                return;
            }

            var selected = (e.State & DrawItemState.Selected) != 0;
            var edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            using (var back = new SolidBrush(selected && !edit ? Theme.FillSelected : Theme.Fill))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
            }

            if (selected && !edit)
            {
                Theme.DrawFrame(e.Graphics, e.Bounds, Theme.Bright);
            }

            TextRenderer.DrawText(
                e.Graphics,
                Convert.ToString(_channelCombo.Items[e.Index]) ?? string.Empty,
                _channelCombo.Font,
                Rectangle.Inflate(e.Bounds, -Theme.S(5), 0),
                Theme.Bright,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        /// <summary>Puts every text of the window in the current language.</summary>
        private void ApplyTexts()
        {
            Text = Texts.T("window");
            _titleLabel.Text = Texts.T("title");
            _languageButton.Text = Texts.T("lang.switch");
            _tabs.Invalidate();
            _checkUpdatesButton.Text = Texts.T("btn.check");
            _verifyButton.Text = Texts.T("btn.verify");
            _folderButton.Text = Texts.T("btn.folder");
            _patchNotesButton.Text = Texts.T("btn.notes");
            _channelLabel.Text = Texts.T("channel");
            _settingChannel = true;
            _channelCombo.Items[0] = Texts.T("channel.prod");
            _channelCombo.Items[1] = Texts.T("channel.test");
            _channelCombo.SelectedIndex = _service.Channel == UpdateChannel.Test ? 1 : 0;
            _settingChannel = false;
            _versionLabel.Text = Texts.T("launcher.version", LauncherService.CurrentLauncherVersion);
            if (_launcherUpdate != null)
            {
                _launcherUpdateButton.Text = Texts.T("btn.launcher", _launcherUpdate.Version);
            }

            if (_latest != null)
            {
                _gameUpdateButton.Text = Texts.T("btn.gameupdate", _latest.Version);
            }

            ApplyLocalLabels();
            ApplyActionButton();
            _statusLabel.Text = _status();
            _modsPage.ApplyTexts();
            _cataloguePage.ApplyTexts();
            ApplyPatchNotes();
            LayoutUi();
        }

        /// <summary>
        /// Manual layout as in the template: header and the "what's new" teaser at the top of the page,
        /// the button stack grows upwards from the status line, so hiding the progress bar or the
        /// self-update button just collapses the gap.
        /// </summary>
        private void LayoutUi()
        {
            var width = ClientSize.Width - (Pad * 2);
            if (width < 100)
            {
                return;
            }

            var titleSize = TextRenderer.MeasureText(_titleLabel.Text, _titleLabel.Font);
            _titleLabel.Bounds = new Rectangle(Pad - Theme.S(2), Theme.S(12), titleSize.Width + Theme.S(8), titleSize.Height + Theme.S(6));
            var languageSize = new Size(Theme.S(48), Theme.S(28));
            _languageButton.Bounds = new Rectangle(ClientSize.Width - Pad - languageSize.Width, _titleLabel.Top + ((_titleLabel.Height - languageSize.Height) / 2), languageSize.Width, languageSize.Height);
            _tabs.Bounds = new Rectangle(Pad, _titleLabel.Bottom + Theme.S(8), width, TabStripHeight);

            var versionHeight = _versionLabel.Font.Height + 2;
            _versionLabel.Bounds = new Rectangle(Pad, ClientSize.Height - Theme.S(8) - versionHeight, width, versionHeight);

            var pageTop = _tabs.Bottom + Theme.S(10);
            _tabs.LayoutPages(new Rectangle(Pad, pageTop, width, _versionLabel.Top - Theme.S(4) - pageTop));
            LayoutGamePage(width);
            Invalidate();
        }

        /// <summary>The frame of the "what's new" block on the Game tab; empty when there is no block.</summary>
        private Rectangle _whatsNewFrame;

        private void LayoutGamePage(int width)
        {
            var height = _gamePage.ClientSize.Height;
            _installedLabel.Location = new Point(0, 0);
            _availableLabel.Location = new Point(0, _installedLabel.Bottom + Theme.S(2));

            var statusHeight = (_statusLabel.Font.Height * 3) + 2;
            _statusLabel.Bounds = new Rectangle(0, height - statusHeight, width, statusHeight);

            var stack = new List<Control> { _actionButton };
            if (_gameUpdateShown)
            {
                stack.Add(_gameUpdateButton);
            }

            if (_launcherUpdateButton.Visible)
            {
                stack.Add(_launcherUpdateButton);
            }

            stack.Add(_checkUpdatesButton);
            stack.Add(_verifyButton);
            stack.Add(_patchNotesButton);
            stack.Add(_channelBox);
            if (_progress.Visible)
            {
                stack.Add(_progress);
            }

            var total = Gap * (stack.Count - 1);
            foreach (var control in stack)
            {
                total += HeightOf(control);
            }

            var y = _statusLabel.Top - Theme.S(8) - total;
            var stackTop = y;
            foreach (var control in stack)
            {
                var h = HeightOf(control);
                if (ReferenceEquals(control, _channelBox))
                {
                    // The caption shares the row with the picker instead of taking one of its own.
                    var captionWidth = TextRenderer.MeasureText(_channelLabel.Text, _channelLabel.Font).Width + Theme.S(10);
                    _channelLabel.Bounds = new Rectangle(0, y, captionWidth, h);
                    _channelBox.Bounds = new Rectangle(captionWidth, y, width - captionWidth, h);
                    _channelBox.LayoutChild();
                }
                else if (ReferenceEquals(control, _verifyButton))
                {
                    // "Verify files" and "Open game folder" share one row.
                    var half = (width - Gap) / 2;
                    _verifyButton.Bounds = new Rectangle(0, y, half, h);
                    _folderButton.Bounds = new Rectangle(half + Gap, y, width - half - Gap, h);
                }
                else
                {
                    control.Bounds = new Rectangle(0, y, width, h);
                }

                y += h + Gap;
            }

            // "What's new" fills the space between the versions and the buttons, in a thin frame.
            _whatsNewFrame = Rectangle.Empty;
            if (_whatsNewTitle.Visible)
            {
                var top = _availableLabel.Bottom + Theme.S(12);
                var frame = new Rectangle(0, top, width, Math.Max(0, stackTop - Theme.S(12) - top));
                var inner = Rectangle.Inflate(frame, -Theme.S(12), -Theme.S(10));
                var titleHeight = _whatsNewTitle.Font.Height + 2;
                var lineHeight = _whatsNewBody.Font.Height;
                if (inner.Height >= titleHeight + lineHeight)
                {
                    _whatsNewFrame = frame;
                    _whatsNewTitle.Bounds = new Rectangle(inner.X, inner.Y, inner.Width, titleHeight);

                    // One bullet per row: long lines are cut with an ellipsis and read in full in the dialog.
                    var rows = Math.Max(1, (inner.Bottom - _whatsNewTitle.Bottom - Theme.S(4)) / lineHeight);
                    var shown = new List<string>();
                    foreach (var line in _whatsNewLines)
                    {
                        if (shown.Count < rows)
                        {
                            shown.Add(Ellipsize(line, _whatsNewBody.Font, inner.Width - 2));
                        }
                    }

                    _whatsNewBody.Text = string.Join(Environment.NewLine, shown);
                    _whatsNewBody.Bounds = new Rectangle(inner.X, _whatsNewTitle.Bottom + Theme.S(4), inner.Width, (lineHeight * Math.Max(1, shown.Count)) + 2);
                }
                else
                {
                    _whatsNewTitle.Bounds = Rectangle.Empty;
                    _whatsNewBody.Bounds = Rectangle.Empty;
                }
            }

            _gamePage.Invalidate();
        }

        private void OnGamePagePaint(object? sender, PaintEventArgs e)
        {
            if (!_whatsNewFrame.IsEmpty)
            {
                using (var back = new SolidBrush(Color.FromArgb(150, Theme.Panel)))
                {
                    e.Graphics.FillRectangle(back, _whatsNewFrame);
                }

                Theme.DrawFrame(e.Graphics, _whatsNewFrame, Theme.Frame);
            }
        }

        private static string Ellipsize(string text, Font font, int width)
        {
            const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            var unlimited = new Size(int.MaxValue, int.MaxValue);
            if (width <= 0 || TextRenderer.MeasureText(text, font, unlimited, Flags).Width <= width)
            {
                return text;
            }

            for (var length = text.Length - 1; length > 0; length--)
            {
                var candidate = text.Substring(0, length).TrimEnd() + "…";
                if (TextRenderer.MeasureText(candidate, font, unlimited, Flags).Width <= width)
                {
                    return candidate;
                }
            }

            return text;
        }

        private int HeightOf(Control control)
        {
            if (ReferenceEquals(control, _actionButton))
            {
                return ActionHeight;
            }

            if (ReferenceEquals(control, _channelBox))
            {
                return _channelBox.ChildHeight;
            }

            return ReferenceEquals(control, _progress) ? ProgressHeight : SmallButtonHeight;
        }

        /// <summary>Ctrl+Tab and Ctrl+Shift+Tab walk the tabs, Ctrl+1..3 open one, as in other programs.</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.PageDown))
            {
                _tabs.Select((_tabs.SelectedIndex + 1) % _tabs.Count);
                return true;
            }

            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab) || keyData == (Keys.Control | Keys.PageUp))
            {
                _tabs.Select((_tabs.SelectedIndex + _tabs.Count - 1) % _tabs.Count);
                return true;
            }

            if ((keyData & Keys.Modifiers) == Keys.Control)
            {
                var key = keyData & Keys.KeyCode;
                if (key >= Keys.D1 && key < Keys.D1 + _tabs.Count)
                {
                    _tabs.Select(key - Keys.D1);
                    return true;
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaintBackground(PaintEventArgs e) => Theme.PaintBackground(e.Graphics, ClientRectangle, Background, Point.Empty);

        // ------------------------------------------------------------ behaviour

        private async void OnTabChanged(object? sender, EventArgs e)
        {
            if (_tabs.SelectedIndex == 1)
            {
                _modsPage.Reload();
            }
            else if (_tabs.SelectedIndex == 2)
            {
                await _cataloguePage.EnsureLoadedAsync();
            }
        }

        private async void OnShown(object? sender, EventArgs e)
        {
            _modsPage.Reload();
            PollGameRunning();
            _gameWatchTimer.Start();

            await RunBusyAsync(async () =>
            {
                await RefreshAsync();
                await CheckLauncherUpdateAsync();
                await LoadPatchNotesAsync();
            });
        }

        private void OnFormClosed(object? sender, FormClosedEventArgs e)
        {
            _gameWatchTimer.Stop();
            DetachGameProcess();
        }

        private void OnLanguageClick(object? sender, EventArgs e)
        {
            Texts.Language = Texts.Language == "ru" ? "en" : "ru";
            _service.Language = Texts.Language;
            _service.SaveSettings();
            ApplyTexts();
        }

        private async Task RefreshAsync()
        {
            _checkError = null;
            _latest = null;
            ApplyLocalLabels(checking: true);
            SetStatus("st.checking");
            SetAction(ActionKind.None, "btn.checking", enabled: false);

            try
            {
                _latest = await _service.CheckAsync(forceManifest: false);
            }
            catch (FileNotFoundException)
            {
                // The channel folder or its manifest is not on the site (no build published there yet).
                _checkError = "";
            }
            catch (Exception ex)
            {
                _checkError = ex.Message;
            }

            ApplyLocalState();
        }

        private void ApplyLocalLabels(bool checking = false)
        {
            var installed = _service.GetInstalled();
            _installedLabel.Text = Texts.T("installed", installed.Version.Length > 0 && _service.IsGameInstalled() ? installed.Version : Texts.T("none"));
            _availableLabel.Text = Texts.T("available", checking ? Texts.T("checking") : (_latest?.Version ?? Texts.T("dash")));

            // An install, update or removal moves the line between new and installed notes.
            if (_patchNotes.Count > 0 && !string.Equals(_notesInstalled, InstalledVersionForNotes(), StringComparison.OrdinalIgnoreCase))
            {
                ApplyPatchNotes();
            }
        }

        /// <summary>The version the change history is cut at; empty while the game is not installed.</summary>
        private string InstalledVersionForNotes() => _service.IsGameInstalled() ? _service.GetInstalled().Version : string.Empty;

        /// <summary>
        /// Derives the action button and the status line from what is on disk plus the last known remote
        /// version. Runs without touching the network, so the game-running watchdog can call it.
        /// </summary>
        private void ApplyLocalState()
        {
            ApplyLocalLabels();
            var installed = _service.GetInstalled();
            var isInstalled = _service.IsGameInstalled();
            _verifyButton.Enabled = !_busy && _latest != null && isInstalled;

            if (_latest == null)
            {
                SetGameUpdateVisible(false);
                SetAction(isInstalled ? ActionKind.Play : ActionKind.Retry, isInstalled ? "btn.play" : "btn.retry");
                if (_checkError != null)
                {
                    var error = _checkError;
                    SetStatus(() => error.Length == 0 ? Texts.T("st.nochannel") : Texts.T("st.checkfail", error));
                }

                return;
            }

            var latest = _latest;
            if (!isInstalled)
            {
                SetGameUpdateVisible(false);
                SetAction(ActionKind.Install, "btn.install");
                SetStatus(() => Texts.T("st.notinstalled", LauncherService.FormatSize(SumPacks(latest), Texts.Language)));
            }
            // Any difference from the channel's version is worth an update, not just a higher number:
            // switching from testing back to release has to bring the stable build back.
            else if (!string.Equals(installed.Version, latest.Version, StringComparison.OrdinalIgnoreCase))
            {
                _gameUpdateButton.Text = Texts.T("btn.gameupdate", latest.Version);
                SetGameUpdateVisible(true);
                SetAction(ActionKind.Play, "btn.play");
                SetStatus(_gameRunning ? (Func<string>)(() => Texts.T("st.differs.running")) : () => Texts.T("st.differs", latest.Version));
            }
            else
            {
                SetGameUpdateVisible(false);
                SetAction(ActionKind.Play, "btn.play");
                SetStatus(_gameRunning ? "st.blocked.running" : "st.latest");
            }
        }

        private static long SumPacks(Manifest manifest)
        {
            long total = 0;
            foreach (var p in manifest.Packs)
            {
                total += p.Size;
            }

            return total;
        }

        private async Task CheckLauncherUpdateAsync()
        {
            try
            {
                var info = await _service.FetchLauncherVersionAsync();
                if (LauncherService.IsNewer(info.Version, LauncherService.CurrentLauncherVersion))
                {
                    _launcherUpdate = info;
                    _launcherUpdateButton.Text = Texts.T("btn.launcher", info.Version);
                    SetLauncherUpdateVisible(true);
                    return;
                }
            }
            catch (Exception)
            {
                // Self-update check is best effort: an offline machine must not break the window.
            }

            _launcherUpdate = null;
            SetLauncherUpdateVisible(false);
        }

        private async void OnCheckUpdatesClick(object? sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            await RunBusyAsync(async () =>
            {
                await RefreshAsync();
                await CheckLauncherUpdateAsync();
                await LoadPatchNotesAsync();
            });
        }

        private async void OnChannelChanged(object? sender, EventArgs e)
        {
            if (_settingChannel)
            {
                return;
            }

            var channel = _channelCombo.SelectedIndex == 1 ? UpdateChannel.Test : UpdateChannel.Prod;
            if (channel == _service.Channel)
            {
                return;
            }

            _service.Channel = channel;
            _service.SaveSettings();
            if (_busy)
            {
                return;
            }

            await RunBusyAsync(async () =>
            {
                await RefreshAsync();
                await LoadPatchNotesAsync();
            });
        }

        private async void OnActionClick(object? sender, EventArgs e)
        {
            if (_gameRunning)
            {
                SetStatus("st.blocked.running");
                return;
            }

            if (_busy)
            {
                return;
            }

            switch (_action)
            {
                case ActionKind.Retry:
                    await RunBusyAsync(async () =>
                    {
                        await RefreshAsync();
                        await LoadPatchNotesAsync();
                    });
                    break;

                case ActionKind.Install:
                case ActionKind.Update:
                    await RunBusyAsync(() => InstallAsync(verify: false));
                    break;

                case ActionKind.Play:
                    try
                    {
                        AttachGameProcess(_service.Play());
                        SetGameRunning(true);
                    }
                    catch (Exception ex)
                    {
                        SetStatus(() => Texts.T("st.playfail", ex.Message));
                    }

                    break;
            }
        }

        private async void OnGameUpdateClick(object? sender, EventArgs e)
        {
            if (_gameRunning)
            {
                SetStatus("st.blocked.running");
                return;
            }

            if (_busy || _latest == null)
            {
                return;
            }

            await RunBusyAsync(() => InstallAsync(verify: false));
        }

        private async void OnVerifyClick(object? sender, EventArgs e)
        {
            if (_gameRunning)
            {
                SetStatus("st.blocked.running");
                return;
            }

            if (_busy || _latest == null)
            {
                return;
            }

            await RunBusyAsync(() => InstallAsync(verify: true));
        }

        private void OnFolderClick(object? sender, EventArgs e)
        {
            var dir = Directory.Exists(_service.GameDir) ? _service.GameDir : null;
            if (dir == null)
            {
                SetStatus("st.nofolder");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus(() => ex.Message);
            }
        }

        private async Task InstallAsync(bool verify)
        {
            if (_latest == null)
            {
                await RefreshAsync();
                return;
            }

            if (verify)
            {
                // Verify compares with the manifest on the site, never with the cached copy.
                try
                {
                    _latest = await _service.CheckAsync(forceManifest: true);
                }
                catch (Exception ex)
                {
                    var error = ex.Message;
                    SetStatus(() => Texts.T("st.checkfail", error));
                    return;
                }
            }

            var manifest = _latest;
            SetProgressVisible(true);
            _progress.Value = 0;
            var progress = new Progress<UpdateProgress>(OnUpdateProgress);
            Func<string>? summary = null;

            try
            {
                var result = await _service.InstallOrUpdateAsync(manifest, verify, progress, CancellationToken.None);
                summary = () => Describe(result);
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                summary = () => Texts.T("st.failed", message);
            }
            finally
            {
                SetProgressVisible(false);
            }

            _latest = manifest;
            ApplyLocalState();
            SetStatus(summary);
            _modsPage.Reload();
            _cataloguePage.LocalChanged();
        }

        private static string Describe(UpdateResult result)
        {
            string text;
            if (result.InstalledFiles == 0 && result.RemovedFiles == 0)
            {
                text = Texts.T("st.uptodate");
            }
            else
            {
                text = Texts.T(
                    "st.done",
                    result.Version,
                    result.InstalledFiles,
                    result.RemovedFiles,
                    LauncherService.FormatSize(result.NetworkBytes, Texts.Language),
                    Math.Round(result.Elapsed.TotalSeconds));
            }

            if (result.BackedUp.Count > 0)
            {
                text += Texts.T("st.backup", string.Join(", ", result.BackedUp));
            }

            return text;
        }

        private void OnUpdateProgress(UpdateProgress p)
        {
            var phase = p.Phase;
            var done = p.Done;
            var total = p.Total;
            var speed = p.Speed;
            _progress.Value = p.Fraction * 100;
            switch (phase)
            {
                case UpdatePhase.Checking:
                    SetStatus(() => Texts.T("st.scan", done, total));
                    break;
                case UpdatePhase.Downloading:
                    SetStatus(() => Texts.T(
                        "st.download",
                        LauncherService.FormatSize(done, Texts.Language),
                        LauncherService.FormatSize(total, Texts.Language),
                        LauncherService.FormatSize((long)speed, Texts.Language)));
                    break;
                case UpdatePhase.Extracting:
                    SetStatus(() => Texts.T("st.extract", done, total));
                    break;
                default:
                    SetStatus("st.apply");
                    break;
            }
        }

        private async void OnLauncherUpdateClick(object? sender, EventArgs e)
        {
            if (_busy || _launcherUpdate == null)
            {
                return;
            }

            var info = _launcherUpdate;
            await RunBusyAsync(async () =>
            {
                SetProgressVisible(true);
                _progress.Value = 0;
                SetStatus(() => Texts.T("st.launcher", info.Version));
                var progress = new Progress<double>(value => _progress.Value = value * 100);

                try
                {
                    var arguments = new StringBuilder();
                    foreach (var arg in _args)
                    {
                        arguments.Append('"').Append(arg.Replace("\"", string.Empty)).Append("\" ");
                    }

                    var script = await _service.PrepareSelfUpdateAsync(info, LauncherService.ProcessPath, arguments.ToString().TrimEnd(), progress, CancellationToken.None);
                    LauncherService.StartSwap(script, Process.GetCurrentProcess().Id);
                    SetStatus("st.launcher.restart");
                    Application.Exit();
                }
                catch (Exception ex)
                {
                    SetStatus(() => Texts.T("st.launcher.fail", ex.Message));
                }
                finally
                {
                    SetProgressVisible(false);
                }
            });
        }

        private async Task RunBusyAsync(Func<Task> work)
        {
            // Disabling the focused control moves the focus on; it comes back when the work ends.
            var focused = ActiveControl;
            _busy = true;
            _actionButton.Enabled = false;
            _gameUpdateButton.Enabled = false;
            _launcherUpdateButton.Enabled = false;
            _checkUpdatesButton.Enabled = false;
            _verifyButton.Enabled = false;
            _patchNotesButton.Enabled = false;
            _channelCombo.Enabled = false;
            try
            {
                await work();
            }
            finally
            {
                _busy = false;
                _launcherUpdateButton.Enabled = true;
                _checkUpdatesButton.Enabled = true;
                _verifyButton.Enabled = _latest != null && _service.IsGameInstalled() && !_gameRunning;
                _patchNotesButton.Enabled = _patchNotes.Count > 0;
                _channelCombo.Enabled = true;
                ApplyActionButton();
                if (focused != null && focused.CanFocus && !ReferenceEquals(ActiveControl, focused))
                {
                    focused.Focus();
                }
            }
        }

        private void SetAction(ActionKind kind, string key, bool enabled = true)
        {
            _action = kind;
            _actionKey = key;
            _actionEnabled = enabled;
            ApplyActionButton();
        }

        /// <summary>
        /// A running game owns the files under game/, so nothing may be installed or updated while it
        /// lives; the button says so instead of offering an action that would fail.
        /// </summary>
        private void ApplyActionButton()
        {
            if (_gameRunning)
            {
                _actionButton.Text = Texts.T("btn.running");
                _actionButton.Enabled = false;
                _gameUpdateButton.Enabled = false;
                _verifyButton.Enabled = false;
                return;
            }

            _actionButton.Text = Texts.T(_actionKey);
            _actionButton.Enabled = _actionEnabled && !_busy;
            _gameUpdateButton.Enabled = !_busy;
        }

        private void SetStatus(string key) => SetStatus(() => Texts.T(key));

        private void SetStatus(Func<string>? text)
        {
            _status = text ?? (() => string.Empty);
            _statusLabel.Text = _status();
        }

        private void SetProgressVisible(bool visible)
        {
            if (_progress.Visible == visible)
            {
                return;
            }

            _progress.Visible = visible;
            LayoutUi();
        }

        private void SetGameUpdateVisible(bool visible)
        {
            if (_gameUpdateShown == visible)
            {
                return;
            }

            _gameUpdateShown = visible;
            _gameUpdateButton.Visible = visible;
            LayoutUi();
        }

        private void SetLauncherUpdateVisible(bool visible)
        {
            if (_launcherUpdateButton.Visible == visible)
            {
                return;
            }

            _launcherUpdateButton.Visible = visible;
            LayoutUi();
        }

        // ---------------------------------------------------------- game running

        private void AttachGameProcess(Process process)
        {
            DetachGameProcess();
            _gameProcess = process;
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += OnGameExited;
            }
            catch (Exception)
            {
                // No exit event: the watchdog timer still notices within a couple of seconds.
            }
        }

        private void DetachGameProcess()
        {
            var process = _gameProcess;
            _gameProcess = null;
            if (process == null)
            {
                return;
            }

            try
            {
                process.Exited -= OnGameExited;
                process.Dispose();
            }
            catch (Exception)
            {
                // Nothing useful to do while releasing a handle.
            }
        }

        private void OnGameExited(object? sender, EventArgs e)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(PollGameRunning));
                }
            }
            catch (Exception)
            {
                // The form went away while the game was closing.
            }
        }

        private void PollGameRunning()
        {
            var running = false;
            var process = _gameProcess;
            if (process != null)
            {
                try
                {
                    running = !process.HasExited;
                }
                catch (Exception)
                {
                    running = false;
                }

                if (!running)
                {
                    DetachGameProcess();
                }
            }

            if (!running)
            {
                running = _service.IsGameRunning();
            }

            SetGameRunning(running);
        }

        private void SetGameRunning(bool running)
        {
            if (_gameRunning == running)
            {
                return;
            }

            _gameRunning = running;
            ApplyActionButton();
            _modsPage.SetGameRunning(running);
            _cataloguePage.SetGameRunning(running);

            // A download in flight owns the status line; do not overwrite its progress text.
            if (!_busy)
            {
                ApplyLocalState();
            }
        }

        // ------------------------------------------------------------ patch notes

        private async Task LoadPatchNotesAsync()
        {
            try
            {
                _patchNotes = await _service.FetchNotesAsync();
            }
            catch (Exception)
            {
                // The file is optional: without it the launcher shows no notes.
                _patchNotes = new List<PatchNote>();
            }

            ApplyPatchNotes();
        }

        private void ApplyPatchNotes()
        {
            var available = _patchNotes.Count > 0;
            _patchNotesButton.Enabled = available && !_busy;
            if (!available)
            {
                SetWhatsNewVisible(false);
                return;
            }

            var split = NotesSplit.Split(_patchNotes, InstalledVersionForNotes());
            _notesInstalled = split.Installed;
            var latest = _patchNotes[0];
            _whatsNewTitle.Text = Texts.T("whatsnew", latest.Version);
            _whatsNewLines.Clear();
            foreach (var note in latest.Lines(Texts.Language))
            {
                if (_whatsNewLines.Count >= TeaserNoteCount)
                {
                    break;
                }

                _whatsNewLines.Add("• " + note);
            }

            if (_whatsNewLines.Count == 0)
            {
                _whatsNewLines.Add("• " + Texts.T("notes.tech"));
            }

            // The teaser shows the newest version only; say when more versions wait between it and the installed one.
            var more = split.HasInstalled ? split.New.Count - 1 : 0;
            if (more > 0)
            {
                _whatsNewLines.Add(more == 1 ? Texts.T("notes.more1") : Texts.T("notes.more", more));
            }

            SetWhatsNewVisible(true);
        }

        private void SetWhatsNewVisible(bool visible)
        {
            _whatsNewTitle.Visible = visible;
            _whatsNewBody.Visible = visible;
            LayoutUi();
        }

        private void OnPatchNotesClick(object? sender, EventArgs e)
        {
            if (_patchNotes.Count == 0)
            {
                return;
            }

            using (var dialog = new PatchNotesForm(_patchNotes, InstalledVersionForNotes()))
            {
                dialog.ShowDialog(this);
            }
        }
    }

    /// <summary>
    /// The tab strip above the page area, drawn as the PipBuck's tabs: framed boxes, the selected one lit with a
    /// glowing caption. Each tab owns a page control; only the selected page is visible. It takes the keyboard
    /// focus: Left and Right (or Home and End) choose a tab.
    /// </summary>
    public sealed class TabStrip : Control
    {
        private readonly List<KeyValuePair<string, Control>> _tabs = new List<KeyValuePair<string, Control>>();
        private readonly List<Rectangle> _hit = new List<Rectangle>();
        private int _selected;
        private int _hover = -1;

        public TabStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Font = Theme.Font(10.5f);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int SelectedIndex => _selected;

        public int Count => _tabs.Count;

        public event EventHandler? SelectedChanged;

        /// <summary>Adds a tab with a caption from <see cref="Texts"/> and the page it shows.</summary>
        public void AddTab(string textKey, Control page)
        {
            _tabs.Add(new KeyValuePair<string, Control>(textKey, page));
            page.Visible = _tabs.Count - 1 == _selected;
            Invalidate();
        }

        public void Select(int index)
        {
            if (index < 0 || index >= _tabs.Count)
            {
                return;
            }

            _selected = index;
            for (var i = 0; i < _tabs.Count; i++)
            {
                _tabs[i].Value.Visible = i == _selected;
            }

            Invalidate();
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }

        public void LayoutPages(Rectangle bounds)
        {
            foreach (var tab in _tabs)
            {
                tab.Value.Bounds = bounds;
            }
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Home || keyData == Keys.End || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var next = e.KeyCode == Keys.Left ? _selected - 1 : e.KeyCode == Keys.Right ? _selected + 1 : e.KeyCode == Keys.Home ? 0 : e.KeyCode == Keys.End ? _tabs.Count - 1 : -2;
            if (next != -2 && next >= 0 && next < _tabs.Count && next != _selected)
            {
                Select(next);
                e.Handled = true;
            }
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

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hover = _hit.FindIndex(r => r.Contains(e.Location));
            if (hover != _hover)
            {
                _hover = hover;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (var i = 0; i < _hit.Count; i++)
            {
                if (_hit[i].Contains(e.Location))
                {
                    Select(i);
                    return;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var form = FindForm();
            var origin = form == null ? Point.Empty : form.PointToClient(PointToScreen(Point.Empty));
            Theme.PaintBackground(g, ClientRectangle, MainForm.Background, origin);

            _hit.Clear();
            var x = 0;
            var gap = Theme.S(6);
            for (var i = 0; i < _tabs.Count; i++)
            {
                var text = Texts.T(_tabs[i].Key);
                var size = TextRenderer.MeasureText(text, Font, new Size(int.MaxValue, Height), TextFormatFlags.NoPrefix);
                var rect = new Rectangle(x, 0, Math.Max(Theme.S(96), size.Width + Theme.S(28)), Height);
                _hit.Add(rect);
                var selected = i == _selected;
                var hover = i == _hover;
                Theme.FillGlow(g, rect, Theme.Hex(selected ? "#00A866" : hover ? "#005C37" : "#003D25"), Theme.Hex(selected ? "#003A24" : hover ? "#00180F" : "#000C07"));
                Theme.DrawFrame(g, rect, selected || hover ? Theme.Bright : Theme.Hex("#0E8A58"));
                const TextFormatFlags Flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                Theme.DrawGlowText(g, text, Font, rect, selected ? Theme.Hex("#E6FFF3") : Theme.Bright, Theme.Bright, Flags, selected ? Theme.S(2) : 0);
                if (selected && Focused && ShowFocusCues)
                {
                    using (var pen = new Pen(Color.FromArgb(170, Theme.Amber)) { DashStyle = DashStyle.Dot })
                    {
                        var f = Rectangle.Inflate(rect, -Theme.S(4), -Theme.S(4));
                        g.DrawRectangle(pen, f.X, f.Y, f.Width - 1, f.Height - 1);
                    }
                }

                x = rect.Right + gap;
            }

            // The line under the tabs, as the frame of the PipBuck's page.
            using (var line = new SolidBrush(Theme.Frame))
            {
                g.FillRectangle(line, new Rectangle(x, Height - 1, Math.Max(0, Width - x), 1));
            }
        }
    }

    /// <summary>Modal window with the full published change history, in the launcher's look.</summary>
    public sealed class PatchNotesForm : Form
    {
        private static int Pad => Theme.S(16);

        private readonly RichTextBox _text;
        private readonly FrameBox _textBox;
        private readonly Button _closeButton;

        /// <summary>Text colour of the versions the player has not installed yet and of their heading.</summary>
        private static readonly Color NewColor = Theme.Amber;

        public PatchNotesForm(IEnumerable<PatchNote> notes, string? installedVersion = null)
        {
            Text = Texts.T("btn.notes");
            BackColor = MainForm.Background;
            ForeColor = MainForm.BodyColor;
            Font = Theme.Font(9.75f);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(Theme.S(560), Theme.S(480));
            MinimumSize = new Size(Width - ClientSize.Width + Theme.S(420), Height - ClientSize.Height + Theme.S(320));
            Theme.DarkTitleBar(this);

            _text = new RichTextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Panel,
                ForeColor = MainForm.BodyColor,
                Font = Theme.Font(10f),
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = false,
                TabStop = false
            };
            Theme.DarkScrollBars(_text);
            Fill(_text, Render(NotesSplit.Split(notes, installedVersion)));
            _textBox = new FrameBox(_text, 8);

            _closeButton = MainForm.MakeButton(Texts.T("btn.close"), PipButtonKind.Normal, 9.75f, FontStyle.Regular);
            _closeButton.DialogResult = DialogResult.OK;

            Controls.Add(_textBox);
            Controls.Add(_closeButton);
            AcceptButton = _closeButton;
            CancelButton = _closeButton;

            Shown += (s, e) =>
            {
                _text.SelectionStart = 0;
                _text.SelectionLength = 0;
                _closeButton.Focus();
            };
            Resize += (s, e) => LayoutUi();
            LayoutUi();
        }

        /// <summary>How a piece of the history is drawn.</summary>
        public enum PieceKind
        {
            NewHeading,
            NewVersion,
            Divider,
            Version,
            Line,
            Empty
        }

        /// <summary>
        /// The history as styled lines: when a game is installed, the versions newer than it come first under a
        /// "not installed yet" heading, then a divider with the installed version, then the installed one and older.
        /// Without an installed game the list is plain, as every entry is new.
        /// </summary>
        public static IList<KeyValuePair<PieceKind, string>> Render(NotesSplit split)
        {
            var pieces = new List<KeyValuePair<PieceKind, string>>();
            void Add(PieceKind kind, string text) => pieces.Add(new KeyValuePair<PieceKind, string>(kind, text));

            void AddNote(PatchNote note, bool isNew)
            {
                if (pieces.Count > 0)
                {
                    Add(PieceKind.Empty, string.Empty);
                }

                var date = note.DisplayDate;
                Add(isNew ? PieceKind.NewVersion : PieceKind.Version, string.IsNullOrEmpty(date) ? note.Version : note.Version + " — " + date);
                foreach (var line in note.Lines(Texts.Language))
                {
                    Add(PieceKind.Line, "• " + line);
                }
            }

            if (!split.HasInstalled)
            {
                foreach (var note in split.New)
                {
                    AddNote(note, false);
                }
            }
            else
            {
                if (split.New.Count > 0)
                {
                    Add(PieceKind.NewHeading, split.New.Count == 1 ? Texts.T("notes.new1") : Texts.T("notes.new", split.New.Count));
                    foreach (var note in split.New)
                    {
                        AddNote(note, true);
                    }

                    Add(PieceKind.Empty, string.Empty);
                    Add(PieceKind.Divider, "——— " + Texts.T("notes.installed", split.Installed) + " ———");
                }

                foreach (var note in split.Old)
                {
                    AddNote(note, false);
                }
            }

            if (pieces.Count == 0)
            {
                Add(PieceKind.Line, Texts.T("notes.none"));
            }

            return pieces;
        }

        private static void Fill(RichTextBox box, IList<KeyValuePair<PieceKind, string>> pieces)
        {
            var regular = box.Font;
            // The box keeps the formatting as RTF, so the bold font is not needed after the fill.
            using var bold = new Font(regular, FontStyle.Bold);
            for (var i = 0; i < pieces.Count; i++)
            {
                var kind = pieces[i].Key;
                box.SelectionStart = box.TextLength;
                box.SelectionLength = 0;
                switch (kind)
                {
                    case PieceKind.NewHeading:
                        box.SelectionFont = bold;
                        box.SelectionColor = NewColor;
                        break;
                    case PieceKind.NewVersion:
                        box.SelectionFont = bold;
                        box.SelectionColor = NewColor;
                        break;
                    case PieceKind.Divider:
                        box.SelectionFont = regular;
                        box.SelectionColor = MainForm.StatusColor;
                        break;
                    case PieceKind.Version:
                        box.SelectionFont = bold;
                        box.SelectionColor = MainForm.BodyColor;
                        break;
                    default:
                        box.SelectionFont = regular;
                        box.SelectionColor = MainForm.BodyColor;
                        break;
                }

                box.AppendText(i + 1 < pieces.Count ? pieces[i].Value + "\n" : pieces[i].Value);
            }

            box.SelectionStart = 0;
            box.SelectionLength = 0;
        }

        private void LayoutUi()
        {
            var width = ClientSize.Width - (Pad * 2);
            if (width < 80)
            {
                return;
            }

            var buttonHeight = Theme.S(32);
            var buttonWidth = Math.Max(Theme.S(120), TextRenderer.MeasureText(_closeButton.Text, _closeButton.Font).Width + Theme.S(32));
            _closeButton.Bounds = new Rectangle(ClientSize.Width - Pad - buttonWidth, ClientSize.Height - Pad - buttonHeight, buttonWidth, buttonHeight);
            _textBox.Bounds = new Rectangle(Pad, Pad, width, _closeButton.Top - Theme.S(12) - Pad);
            _textBox.LayoutChild();
        }

        protected override void OnPaintBackground(PaintEventArgs e) => Theme.PaintBackground(e.Graphics, ClientRectangle, BackColor, Point.Empty);
    }

    /// <summary>
    /// The progress bar as the game's loading bar: a thin frame and a fill of green segments. The stock WinForms
    /// bar ignores custom colours under visual styles.
    /// </summary>
    public sealed class ProgressStripe : Control
    {
        private double _value;

        public ProgressStripe()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        /// <summary>Progress in percent, 0..100.</summary>
        public double Value
        {
            get => _value;
            set
            {
                var clamped = value < 0d ? 0d : (value > 100d ? 100d : value);
                if (Math.Abs(clamped - _value) < 0.01)
                {
                    return;
                }

                _value = clamped;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            using (var track = new SolidBrush(Theme.Panel))
            {
                g.FillRectangle(track, r);
            }

            Theme.DrawFrame(g, r, Theme.Frame);
            var inner = Rectangle.Inflate(r, -2, -2);
            var filled = (int)Math.Round(inner.Width * (_value / 100d));
            if (filled <= 0 || inner.Height <= 0)
            {
                return;
            }

            var segment = Math.Max(4, Theme.S(8));
            var gap = Math.Max(1, Theme.S(2));
            using (var fill = new SolidBrush(Theme.Bright))
            using (var glow = new SolidBrush(Color.FromArgb(70, Theme.Bright)))
            {
                g.FillRectangle(glow, new Rectangle(inner.X, inner.Y, filled, inner.Height));
                for (var x = inner.X; x < inner.X + filled; x += segment + gap)
                {
                    var w = Math.Min(segment, inner.X + filled - x);
                    g.FillRectangle(fill, new Rectangle(x, inner.Y + 1, w, Math.Max(1, inner.Height - 2)));
                }
            }
        }
    }
}
