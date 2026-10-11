using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// The remote screen (File, Show the remote screen; Control Shift S). Off unless opened: nothing is
    /// taken or sent until then, and closing it stops it. Opens maximized, so the picture is as near its
    /// real size as this screen allows, which suits NVDA's OCR (NVDA+R) as well as anyone watching.
    /// Escape goes back to the main window and leaves it on; Control Shift S turns it off (closes it);
    /// F5 asks for a whole new picture; Control Shift Enter controls the remote PC.
    /// Minimized, it pauses: the remote PC takes no pictures while there is nothing to look at.
    /// After a dropped connection it carries on with the next one by itself.
    /// </summary>
    internal sealed class VideoForm : Form
    {
        private readonly Func<Client?> _client;
        private readonly Action<string> _say;
        private readonly Action _controlRemote, _back;
        private readonly ScreenView _view = new();
        private readonly Timer _follow = new() { Interval = 1000 };
        private Client? _watching;
        private VideoStream? _stream;
        private bool _saidShowing, _minimized;
        private string? _saidNote;

        public VideoForm(Func<Client?> client, Action<string> say, Action controlRemote, Action back)
        {
            _client = client;
            _say = say;
            _controlRemote = controlRemote;
            _back = back;
            Text = "Kova - remote screen, " + Rate(0);
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1024, 640);
            WindowState = FormWindowState.Maximized;
            BackColor = Color.Black;
            Controls.Add(_view);
            _follow.Tick += (_, _) => { Follow(); ShowRate(); };
            Shown += (_, _) => { Follow(); _follow.Start(); };
            Resize += (_, _) =>
            {
                // Only when minimized or back: every step of a resize would otherwise ask for a whole picture.
                bool minimized = WindowState == FormWindowState.Minimized;
                if (minimized != _minimized) { _minimized = minimized; _stream?.Pause(minimized); }
            };
            Menus.FocusWhenShown(this, () => _view);
        }

        /// <summary>Frames a second right now: 0 while the remote screen is still (nothing to send).</summary>
        public int PerSecond => _stream?.PerSecond ?? 0;

        public static string Rate(int fps) => fps + (fps == 1 ? " frame" : " frames") + " a second";

        /// <summary>The frames a second in the title bar, always, so NVDA+T reads it (0 while the screen is still).</summary>
        private void ShowRate()
        {
            string t = "Kova - remote screen, " + Rate(PerSecond);
            if (Text != t) Text = t;
        }

        /// <summary>Watching whichever connection is in front now: a new one after a reconnect or a switch of PC.</summary>
        private void Follow()
        {
            var c = _client();
            if (c == _watching && (_stream != null || c == null)) return;
            _stream?.Dispose();
            _stream = null;
            _view.Stream = null;
            _watching = c;
            if (c == null) return;
            if (!c.CanWatch)
            {
                Note(c.ListenOnly ? "A listen-only connection cannot see the remote screen." : "The remote PC's Kova is too old to show its screen. Update it there.");
                return;
            }
            var s = c.WatchScreen();
            if (s == null) return;
            s.Updated += () =>
            {
                if (IsDisposed) return;
                try
                {
                    BeginInvoke(() =>
                    {
                        _view.Note = null;
                        _view.Invalidate();
                        if (!_saidShowing) { _saidShowing = true; _saidNote = null; _say("Showing the remote screen. Escape goes back to the main window; Control Shift S turns it off."); }
                    });
                }
                catch { }
            };
            s.Note += t => { try { BeginInvoke(() => Note(t)); } catch { } };
            if (_minimized) s.Pause(true);
            _stream = s;
            _view.Stream = s;
        }

        private void Note(string text)
        {
            if (text == _saidNote) return;
            _saidNote = text;
            _saidShowing = false;
            _view.Note = text;
            _view.Invalidate();
            _say(text);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { _back(); e.Handled = true; }
            else if (e.KeyCode == Keys.S && e.Control && e.Shift && !e.Alt) { Close(); _say("unchecked. Stopped showing the remote screen."); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { _stream?.Refresh(); _say("A whole new picture."); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && e.Control && e.Shift) { _controlRemote(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _follow.Stop();
            _follow.Dispose();
            _stream?.Dispose();
            _stream = null;
            base.OnFormClosed(e);
        }

        /// <summary>The picture, scaled to fit and keeping its shape; or what the remote PC said instead.</summary>
        private sealed class ScreenView : Control
        {
            public VideoStream? Stream;
            public string? Note;

            public ScreenView()
            {
                Dock = DockStyle.Fill;
                DoubleBuffered = true;
                SetStyle(ControlStyles.Selectable, true);
                TabStop = true;
                AccessibleName = "Remote screen";
                AccessibleRole = AccessibleRole.Graphic;
                AccessibleDescription = "NVDA plus R reads it.";
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Color.Black);
                var canvas = Note == null ? Stream?.Canvas : null; // what the host said, rather than an old picture
                if (canvas != null)
                {
                    lock (canvas.Gate)
                    {
                        var p = canvas.Picture;
                        if (p != null)
                        {
                            double scale = Math.Min((double)ClientSize.Width / p.Width, (double)ClientSize.Height / p.Height);
                            int w = Math.Max(1, (int)(p.Width * scale)), h = Math.Max(1, (int)(p.Height * scale));
                            // Exactly its size when it fits: no blur at all on the text. Otherwise smoothed, the
                            // quick way: up to 60 times a second, the costly way would hold up the window.
                            g.InterpolationMode = Math.Abs(scale - 1) < 0.001 ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(p, new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h));
                            return;
                        }
                    }
                }
                string text = Note ?? "Waiting for the remote screen...";
                TextRenderer.DrawText(g, text, Font, ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
        }
    }
}
