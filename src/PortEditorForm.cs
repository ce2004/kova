using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// Opens and closes TailRemote's ports in Windows Firewall. Lists every port
    /// TailRemote has ever used on this PC (kept in the settings), so a port can
    /// still be closed after the port setting has changed. Only those ports can
    /// be opened or closed here.
    /// </summary>
    internal sealed class PortEditorForm : Form
    {
        private readonly Settings _settings;
        private readonly ListBox _list = new() { Width = 320, Height = 140 };
        private readonly Button _open = new() { Text = "&Open port", AutoSize = true };
        private readonly Button _close = new() { Text = "C&lose port", AutoSize = true };
        private readonly TextBox _newPort = new() { Width = 100 };
        private readonly Button _add = new() { Text = "&Add and open", AutoSize = true };
        private readonly Button _done = new() { Text = "&Done", AutoSize = true, DialogResult = DialogResult.OK };
        private readonly Dictionary<int, bool?> _state = new();
        // A change is waiting on its administrator prompt: no other button may start a second one.
        private bool _busy;
        // Counts changes per port, so the slow first check never writes back an answer older than a change's.
        private readonly Dictionary<int, int> _changes = new();

        public PortEditorForm(Settings settings)
        {
            _settings = settings;
            Text = "Port editor";
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(12) };
            panel.Controls.Add(new Label { Text = "&Ports Kova has used, and whether Windows Firewall lets them in", AutoSize = true });
            _list.AccessibleName = "Ports Kova has used";
            panel.Controls.Add(_list);
            Menus.FocusWhenShown(this, () => _list);
            var row = new FlowLayoutPanel { AutoSize = true };
            row.Controls.Add(_open);
            row.Controls.Add(_close);
            panel.Controls.Add(row);
            panel.Controls.Add(new Label { Text = "&New port", AutoSize = true });
            _newPort.AccessibleName = "New port";
            panel.Controls.Add(_newPort);
            panel.Controls.Add(_add);
            panel.Controls.Add(_done);
            Controls.Add(panel);
            CancelButton = _done;

            _open.Click += (_, _) => Change(Selected, true);
            _close.Click += (_, _) => Change(Selected, false);
            _add.Click += (_, _) => Add();
            _newPort.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (_add.Enabled) Add(); } }; // not while a change still waits on its administrator prompt
            _list.SelectedIndexChanged += (_, _) => UpdateButtons();

            foreach (int p in _settings.KnownPorts) _state[p] = null;
            Refill();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _list.Focus();
            // Checking takes a moment per port; do it off the window's thread.
            foreach (int p in _state.Keys.ToList())
            {
                int before = _changes.GetValueOrDefault(p);
                bool open = await Task.Run(() => Firewall.IsOpen(p));
                if (IsDisposed) return;
                if (_changes.GetValueOrDefault(p) != before) continue; // opened or closed meanwhile: that answer is newer
                _state[p] = open;
                Refill();
            }
        }

        private int? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _state.Count
            ? _state.Keys.OrderBy(p => p).ElementAt(_list.SelectedIndex) : null;

        private void Refill()
        {
            int keep = Math.Max(0, _list.SelectedIndex);
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (int p in _state.Keys.OrderBy(p => p))
            {
                string what = _state[p] switch { true => "open", false => "closed", null => "checking" };
                _list.Items.Add(p + ", " + what);
            }
            if (_list.Items.Count == 0) _list.Items.Add("No ports yet. Type one under New port and press Add and open.");
            _list.SelectedIndex = Math.Min(keep, _list.Items.Count - 1);
            _list.EndUpdate();
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            int? p = Selected;
            _open.Enabled = !_busy && p != null && _state[p.Value] != true;
            _close.Enabled = !_busy && p != null && _state[p.Value] != false;
            _add.Enabled = !_busy;
        }

        private void Add()
        {
            string? problem = Protocol.PortProblem(_newPort.Text.Trim(), out int port);
            if (problem != null) { MessageBox.Show(this, problem, "Port editor"); _newPort.Focus(); return; } // NVDA reads the box; speaking too said it twice
            Remember(_settings, port);
            if (!_state.ContainsKey(port)) _state[port] = false;
            _newPort.Clear();
            Refill();
            _list.SelectedIndex = _state.Keys.OrderBy(p => p).ToList().IndexOf(port);
            Change(port, true);
        }

        private async void Change(int? port, bool open)
        {
            if (port == null || _busy) return;
            int p = port.Value;
            Speech.Speak((open ? "Opening port " : "Closing port ") + p + ". Windows asks for administrator permission.");
            // Kept off until the change is over: selecting another port used to turn Open and Close
            // back on while the prompt was still up, and a second press started a second change.
            _busy = true;
            _changes[p] = _changes.GetValueOrDefault(p) + 1;
            UpdateButtons();
            ChangeResult result;
            try
            {
                result = await Firewall.ChangeAsync(p, open);
                if (IsDisposed) return;
                // Checking takes a moment; do it off the window's thread, as OnShown does.
                _state[p] = await Task.Run(() => Firewall.IsOpen(p));
                if (IsDisposed) return;
            }
            finally { _busy = false; }
            _changes[p]++;
            Refill();
            _list.Focus();
            Speech.Speak(result switch
            {
                ChangeResult.Done => "Port " + p + (open ? " is open." : " is closed."),
                ChangeResult.Declined => "Port " + p + " was not changed: administrator permission was not given.",
                _ => "Port " + p + " could not be " + (open ? "opened" : "closed") + ": Windows Firewall did not take the change.",
            });
        }

        /// <summary>Adds a port to the record of ports TailRemote has used.</summary>
        public static void Remember(Settings settings, int port)
        {
            if (settings.KnownPorts.Contains(port)) return;
            settings.KnownPorts.Add(port);
            settings.Save();
        }
    }
}
