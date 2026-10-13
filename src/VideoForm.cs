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
    /// F5 asks for a whole new picture; Control Shift Enter controls the remote PC; 1 to 9, Page Down and
    /// Page Up choose which of the remote PC's screens is shown.
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

        /// <summary>
        /// The frames a second in the title bar, always, so NVDA+T reads it (0 while the screen is still);
        /// and which screen is shown when the remote PC has more than one.
        /// </summary>
        private void ShowRate()
        {
            string which = _stream?.Screens is { Count: > 1 } c ? "screen " + (c.Shown + 1) + " of " + c.Count + ", " : "";
            string t = "Kova - remote screen, " + which + Rate(PerSecond);
            if (Text != t) Text = t;
        }

        // A screen was chosen here: the host's answer is said (once), rather than every time it says which.
        private bool _sayScreen;

        /// <summary>
        /// Shows another of the remote PC's screens (0: the first, the main one). What is known already is
        /// said here (only one screen, no such screen, that one already); otherwise the host's answer is.
        /// </summary>
        private void ChooseScreen(int screen)
        {
            var s = _stream;
            if (s == null) { _say("Not watching the remote screen right now."); return; }
            var c = s.Screens;
            if (c != null)
            {
                if (c.Count == 1) { _say("The remote PC has only one screen."); return; }
                if (screen >= c.Count) { _say("There is no screen " + (screen + 1) + ". The remote PC has " + c.Count + " screens."); return; }
                if (screen == c.Shown && !c.Refused) { _say(c.Say()); return; }
            }
            _sayScreen = true;
            s.ChooseScreen(screen);
        }

        /// <summary>The next or previous screen, round and round (Page Down, Page Up).</summary>
        private void NextScreen(int by)
        {
            var c = _stream?.Screens;
            if (c == null) { ChooseScreen(0); return; }
            ChooseScreen(((c.Shown + by) % c.Count + c.Count) % c.Count);
        }

        private int? _lastShown;
        private System.Net.IPEndPoint? _keptFor; // the PC the last stream watched, and the screen chosen there (-1: none)
        private int _kept = -1;
        private int _pending; // a redraw is already on its way to the window's thread (Interlocked)

        private void ScreenTold(ScreenChoice c)
        {
            ShowRate();
            int? before = _lastShown;
            _lastShown = c.Shown;
            if (_sayScreen) { _sayScreen = false; _say(c.Say()); return; }
            // Not asked for here: the host says the screen being shown was unplugged on the remote PC, and it
            // went back to the main one. Only the host's word counts: a new line first shows the main one
            // until our choice reaches it, and unplugging another screen renumbers this one.
            if (c.Gone && !c.Refused) _say((before is int was && was != c.Shown ? "Screen " + (was + 1) : "The screen shown") + " is gone from the remote PC. " + c.Say());
        }

        /// <summary>Watching whichever connection is in front now: a new one after a reconnect or a switch of PC.</summary>
        private void Follow()
        {
            var c = _client();
            if (c == _watching && (_stream != null || c == null)) return;
            // A reconnect to the same PC keeps the screen chosen; another PC starts on its main one. Kept
            // past the stream: a dropped connection is first no connection at all (the stream goes), then
            // a new one, so comparing with the stream just before lost the choice on every reconnect.
            if (_stream != null && _watching != null) { _keptFor = _watching.HostEndPoint; _kept = _stream.Chosen; }
            int keep = c != null && _keptFor != null && _keptFor.Equals(c.HostEndPoint) ? _kept : -1;
            _stream?.Dispose();
            _stream = null;
            _view.Stream = null;
            _watching = c;
            _lastShown = null;
            if (keep < 0) _sayScreen = false; // a choice just made on the same PC is still answered, by the new stream
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
                // One redraw waiting at a time: up to 60 pictures a second would otherwise queue one each
                // behind a window that is busy (or stuck), without end. The one waiting draws the newest.
                if (System.Threading.Interlocked.Exchange(ref _pending, 1) == 1) return;
                try
                {
                    BeginInvoke(() =>
                    {
                        System.Threading.Volatile.Write(ref _pending, 0);
                        _view.Note = null;
                        _view.Invalidate();
                        if (!_saidShowing)
                        {
                            _saidShowing = true; _saidNote = null;
                            // The host says how many screens it has before its first picture: the keys are
                            // only mentioned where there is a choice to make.
                            int count = s.Screens?.Count ?? 1;
                            _say("Showing the remote screen. Escape goes back to the main window; Control Shift S turns it off."
                                + (count > 1 ? " The remote PC has " + count + " screens: Page Down shows the next." : ""));
                        }
                    });
                }
                catch { System.Threading.Volatile.Write(ref _pending, 0); }
            };
            s.Note += t => { try { BeginInvoke(() => Note(t)); } catch { } };
            s.ScreenTold += c => { try { BeginInvoke(() => { if (_stream == s) ScreenTold(c); }); } catch { } };
            if (_minimized) s.Pause(true);
            if (keep >= 0) s.ChooseScreen(keep);
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
            // The remote PC's screens: 1 to 9 pick one (on the number row or the number pad), Page Down and
            // Page Up go to the next and the previous.
            else if (e.Modifiers == Keys.None && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9) { ChooseScreen(e.KeyCode - Keys.D1); e.Handled = true; }
            else if (e.Modifiers == Keys.None && e.KeyCode >= Keys.NumPad1 && e.KeyCode <= Keys.NumPad9) { ChooseScreen(e.KeyCode - Keys.NumPad1); e.Handled = true; }
            else if (e.Modifiers == Keys.None && e.KeyCode == Keys.PageDown) { NextScreen(1); e.Handled = true; }
            else if (e.Modifiers == Keys.None && e.KeyCode == Keys.PageUp) { NextScreen(-1); e.Handled = true; }
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
