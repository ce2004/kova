using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KovaRelay
{
    /// <summary>
    /// The relay's one window. Tab goes: status, port, Use this port, Stop/Start, Allow new host names, hosts list,
    /// Forget this host name, Start with Windows, Open the log, Exit the relay. Escape or the close button only hide
    /// it; the relay keeps running in the tray.
    /// </summary>
    internal sealed class OptionsForm : Form
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "Kova Relay";

        private readonly TrayApp _app;
        private readonly TextBox _status = new();
        private readonly TextBox _port = new();
        private readonly Button _usePort = new();
        private readonly Button _startStop = new();
        private readonly SpokenCheckBox _allowNew = new();
        private readonly ListBox _hosts = new();
        private readonly Button _forget = new();
        private readonly SpokenCheckBox _startWithWindows = new();
        private readonly SpokenCheckBox _asService = new();
        private readonly Button _openLog = new();
        private readonly Button _exit = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
        private List<HostRow> _rows = new();
        private bool _settingChecks;
        private string? _note;          // the last thing a button did, shown at the end of the status
        public bool ReallyClose;

        public OptionsForm(TrayApp app)
        {
            _app = app;
            Text = "Kova Relay";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Font = SystemFonts.MessageBoxFont ?? Font;
            Icon = app.AppIcon;

            int tab = 0;
            int x = 12, w = 440, y = 10;
            Label L(string text, int top)
            {
                var l = new Label { Text = text, AutoSize = true, Left = x, Top = top, TabIndex = tab++ };
                Controls.Add(l);
                return l;
            }

            L("&Status", y); y += 20;
            _status.SetBounds(x, y, w, 64);
            _status.Multiline = true; _status.ReadOnly = true; _status.TabStop = true;
            _status.BackColor = SystemColors.Window;
            _status.AccessibleName = "Status";
            _status.TabIndex = tab++;
            Controls.Add(_status);
            y += 74;

            L("&Port", y + 4);
            _port.SetBounds(x + 50, y, 80, 24);
            _port.AccessibleName = "Port";
            _port.MaxLength = 5;
            _port.TabIndex = tab++;
            _port.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; UsePort(); } };
            Controls.Add(_port);
            _usePort.Text = "&Use this port";
            _usePort.SetBounds(x + 140, y - 1, 130, 27);
            _usePort.TabIndex = tab++;
            _usePort.Click += (_, _) => UsePort();
            Controls.Add(_usePort);
            y += 36;

            _startStop.Text = "Stop the &relay";
            _startStop.SetBounds(x, y, 150, 27);
            _startStop.TabIndex = tab++;
            _startStop.Click += (_, _) => StartStop();
            Controls.Add(_startStop);
            y += 36;

            _allowNew.Text = "&Allow new host names";
            _allowNew.AutoSize = true;
            _allowNew.Location = new Point(x, y);
            _allowNew.TabIndex = tab++;
            _allowNew.CheckedChanged += (_, _) => AllowNewChanged();
            Controls.Add(_allowNew);
            y += 30;

            L("&Hosts", y); y += 20;
            _hosts.SetBounds(x, y, w, 120);
            _hosts.AccessibleName = "Hosts";
            _hosts.IntegralHeight = false;
            _hosts.TabIndex = tab++;
            Controls.Add(_hosts);
            y += 128;

            _forget.Text = "&Forget this host name";
            _forget.SetBounds(x, y, 180, 27);
            _forget.TabIndex = tab++;
            _forget.Click += (_, _) => Forget();
            Controls.Add(_forget);
            y += 38;

            _startWithWindows.Text = "Start with &Windows";
            _startWithWindows.AutoSize = true;
            _startWithWindows.Location = new Point(x, y);
            _startWithWindows.TabIndex = tab++;
            _startWithWindows.CheckedChanged += (_, _) => StartWithWindowsChanged();
            Controls.Add(_startWithWindows);
            y += 30;

            _asService.Text = "Run as a Windows ser&vice, starts when Windows starts";
            _asService.AutoSize = true;
            _asService.Location = new Point(x, y);
            _asService.TabIndex = tab++;
            _asService.CheckedChanged += (_, _) => AsServiceChanged();
            Controls.Add(_asService);
            y += 32;

            _openLog.Text = "Open the &log";
            _openLog.SetBounds(x, y, 130, 27);
            _openLog.TabIndex = tab++;
            _openLog.Click += (_, _) => OpenLog();
            Controls.Add(_openLog);
            _exit.Text = "E&xit the relay";
            _exit.SetBounds(x + 140, y, 300, 27);
            _exit.TabIndex = tab++;
            _exit.Click += (_, _) => _app.ExitAll();
            Controls.Add(_exit);
            y += 40;

            ClientSize = new Size(w + 2 * x, y);
            _timer.Tick += (_, _) => RefreshState();
        }

        /// <summary>Shows the window (or brings it forward) with the status box focused.</summary>
        public void ShowAndFocus()
        {
            SetChecks();
            _port.Text = _app.Current(null).Snap.Port.ToString(CultureInfo.InvariantCulture);
            RefreshState(force: true);
            // ActiveControl, not only Focus(): whenever Windows activates the window (now or a moment later, once it
            // is allowed to come forward), WinForms puts the focus back on the active control.
            ActiveControl = _status;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            TrayIcon.SetForegroundWindow(Handle);
            _status.Focus();
            _status.Select(0, 0);
            _timer.Start();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { HideWindow(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ReallyClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideWindow(); return; }
            _timer.Stop();
            base.OnFormClosing(e);
        }

        private void HideWindow()
        {
            _timer.Stop();
            _note = null;
            Hide();
        }

        private void SetChecks()
        {
            _settingChecks = true;
            try
            {
                TrayApp.View v = _app.Current(null);
                _allowNew.Checked = v.AllowNew;
                _startWithWindows.Checked = ReadStartWithWindows();
                _asService.Checked = v.Service;
            }
            finally { _settingChecks = false; }
        }

        /// <summary>Every 2 seconds: the status, the button and the list, each changed only when its text changed.</summary>
        private void RefreshState(bool force = false)
        {
            if (_busy) return;   // an administrator prompt is open: leave everything as it is until it is answered
            TrayApp.View view = _app.Current(_note);
            RelaySnapshot snap = view.Snap;
            string text = view.Text;
            if (force || _status.Text != text)
            {
                _status.Text = text;
                if (force) _status.Select(0, 0);
            }
            string ss = snap.Running ? "Stop the &relay" : "Start the &relay";
            if (_startStop.Text != ss) _startStop.Text = ss;
            string ex = view.Service ? "E&xit the tray icon (the service keeps running)" : "E&xit the relay";
            if (_exit.Text != ex) _exit.Text = ex;

            var rows = snap.Rows.ToList();
            string[] items = rows.Count == 0 ? new[] { "No hosts have registered yet." } : rows.Select(RowText).ToArray();
            string[] now = _hosts.Items.Cast<string>().ToArray();
            if (force || !items.SequenceEqual(now))
            {
                string? selectedName = _hosts.SelectedIndex >= 0 && _hosts.SelectedIndex < _rows.Count ? _rows[_hosts.SelectedIndex].Name : null;
                int keepIndex = Math.Max(0, _hosts.SelectedIndex);
                _hosts.BeginUpdate();
                _hosts.Items.Clear();
                _hosts.Items.AddRange(items);
                int sel = selectedName is null ? -1 : rows.FindIndex(r => string.Equals(r.Name, selectedName, StringComparison.OrdinalIgnoreCase));
                _hosts.SelectedIndex = sel >= 0 ? sel : Math.Min(keepIndex, items.Length - 1);
                _hosts.EndUpdate();
            }
            _rows = rows;
        }

        internal static string StatusText(RelaySnapshot s, string? filesProblem, string? note)
        {
            var parts = new List<string>();
            if (s.Running)
            {
                parts.Add($"Running on port {s.Port}.");
                parts.Add(CountsText(s));
            }
            else if (s.Problem is not null) parts.Add("The relay is not running. " + s.Problem);
            else parts.Add("The relay is stopped. Press Start the relay to start it again.");
            if (filesProblem is not null) parts.Add(filesProblem);
            if (note is not null) parts.Add(note);
            return string.Join(" ", parts);
        }

        internal static string CountsText(RelaySnapshot s) =>
            (s.Hosts switch { 0 => "No hosts are connected.", 1 => "1 host is connected.", _ => $"{s.Hosts} hosts are connected." }) + " " +
            (s.ControllingPcs switch
            {
                0 => "No controlling PCs are connected now.",
                1 => "1 controlling PC is connected now.",
                _ => $"{s.ControllingPcs} controlling PCs are connected now.",
            });

        internal static string RowText(HostRow r)
        {
            if (r.Connected)
                return r.ControllingPcs switch
                {
                    0 => $"{r.Name}, connected",
                    1 => $"{r.Name}, connected, 1 controlling PC",
                    _ => $"{r.Name}, connected, {r.ControllingPcs} controlling PCs",
                };
            return $"{r.Name}, not connected, last seen {SpokenDate(r.LastSeenUtc)}";
        }

        internal static string SpokenDate(DateTime utc) =>
            utc.ToLocalTime().ToString("d MMMM yyyy, h:mm tt", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------ actions

        private void UsePort()
        {
            string t = _port.Text.Trim();
            if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show(this, "The port must be a number from 1 to 65535.", "Kova Relay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _port.Focus(); _port.SelectAll();
                return;
            }
            if (_app.ServiceMode)
            {
                Elevated($"--service-port {_app.ServiceName} {port}", r => r == RelayService.Result.Ok
                    ? $"The relay service now uses port {port}."
                    : r == RelayService.Result.WouldNotStart ? $"Port {port} is saved, but the service did not start on it. Another program may be using that port." : RelayService.Say(r, ""));
                return;
            }
            RelayServer server = _app.Server;
            if (server.Running && server.Port == port)
            {
                _note = $"The relay is already using port {port}.";
                RefreshState(force: true);
                MessageBox.Show(this, _note, "Kova Relay", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int old = server.Port;
            bool wasRunning = server.Running;
            string? why = server.Start(port);
            if (why is not null)
            {
                if (wasRunning) server.Start(old);
                _note = wasRunning ? $"Still using port {old}." : null;
                RefreshState(force: true);
                MessageBox.Show(this, why, "Kova Relay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _port.Focus(); _port.SelectAll();
                return;
            }
            bool saved = _app.Files.WritePort(port);
            _note = saved ? $"The relay now uses port {port}." : $"The relay now uses port {port}, but it could not save that in port.txt, so it will go back to the old port when it restarts.";
            _app.UpdateTip();
            RefreshState(force: true);
            MessageBox.Show(this, _note, "Kova Relay", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void StartStop()
        {
            if (_app.ServiceMode)
            {
                bool running = RelayService.State(_app.ServiceName) == RelayService.Running;
                Elevated($"--service-control {_app.ServiceName} {(running ? "stop" : "start")}", r => r == RelayService.Result.Ok
                    ? (running ? "The relay service is stopped. It starts again when Windows starts, or when you press Start the relay." : "The relay service is running again.")
                    : RelayService.Say(r, ""));
                return;
            }
            RelayServer server = _app.Server;
            if (server.Running)
            {
                server.Stop();
                _note = "The relay is stopped. Hosts and controlling PCs cannot reach each other through it until you start it again.";
            }
            else
            {
                string? why = server.Start(server.Port);
                _note = why is null ? "The relay is running again." : null;
                if (why is not null) MessageBox.Show(this, why, "Kova Relay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            _app.UpdateTip();
            RefreshState(force: true);
        }

        private void AllowNewChanged()
        {
            if (_settingChecks) return;
            if (_app.ServiceMode)
            {
                bool want = _allowNew.Checked;
                Elevated($"--service-set {_app.ServiceName} {(want ? "yes" : "no")}", r => r == RelayService.Result.Ok
                    ? (want ? "The relay service allows new host names." : "The relay service no longer takes new host names.")
                    : RelayService.Say(r, ""), quietSuccess: true);
                return;
            }
            _app.Server.AllowNewNames = _allowNew.Checked;
            _app.Log.Write(_allowNew.Checked ? "New host names are allowed." : "New host names are turned off.");
        }

        private void Forget()
        {
            int i = _hosts.SelectedIndex;
            if (i < 0 || i >= _rows.Count)
            {
                MessageBox.Show(this, "There is no host name to forget. Choose one in the Hosts list first.", "Kova Relay", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _hosts.Focus();
                return;
            }
            HostRow row = _rows[i];
            string question = row.Connected
                ? $"Forget the host name {row.Name}? That host is connected now and will be disconnected. Any host can then take the name."
                : $"Forget the host name {row.Name}? Any host can then take the name.";
            if (MessageBox.Show(this, question, "Kova Relay", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            if (_app.ServiceMode)
            {
                Elevated($"--service-forget {_app.ServiceName} {row.Name}", r => r == RelayService.Result.Ok
                    ? $"The relay service is forgetting the host name {row.Name}." : RelayService.Say(r, ""), quietSuccess: true);
                return;
            }
            _note = _app.Server.Forget(row.Name) ? $"Forgot the host name {row.Name}." : $"The host name {row.Name} was already gone.";
            RefreshState(force: true);
            _hosts.Focus();
        }

        private static string ExeQuoted => "\"" + Environment.ProcessPath + "\"";

        internal static bool ReadStartWithWindows()
        {
            try
            {
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(RunValue) is string;
            }
            catch (Exception) { return false; }
        }

        private void StartWithWindowsChanged()
        {
            if (_settingChecks) return;
            try
            {
                using RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey, true);
                if (_startWithWindows.Checked) k.SetValue(RunValue, ExeQuoted, RegistryValueKind.String);
                else k.DeleteValue(RunValue, false);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "Windows would not let the relay change whether it starts with Windows: " + e.Message, "Kova Relay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetChecks();
            }
        }

        private void OpenLog()
        {
            try
            {
                _app.Log.Flush();
                string path = _app.ServiceMode ? Path.Combine(RelayService.DataDir(_app.ServiceName), "relay-log.txt") : _app.Log.Path;
                if (!File.Exists(path)) File.WriteAllText(path, "");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "The log could not be opened: " + e.Message, "Kova Relay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private bool _busy;

        /// <summary>
        /// Runs an elevated copy of the relay off the window's thread (the administrator prompt can take a while), then
        /// says how it went: in a message box, or only in the status box when it worked and the change speaks for itself.
        /// </summary>
        private async void Elevated(string args, Func<RelayService.Result, string> say, bool quietSuccess = false)
        {
            if (_busy) return;
            _busy = true;
            UseWaitCursor = true;
            RelayService.Result r;
            try { r = await Task.Run(() => RelayService.RunElevated(args)); }
            finally { _busy = false; UseWaitCursor = false; }
            if (IsDisposed) return;
            _note = say(r);
            if (r != RelayService.Result.Ok) SetChecks();   // a check box that did not take goes back
            _app.UpdateTip();
            RefreshState(force: true);
            if (r != RelayService.Result.Ok || !quietSuccess)
                MessageBox.Show(this, _note, "Kova Relay", MessageBoxButtons.OK, r == RelayService.Result.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private async void AsServiceChanged()
        {
            if (_settingChecks || _busy) return;
            string exe = Environment.ProcessPath!;
            string name = _app.ServiceName;
            RelayService.Result r;
            if (_asService.Checked)
            {
                // Checked here first for a quick answer; the elevated copy checks again before it installs anything.
                RelayService.Result early = !RelayService.CanRunInPlace(exe) ? RelayService.Result.CannotRunHere
                    : !RelayService.ProtectedFromUsers(exe) ? RelayService.Result.NotProtected : RelayService.Result.Ok;
                if (early != RelayService.Result.Ok)
                {
                    SetChecks();
                    MessageBox.Show(this, RelayService.Say(early, exe), "Kova Relay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                RelayServer server = _app.Server;
                bool wasRunning = server.Running;
                int port = server.Port;
                server.Stop();   // the service needs the port
                _busy = true; UseWaitCursor = true;
                string args = $"--service-install {name}" + (server.LoopbackOnly ? " --loopback" : "");
                try
                {
                    r = await Task.Run(() => RelayService.RunElevated(args));
                    // Leave nothing half done: a service that was made but would not start is removed again.
                    if (r != RelayService.Result.Ok && RelayService.IsInstalled(name))
                        await Task.Run(() => RelayService.RunElevated($"--service-remove {name}"));
                }
                finally { _busy = false; UseWaitCursor = false; }
                if (r != RelayService.Result.Ok)
                {
                    if (wasRunning) server.Start(port);
                    _note = RelayService.Say(r, exe);
                }
                else _note = $"The relay now runs as a Windows service. It starts when Windows starts, even before anyone signs in, and keeps running when you close this. Its host list, settings and log are kept in {RelayService.DataDir(name)}.";
            }
            else
            {
                _busy = true; UseWaitCursor = true;
                try { r = await Task.Run(() => RelayService.RunElevated($"--service-remove {name}")); }
                finally { _busy = false; UseWaitCursor = false; }
                if (r == RelayService.Result.Ok)
                {
                    string? why = _app.Server.Start(_app.Files.ReadPort(out _));
                    _note = why is null ? "The relay no longer runs as a service. It runs here in the tray again, while you are signed in."
                        : "The relay no longer runs as a service, and it could not start here: " + why;
                }
                else _note = RelayService.Say(r, exe);
            }
            if (IsDisposed) return;
            SetChecks();
            _app.UpdateTip();
            RefreshState(force: true);
            MessageBox.Show(this, _note, "Kova Relay", MessageBoxButtons.OK, r == RelayService.Result.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
