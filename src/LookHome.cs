using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>What the home screen shows: taken from the window once a second and whenever something changes.</summary>
    internal sealed record HomeState(
        HomeState.Kind What,
        string Headline,                 // "Not connected", "Connected to Laptop", "This PC can be controlled"
        string Detail,                   // the line under it: ping and sound, or who is connected
        bool Controlling,                // keys are going to the remote PC
        bool ListenOnly,
        bool ScreenOpen,
        IReadOnlyList<(string Name, string Address)> Saved,
        string Transfers,                // "" when nothing is moving
        string Version)
    {
        public enum Kind { Idle, Connecting, Connected, Hosting }

        /// <summary>Everything that is painted, as one line: the screen is painted again only when this changes.</summary>
        public string Signature() =>
            What + "|" + Headline + "|" + Detail + "|" + Controlling + ListenOnly + ScreenOpen + "|" + Transfers + "|" + Version + "|" +
            string.Join(";", Saved.Select(s => s.Name + "," + s.Address));
    }

    internal enum HomeAction { Connect, Host, ConnectSaved, ToggleControl, Screen, SendFiles, SendClipboard, GetFiles, Disconnect, StopHosting }

    /// <summary>
    /// Kova's face for somebody who can see it (4.11): what is going on, in large type, and the things done most as big
    /// targets for the mouse. Connect to a PC, Host this PC and the saved PCs while nothing is connected; the remote
    /// PC's controls while one is.
    ///
    /// It is one painted surface, for the mouse only. It never takes the keyboard and is not a row of buttons to Tab
    /// through: every one of its actions is a menu item with a shortcut already, and a screen reader user's Kova is
    /// exactly what it was (the menu bar, the title, what is spoken). That is on purpose. Buttons here would put a
    /// second, different way through the program in front of NVDA, and change what is read when the window opens.
    /// </summary>
    internal sealed class HomeView : Control
    {
        private Look _look = Look.Current();
        private HomeState _state = new(HomeState.Kind.Idle, "Not connected", "", false, false, false, Array.Empty<(string, string)>(), "", "");
        private string _painted = "";

        /// <summary>A target was clicked: which, and for a saved PC its place in the list.</summary>
        public event Action<HomeAction, int>? Chosen;

        private sealed record Target(RectangleF Area, HomeAction Action, int Index);
        private readonly List<Target> _targets = new();
        private Target? _hover, _pressed;

        public HomeView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            // Never the keyboard's: see the class comment.
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "Kova";
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += PreferenceChanged;
        }

        protected override void Dispose(bool disposing)
        {
            // A static event: left attached, every window ever made would be kept for good (Kova runs for months).
            if (disposing) Microsoft.Win32.SystemEvents.UserPreferenceChanged -= PreferenceChanged;
            base.Dispose(disposing);
        }

        private void PreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => { _look = Look.Current(); LookChanged?.Invoke(_look); Invalidate(); }); } catch { }
        }

        /// <summary>Windows went light or dark: the window restyles its frame and menu bar with it.</summary>
        public event Action<Look>? LookChanged;

        public Look Look => _look;

        public void Show(HomeState state)
        {
            string now = state.Signature();
            _state = state;
            if (now == _painted) return;
            Invalidate();
        }

        // ---- painting ----

        protected override void OnPaint(PaintEventArgs e)
        {
            _painted = _state.Signature();
            Draw(e.Graphics, ClientRectangle, DeviceDpi / 96f, _look, _state, _targets, _hover, _pressed);
        }

        /// <summary>A picture of the home screen, made without any window: how its look is checked.</summary>
        public static Bitmap Picture(HomeState state, Size size, bool dark, float scale = 1f)
        {
            // No see-through layer: Windows' text drawing leaves one in tatters, and the letters come out ragged.
            var picture = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(picture);
            Draw(g, new Rectangle(Point.Empty, size), scale, Look.Of(dark), state, new List<Target>(), null, null);
            return picture;
        }

        private static void Draw(Graphics g, Rectangle bounds, float s, Look look, HomeState state, List<Target> targets, Target? hover, Target? pressed)
        {
            targets.Clear();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(look.Window);

            using var title = new Font(Look.TitleFace, 19f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            using var heading = new Font(Look.TitleFace, 26f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            using var strong = new Font(Look.TextFace, 16f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            using var body = new Font(Look.TextFace, 14f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var small = new Font(Look.TextFace, 12.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var label = new Font(Look.TextFace, 12f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            using Font? icons = Look.IconFace == null ? null : new Font(Look.IconFace, 22f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using Font? smallIcons = Look.IconFace == null ? null : new Font(Look.IconFace, 16f * s, FontStyle.Regular, GraphicsUnit.Pixel);

            float pad = 24 * s, x = bounds.X + pad, right = bounds.Right - pad, y = bounds.Y + 18 * s;
            float width = Math.Max(1, right - x);

            // ---- the top line: the mark, the name, and what is going on, as a coloured pill
            float mark = 34 * s;
            Look.Mark(g, new RectangleF(x, y, mark, mark), look.Accent, look.AccentText);
            Text(g, "Kova", title, look.Text, new RectangleF(x + mark + 10 * s, y, 120 * s, mark), middle: true);

            (string pill, Color dot) = state.What switch
            {
                HomeState.Kind.Connected => (state.ListenOnly ? "Listening" : state.Controlling ? "Controlling the remote PC" : "Connected", look.Good),
                HomeState.Kind.Hosting => ("Hosting", look.Good),
                HomeState.Kind.Connecting => ("Connecting", look.Warn),
                _ => ("Not connected", look.Soft),
            };
            var pillSize = Measure(g, pill, label);
            float pillW = pillSize.Width + 34 * s, pillH = 28 * s;
            var pillRect = new RectangleF(right - pillW, y + (mark - pillH) / 2, pillW, pillH);
            Look.Fill(g, pillRect, pillH / 2, look.Card, look.Edge);
            using (var brush = new SolidBrush(dot)) g.FillEllipse(brush, pillRect.X + 11 * s, pillRect.Y + pillH / 2 - 4 * s, 8 * s, 8 * s);
            Text(g, pill, label, look.Text, new RectangleF(pillRect.X + 24 * s, pillRect.Y, pillRect.Width - 26 * s, pillH), middle: true);

            y += mark + 22 * s;

            // ---- the headline and the line under it
            Text(g, state.Headline, heading, look.Text, new RectangleF(x, y, width, 34 * s), ellipsis: true);
            y += 38 * s;
            if (state.Detail.Length > 0)
            {
                var need = Measure(g, state.Detail, body, width);
                float h = Math.Min(need.Height + 2 * s, 60 * s);
                Text(g, state.Detail, body, look.Soft, new RectangleF(x, y, width, h), wrap: true);
                y += h + 6 * s;
            }
            y += 14 * s;

            float gap = 14 * s;

            if (state.What is HomeState.Kind.Idle or HomeState.Kind.Connecting)
            {
                // ---- the two ways in, side by side
                float tileW = (width - gap) / 2, tileH = 112 * s;
                Tile(new RectangleF(x, y, tileW, tileH), HomeAction.Connect, Look.IconConnect, "Connect to a PC",
                    "Control another PC from this keyboard and hear everything it plays.", "Ctrl+N", primary: true);
                Tile(new RectangleF(x + tileW + gap, y, tileW, tileH), HomeAction.Host, Look.IconHost, "Host this PC",
                    "Let this PC be controlled from another one, with a password.", "Ctrl+H", primary: false);
                y += tileH + 26 * s;

                // ---- the PCs saved, each one click away
                Text(g, "SAVED PCS", label, look.Soft, new RectangleF(x, y, width, 18 * s));
                y += 26 * s;
                if (state.Saved.Count == 0)
                {
                    var empty = new RectangleF(x, y, width, 64 * s);
                    Look.Fill(g, empty, 10 * s, look.Card, look.Edge);
                    Text(g, "No PCs saved yet. Connect to one and Kova offers to remember it.", body, look.Soft,
                        new RectangleF(empty.X + 18 * s, empty.Y, empty.Width - 36 * s, empty.Height), middle: true, wrap: true);
                }
                else
                {
                    float rowH = 54 * s;
                    for (int i = 0; i < state.Saved.Count; i++)
                    {
                        if (y + rowH > bounds.Bottom - 34 * s)
                        {
                            Text(g, "and " + (state.Saved.Count - i) + " more in File, Connect to a PC", small, look.Soft, new RectangleF(x, y, width, 20 * s));
                            break;
                        }
                        var row = new RectangleF(x, y, width, rowH);
                        var target = new Target(row, HomeAction.ConnectSaved, i);
                        targets.Add(target);
                        Look.Fill(g, row, 10 * s, Shade(target, look.Card), look.Edge);
                        if (icons != null) Text(g, Look.IconPc, icons, look.Accent, new RectangleF(row.X + 14 * s, row.Y, 34 * s, rowH), middle: true);
                        float tx = row.X + (icons != null ? 56 : 18) * s, tw = row.Width - (icons != null ? 56 : 18) * s - 110 * s;
                        var (name, address) = state.Saved[i];
                        bool named = name.Length > 0 && !string.Equals(name, address, StringComparison.OrdinalIgnoreCase);
                        if (named)
                        {
                            Text(g, name, strong, look.Text, new RectangleF(tx, row.Y + 8 * s, tw, 22 * s), ellipsis: true);
                            Text(g, address, small, look.Soft, new RectangleF(tx, row.Y + 30 * s, tw, 18 * s), ellipsis: true);
                        }
                        else Text(g, address, strong, look.Text, new RectangleF(tx, row.Y, tw, rowH), middle: true, ellipsis: true);
                        Text(g, "Connect", label, look.Accent, new RectangleF(row.Right - 104 * s, row.Y, 70 * s, rowH), middle: true, rightAlign: true);
                        if (smallIcons != null) Text(g, Look.IconChevron, smallIcons, look.Accent, new RectangleF(row.Right - 32 * s, row.Y, 22 * s, rowH), middle: true);
                        y += rowH + 8 * s;
                    }
                }
            }
            else if (state.What == HomeState.Kind.Connected)
            {
                // ---- the remote PC's controls, the one used most first and largest
                float bigH = 84 * s;
                if (!state.ListenOnly)
                {
                    Tile(new RectangleF(x, y, width, bigH), HomeAction.ToggleControl, Look.IconKeyboard,
                        state.Controlling ? "Stop controlling the remote PC" : "Control the remote PC",
                        state.Controlling ? "Your keys and mouse are going to the remote PC." : "Send your keys and mouse to it. The same keys bring you back.",
                        "Ctrl+Shift+Enter", primary: !state.Controlling);
                    y += bigH + gap;
                }
                float cellW = (width - gap * 2) / 3, cellH = 92 * s;
                var cells = new List<(HomeAction Action, string Icon, string Title, string Key)>
                {
                    (HomeAction.Screen, Look.IconScreen, state.ScreenOpen ? "Hide the screen" : "Show the screen", "Ctrl+Shift+S"),
                };
                if (!state.ListenOnly)
                {
                    cells.Add((HomeAction.SendFiles, Look.IconSend, "Send files", "Ctrl+I"));
                    cells.Add((HomeAction.GetFiles, Look.IconGet, "Get files", "Ctrl+G"));
                    cells.Add((HomeAction.SendClipboard, Look.IconClipboard, "Send the clipboard", "Ctrl+B"));
                }
                for (int i = 0; i < cells.Count; i++)
                {
                    var cell = new RectangleF(x + (i % 3) * (cellW + gap), y + (i / 3) * (cellH + gap), cellW, cellH);
                    Small(cell, cells[i].Action, cells[i].Icon, cells[i].Title, cells[i].Key, danger: false);
                }
                // Disconnect closes the grid, in the colour of something that ends things.
                int last = cells.Count;
                Small(new RectangleF(x + (last % 3) * (cellW + gap), y + (last / 3) * (cellH + gap), cellW, cellH),
                    HomeAction.Disconnect, Look.IconStop, "Disconnect", "", danger: true);
                y += ((last / 3) + 1) * (cellH + gap);
            }
            else
            {
                // ---- hosting: one thing to do from here, and it ends it
                var card = new RectangleF(x, y, width, 96 * s);
                Look.Fill(g, card, 12 * s, look.Card, look.Edge);
                if (icons != null) Text(g, Look.IconSound, icons, look.Accent, new RectangleF(card.X + 18 * s, card.Y, 34 * s, card.Height), middle: true);
                Text(g, "Other PCs reach this one with its address and password. Kova can stay in the notification area: closing this window keeps it hosting.",
                    body, look.Soft, new RectangleF(card.X + (icons != null ? 62 : 20) * s, card.Y, card.Width - (icons != null ? 82 : 40) * s, card.Height), middle: true, wrap: true);
                y += card.Height + gap;
                Small(new RectangleF(x, y, (width - gap * 2) / 3, 92 * s), HomeAction.StopHosting, Look.IconStop, "Stop hosting", "", danger: true);
                y += 92 * s + gap;
            }

            // ---- files on the move, and the version, along the bottom
            float foot = bounds.Bottom - 26 * s;
            if (state.Transfers.Length > 0)
                Text(g, state.Transfers, small, look.Accent, new RectangleF(x, foot, width - 90 * s, 18 * s), ellipsis: true);
            if (state.Version.Length > 0)
                Text(g, "Version " + state.Version, small, look.Soft, new RectangleF(right - 160 * s, foot, 160 * s, 18 * s), rightAlign: true);

            // ---- the pieces ----

            Color Shade(Target target, Color rest) =>
                pressed != null && pressed.Action == target.Action && pressed.Index == target.Index ? look.CardPressed
                : hover != null && hover.Action == target.Action && hover.Index == target.Index ? look.CardHover : rest;

            void Tile(RectangleF r, HomeAction action, string icon, string name, string about, string key, bool primary)
            {
                var target = new Target(r, action, -1);
                targets.Add(target);
                bool hot = hover != null && hover.Action == action;
                Look.Fill(g, r, 12 * s, Shade(target, look.Card), primary || hot ? look.Accent : look.Edge, primary ? 1.6f * s : 1f);
                float ix = r.X + 18 * s, badge = 44 * s, by = r.Y + (r.Height - badge) / 2;
                Look.Fill(g, new RectangleF(ix, by, badge, badge), 11 * s, primary ? look.Accent : look.AccentSoft);
                if (icons != null) Text(g, icon, icons, primary ? look.AccentText : look.Accent, new RectangleF(ix, by, badge, badge), middle: true, centre: true);
                float tx = ix + badge + 16 * s, tw = r.Right - tx - 16 * s;
                float block = 22 * s + 6 * s + 36 * s, top = r.Y + Math.Max(10 * s, (r.Height - block) / 2);
                Text(g, name, strong, look.Text, new RectangleF(tx, top, tw - 96 * s, 22 * s), ellipsis: true);
                if (key.Length > 0) Text(g, key, small, look.Soft, new RectangleF(r.Right - 124 * s, top + 2 * s, 108 * s, 18 * s), rightAlign: true);
                Text(g, about, small, look.Soft, new RectangleF(tx, top + 28 * s, tw, 36 * s), wrap: true);
            }

            void Small(RectangleF r, HomeAction action, string icon, string name, string key, bool danger)
            {
                var target = new Target(r, action, -1);
                targets.Add(target);
                bool hot = hover != null && hover.Action == action;
                Color ink = danger ? look.Bad : look.Accent;
                Look.Fill(g, r, 12 * s, hot && danger ? look.BadSoft : Shade(target, look.Card), hot ? ink : look.Edge);
                if (icons != null) Text(g, icon, icons, ink, new RectangleF(r.X + 16 * s, r.Y + 14 * s, 30 * s, 28 * s));
                Text(g, name, strong, danger ? look.Bad : look.Text, new RectangleF(r.X + 16 * s, r.Y + 46 * s, r.Width - 28 * s, 22 * s), ellipsis: true);
                if (key.Length > 0) Text(g, key, small, look.Soft, new RectangleF(r.X + 16 * s, r.Y + 68 * s, r.Width - 28 * s, 18 * s), ellipsis: true);
            }
        }

        /// <summary>
        /// Every word on the home screen. Drawn by GDI+ itself, smoothed in greys: Windows' other text drawing
        /// (TextRenderer) mixes its coloured edges with whatever it believes is behind the letters, and on a surface
        /// that is being painted it believes black, which left every dark letter on a light card ragged.
        /// </summary>
        private static void Text(Graphics g, string text, Font font, Color colour, RectangleF area, bool middle = false, bool wrap = false,
            bool ellipsis = false, bool rightAlign = false, bool centre = false)
        {
            using var format = new StringFormat(StringFormat.GenericTypographic);
            format.FormatFlags = wrap ? StringFormatFlags.LineLimit : StringFormatFlags.NoWrap;
            format.Trimming = ellipsis || wrap ? StringTrimming.EllipsisCharacter : StringTrimming.None;
            format.LineAlignment = middle ? StringAlignment.Center : StringAlignment.Near;
            format.Alignment = rightAlign ? StringAlignment.Far : centre ? StringAlignment.Center : StringAlignment.Near;
            using var brush = new SolidBrush(colour);
            g.DrawString(text, font, brush, area, format);
        }

        private static SizeF Measure(Graphics g, string text, Font font, float width = 0)
        {
            using var format = new StringFormat(StringFormat.GenericTypographic);
            format.FormatFlags = width > 0 ? StringFormatFlags.LineLimit | StringFormatFlags.MeasureTrailingSpaces : StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            return width > 0 ? g.MeasureString(text, font, new SizeF(width, 10000), format) : g.MeasureString(text, font, PointF.Empty, format);
        }

        // ---- the mouse ----

        private Target? At(Point p)
        {
            foreach (var target in _targets)
                if (target.Area.Contains(p)) return target;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var now = At(e.Location);
            if (!Equals(now, _hover))
            {
                _hover = now;
                Cursor = now != null ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != null || _pressed != null) { _hover = _pressed = null; Cursor = Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _pressed = At(e.Location);
            if (_pressed != null) Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var was = _pressed;
            _pressed = null;
            if (was == null) return;
            Invalidate();
            // Only a click that ends where it began, as every button behaves.
            if (e.Button == MouseButtons.Left && Equals(At(e.Location), was)) Chosen?.Invoke(was.Action, was.Index);
        }
    }
}
