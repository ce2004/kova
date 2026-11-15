using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TailRemote
{
    /// <summary>
    /// How Kova looks to somebody who can see it (4.11): colours for light and dark, the fonts, the icon font, and the
    /// few shapes everything is drawn from. Until 4.11 the window was a menu bar and nothing else, which is all a
    /// screen reader needs and nothing a sighted person can find their way around.
    ///
    /// Nothing here changes what the keyboard or NVDA meets. It is paint.
    /// </summary>
    internal sealed class Look
    {
        public bool Dark { get; }
        public Color Window, Card, CardHover, CardPressed, Edge, Text, Soft, Accent, AccentText, AccentSoft, Good, Warn, Bad, BadSoft;

        private Look(bool dark, bool highContrast)
        {
            Dark = dark;
            if (highContrast)
            {
                // Windows' own contrast colours, untouched: somebody chose them because they need them.
                Window = SystemColors.Window; Card = SystemColors.Window; CardHover = SystemColors.Highlight; CardPressed = SystemColors.Highlight;
                Edge = SystemColors.WindowText; Text = SystemColors.WindowText; Soft = SystemColors.WindowText;
                Accent = SystemColors.Highlight; AccentText = SystemColors.HighlightText; AccentSoft = SystemColors.Window;
                Good = SystemColors.WindowText; Warn = SystemColors.WindowText; Bad = SystemColors.WindowText; BadSoft = SystemColors.Window;
                HighContrast = true;
                return;
            }
            if (dark)
            {
                Window = Color.FromArgb(0x16, 0x18, 0x1C); Card = Color.FromArgb(0x21, 0x24, 0x2A); CardHover = Color.FromArgb(0x2A, 0x2E, 0x36);
                CardPressed = Color.FromArgb(0x1C, 0x1F, 0x24); Edge = Color.FromArgb(0x33, 0x38, 0x41);
                Text = Color.FromArgb(0xF2, 0xF4, 0xF7); Soft = Color.FromArgb(0xA3, 0xAB, 0xB8);
                Accent = Color.FromArgb(0x4C, 0xA6, 0xFF); AccentText = Color.FromArgb(0x08, 0x16, 0x26); AccentSoft = Color.FromArgb(0x1B, 0x2C, 0x40);
                Good = Color.FromArgb(0x4A, 0xD1, 0x8B); Warn = Color.FromArgb(0xF2, 0xB8, 0x4B); Bad = Color.FromArgb(0xFF, 0x7B, 0x72);
                BadSoft = Color.FromArgb(0x3A, 0x20, 0x20);
            }
            else
            {
                Window = Color.FromArgb(0xF4, 0xF6, 0xF9); Card = Color.White; CardHover = Color.FromArgb(0xEE, 0xF4, 0xFD);
                CardPressed = Color.FromArgb(0xE2, 0xEC, 0xFA); Edge = Color.FromArgb(0xDD, 0xE2, 0xEA);
                Text = Color.FromArgb(0x16, 0x1B, 0x24); Soft = Color.FromArgb(0x5B, 0x66, 0x77);
                Accent = Color.FromArgb(0x1F, 0x6F, 0xEB); AccentText = Color.White; AccentSoft = Color.FromArgb(0xE5, 0xEF, 0xFE);
                Good = Color.FromArgb(0x1A, 0x8F, 0x55); Warn = Color.FromArgb(0xB3, 0x6B, 0x00); Bad = Color.FromArgb(0xC9, 0x2A, 0x2A);
                BadSoft = Color.FromArgb(0xFD, 0xEC, 0xEC);
            }
        }

        public bool HighContrast { get; }

        /// <summary>The look Windows is set to right now: its light or dark app mode, or its contrast theme.</summary>
        public static Look Current() => TestDark is bool dark ? new(dark, highContrast: false)
            : new(dark: WindowsIsDark(), highContrast: SystemInformation.HighContrast);

        /// <summary>For --lookshot: pictures in the dark look (or the light one) whatever Windows is set to.</summary>
        internal static bool? TestDark;

        /// <summary>A chosen look, for pictures of the window made without showing it.</summary>
        public static Look Of(bool dark) => new(dark, highContrast: false);

        private static bool WindowsIsDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch { return false; }
        }

        // ---- fonts ----

        /// <summary>Windows 11's text face where it is installed, Segoe UI everywhere else.</summary>
        public static readonly string TextFace = Installed("Segoe UI Variable Text") ?? "Segoe UI";
        public static readonly string TitleFace = Installed("Segoe UI Variable Display") ?? "Segoe UI";

        /// <summary>The icon font Windows ships (Fluent on 11, MDL2 on 10: the same pictures at the same numbers), or null.</summary>
        public static readonly string? IconFace = Installed("Segoe Fluent Icons") ?? Installed("Segoe MDL2 Assets");

        private static string? Installed(string face)
        {
            try
            {
                using var fonts = new InstalledFontCollection();
                foreach (var family in fonts.Families)
                    if (string.Equals(family.Name, face, StringComparison.OrdinalIgnoreCase)) return family.Name;
            }
            catch { }
            return null;
        }

        // The pictures used, by their place in the icon font.
        public const string IconConnect = "", IconHost = "", IconPc = "", IconKeyboard = "", IconScreen = "",
            IconSend = "", IconGet = "", IconClipboard = "", IconStop = "", IconSound = "",
            IconChevron = "", IconEar = "", IconFolder = "";

        // ---- shapes ----

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color fill, Color? edge = null, float edgeWidth = 1f)
        {
            using var path = Rounded(r, radius);
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            if (edge is Color e)
            {
                using var pen = new Pen(e, edgeWidth);
                g.DrawPath(pen, path);
            }
        }

        /// <summary>Kova's own mark: a rounded square in the accent colour with a K, at any size. Also the window and tray icon.</summary>
        public static void Mark(Graphics g, RectangleF r, Color accent, Color letter)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Fill(g, r, r.Width * 0.26f, accent);
            // The K, drawn rather than typed, so it is the same on every PC whatever fonts it has.
            float w = r.Width, x = r.X, y = r.Y;
            using var pen = new Pen(letter, Math.Max(1.5f, w * 0.13f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLine(pen, x + w * 0.34f, y + w * 0.26f, x + w * 0.34f, y + w * 0.74f);
            g.DrawLines(pen, new[] { new PointF(x + w * 0.68f, y + w * 0.26f), new PointF(x + w * 0.38f, y + w * 0.52f), new PointF(x + w * 0.70f, y + w * 0.74f) });
            g.SmoothingMode = old;
        }

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        /// <summary>The mark as an icon of its own (a copy that owns its memory, so the handle made on the way is let go).</summary>
        public static Icon AppIcon(int size)
        {
            using var picture = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(picture))
            {
                g.Clear(Color.Transparent);
                Mark(g, new RectangleF(0.5f, 0.5f, size - 1, size - 1), Color.FromArgb(0x1F, 0x6F, 0xEB), Color.White);
            }
            IntPtr handle = picture.GetHicon();
            try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
            finally { DestroyIcon(handle); }
        }

        // ---- the window's frame and its menu bar ----

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        /// <summary>The title bar follows the look: dark with dark, as every Windows 11 app's does.</summary>
        public void Frame(IntPtr window)
        {
            try
            {
                int on = Dark && !HighContrast ? 1 : 0;
                DwmSetWindowAttribute(window, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
            }
            catch { }
        }

        /// <summary>The menu bar and its menus in the look's colours. Contrast themes keep Windows' own.</summary>
        public void Style(MenuStrip bar)
        {
            if (HighContrast) { bar.RenderMode = ToolStripRenderMode.System; return; }
            bar.Renderer = new ToolStripProfessionalRenderer(new MenuColours(this)) { RoundedEdges = false };
            bar.BackColor = Window;
            bar.ForeColor = Text;
            void Paint(ToolStripItemCollection items)
            {
                foreach (ToolStripItem item in items)
                {
                    item.ForeColor = Text;
                    if (item is ToolStripDropDownItem down)
                    {
                        down.DropDown.BackColor = Card;
                        down.DropDown.ForeColor = Text;
                        // Menus are rebuilt as they open (lists of PCs, devices), so each is coloured again then.
                        down.DropDownOpening -= Recolour;
                        down.DropDownOpening += Recolour;
                    }
                }
            }
            void Recolour(object? sender, EventArgs e) { if (sender is ToolStripDropDownItem down) Paint(down.DropDownItems); }
            Paint(bar.Items);
        }

        // ---- every other window ----

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string? app, string? ids);

        /// <summary>
        /// Dresses a whole window in the look: its background, its text, its boxes, lists and buttons, and (dark) its
        /// title bar and scroll bars. The button a window treats as its answer to Enter is filled in the accent
        /// colour, so the eye lands on it as the keyboard does.
        ///
        /// Colours and edges only. No control is added, removed, renamed or reordered, so what a screen reader
        /// meets in the window, and the order Tab goes round it, are exactly as they were. Contrast themes are
        /// left alone altogether: Windows' own colours are the point of them.
        /// </summary>
        public static void Dress(Form form)
        {
            var look = Current();
            try { form.Icon = SharedIcon.Value; } catch { }
            if (look.HighContrast) return;
            form.BackColor = look.Window;
            form.ForeColor = look.Text;
            void Framed() { look.Frame(form.Handle); }
            if (form.IsHandleCreated) Framed(); else form.HandleCreated += (_, _) => Framed();
            look.DressChildren(form, form.AcceptButton as Button);
            // Windows that add their rows later (lists of PCs, of transfers) dress those as they arrive.
            form.ControlAdded += (_, e) => { if (e.Control != null) look.DressOne(e.Control, form.AcceptButton as Button); };
        }

        /// <summary>One icon for every window, made once: Kova runs for months and opens its windows over and over.</summary>
        private static readonly Lazy<Icon> SharedIcon = new(() => AppIcon(32));

        private void DressChildren(Control parent, Button? main)
        {
            foreach (Control child in parent.Controls) DressOne(child, main);
        }

        private void DressOne(Control c, Button? main)
        {
            switch (c)
            {
                case HomeView: return; // paints itself
                case MenuStrip bar: Style(bar); return;
                case Button b:
                    bool primary = ReferenceEquals(b, main);
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderSize = 1;
                    b.FlatAppearance.BorderColor = primary ? Accent : Edge;
                    b.FlatAppearance.MouseOverBackColor = primary ? Blend(Accent, Text, 0.12f) : CardHover;
                    b.FlatAppearance.MouseDownBackColor = primary ? Blend(Accent, Window, 0.2f) : CardPressed;
                    b.BackColor = primary ? Accent : Card;
                    b.ForeColor = primary ? AccentText : Text;
                    b.UseVisualStyleBackColor = false;
                    b.Cursor = Cursors.Hand;
                    // Room to hit with a mouse, where the button sizes itself; one laid out by hand keeps its size.
                    if (b.AutoSize)
                    {
                        int unit = Math.Max(1, b.Font.Height);
                        b.MinimumSize = new Size(Math.Max(b.MinimumSize.Width, unit * 5), Math.Max(b.MinimumSize.Height, unit * 2));
                        b.Padding = new Padding(Math.Max(b.Padding.Left, unit / 2), b.Padding.Top, Math.Max(b.Padding.Right, unit / 2), b.Padding.Bottom);
                    }
                    break;
                case TextBoxBase t:
                    // A read-only box that was dressed as plain text (no border, the window's colour) stays plain text.
                    bool plain = t.BorderStyle == BorderStyle.None;
                    t.BackColor = plain ? Window : Card;
                    t.ForeColor = Text;
                    if (!plain) t.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ListBox or ListView or TreeView or ComboBox or NumericUpDown or DataGridView:
                    c.BackColor = Card;
                    c.ForeColor = Text;
                    if (c is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
                    if (c is ListBox list) list.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case LinkLabel link:
                    link.LinkColor = link.ActiveLinkColor = Accent;
                    link.VisitedLinkColor = Accent;
                    link.BackColor = Color.Transparent;
                    break;
                case Label or CheckBox or RadioButton:
                    // Help text set in grey stays the look's own softer colour; everything else is the text colour.
                    c.ForeColor = c.ForeColor.ToArgb() == SystemColors.GrayText.ToArgb() ? Soft : Text;
                    c.BackColor = Color.Transparent;
                    break;
                case GroupBox or Panel or TableLayoutPanel or FlowLayoutPanel or TabPage or SplitContainer:
                    c.BackColor = Window;
                    c.ForeColor = Text;
                    break;
                case ProgressBar:
                    break;
                default:
                    c.ForeColor = Text;
                    break;
            }

            // Dark scroll bars and list headers: Windows draws those, and only goes dark when told to per control.
            if (Dark && c is ListBox or ListView or TreeView or TextBoxBase or ComboBox or ScrollableControl)
            {
                void Themed() { try { SetWindowTheme(c.Handle, c is ComboBox ? "DarkMode_CFD" : "DarkMode_Explorer", null); } catch { } }
                if (c.IsHandleCreated) Themed(); else c.HandleCreated += (_, _) => Themed();
            }

            if (c.HasChildren) DressChildren(c, main);
            c.ControlAdded += (_, e) => { if (e.Control != null) DressOne(e.Control, main); };
        }

        private static Color Blend(Color a, Color b, float much) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * much), (int)(a.G + (b.G - a.G) * much), (int)(a.B + (b.B - a.B) * much));

        private sealed class MenuColours : ProfessionalColorTable
        {
            private readonly Look _look;
            public MenuColours(Look look) { _look = look; UseSystemColors = false; }
            public override Color MenuStripGradientBegin => _look.Window;
            public override Color MenuStripGradientEnd => _look.Window;
            public override Color ToolStripDropDownBackground => _look.Card;
            public override Color ImageMarginGradientBegin => _look.Card;
            public override Color ImageMarginGradientMiddle => _look.Card;
            public override Color ImageMarginGradientEnd => _look.Card;
            public override Color MenuBorder => _look.Edge;
            public override Color MenuItemBorder => _look.Accent;
            public override Color MenuItemSelected => _look.CardHover;
            public override Color MenuItemSelectedGradientBegin => _look.CardHover;
            public override Color MenuItemSelectedGradientEnd => _look.CardHover;
            public override Color MenuItemPressedGradientBegin => _look.Card;
            public override Color MenuItemPressedGradientMiddle => _look.Card;
            public override Color MenuItemPressedGradientEnd => _look.Card;
            public override Color SeparatorDark => _look.Edge;
            public override Color SeparatorLight => _look.Edge;
            public override Color CheckBackground => _look.AccentSoft;
            public override Color CheckSelectedBackground => _look.AccentSoft;
            public override Color CheckPressedBackground => _look.AccentSoft;
            public override Color ButtonSelectedBorder => _look.Accent;
        }
    }
}
