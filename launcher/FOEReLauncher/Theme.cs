using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FOEReLauncher
{
    /// <summary>
    /// The launcher's look, drawn in code after the game's PipBuck screens: black-green background with faint
    /// scan lines, green text, thin green frames, buttons with an inner glow and yellow column captions. Sizes in
    /// pixels go through <see cref="S"/> so the layout follows the screen's DPI; fonts are system fonts at run time.
    /// </summary>
    internal static class Theme
    {
        internal static readonly Color Background = Hex("#020A07");
        internal static readonly Color Panel = Hex("#000503");
        internal static readonly Color Bright = Hex("#00FF99");
        internal static readonly Color Body = Hex("#3BE6A2");
        internal static readonly Color Dim = Hex("#1FA673");
        internal static readonly Color Faint = Hex("#147252");
        internal static readonly Color Amber = Hex("#F2DE4C");
        internal static readonly Color Bad = Hex("#FF6A5C");
        internal static readonly Color Frame = Hex("#0B5E3D");
        internal static readonly Color FrameBright = Hex("#00D985");
        internal static readonly Color Fill = Hex("#00170E");
        internal static readonly Color FillHover = Hex("#002C1B");
        internal static readonly Color FillSelected = Hex("#003A24");
        internal static readonly Color GroupFill = Hex("#00221A");
        internal static readonly Color Disabled = Hex("#1B5A42");
        internal static readonly Color DisabledFrame = Hex("#0A3324");

        private const string BodyFamily = "Arial";
        private const string TitleFamily = "Consolas";

        /// <summary>Screen pixels per layout pixel: the system DPI over 96, times an optional extra factor.</summary>
        internal static float Scale { get; private set; } = 1f;

        /// <summary>Extra factor for fonts and sizes, from FOERE_UI_SCALE (layout checks of 125% and 150% on a 100% screen).</summary>
        internal static float Extra { get; private set; } = 1f;

        static Theme()
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    Scale = g.DpiX / 96f;
                }
            }
            catch (Exception)
            {
                Scale = 1f;
            }

            var extra = Environment.GetEnvironmentVariable("FOERE_UI_SCALE");
            if (!string.IsNullOrEmpty(extra) && float.TryParse(extra, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f >= 1f && f <= 3f)
            {
                Extra = f;
                Scale *= f;
            }
        }

        internal static int S(int px) => (int)Math.Round(px * Scale);

        internal static Color Hex(string hex) => ColorTranslator.FromHtml(hex);

        internal static Font Font(float size, FontStyle style = FontStyle.Regular) =>
            new Font(BodyFamily, size * Extra, style, GraphicsUnit.Point);

        internal static Font TitleFont(float size) => new Font(TitleFamily, size * Extra, FontStyle.Bold, GraphicsUnit.Point);

        /// <summary>The window background: flat black-green with the PipBuck's faint scan lines.</summary>
        internal static void PaintBackground(Graphics g, Rectangle area, Color back, Point origin)
        {
            using (var b = new SolidBrush(back))
            {
                g.FillRectangle(b, area);
            }

            var step = Math.Max(3, S(3));
            using (var line = new Pen(Color.FromArgb(10, Bright)))
            {
                // Lines on absolute window rows so neighbouring panels continue the same pattern.
                var first = area.Top - ((area.Top + origin.Y) % step);
                for (var y = first; y < area.Bottom; y += step)
                {
                    g.DrawLine(line, area.Left, y, area.Right, y);
                }
            }
        }

        /// <summary>A thin frame inside <paramref name="r"/>.</summary>
        internal static void DrawFrame(Graphics g, Rectangle r, Color color, int width = 1)
        {
            using (var pen = new Pen(color, width) { Alignment = PenAlignment.Inset })
            {
                g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
        }

        /// <summary>A PipBuck panel fill: dark in the middle, lighter towards the edges.</summary>
        internal static void FillGlow(Graphics g, Rectangle r, Color edge, Color centre)
        {
            if (r.Width <= 0 || r.Height <= 0)
            {
                return;
            }

            using (var b = new SolidBrush(centre))
            {
                g.FillRectangle(b, r);
            }

            var depth = Math.Max(2, Math.Min(r.Height / 2, S(10)));
            for (var i = 0; i < depth; i++)
            {
                var alpha = (int)(170 * Math.Pow(1 - (i / (double)depth), 2));
                using (var pen = new Pen(Color.FromArgb(alpha, edge)))
                {
                    g.DrawRectangle(pen, r.X + i, r.Y + i, r.Width - 1 - (2 * i), r.Height - 1 - (2 * i));
                }
            }
        }

        /// <summary>Text with a soft green halo, as the game's menu captions.</summary>
        internal static void DrawGlowText(Graphics g, string text, Font font, Rectangle r, Color core, Color glow, TextFormatFlags flags, int radius = 2)
        {
            if (radius > 0)
            {
                // GDI text has no alpha: the halo is the glow colour dimmed as it would look over the dark background.
                var k = radius >= 2 ? 0.28 : 0.38;
                var halo = Color.FromArgb((int)(glow.R * k), (int)(glow.G * k), (int)(glow.B * k));
                for (var dx = -radius; dx <= radius; dx++)
                {
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        if ((dx == 0 && dy == 0) || (dx * dx) + (dy * dy) > radius * radius + 1)
                        {
                            continue;
                        }

                        var o = r;
                        o.Offset(dx, dy);
                        TextRenderer.DrawText(g, text, font, o, halo, flags);
                    }
                }
            }

            TextRenderer.DrawText(g, text, font, r, core, flags);
        }

        /// <summary>A check box: frame, tick, or a filled square for "some of them".</summary>
        internal static void DrawCheck(Graphics g, Rectangle box, CheckState state, bool enabled)
        {
            var frame = enabled ? FrameBright : DisabledFrame;
            using (var back = new SolidBrush(Panel))
            {
                g.FillRectangle(back, box);
            }

            DrawFrame(g, box, frame);
            var color = enabled ? Bright : Disabled;
            if (state == CheckState.Indeterminate)
            {
                var inner = Rectangle.Inflate(box, -Math.Max(3, box.Width / 4), -Math.Max(3, box.Height / 4));
                using (var b = new SolidBrush(color))
                {
                    g.FillRectangle(b, inner);
                }
            }
            else if (state == CheckState.Checked)
            {
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(color, Math.Max(2f, box.Width / 7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    var w = box.Width;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(box.X + (w * 0.22f), box.Y + (w * 0.52f)),
                        new PointF(box.X + (w * 0.42f), box.Y + (w * 0.72f)),
                        new PointF(box.X + (w * 0.78f), box.Y + (w * 0.28f))
                    });
                }

                g.SmoothingMode = old;
            }
        }

        /// <summary>A small triangle: pointing down for an open group, right for a closed one.</summary>
        internal static void DrawChevron(Graphics g, Rectangle r, bool open, Color color)
        {
            var size = Math.Max(4, Math.Min(r.Width, r.Height) / 3);
            var cx = r.X + (r.Width / 2f);
            var cy = r.Y + (r.Height / 2f);
            var pts = open
                ? new[] { new PointF(cx - size, cy - (size / 2f)), new PointF(cx + size, cy - (size / 2f)), new PointF(cx, cy + (size / 1.2f)) }
                : new[] { new PointF(cx - (size / 2f), cy - size), new PointF(cx - (size / 2f), cy + size), new PointF(cx + (size / 1.2f), cy) };
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(color))
            {
                g.FillPolygon(b, pts);
            }

            g.SmoothingMode = old;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>A dark window title bar on Windows 10 (20H1) and later; older systems keep theirs.</summary>
        internal static void DarkTitleBar(Form form)
        {
            void Apply()
            {
                try
                {
                    var on = 1;
                    if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
                    }
                }
                catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
                {
                    // No desktop composition: the system title bar stays.
                }
            }

            if (form.IsHandleCreated)
            {
                Apply();
            }

            form.HandleCreated += (s, e) => Apply();

            // The frame is drawn before the first show; redraw it once shown so the dark caption takes effect.
            form.Shown += (s, e) => SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0027);
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>The dark system look of a drop-down list's button and list (Windows 10 and later).</summary>
        internal static void DarkCombo(ComboBox combo)
        {
            void Apply()
            {
                try
                {
                    SetWindowTheme(combo.Handle, "DarkMode_CFD", null);
                }
                catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
                {
                    // No themes.
                }
            }

            if (combo.IsHandleCreated)
            {
                Apply();
            }

            combo.HandleCreated += (s, e) => Apply();
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

        /// <summary>Dark scroll bars on Windows 10 and later (the system theme "DarkMode_Explorer"); older systems keep theirs.</summary>
        internal static void DarkScrollBars(Control control)
        {
            void Apply()
            {
                try
                {
                    SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
                }
                catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
                {
                    // No themes: the stock scroll bars stay.
                }
            }

            if (control.IsHandleCreated)
            {
                Apply();
            }

            control.HandleCreated += (s, e) => Apply();
        }
    }

    /// <summary>A panel that paints the window background (scan lines) instead of a flat colour.</summary>
    public class ThemedPanel : Panel
    {
        public ThemedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Background;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var form = FindForm();
            var origin = form == null ? Point.Empty : form.PointToClient(PointToScreen(Point.Empty));
            Theme.PaintBackground(e.Graphics, ClientRectangle, Theme.Background, origin);
        }
    }

    /// <summary>A thin green frame around one child control (list, text box, picker), as the PipBuck's boxes.</summary>
    public sealed class FrameBox : Panel
    {
        private bool _focused;

        public FrameBox(Control child, int padding = 1)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Panel;
            Child = child;
            Inner = padding;
            Controls.Add(child);
            child.GotFocus += (s, e) => SetFocused(true);
            child.LostFocus += (s, e) => SetFocused(false);
            Resize += (s, e) => LayoutChild();
        }

        public Control Child { get; }

        /// <summary>The child is one pixel larger on every side than the box, so its own border is cut off (pickers).</summary>
        public bool ClipChild { get; set; }

        /// <summary>The height the box needs for its child.</summary>
        public int ChildHeight => ClipChild ? Child.Height - 2 : Child.Height + (2 * Theme.S(Inner));

        /// <summary>Space between the frame and the child, in layout pixels.</summary>
        public int Inner { get; }

        private void SetFocused(bool focused)
        {
            _focused = focused;
            Invalidate();
        }

        public void LayoutChild()
        {
            if (ClipChild)
            {
                Child.Bounds = new Rectangle(-1, -1, Width + 2, Child.Height);
                return;
            }

            var p = Theme.S(Inner);
            Child.Bounds = new Rectangle(p, p, Math.Max(1, Width - (2 * p)), Math.Max(1, Height - (2 * p)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var b = new SolidBrush(BackColor))
            {
                e.Graphics.FillRectangle(b, ClientRectangle);
            }

            Theme.DrawFrame(e.Graphics, ClientRectangle, _focused ? Theme.FrameBright : Theme.Frame);
        }
    }

    /// <summary>A caption with the game menu's glow (the window title).</summary>
    public sealed class GlowLabel : Control
    {
        public GlowLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var form = FindForm();
            var origin = form == null ? Point.Empty : form.PointToClient(PointToScreen(Point.Empty));
            Theme.PaintBackground(e.Graphics, ClientRectangle, Theme.Background, origin);
            Theme.DrawGlowText(e.Graphics, Text, Font, ClientRectangle, Theme.Hex("#D9FFEE"), ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine, Theme.S(3));
        }
    }

    /// <summary>How a <see cref="PipButton"/> is drawn.</summary>
    public enum PipButtonKind
    {
        /// <summary>The usual button: dark fill, thin frame.</summary>
        Normal,

        /// <summary>The one main action of a page: brighter fill, double frame, glowing caption.</summary>
        Primary,

        /// <summary>Removes or overwrites something: red frame on hover.</summary>
        Danger
    }

    /// <summary>The game's button look drawn in code: inner glow, thin frame that lights up on hover and focus.</summary>
    public class PipButton : Button
    {
        private bool _hover;
        private bool _down;

        public PipButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            UseMnemonic = false;
            Cursor = Cursors.Hand;
        }

        public PipButtonKind Kind { get; set; } = PipButtonKind.Normal;

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _down = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            _down = mevent.Button == MouseButtons.Left;
            Invalidate();
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            var r = ClientRectangle;
            var parentBack = Parent?.BackColor ?? Theme.Background;
            using (var b = new SolidBrush(parentBack))
            {
                g.FillRectangle(b, r);
            }

            var primary = Kind == PipButtonKind.Primary;
            var lit = Enabled && (_hover || Focused);
            if (!Enabled)
            {
                using (var b = new SolidBrush(Hexed(primary ? "#000F09" : "#000A06")))
                {
                    g.FillRectangle(b, r);
                }

                Theme.DrawFrame(g, r, Theme.DisabledFrame);
            }
            else
            {
                var edge = primary ? Theme.Hex(_down ? "#00995C" : lit ? "#00C777" : "#00A866") : Theme.Hex(_down ? "#007A49" : lit ? "#006E43" : "#00512F");
                var centre = primary ? Theme.Hex(lit ? "#00331F" : "#002315") : Theme.Hex(lit ? "#00180F" : "#000C07");
                Theme.FillGlow(g, r, edge, centre);
                var frame = Kind == PipButtonKind.Danger && lit ? Theme.Bad : lit || primary ? Theme.Bright : Theme.Hex("#0E8A58");
                Theme.DrawFrame(g, r, frame);
                if (primary)
                {
                    Theme.DrawFrame(g, Rectangle.Inflate(r, -Theme.S(3), -Theme.S(3)), Color.FromArgb(lit ? 150 : 90, Theme.Bright));
                }
            }

            const TextFormatFlags Flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                                          TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            var text = Rectangle.Inflate(r, -Theme.S(6), 0);
            if (!Enabled)
            {
                TextRenderer.DrawText(g, Text, Font, text, Theme.Disabled, Flags);
            }
            else if (primary)
            {
                Theme.DrawGlowText(g, Text, Font, text, Theme.Hex("#E6FFF3"), Theme.Bright, Flags, Theme.S(2));
            }
            else
            {
                Theme.DrawGlowText(g, Text, Font, text, lit ? Theme.Hex("#B8FFDD") : Theme.Bright, Theme.Bright, Flags, lit ? 1 : 0);
            }

            if (Focused && ShowFocusCues)
            {
                using (var pen = new Pen(Color.FromArgb(160, Theme.Amber)) { DashStyle = DashStyle.Dot })
                {
                    var f = Rectangle.Inflate(r, -Theme.S(primary ? 6 : 3), -Theme.S(primary ? 6 : 3));
                    g.DrawRectangle(pen, f.X, f.Y, f.Width - 1, f.Height - 1);
                }
            }
        }

        private static Color Hexed(string hex) => Theme.Hex(hex);
    }

    /// <summary>A <see cref="PipButton"/> that is clicked with the mouse only and never holds the keyboard focus.</summary>
    public sealed class NoFocusButton : PipButton
    {
        public NoFocusButton()
        {
            SetStyle(ControlStyles.Selectable, false);
        }
    }
}
