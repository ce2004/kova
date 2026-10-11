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
                bool open = await Task.Run(() => Firewall.IsOpen(p));
                if (IsDisposed) return;
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
            _open.Enabled = p != null && _state[p.Value] != true;
            _close.Enabled = p != null && _state[p.Value] != false;
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
            if (port == null) return;
            int p = port.Value;
            Speech.Speak((open ? "Opening port " : "Closing port ") + p + ". Windows asks for administrator permission.");
            _open.Enabled = _close.Enabled = _add.Enabled = false;
            bool ok = await Firewall.SetAsync(p, open);
            if (IsDisposed) return;
            // Checking takes a moment; do it off the window's thread, as OnShown does.
            _state[p] = await Task.Run(() => Firewall.IsOpen(p));
            if (IsDisposed) return;
            Refill();
            _add.Enabled = true;
            _list.Focus();
            Speech.Speak(ok
                ? "Port " + p + (open ? " is open." : " is closed.")
                : "Port " + p + " was not changed: administrator permission was not given.");
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
