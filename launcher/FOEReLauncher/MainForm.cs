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

        // Dark palette of the template launcher, one to one.
        internal static readonly Color Background = FromHex("#12141A");
        internal static readonly Color TitleColor = FromHex("#F2F4F8");
        internal static readonly Color BodyColor = FromHex("#C3C9D4");
        internal static readonly Color StatusColor = FromHex("#8C94A6");
        internal static readonly Color VersionColor = FromHex("#5C6478");
        internal static readonly Color Accent = FromHex("#3D6FF5");
        internal static readonly Color Secondary = FromHex("#2A2F3C");
        internal static readonly Color SecondaryText = FromHex("#E6E9F0");
        internal static readonly Color ProgressTrack = FromHex("#232734");

        private const int Pad = 24;
        private const int Gap = 10;
        private const int ActionHeight = 46;
        private const int SmallButtonHeight = 32;
        private const int ProgressHeight = 8;
        private const int ChannelRowHeight = 26;
        private const int TabStripHeight = 30;

        /// <summary>Width of the "Channel:" caption in front of the channel picker.</summary>
        private const int ChannelLabelWidth = 72;

        /// <summary>Bullets shown in the "what's new" teaser; the full list lives in the dialog.</summary>
        private const int TeaserNoteCount = 3;

        /// <summary>How often foreign game launches are noticed.</summary>
        private const int GameWatchIntervalMs = 2000;

        private readonly LauncherService _service;
        private readonly string[] _args;

        private readonly Label _titleLabel;
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
        private readonly Button _launcherUpdateButton;
        private readonly Button _checkUpdatesButton;
        private readonly Button _verifyButton;
        private readonly Button _folderButton;
        private readonly Button _patchNotesButton;
        private readonly Label _channelLabel;
        private readonly ComboBox _channelCombo;
        private readonly ProgressStripe _progress;
        private readonly Label _statusLabel;
        private readonly Label _versionLabel;
        private readonly System.Windows.Forms.Timer _gameWatchTimer;

        private Manifest? _latest;
        private string? _checkError;
        private LauncherVersionInfo? _launcherUpdate;
        private IList<PatchNote> _patchNotes = new List<PatchNote>();
        private readonly List<string> _whatsNewLines = new List<string>();

        private ActionKind _action = ActionKind.None;
        private string _actionKey = "btn.checking";
        private bool _actionEnabled;
        private bool _busy;

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
            Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            ClientSize = new Size(520, 640);
            MinimumSize = new Size(Width - (ClientSize.Width - 460), Height);
            DoubleBuffered = true;

            _titleLabel = MakeLabel(string.Empty, 19.5f, FontStyle.Bold, TitleColor);

            // Never takes the keyboard focus: when the channel picker is disabled for a refresh, Windows moves the focus to
            // the next control, and a following Space or Enter must not switch the language.
            _languageButton = MakeButton(string.Empty, Secondary, SecondaryText, 8.25f, FontStyle.Regular, selectable: false);
            _languageButton.Click += OnLanguageClick;

            _tabs = new TabStrip { BackColor = Background, AccessibleName = "tabs" };
            _gamePage = new Panel { BackColor = Background };
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

            _whatsNewTitle = MakeLabel(string.Empty, 9f, FontStyle.Bold, StatusColor);
            _whatsNewTitle.AutoSize = false;
            _whatsNewTitle.Visible = false;

            _whatsNewBody = MakeLabel(string.Empty, 8.25f, FontStyle.Regular, StatusColor);
            _whatsNewBody.AutoSize = false;
            _whatsNewBody.Visible = false;

            _actionButton = MakeButton(string.Empty, Accent, Color.White, 12.75f, FontStyle.Bold);
            _actionButton.Enabled = false;
            _actionButton.Click += OnActionClick;

            _launcherUpdateButton = MakeButton(string.Empty, Secondary, SecondaryText, 9.75f, FontStyle.Regular);
            _launcherUpdateButton.Visible = false;
            _launcherUpdateButton.Click += OnLauncherUpdateClick;

            _checkUpdatesButton = MakeButton(string.Empty, Secondary, SecondaryText, 9.75f, FontStyle.Regular);
            _checkUpdatesButton.Click += OnCheckUpdatesClick;

            _verifyButton = MakeButton(string.Empty, Secondary, SecondaryText, 9.75f, FontStyle.Regular);
            _verifyButton.Enabled = false;
            _verifyButton.Click += OnVerifyClick;

            _folderButton = MakeButton(string.Empty, Secondary, SecondaryText, 9.75f, FontStyle.Regular);
            _folderButton.Click += OnFolderClick;

            _patchNotesButton = MakeButton(string.Empty, Secondary, SecondaryText, 9.75f, FontStyle.Regular);
            _patchNotesButton.Enabled = false;
            _patchNotesButton.Click += OnPatchNotesClick;

            _channelLabel = MakeLabel(string.Empty, 9.75f, FontStyle.Regular, StatusColor);
            _channelLabel.AutoSize = false;
            _channelLabel.TextAlign = ContentAlignment.MiddleLeft;

            _channelCombo = MakeChannelCombo();
            _channelCombo.SelectedIndexChanged += OnChannelChanged;

            _progress = new ProgressStripe { Visible = false, TrackColor = ProgressTrack, FillColor = Accent };

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
                _actionButton, _launcherUpdateButton, _checkUpdatesButton, _verifyButton, _folderButton,
                _patchNotesButton, _channelLabel, _channelCombo, _progress, _statusLabel
            });
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
            Font = new Font("Segoe UI", size, style, GraphicsUnit.Point),
            UseMnemonic = false
        };

        internal static Button MakeButton(string text, Color back, Color fore, float size, FontStyle style, bool selectable = true)
        {
            var button = selectable ? new Button() : new NoFocusButton();
            button.Text = text;
            button.BackColor = back;
            button.ForeColor = fore;
            button.FlatStyle = FlatStyle.Flat;
            button.Font = new Font("Segoe UI", size, style, GraphicsUnit.Point);
            button.UseVisualStyleBackColor = false;
            button.UseMnemonic = false;
            button.TabStop = selectable;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.12f);
            button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(back, 0.05f);
            return button;
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
                BackColor = Secondary,
                ForeColor = SecondaryText,
                Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 20,
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
            using (var back = new SolidBrush(selected ? Accent : Secondary))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
            }

            TextRenderer.DrawText(
                e.Graphics,
                Convert.ToString(_channelCombo.Items[e.Index]) ?? string.Empty,
                _channelCombo.Font,
                Rectangle.Inflate(e.Bounds, -3, 0),
                selected ? Color.White : SecondaryText,
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

            _titleLabel.Location = new Point(Pad, 16);
            _languageButton.Bounds = new Rectangle(ClientSize.Width - Pad - 44, 22, 44, 26);
            _tabs.Bounds = new Rectangle(Pad, _titleLabel.Bottom + 8, width, TabStripHeight);

            var versionHeight = _versionLabel.Font.Height + 2;
            _versionLabel.Bounds = new Rectangle(Pad, ClientSize.Height - Pad - versionHeight, width, versionHeight);

            var pageTop = _tabs.Bottom + 6;
            _tabs.LayoutPages(new Rectangle(Pad, pageTop, width, _versionLabel.Top - 6 - pageTop));
            LayoutGamePage(width);
        }

        private void LayoutGamePage(int width)
        {
            var height = _gamePage.ClientSize.Height;
            _installedLabel.Location = new Point(0, 8);
            _availableLabel.Location = new Point(0, _installedLabel.Bottom + 4);

            if (_whatsNewTitle.Visible)
            {
                var titleHeight = _whatsNewTitle.Font.Height + 2;
                _whatsNewTitle.Bounds = new Rectangle(0, _availableLabel.Bottom + 14, width, titleHeight);

                // One bullet per row: long lines are cut with an ellipsis and read in full in the dialog.
                var shown = new List<string>();
                foreach (var line in _whatsNewLines)
                {
                    shown.Add(Ellipsize(line, _whatsNewBody.Font, width - 2));
                }

                _whatsNewBody.Text = string.Join(Environment.NewLine, shown);
                var rows = shown.Count < 1 ? 1 : shown.Count;
                _whatsNewBody.Bounds = new Rectangle(0, _whatsNewTitle.Bottom + 4, width, (_whatsNewBody.Font.Height * rows) + 2);
            }

            var statusHeight = (_statusLabel.Font.Height * 3) + 2;
            _statusLabel.Bounds = new Rectangle(0, height - statusHeight, width, statusHeight);

            var stack = new List<Control> { _actionButton };
            if (_launcherUpdateButton.Visible)
            {
                stack.Add(_launcherUpdateButton);
            }

            stack.Add(_checkUpdatesButton);
            stack.Add(_verifyButton);
            stack.Add(_patchNotesButton);
            stack.Add(_channelCombo);
            if (_progress.Visible)
            {
                stack.Add(_progress);
            }

            var total = Gap * (stack.Count - 1);
            foreach (var control in stack)
            {
                total += HeightOf(control);
            }

            var y = _statusLabel.Top - 10 - total;
            foreach (var control in stack)
            {
                var h = HeightOf(control);
                if (ReferenceEquals(control, _channelCombo))
                {
                    // The caption shares the row with the picker instead of taking one of its own.
                    _channelLabel.Bounds = new Rectangle(0, y, ChannelLabelWidth, h);
                    control.Bounds = new Rectangle(ChannelLabelWidth, y, width - ChannelLabelWidth, h);
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

            if (ReferenceEquals(control, _channelCombo))
            {
                return ChannelRowHeight;
            }

            return ReferenceEquals(control, _progress) ? ProgressHeight : SmallButtonHeight;
        }

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
        }

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
                SetAction(ActionKind.Install, "btn.install");
                SetStatus(() => Texts.T("st.notinstalled", LauncherService.FormatSize(SumPacks(latest), Texts.Language)));
            }
            // Any difference from the channel's version is worth an update, not just a higher number:
            // switching from testing back to release has to bring the stable build back.
            else if (!string.Equals(installed.Version, latest.Version, StringComparison.OrdinalIgnoreCase))
            {
                SetAction(ActionKind.Update, "btn.update");
                SetStatus(_gameRunning ? (Func<string>)(() => Texts.T("st.differs.running")) : () => Texts.T("st.differs", latest.Version));
            }
            else
            {
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
                    await RunBusyAsync(RefreshAsync);
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
                _verifyButton.Enabled = false;
                return;
            }

            _actionButton.Text = Texts.T(_actionKey);
            _actionButton.Enabled = _actionEnabled && !_busy;
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

            using (var dialog = new PatchNotesForm(_patchNotes))
            {
                dialog.ShowDialog(this);
            }
        }
    }

    /// <summary>A button that is clicked with the mouse only and never holds the keyboard focus.</summary>
    public sealed class NoFocusButton : Button
    {
        public NoFocusButton()
        {
            SetStyle(ControlStyles.Selectable, false);
        }
    }

    /// <summary>
    /// The tab strip above the page area: flat captions in the window's palette, the selected one
    /// underlined in the accent colour. Each tab owns a page control; only the selected page is visible.
    /// </summary>
    public sealed class TabStrip : Control
    {
        private readonly List<KeyValuePair<string, Control>> _tabs = new List<KeyValuePair<string, Control>>();
        private readonly List<Rectangle> _hit = new List<Rectangle>();
        private int _selected;

        public TabStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
            Cursor = Cursors.Hand;
        }

        public int SelectedIndex => _selected;

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
            using (var back = new SolidBrush(BackColor))
            {
                g.FillRectangle(back, ClientRectangle);
            }

            using (var line = new SolidBrush(MainForm.Secondary))
            {
                g.FillRectangle(line, new Rectangle(0, Height - 1, Width, 1));
            }

            _hit.Clear();
            var x = 0;
            for (var i = 0; i < _tabs.Count; i++)
            {
                var text = Texts.T(_tabs[i].Key);
                var size = TextRenderer.MeasureText(text, Font, new Size(int.MaxValue, Height), TextFormatFlags.NoPrefix);
                var rect = new Rectangle(x, 0, size.Width + 16, Height);
                _hit.Add(rect);
                var selected = i == _selected;
                TextRenderer.DrawText(
                    g,
                    text,
                    Font,
                    rect,
                    selected ? MainForm.TitleColor : MainForm.StatusColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (selected)
                {
                    using (var accent = new SolidBrush(MainForm.Accent))
                    {
                        g.FillRectangle(accent, new Rectangle(rect.X + 4, Height - 3, rect.Width - 8, 3));
                    }
                }

                x = rect.Right + 8;
            }
        }
    }

    /// <summary>Modal window with the full published change history, same dark palette as the launcher.</summary>
    public sealed class PatchNotesForm : Form
    {
        private const int Pad = 20;
        private const int ButtonHeight = 32;

        private readonly RichTextBox _text;
        private readonly Button _closeButton;

        public PatchNotesForm(IEnumerable<PatchNote> notes)
        {
            Text = Texts.T("btn.notes");
            BackColor = MainForm.Background;
            ForeColor = MainForm.BodyColor;
            Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(540, 480);
            MinimumSize = new Size(Width - (ClientSize.Width - 420), Height - (ClientSize.Height - 320));

            _text = new RichTextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = ColorTranslator.FromHtml("#181B23"),
                ForeColor = MainForm.BodyColor,
                Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point),
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = false,
                TabStop = false,
                Text = Render(notes)
            };

            _closeButton = MainForm.MakeButton(Texts.T("btn.close"), MainForm.Secondary, MainForm.SecondaryText, 9.75f, FontStyle.Regular);
            _closeButton.DialogResult = DialogResult.OK;

            Controls.Add(_text);
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

        /// <summary>Flattens the history into "version — date" blocks separated by a blank line.</summary>
        public static string Render(IEnumerable<PatchNote> notes)
        {
            var sb = new StringBuilder();
            foreach (var note in notes)
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                var date = note.DisplayDate;
                sb.AppendLine(string.IsNullOrEmpty(date) ? note.Version : note.Version + " — " + date);
                foreach (var line in note.Lines(Texts.Language))
                {
                    sb.AppendLine("• " + line);
                }
            }

            return sb.Length == 0 ? Texts.T("notes.none") : sb.ToString().TrimEnd();
        }

        private void LayoutUi()
        {
            var width = ClientSize.Width - (Pad * 2);
            if (width < 80)
            {
                return;
            }

            _closeButton.Bounds = new Rectangle(ClientSize.Width - Pad - 120, ClientSize.Height - Pad - ButtonHeight, 120, ButtonHeight);
            _text.Bounds = new Rectangle(Pad, Pad, width, _closeButton.Top - 14 - Pad);
        }
    }

    /// <summary>
    /// Flat progress bar: the stock WinForms one ignores custom colours under visual styles,
    /// and the launcher needs the accent blue on the dark track.
    /// </summary>
    public sealed class ProgressStripe : Control
    {
        private double _value;

        public ProgressStripe()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        public Color TrackColor { get; set; } = Color.FromArgb(0x23, 0x27, 0x34);

        public Color FillColor { get; set; } = Color.FromArgb(0x3D, 0x6F, 0xF5);

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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = new SolidBrush(TrackColor))
            {
                g.FillRectangle(track, ClientRectangle);
            }

            var filled = (int)Math.Round(ClientRectangle.Width * (_value / 100d));
            if (filled <= 0)
            {
                return;
            }

            using (var fill = new SolidBrush(FillColor))
            {
                g.FillRectangle(fill, new Rectangle(0, 0, filled, ClientRectangle.Height));
            }
        }
    }
}
