using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// Opens and closes Kova's ports in Windows Firewall. Lists every port Kova has used on this PC (kept in
    /// the settings) and every port Windows Firewall has a Kova or TailRemote rule for, so a port can still be
    /// closed after the port setting has moved on. Remove port closes one and takes it off the list. The port
    /// Kova uses now is marked "in use" and is never closed from here. Clean up old firewall rules removes the
    /// rules Windows piled up for copies of Kova that are gone or no longer used, and old TailRemote rules.
    /// </summary>
    internal sealed class PortEditorForm : Form
    {
        private enum State { Checking, Open, OpenOld, Closed }

        private readonly Settings _settings;
        private readonly IReadOnlyCollection<int> _inUse;
        private readonly ListBox _list = new() { Width = 380, Height = 150 };
        private readonly Button _open = new() { Text = "&Open port", AutoSize = true };
        private readonly Button _close = new() { Text = "C&lose port", AutoSize = true };
        private readonly Button _remove = new() { Text = "&Remove port", AutoSize = true };
        private readonly Button _clean = new() { Text = "Clean &up old firewall rules (checking)", AutoSize = true };
        private readonly TextBox _newPort = new() { Width = 100 };
        private readonly Button _add = new() { Text = "&Add and open", AutoSize = true };
        private readonly Button _done = new() { Text = "&Done", AutoSize = true, DialogResult = DialogResult.OK };
        private readonly Dictionary<int, State> _state = new();
        // A change is waiting on its administrator prompt: no other button may start a second one.
        private bool _busy;
        // Counts changes, so a slow check never writes back an answer older than a change's.
        private int _changes;
        // What the last look at Windows Firewall found (null: not looked yet, or it could not be asked).
        private List<Firewall.Rule>? _rules;

        public PortEditorForm(Settings settings, IReadOnlyCollection<int> inUse)
        {
            _settings = settings;
            _inUse = inUse;
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
            row.Controls.Add(_remove);
            panel.Controls.Add(row);
            panel.Controls.Add(_clean);
            panel.Controls.Add(new Label { Text = "&New port", AutoSize = true });
            _newPort.AccessibleName = "New port";
            panel.Controls.Add(_newPort);
            panel.Controls.Add(_add);
            panel.Controls.Add(_done);
            Controls.Add(panel);
            CancelButton = _done;

            _open.Click += (_, _) => Change(Selected, true);
            _close.Click += (_, _) => Change(Selected, false);
            _remove.Click += (_, _) => Remove(Selected);
            _clean.Click += (_, _) => CleanUp();
            _add.Click += (_, _) => Add();
            _newPort.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (_add.Enabled) Add(); } }; // not while a change still waits on its administrator prompt
            _list.SelectedIndexChanged += (_, _) => UpdateButtons();

            foreach (int p in _settings.KnownPorts.Concat(_inUse)) _state[p] = State.Checking;
            Refill();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _list.Focus();
            await Look();
        }

        /// <summary>Asks Windows Firewall what it has for Kova (off the window's thread: it takes a moment), and shows it.</summary>
        private async Task Look()
        {
            int before = _changes;
            var inUse = _inUse;
            // Off the window's thread, the clean-up list too: it looks at every running program and every rule's file,
            // and one on a network drive that does not answer would freeze the window.
            var (rules, stale) = await Task.Run(() =>
            {
                var found = Firewall.Scan();
                return (found, found == null ? new List<Firewall.Rule>() : Firewall.Stale(found, inUse, thorough: true));
            });
            if (IsDisposed || _changes != before) return; // changed meanwhile: that change looks again
            _rules = rules;
            _stale = stale;
            if (rules == null)
            {
                // Windows could not be asked all at once: each port on its own, as before.
                foreach (int p in _state.Keys.ToList())
                {
                    bool open = await Task.Run(() => Firewall.IsOpen(p));
                    if (IsDisposed || _changes != before) return;
                    _state[p] = open ? State.Open : State.Closed;
                }
            }
            else
            {
                foreach (int p in rules.Select(r => r.Port).OfType<int>()) _state.TryAdd(p, State.Checking);
                foreach (int p in _state.Keys.ToList())
                    _state[p] = rules.Any(r => r.Port == p && !r.Old) ? State.Open : rules.Any(r => r.Port == p) ? State.OpenOld : State.Closed;
            }
            Refill();
        }

        // What Clean up old firewall rules would remove, worked out with the last look.
        private List<Firewall.Rule> _stale = new();
        private List<Firewall.Rule> Stale => _rules == null ? new() : _stale;

        private int? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _state.Count
            ? _state.Keys.OrderBy(p => p).ElementAt(_list.SelectedIndex) : null;

        private void Refill()
        {
            int keep = Math.Max(0, _list.SelectedIndex);
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (int p in _state.Keys.OrderBy(p => p))
            {
                string what = _state[p] switch
                {
                    State.Open => "open",
                    State.OpenOld => "open by an old TailRemote rule",
                    State.Closed => "closed",
                    _ => "checking",
                };
                _list.Items.Add(p + ", " + what + (_inUse.Contains(p) ? ", in use" : ""));
            }
            if (_list.Items.Count == 0) _list.Items.Add("No ports yet. Type one under New port and press Add and open.");
            _list.SelectedIndex = Math.Min(keep, _list.Items.Count - 1);
            _list.EndUpdate();
            int stale = Stale.Count;
            _clean.Text = _rules == null ? "Clean &up old firewall rules" + (_state.Values.Contains(State.Checking) ? " (checking)" : "")
                : stale == 0 ? "Clean &up old firewall rules (there are none)"
                : "Clean &up " + Count(stale, "old firewall rule", "old firewall rules");
            UpdateButtons();
        }

        private static string Count(int n, string one, string many) => n == 1 ? "one " + one : n + " " + many;

        private void UpdateButtons()
        {
            int? p = Selected;
            _open.Enabled = !_busy && p != null && _state[p.Value] != State.Open;
            // Left on for the port in use: pressing it says why it stays open (a button that is off is skipped by Tab, and says nothing).
            _close.Enabled = !_busy && p != null && _state[p.Value] != State.Closed;
            _remove.Enabled = !_busy && p != null;
            _clean.Enabled = _add.Enabled = !_busy;
        }

        private bool RefuseInUse(int p, string doing)
        {
            if (!_inUse.Contains(p)) return false;
            Speech.Speak("Port " + p + " is the port Kova uses now, so it stays open. To " + doing + " it, change Port in the main window first.");
            return true;
        }

        private void Add()
        {
            string? problem = Protocol.PortProblem(_newPort.Text.Trim(), out int port);
            if (problem != null) { MessageBox.Show(this, problem, "Port editor"); _newPort.Focus(); return; } // NVDA reads the box; speaking too said it twice
            // Said, not lost in silence: the port is still opened, but Port editor may not list it next time.
            if (!Remember(_settings, port))
                Speech.Speak((_settings.SaveProblem ?? "Kova could not save its settings.") + " Port editor may not list port " + port + " next time.", interrupt: false);
            if (!_state.ContainsKey(port)) _state[port] = State.Closed;
            _newPort.Clear();
            Refill();
            _list.SelectedIndex = _state.Keys.OrderBy(p => p).ToList().IndexOf(port);
            Change(port, true);
        }

        private async void Change(int? port, bool open)
        {
            if (port == null || _busy) return;
            int p = port.Value;
            if (!open && RefuseInUse(p, "close")) return;
            Speech.Speak((open ? "Opening port " : "Closing port ") + p + ". Windows asks for administrator permission.");
            var result = await Busy(() => Firewall.ChangeAsync(p, open));
            if (IsDisposed) return;
            await Look();
            if (IsDisposed) return;
            _list.Focus();
            Speech.Speak(result switch
            {
                ChangeResult.Done => "Port " + p + (open ? " is open." : " is closed."),
                ChangeResult.InUse => "Port " + p + " is the port the Kova service hosts on, so it stays open.",
                ChangeResult.Declined => "Port " + p + " was not changed: administrator permission was not given.",
                _ => "Port " + p + " could not be " + (open ? "opened" : "closed") + ": Windows Firewall did not take the change.",
            });
        }

        /// <summary>Closes the port if it is open, and takes it off the list for good.</summary>
        private async void Remove(int? port)
        {
            if (port == null || _busy) return;
            int p = port.Value;
            if (RefuseInUse(p, "remove")) return;
            var result = ChangeResult.Done;
            if (_state[p] != State.Closed)
            {
                Speech.Speak("Closing port " + p + " and taking it off the list. Windows asks for administrator permission.");
                result = await Busy(() => Firewall.ChangeAsync(p, false));
                if (IsDisposed) return;
            }
            bool saved = true;
            if (result == ChangeResult.Done)
            {
                saved = Forget(_settings, p);
                _state.Remove(p);
            }
            await Look();
            if (IsDisposed) return;
            _list.Focus();
            Speech.Speak(result switch
            {
                ChangeResult.Done => "Port " + p + " is closed and off the list." + (saved ? "" : " " + (_settings.SaveProblem ?? "Kova could not save its settings.") + " It may be listed again next time."),
                ChangeResult.InUse => "Port " + p + " is the port the Kova service hosts on, so it stays open.",
                ChangeResult.Declined => "Port " + p + " was not changed: administrator permission was not given.",
                _ => "Port " + p + " could not be closed: Windows Firewall did not take the change.",
            });
        }

        private async void CleanUp()
        {
            if (_busy) return;
            if (_rules == null)
            {
                Speech.Speak(_state.Values.Contains(State.Checking) ? "Still looking at Windows Firewall. Try again in a moment." : "Kova could not ask Windows Firewall for its rules, so nothing can be cleaned up.");
                return;
            }
            var stale = Stale;
            if (stale.Count == 0) { Speech.Speak("There are no old Kova firewall rules to clean up."); return; }
            int copies = stale.Count(r => r.Port == null);
            var oldPorts = stale.Where(r => r.Port != null).Select(r => r.Port!.Value).Distinct().OrderBy(p => p).ToList();
            string what = (copies > 0 ? Count(copies, "rule for a copy of Kova that is gone or no longer used", "rules for copies of Kova that are gone or no longer used") : "") +
                (copies > 0 && oldPorts.Count > 0 ? ", and " : "") +
                (oldPorts.Count > 0 ? "old TailRemote rules for " + (oldPorts.Count == 1 ? "port " + oldPorts[0] : "ports " + string.Join(", ", oldPorts.Take(oldPorts.Count - 1)) + " and " + oldPorts[^1]) : "");
            string keep = _inUse.Count == 0 ? "" : " " + (_inUse.Count == 1 ? "Port " + _inUse.First() + ", which Kova uses now, stays open." : "Ports " + string.Join(" and ", _inUse.OrderBy(p => p)) + ", which Kova uses now, stay open.");
            if (MessageBox.Show(this, "Kova found " + what + "." + keep + " Kova's own port rules are left as they are." + Environment.NewLine + Environment.NewLine +
                    "Remove " + (stale.Count == 1 ? "it" : "them") + "? Windows asks for administrator permission.",
                    "Clean up old firewall rules", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            Speech.Speak("Cleaning up. Windows asks for administrator permission.");
            var result = await Busy(() => Firewall.CleanAsync(_inUse));
            if (IsDisposed) return;
            await Look();
            if (IsDisposed) return;
            _clean.Focus();
            int left = Stale.Count;
            Speech.Speak(result switch
            {
                ChangeResult.Done => "Removed " + Count(stale.Count, "old firewall rule", "old firewall rules") + ".",
                ChangeResult.Declined => "Nothing was removed: administrator permission was not given.",
                _ => left < stale.Count ? "Removed " + Count(stale.Count - left, "old firewall rule", "old firewall rules") + ", but Windows Firewall kept " + Count(left, "other", "others") + "."
                    : "Nothing was removed: Windows Firewall did not take the change.",
            });
        }

        /// <summary>Runs a change with every button off until it is over: a second press while the prompt is up used to start a second change.</summary>
        private async Task<ChangeResult> Busy(Func<Task<ChangeResult>> change)
        {
            _busy = true;
            _changes++;
            UpdateButtons();
            try { return await change(); }
            finally { _busy = false; _changes++; if (!IsDisposed) UpdateButtons(); }
        }

        /// <summary>Adds a port to the record of ports Kova has used. False if that could not be saved (Settings.SaveProblem says why).</summary>
        public static bool Remember(Settings settings, int port)
        {
            if (settings.KnownPorts.Contains(port)) return true;
            settings.KnownPorts.Add(port);
            try { return settings.Save(retries: 1); } catch { return false; } // on the window's thread: not frozen for long
        }

        /// <summary>Takes a closed port off the record. False if that could not be saved.</summary>
        public static bool Forget(Settings settings, int port)
        {
            if (!settings.KnownPorts.Remove(port)) return true;
            try { return settings.Save(retries: 1); } catch { return false; }
        }
    }
}
