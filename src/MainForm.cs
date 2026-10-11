using System;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;

namespace TailRemote
{
    internal sealed class MainForm : Form
    {
        private readonly Settings _settings = Settings.Load();
        private readonly bool _autoHost, _autoConnect, _updated;

        // The window is blank: everything is in the menu bar (Alt), File, Clipboard and Settings.
        private readonly MenuChoice _mode = new("&Mode");
        private readonly MenuText _address = new("&Address", "The other PC's Tailscale name, IP address or internet address:");
        private readonly MenuText _port = new("&Port", "The port (the same on both PCs; 47120 unless you changed it):");
        private readonly MenuText _password = new("Pass&word", "The password (at least 6 characters, the same on both PCs):", secret: true);
        private readonly MenuChoice _device = new("&Output device");
        private readonly MenuChoice _quality = new("Sound &quality");
        private readonly MenuStatus _streaming = new("Not connected");
        private readonly MenuChoice _captureFrom = new("C&apture sound from (what other PCs hear)");
        private bool _fillingCapture;
        private readonly MenuChoice _saved = new("Sa&ved PCs");
        private readonly MenuItem _savePc = new("Save t&his PC");
        private readonly MenuItem _forgetPc = new("&Forget the chosen saved PC") { WhyNot = "Choose a saved PC first, in Saved PCs." };
        private readonly MenuText _listenPassword = new("&Listen-only password", "A second password that lets someone hear this PC but not control it. Leave it empty for none:", secret: true);
        private readonly MenuItem _sendClipboard = new("Send the clip&board") { ShortcutKeys = Keys.Control | Keys.B };
        private readonly MenuItem _sendFiles = new("Send f&iles") { ShortcutKeys = Keys.Control | Keys.I };
        private readonly MenuItem _sendFolder = new("Send a folder and everything in i&t") { ShortcutKeys = Keys.Control | Keys.T };
        // Files: first in the Clipboard menu. While files move, it opens a line per file.
        private readonly MenuStatus _transferStatus = new("");
        private readonly MenuItem _transferStop = new("&Stop the file transfer") { Enabled = false, WhyNot = "Nothing is being sent or received." };
        // Where Send files and Send a folder from the other PC are saved: the menu item says where.
        private readonly MenuItem _pickReceiveFolder = new("Pic&k where received files go");
        private readonly MenuItem _logging = Menus.Check("Enable lo&gging (writes Kova-log.txt next to Kova)");
        private readonly MenuItem _sounds = new("Sounds for connecting, clipboard and fi&les...");
        private readonly MenuItem _speedUp = Menus.Check("Catch up b&y fast-forwarding the sound, same pitch (otherwise it skips ahead)");
        private readonly MenuItem _startup = Menus.CheckAsking("Start &hosting when Windows starts (asks for administrator)...");
        // (WhyNot for _startup is set in the constructor: CheckAsking makes it.)
        private readonly MenuItem _service = Menus.CheckAsking("Run as a Windows servi&ce: works at the lock screen, sign-in and UAC prompts; Control Alt End sends Control Alt Delete...");
        private bool _settingService; // set while the checkbox is changed by code, not by the user
        private readonly MenuItem _go = new() { WhyNot = "Still connecting." };
        private readonly MenuItem _toggle = new("Control &remote PC") { ShortcutKeyDisplayString = "Ctrl+Shift+Enter" };
        private readonly MenuItem _update = new("Check for &updates (you have " + Updater.Current + ")") { ShortcutKeys = Keys.Control | Keys.U, WhyNot = "Already checking for updates." };
        private readonly MenuItem _audioSetup = new("Set up au&dio device...") { WhyNot = "Already working on the audio device." };
        private readonly MenuItem _audioRemove = new("Remove audio de&vice...") { WhyNot = "Already working on the audio device." };
        private readonly MenuItem _portEditor = new("Port &editor...");
        private readonly MenuItem _restart = new("Restart rem&ote PC and reconnect...");
        private readonly MenuItem _updateRemote = new("Update the remote P&C to this PC's version");
        private readonly MenuItem _remoteInfo = new("Remote PC &info...") { ShortcutKeys = Keys.Control | Keys.Shift | Keys.I };
        // The remote screen: a checkbox, off unless ticked (nothing is taken or sent until then). Escape in
        // its window comes back here and leaves it on; unticking it (here or Control Shift S) turns it off.
        private readonly MenuItem _watch = new("Show the remote &screen") { ShortcutKeys = Keys.Control | Keys.Shift | Keys.S };
        private VideoForm? _screen;
        private readonly MenuItem _getFiles = new("&Get files from the remote PC...") { ShortcutKeys = Keys.Control | Keys.G };
        private readonly MenuItem _getClipboard = new("Get the remote PC's clip&board") { ShortcutKeys = Keys.Control | Keys.Shift | Keys.B };
        private readonly MenuItem _announceQuality = Menus.Check("A&nnounce when the sound quality changes");
        private readonly MenuItem _muteLocal = Menus.Check("&Mute the remote PC while you are not controlling it (it stays connected)");
        private readonly MenuItem _rideOut = Menus.Check("Ride out &Wi-Fi scans: when the sound keeps stalling, hold enough to cover it (a little more delay until it stops)");
        private readonly MenuItem _switchTo = new("Switch &to PC");
        // Wake-on-LAN for saved PCs: sent from here and from every PC connected to, then it connects.
        private readonly MenuItem _wake = new("Wa&ke a saved PC and connect");
        private readonly MenuItem _announceHealth = Menus.Check("Announce the remote PC's heal&th: on battery, battery low, drive nearly full, processor busy, memory nearly gone");
        private readonly MenuItem _speedHere = new("Meas&ure this PC's internet speed...") { WhyNot = "A speed test is already running on this PC." };
        private readonly MenuItem _speedRemote = new("Measure the r&emote PC's internet speed...");
        private readonly MenuItem _backup = new("&Back up settings to a file...");
        private readonly MenuItem _restore = new("&Restore settings from a backup...");
        // Several PCs at once: the one in front is _client; these wait in the background, connected
        // but silent, until switched to (Control 1 to 9, or File, Switch to PC).
        private sealed record Parked(string Name, string Address, int Port, string Password, Client Client);
        private readonly System.Collections.Generic.List<Parked> _parked = new();
        private const int MaxParked = 8;
        private bool _loadingSettings; // a restored backup is going into the window: nothing is saved halfway
        // Remote PCs already told they are older, by address, port and version: once each (holding the
        // Client objects themselves kept every closed connection alive).
        private readonly System.Collections.Generic.HashSet<string> _versionTold = new();
        private Client? _seenClient;  // the connection the version and quality below belong to
        private int _qualitySaid;      // the bitrate step last announced
        private bool _expectRestart; // the remote PC was asked to restart: say when it is back
        private string? _expectUpdate; // the remote PC said it is updating TailRemote to this version: say when it is back

        // Host: this PC's addresses, to give the other PC. Enter copies the best one.
        private readonly MenuItem _copyAddress = new();
        private string? _bestAddress;
        private readonly System.Collections.Generic.List<(string Id, string Name)> _devices = new();
        private static string NotConnected(string role) => "The " + role + " device chosen before (not connected now: Windows default until it is back)";

        private readonly NotifyIcon _tray = new() { Text = "Kova, hosting", Icon = SystemIcons.Application };
        private bool _reallyExit; // Stop hosting and exit, from the tray: close for real
        private Client? _client;
        private Player? _player;       // kept between connections: no device opens or closes on connect
        private string? _playerDevice;
        private Host? _host;
        private KeyCapture? _keys;
        private IntPtr _handle;
        private bool _connecting, _reconnecting;
        private int _attempt; // bumped to abandon a connection attempt still under way
        private readonly Timer _titleTimer = new() { Interval = 1000 };
        private readonly Timer _retryTimer = new() { Interval = 1000 }; // Connect keeps trying every second until Disconnect (a PC coming back from a restart is caught at once)
        private bool _resumeRemote;     // was controlling the remote PC when the connection dropped
        private bool _quietModeChange;  // the reconnect message already says it

        public MainForm(bool autoHost, bool autoConnect, bool updated)
        {
            _autoHost = autoHost;
            _autoConnect = autoConnect;
            _updated = updated;
            Text = "Kova";
            Font = new Font("Segoe UI", 10f);
            ClientSize = new Size(420, 120);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            _mode.Add("Control another PC");
            _mode.Add("Host: let this PC be controlled");
            _copyAddress.Click += (_, _) => Menus.AfterMenu(() => CopyAddress());
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Later(FillMyAddress);
            FillMyAddress();
            _pickReceiveFolder.Click += (_, _) => Menus.AfterMenu(() => PickReceiveFolder());
            _transferStatus.Text = TransferIdle;
            FillHistory();
            _transferStop.Click += (_, _) =>
            {
                if (_client != null) _client.CancelTransfer();
                else Client.ForgetTransfers(); // waiting for a reconnection: stopped for good
                _host?.CancelTransfer();
                _serviceLink?.CancelTransfer();
            };

            // The menu bar (Alt, then the arrows): File, Clipboard, Settings.
            var file = new MenuItem("&File");
            file.DropDownItems.AddRange(new ToolStripItem[]
            {
                _go, _switchTo, _wake, _toggle, _watch, _restart, _remoteInfo, _updateRemote, _streaming.Item, new ToolStripSeparator(),
                _speedHere, _speedRemote, new ToolStripSeparator(),
                _mode.Menu, _saved.Menu, _address.Item, _port.Item, _password.Item, _listenPassword.Item, _copyAddress,
                _savePc, _forgetPc, new ToolStripSeparator(),
                Menus.Action("E&xit", ExitForGood),
            });
            var clip = new MenuItem("&Clipboard");
            clip.DropDownItems.AddRange(new ToolStripItem[]
            {
                _transferStatus.Item, _history, _sendClipboard, _sendFiles, _sendFolder, _getFiles, _getClipboard, _transferStop, new ToolStripSeparator(), _pickReceiveFolder,
            });
            var set = new MenuItem("&Settings");
            set.DropDownItems.AddRange(new ToolStripItem[]
            {
                _quality.Menu, _announceQuality, _announceHealth, _muteLocal, _rideOut, _device.Menu, _captureFrom.Menu, _speedUp, _sounds, new ToolStripSeparator(),
                _startup, _service, _portEditor, _audioSetup, _audioRemove, new ToolStripSeparator(),
                _backup, _restore, _logging,
            });
            // Flow, not the usual single row: a row hides whatever does not fit, with no word to a
            // screen reader, and with Windows' text scaling About did not fit.
            var bar = new MenuStrip { Dock = DockStyle.Top, LayoutStyle = ToolStripLayoutStyle.Flow };
            bar.Items.AddRange(new ToolStripItem[] { file, clip, set, AboutMenu.Build(this, bar, _update) });
            Menus.Attach(bar);
            Controls.Add(bar);
            MainMenuStrip = bar;

            _mode.SelectedIndex = _settings.HostMode ? 1 : 0;
            _address.Text = _settings.Address;
            _port.Text = _settings.Port.ToString();
            _password.Text = _settings.Password;
            _listenPassword.Text = _settings.ListenPassword;
            _logging.Checked = _settings.Logging;
            _speedUp.Checked = _settings.CatchUpBySpeed;
            _announceQuality.Checked = _settings.AnnounceQuality;
            _announceHealth.Checked = _settings.AnnounceHealth;
            _muteLocal.Checked = _settings.MuteWhenNotControlling;
            _rideOut.Checked = _settings.RideOutStalls;
            ApplyReceiveFolder();
            Sounds.Key = Math.Clamp(_settings.SoundKey, 0, 11);
            Sounds.Choice = t => _settings.SoundChoices.TryGetValue(t.ToString(), out var s) && (s == Sounds.RandomName || Sounds.All.Contains(s)) ? s : Sounds.DefaultName;
            _sounds.Click += (_, _) => Menus.AfterMenu(() =>
            {
                Doing("choosing sounds");
                using var f = new SoundsForm(_settings);
                f.ShowDialog(this);
            });
            Sounds.Warm();
            _quality.Add("Variable: follows the connection");
            foreach (var (kbps, _) in Protocol.OpusSteps) _quality.Add("Locked at " + kbps + " kbit/s");
            _quality.SelectedIndex = Math.Clamp(_settings.SoundQuality + 1, 0, _quality.Count - 1);
            _quality.SelectedIndexChanged += (_, _) =>
            {
                SaveSettings();
                // Muted, it stays at the lowest until it is unmuted, which then uses this choice.
                if (_client != null && !_client.Muted) _client.LockedStep = _quality.SelectedIndex - 1;
            };
            _speedUp.CheckedChanged += (_, _) => { SaveSettings(); if (_player != null) _player.SpeedUp = _speedUp.Checked; };
            DiagLog.Enabled = _settings.Logging;
            _startup.Checked = Startup.IsEnabled();
            _settingService = true;
            _service.Checked = ServiceHost.IsInstalled();
            _settingService = false;
            FillSaved();

            _device.Add("Windows default");
            _devices.Add(("", "Windows default"));
            foreach (var d in Wasapi.OutputDevices()) { _devices.Add(d); _device.Add(d.Name); }
            // A chosen device that is unplugged right now keeps its place: it showed as Windows default, and
            // the next setting saved wrote that back, so the headset was forgotten for good.
            foreach (var (id, role) in new[] { (_settings.OutputDevice, "output"), (_settings.CaptureDevice, "capture") })
                if (!string.IsNullOrEmpty(id) && !_devices.Exists(d => d.Id == id))
                {
                    _devices.Add((id, NotConnected(role)));
                    _device.Add(NotConnected(role));
                }
            int sel = _devices.FindIndex(d => d.Id == _settings.OutputDevice);
            _device.SelectedIndex = sel < 0 ? 0 : sel;
            // Wired after the initial SelectedIndex so it fires only on the person's choice:
            // save it and, if a player exists, switch it live rather than only on reconnect.
            _device.SelectedIndexChanged += (_, _) =>
            {
                SaveSettings();
                string device = _devices[Math.Max(0, _device.SelectedIndex)].Id;
                if (_player != null) { _player.SetDevice(device); _playerDevice = device; }
            };
            _fillingCapture = true;
            foreach (var d in _devices) _captureFrom.Add(d.Name);
            int cap = _devices.FindIndex(d => d.Id == _settings.CaptureDevice);
            _captureFrom.SelectedIndex = cap < 0 ? 0 : cap;
            _fillingCapture = false;
            _captureFrom.SelectedIndexChanged += (_, _) => CaptureChanged();

            _mode.SelectedIndexChanged += (_, _) => UpdateMode();
            _saved.SelectedIndexChanged += (_, _) => UseSaved();
            _savePc.Click += (_, _) => Menus.AfterMenu(() => SavePc());
            _forgetPc.Click += (_, _) => Menus.AfterMenu(() => ForgetPc());
            _sendClipboard.Click += (_, _) => Menus.AfterMenu(() => SendClipboard());
            _sendFiles.Click += (_, _) => Menus.AfterMenu(() => SendFiles(folder: false));
            _sendFolder.Click += (_, _) => Menus.AfterMenu(() => SendFiles(folder: true));
            _logging.CheckedChanged += (_, _) =>
            {
                SaveSettings();
                DiagLog.Enabled = _logging.Checked;
                Say(_logging.Checked ? "Logging to " + DiagLog.FilePath + "." + (_service.Checked ? " Press Apply settings to the service to log there too." : "")
                                     : "Logging is off.");
            };
            _go.Click += (_, _) => Menus.AfterMenu(() => Go());
            _address.Changed += SaveSettings;
            _port.Changed += SaveSettings;
            _password.Changed += SaveSettings;
            _listenPassword.Changed += SaveSettings;
            _toggle.Click += (_, _) => Menus.AfterMenu(() => _keys?.Toggle());
            _update.Click += (_, _) => Menus.AfterMenu(() => CheckForUpdates());
            _restart.Click += (_, _) => Menus.AfterMenu(() => RestartRemote());
            _updateRemote.Click += (_, _) => Menus.AfterMenu(() => UpdateRemote());
            _remoteInfo.Click += (_, _) => Menus.AfterMenu(() => RemoteInfo());
            _watch.Click += (_, _) => Menus.AfterMenu(() => ShowScreen());
            _getFiles.Click += (_, _) => Menus.AfterMenu(() => GetFiles());
            _getClipboard.Click += (_, _) => Menus.AfterMenu(() => GetClipboard());
            _speedHere.Click += (_, _) => Menus.AfterMenu(() => SpeedTestHere());
            _speedRemote.Click += (_, _) => Menus.AfterMenu(() => SpeedTestRemote());
            _backup.Click += (_, _) => Menus.AfterMenu(() => BackupSettings());
            _restore.Click += (_, _) => Menus.AfterMenu(() => RestoreSettings());
            _switchTo.DropDownItems.Add("(filled in when opened)");
            _switchTo.DropDownOpening += (_, _) => FillSwitchMenu();
            _announceQuality.CheckedChanged += (_, _) => SaveSettings();
            _announceHealth.CheckedChanged += (_, _) => SaveSettings();
            _wake.DropDownOpening += (_, _) => FillWakeMenu();
            _wake.DropDownItems.Add(new ToolStripMenuItem("...")); // so it shows as a submenu before it is first filled
            _muteLocal.CheckedChanged += (_, _) => { SaveSettings(); ApplyMute(); };
            _rideOut.CheckedChanged += (_, _) => { SaveSettings(); if (_player != null) _player.RideOut = _rideOut.Checked; };
            _audioSetup.Click += (_, _) => Menus.AfterMenu(() => SetUpAudio());
            _audioRemove.Click += (_, _) => Menus.AfterMenu(() => RemoveAudio());
            _portEditor.Click += (_, _) => Menus.AfterMenu(() => { SaveSettings(); using var f = new PortEditorForm(_settings); f.ShowDialog(this); });
            _startup.CheckedChanged += (_, _) => Menus.AfterMenu(StartupChanged); // after the menu closes, so NVDA reads what comes up
            _service.CheckedChanged += (_, _) => { if (!_settingService) Menus.AfterMenu(ServiceChanged); };
            _titleTimer.Tick += (_, _) => UpdateTitle();
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("&Open Kova", null, (_, _) => RestoreFromTray());
            _trayExit = (ToolStripMenuItem)trayMenu.Items.Add("&Stop hosting and exit", null, (_, _) => { _reallyExit = true; RestoreFromTray(); Close(); });
            _tray.ContextMenuStrip = trayMenu;
            _tray.Click += (_, e) => { if (e is not MouseEventArgs m || m.Button == MouseButtons.Left) RestoreFromTray(); };
            _retryTimer.Tick += (_, _) => { if (_client == null && !_connecting) Connect(quiet: true); };
            UpdateMode();
        }

        /// <summary>
        /// Host: the addresses the other PC can connect to. The LAN one (a network with a router),
        /// then Tailscale's, then the PC's name. The LAN address is what Copy IP address copies,
        /// or Tailscale's when there is no LAN.
        /// </summary>
        private void FillMyAddress()
        {
            var lan = new System.Collections.Generic.List<(string Ip, string Network)>();
            string? tailscale = null;
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    var props = nic.GetIPProperties();
                    bool router = props.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                    foreach (var a in props.UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(a.Address)) continue;
                        if (Protocol.IsTailscale(a.Address)) tailscale = a.Address.ToString();
                        else if (router && a.Address.GetAddressBytes()[0] != 169) lan.Add((a.Address.ToString(), nic.Name));
                    }
                }
            }
            catch { }
            var parts = lan.Select(l => l.Ip + " (" + l.Network + ")").ToList();
            if (tailscale != null) parts.Add("Tailscale " + tailscale);
            parts.Add("name " + Environment.MachineName);
            _bestAddress = lan.Count > 0 ? lan[0].Ip : tailscale;
            string text = "Copy this PC's IP a&ddress: " + string.Join(", ", parts).Replace("&", "&&");
            if (_copyAddress.Text != text) _copyAddress.Text = text;
            _copyAddress.Enabled = _bestAddress != null;
        }

        private void CopyAddress()
        {
            FillMyAddress();
            if (_bestAddress is not string ip) { Say("This PC has no network address right now."); return; }
            ClipboardJobs.TryAdd(() => SetClipboard(() => Clipboard.SetText(ip))); // on the clipboard's own thread, never the window's
            Say("Copied " + ip + ".");
        }

        private bool HostMode => _mode.SelectedIndex == 1;

        private void UpdateMode()
        {
            bool host = HostMode;
            _address.Item.Available = !host;
            _device.Menu.Available = !host;
            _saved.Menu.Available = _savePc.Available = _forgetPc.Available = !host;
            _listenPassword.Item.Available = host;
            _copyAddress.Available = host;
            if (host) FillMyAddress();
            _captureFrom.Menu.Available = host;
            _speedUp.Available = !host; // it is about how this PC plays the sound
            _sounds.Available = !host; // a host's sounds would go out with its own sound
            _quality.Menu.Available = !host; // the host always sends the best unless asked for less
            _streaming.Item.Available = !host;
            _toggle.Available = !host;
            _restart.Available = _updateRemote.Available = _remoteInfo.Available = _watch.Available = _getFiles.Available = _getClipboard.Available = _speedRemote.Available = _switchTo.Available = !host;
            _announceQuality.Available = _announceHealth.Available = _muteLocal.Available = _rideOut.Available = _wake.Available = !host;
            _startup.Available = host;
            _service.Available = host;
            _startup.Enabled = !_service.Checked; // the service replaces the at-sign-in task
            _startup.WhyNot = "Not needed: the Kova service already starts with Windows.";
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool busy = _client != null || _host != null || _reconnecting || _connecting || _parked.Count > 0;
            _mode.Menu.Enabled = !busy;
            // Alt C and Alt S are the Clipboard and Settings menus, so the button uses other letters.
            if (HostMode) _go.Text = _service.Checked ? "Appl&y settings to the service" : _host == null ? "Start &hosting" : "Stop &hosting";
            else _go.Text = _client == null && !_reconnecting ? "Co&nnect" : "Disco&nnect";
            bool canUse = _client != null && !_client.ListenOnly;
            string why = _client == null ? "Connect to a PC first." : "Not on a listen-only connection: it needs the control password.";
            foreach (var item in new[] { _toggle, _restart, _updateRemote, _remoteInfo, _getFiles, _getClipboard, _speedRemote })
            {
                item.Enabled = canUse;
                item.WhyNot = why;
            }
            if (_speedRemoteRunning) { _speedRemote.Enabled = false; _speedRemote.WhyNot = "A speed test is already running on the remote PC."; }
            _mode.Menu.WhyNot = "Disconnect, or stop hosting, first.";
            EnsureServiceLink();
            SaveResumeState();
        }

        private ServiceLink.Client? _serviceLink;

        /// <summary>
        /// While the service hosts, this window still does the clipboard and sends files, through
        /// the service's agent: the same buttons and Files line as when this window hosts itself.
        /// </summary>
        private void EnsureServiceLink()
        {
            bool want = HostMode && _service.Checked;
            if (want && _serviceLink == null)
            {
                var link = new ServiceLink.Client { ReceiveFolder = _settings.ReceiveFolder };
                link.TextArrived += text => Later(() => ClipboardArrived(text));
                link.FilesArrived += paths => Later(() => ClipboardFilesArrived(paths));
                link.TransferProgress += t => Later(() => ShowTransfer(t));
                _serviceLink = link;
            }
            else if (!want && _serviceLink != null)
            {
                _serviceLink.Dispose();
                _serviceLink = null;
            }
        }

        /// <summary>
        /// Remembers at once whether this PC is hosting or connected, so an update or
        /// a crash comes back the same way. Not before the window is shown: until
        /// then the state from last time has not been used yet.
        /// </summary>
        private void SaveResumeState()
        {
            if (!_shown) return;
            string state = _host != null ? "host" : _client != null || _reconnecting || _connecting ? "connect" : "";
            if (state == _settings.ResumeState) return;
            _settings.ResumeState = state;
            try { _settings.Save(); } catch { }
        }

        private bool _shown;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _handle = Handle;
        }

        // ---- Send clipboard and Send files ----
        // Nothing is shared by itself. Send clipboard sends what is on this clipboard (text, or
        // copied files and folders) to the other PC's clipboard, ready for Control V there.
        // Send files sends chosen files to Downloads, TailRemote on the other PC.
        //
        // Clipboard work runs on a thread of its own: Windows' clipboard calls wait whenever
        // another program has it open, and on the window's thread that froze TailRemote.

        private static readonly System.Collections.Concurrent.BlockingCollection<Action> ClipboardJobs = StartClipboardThread();

        private static System.Collections.Concurrent.BlockingCollection<Action> StartClipboardThread()
        {
            var jobs = new System.Collections.Concurrent.BlockingCollection<Action>(64);
            var t = new System.Threading.Thread(() =>
            {
                foreach (var job in jobs.GetConsumingEnumerable())
                {
                    try { job(); }
                    catch (Exception e)
                    {
                        // Speech.Speak and DiagLog.Write are both thread-safe from any thread: a
                        // failed clipboard send must not vanish silently.
                        DiagLog.Write("clipboard job: " + e);
                        Speech.Speak("Could not finish the clipboard action: " + e.Message);
                    }
                }
            })
                { IsBackground = true, Name = "Kova clipboard" };
            t.SetApartmentState(System.Threading.ApartmentState.STA); // the clipboard needs it
            t.Start();
            return jobs;
        }

        private bool CanSend(out Client? client, out Host? host) => CanSend(out client, out host, out _);

        private bool CanSend(out Client? client, out Host? host, out ServiceLink.Client? link)
        {
            client = _client;
            host = _host;
            link = HostMode && _service.Checked ? _serviceLink : null;
            if (link != null)
            {
                if (!link.Connected)
                {
                    // An older service has no link to this window yet: it is asked to update itself
                    // (at most once a minute), and what happened is said once that is known.
                    if (ServiceHost.InstalledVersion() is Version v && v < Updater.Current) OfferServiceUpdate(fromSend: true);
                    else Say("The Kova service is not answering. Check that it is running, or press Apply settings to the service.");
                    return false;
                }
                if (!link.HasController) { Say("No one is controlling this PC, so there is no one to send to."); return false; }
                return true;
            }
            if (client == null && host == null) { Say("Connect, or start hosting, first."); return false; }
            if (client?.ListenOnly == true) { Say("Listeners cannot send anything."); return false; }
            if (host != null && !host.HasController) { Say("No one is controlling this PC, so there is no one to send to."); return false; }
            return true;
        }

        // What this PC's clipboard held when it was read, so Send the clipboard and a controller's
        // pull of the host's clipboard share the one read (text, file-drop lists, stale/missing files
        // dropped, and non-text noticed).
        private enum ClipKind { Files, FilesGone, SpacesOnly, Text, Inaccessible, Other, Empty }

        /// <summary>Reads this PC's clipboard on the clipboard (STA) thread; call only from a ClipboardJobs job.</summary>
        private static (ClipKind Kind, string[]? Files, string? Text) ReadClipboardOnce()
        {
            string[]? files = null;
            string? text = null;
            bool inaccessible = false, other = false;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (Clipboard.ContainsFileDropList())
                    {
                        var list = Clipboard.GetFileDropList();
                        files = new string[list.Count];
                        list.CopyTo(files, 0);
                        // Files cut and pasted elsewhere stay listed on the clipboard though they are gone:
                        // sent, they arrived as nothing and wiped the other PC's clipboard.
                        int listed = files.Length;
                        files = files.Where(f => System.IO.File.Exists(f) || System.IO.Directory.Exists(f)).ToArray();
                        if (files.Length == 0 && listed > 0) return (ClipKind.FilesGone, null, null);
                    }
                    else if (Clipboard.ContainsText()) text = Clipboard.GetText();
                    else other = Clipboard.GetDataObject()?.GetFormats()?.Length > 0;
                    inaccessible = false;
                    break;
                }
                catch { inaccessible = true; System.Threading.Thread.Sleep(50); } // another program has it open
            }
            if (files is { Length: > 0 }) return (ClipKind.Files, files, null);
            if (text != null && text.Length > 0 && string.IsNullOrWhiteSpace(text)) return (ClipKind.SpacesOnly, null, null);
            if (!string.IsNullOrEmpty(text)) return (ClipKind.Text, null, text);
            if (inaccessible) return (ClipKind.Inaccessible, null, null);
            if (other) return (ClipKind.Other, null, null);
            return (ClipKind.Empty, null, null);
        }

        private void SendClipboard()
        {
            Doing("sending the clipboard");
            if (!CanSend(out _, out _)) return;
            ClipboardJobs.TryAdd(() =>
            {
                var (kind, files, text) = ReadClipboardOnce();
                switch (kind)
                {
                    case ClipKind.Files:
                        Later(() =>
                        {
                            if (!CanSend(out var client, out var host, out var link)) return; // the connection may have closed meanwhile
                            client?.SendClipboardFiles(files!);
                            host?.SendClipboardFiles(files!);
                            link?.SendClipboardFiles(files!);
                            Tone(Sounds.Tone.ClipboardSent); // the Files line says the rest, without speaking
                        });
                        break;
                    case ClipKind.FilesGone:
                        Later(() => Say("Nothing sent: the files on the clipboard are not there any more. Copy them again."));
                        break;
                    case ClipKind.SpacesOnly:
                        Later(() => Say("Nothing sent: the clipboard has only spaces or blank lines."));
                        break;
                    case ClipKind.Text:
                        if ((long)text!.Length * 3 > FileChannel.MaxText) { Later(() => Say("That is too much text to send: over 512 megabytes.")); break; }
                        Later(() =>
                        {
                            if (!CanSend(out var client, out var host, out var link)) return; // the connection may have closed meanwhile
                            client?.SendClipboard(text);
                            host?.SendClipboard(text);
                            link?.SendClipboard(text);
                            Tone(Sounds.Tone.ClipboardSent);
                        });
                        break;
                    case ClipKind.Inaccessible: Later(() => Say("Could not read the clipboard: try again.")); break;
                    case ClipKind.Other: Later(() => Say("Nothing sent: the clipboard has something other than text or files, like a picture. Only text and files can be sent.")); break;
                    default: Later(() => Say("Nothing sent: the clipboard is empty. Copy something first.")); break;
                }
            });
        }

        /// <summary>
        /// A controller asked for this host's clipboard (the window hosts itself). Reads it on the
        /// clipboard thread and sends it to the controllers, like Send the clipboard but with no word
        /// here: the host's user did not ask for it. Nothing to send (empty, a picture, files gone)
        /// quietly sends nothing, so the controller's clipboard stays as it was.
        /// </summary>
        private void SendClipboardToControllers(Host host)
        {
            ClipboardJobs.TryAdd(() =>
            {
                var (kind, files, text) = ReadClipboardOnce();
                if (kind == ClipKind.Files) host.SendClipboardFiles(files!);
                else if (kind == ClipKind.Text && (long)text!.Length * 3 <= FileChannel.MaxText) host.SendClipboard(text);
            });
        }

        /// <summary>Controller side: ask the remote PC for its clipboard. It arrives on this clipboard through the usual path.</summary>
        private void GetClipboard()
        {
            if (_client == null || _client.ListenOnly) { Say(_client == null ? "Connect first." : "Listeners cannot do that."); return; }
            Doing("getting the remote PC's clipboard");
            // A single press speaks as before; a held or mashed key is throttled and the extras drop quietly.
            if (_client.RequestClipboard()) Say("Asked the remote PC for its clipboard.");
        }

        private bool _picking;

        /// <summary>
        /// The Windows file picker, on a thread of its own: it looks through every drive and
        /// add-on as it opens, and a slow one froze TailRemote when it ran on the window's
        /// thread (1.8.4, Brock). Null: nothing chosen.
        /// </summary>
        internal static System.Threading.Tasks.Task<string[]?> PickFiles(bool folder)
        {
            var done = new System.Threading.Tasks.TaskCompletionSource<string[]?>();
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    if (folder)
                    {
                        using var pick = new FolderBrowserDialog { Description = "Choose a folder to send to the other PC, with everything in it", UseDescriptionForTitle = true };
                        done.TrySetResult(pick.ShowDialog() == DialogResult.OK ? new[] { pick.SelectedPath } : null);
                        return;
                    }
                    using var files = new OpenFileDialog { Multiselect = true, Title = "Choose files to send to the other PC" };
                    done.TrySetResult(files.ShowDialog() == DialogResult.OK ? files.FileNames : null);
                }
                catch (Exception e) { done.TrySetException(e); }
            }) { IsBackground = true, Name = "Kova file picker" };
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            return done.Task;
        }

        /// <summary>Received files go where the setting says (Downloads\TailRemote when it is empty), here and in the service.</summary>
        private void ApplyReceiveFolder()
        {
            string folder = _settings.ReceiveFolder;
            FileChannel.DownloadsOverride = folder.Length > 0 ? folder : null;
            _pickReceiveFolder.Text = "Pic&k where received files go (now " + FileChannel.Downloads.Replace("&", "&&") + ")...";
            if (_serviceLink != null) _serviceLink.ReceiveFolder = folder;
        }

        private bool _pickingFolder;

        /// <summary>Pick where received files go: a folder picker on its own thread, like Send a folder.</summary>
        private async void PickReceiveFolder()
        {
            Doing("picking where received files go");
            if (_pickingFolder) return;
            _pickingFolder = true;
            try
            {
                var done = new System.Threading.Tasks.TaskCompletionSource<string?>();
                var t = new System.Threading.Thread(() =>
                {
                    try
                    {
                        using var pick = new FolderBrowserDialog
                        {
                            Description = "Choose where files from the other PC are saved",
                            UseDescriptionForTitle = true,
                            SelectedPath = FileChannel.Downloads,
                        };
                        done.TrySetResult(pick.ShowDialog() == DialogResult.OK ? pick.SelectedPath : null);
                    }
                    catch (Exception e) { done.TrySetException(e); }
                }) { IsBackground = true, Name = "Kova folder picker" };
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.Start();
                string? chosen = await done.Task;
                if (chosen == null) return;
                _settings.ReceiveFolder = chosen;
                SaveSettings();
                ApplyReceiveFolder();
                Say("Received files will go to " + chosen + ".");
            }
            catch (Exception e) { Say("Could not choose the folder: " + e.Message); }
            finally { _pickingFolder = false; }
        }

        private async void SendFiles(bool folder)
        {
            Doing(folder ? "sending a folder" : "sending files");
            if (!CanSend(out _, out _)) return;
            if (_picking) { Say("The file picker is already open."); return; }
            try
            {
                _picking = true;
                string[]? paths;
                try { paths = await PickFiles(folder); }
                finally { _picking = false; }
                if (paths == null || paths.Length == 0) return;
                if (!CanSend(out var client, out var host, out var link)) return; // the connection may have closed meanwhile
                client?.SendFiles(paths);
                host?.SendFiles(paths);
                link?.SendFiles(paths);
            }
            catch (Exception e)
            {
                DiagLog.Write("send files: " + e);
                Say("Could not send the files: " + e.Message);
            }
        }

        /// <summary>Text the other PC sent with Send clipboard: onto this clipboard.</summary>
        private void ClipboardArrived(string text)
        {
            Doing("receiving the clipboard");
            if (text.Length == 0) return;
            Tone(Sounds.Tone.ClipboardReceived);
            ClipboardJobs.TryAdd(() => SetClipboard(() => Clipboard.SetDataObject(text, true, 2, 50)));
        }

        /// <summary>Files the other PC sent with Send clipboard, in the holding folder: onto this clipboard, so Control V pastes them.</summary>
        private void ClipboardFilesArrived(string[] paths)
        {
            if (paths.Length == 0) return;
            Tone(Sounds.Tone.ClipboardReceived);
            ClipboardJobs.TryAdd(() =>
            {
                var list = new System.Collections.Specialized.StringCollection();
                list.AddRange(paths);
                var data = new DataObject();
                data.SetFileDropList(list);
                // Paste MOVES them out of the holding folder: on the same drive that is an instant rename,
                // so the files never take up their space twice.
                data.SetData("Preferred DropEffect", new System.IO.MemoryStream(BitConverter.GetBytes(2)));
                SetClipboard(() => Clipboard.SetDataObject(data, true, 2, 50));
            });
        }

        private static void SetClipboard(Action set)
        {
            for (int i = 0; i < 5; i++)
            {
                try { set(); return; }
                catch { System.Threading.Thread.Sleep(100); }
            }
        }

        // ---- Which output the host sends ----

        private void CaptureChanged()
        {
            Doing("changing the capture device");
            if (_fillingCapture) return;
            SaveSettings();
            string id = _settings.CaptureDevice;
            _host?.SetCaptureDevice(id.Length == 0 ? null : id);
            if (_service.Checked) Say("Press Apply settings to the service to use this there too.");
            else if (_host != null) Say("Now sending " + _devices[Math.Max(0, _captureFrom.SelectedIndex)].Name + ".");
        }

        // ---- The remote PC: update it, its info, get files from it, switch with Control 1 to 9 ----

        private bool CanUseRemote(out Client c)
        {
            c = _client!;
            if (_client == null || _client.ListenOnly) { Say(_client == null ? "Connect first." : "Listeners cannot do that."); return false; }
            if (!_client.CanRemoteTools) { Say("The remote PC's Kova is too old for that. Update it once with Check for updates on that PC."); return false; }
            return true;
        }

        // The connection whose Update the remote PC is asking GitHub now: a second press meanwhile is
        // told so, rather than asking twice.
        private Client? _remoteUpdateAsking;

        private async void UpdateRemote()
        {
            if (!CanUseRemote(out var c)) return;
            if (_remoteUpdateAsking == c) { Say("Still checking GitHub for the newest Kova."); return; }
            // Said first: asking GitHub can take a while, and silence sounded like nothing happened.
            Say("Checking GitHub for the newest Kova.");
            // The newest on GitHub, not just this PC's own version: a release that changes how the PCs
            // connect means the remote PC must be updated first, while this one can still reach it.
            Version want = Updater.Current;
            _remoteUpdateAsking = c;
            try
            {
                // At most 15 seconds: GitHub not answering must not leave the request hanging.
                var latest = await Updater.LatestAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if (latest > want) want = latest;
            }
            catch { } // offline: this PC's own version, as before
            finally { if (_remoteUpdateAsking == c) _remoteUpdateAsking = null; }
            if (_client != c) return;
            if (c.HostVersion is Version v && v >= want)
            {
                Say("The remote PC already has Kova " + v + (v == want ? ", the newest." : ", newer than " + want + "."));
                return;
            }
            c.RequestUpdate(want);
            Say("Asked the remote PC to update to Kova " + want + ".");
        }

        /// <summary>Turns the remote screen on (its window opens) or off: a checkbox, nothing else to choose (it follows the line by itself).</summary>
        private void ShowScreen()
        {
            Doing("showing the remote screen");
            if (_screen != null && !_screen.IsDisposed)
            {
                _screen.Close();
                Say("unchecked. Stopped showing the remote screen.");
                return;
            }
            if (_client == null) { Say("Connect to a PC first."); return; }
            if (_client.ListenOnly) { Say("A listen-only connection cannot see the remote screen."); return; }
            if (!_client.CanWatch) { Say("The remote PC's Kova is too old to show its screen. Update it there."); return; }
            _screen = new VideoForm(() => _client, Say, () => _keys?.Toggle(), BackFromScreen);
            _screen.FormClosed += (_, _) => { _screen = null; _watch.Checked = false; UpdateTitle(); };
            _watch.Checked = true;
            _screen.Show(this);
        }

        /// <summary>Escape in the screen window: back here, the screen still on (and its frames a second in this title).</summary>
        private void BackFromScreen()
        {
            Activate();
            UpdateTitle();
            Say("Back to the main window. The remote screen stays on; Control Shift S turns it off.");
        }

        private async void RemoteInfo()
        {
            if (!CanUseRemote(out var c)) return;
            Doing("asking for remote PC info");
            string text;
            try { text = await c.RequestInfoAsync(); }
            catch { Say("The remote PC did not answer."); return; }
            if (_client != c) return;
            using var f = new RemoteInfoForm(() => _client, text, Say);
            f.ShowDialog(this);
        }

        private void GetFiles()
        {
            if (!CanUseRemote(out var c)) return;
            Doing("getting files from the remote PC");
            using var f = new RemoteFilesForm(() => _client, Say);
            f.ShowDialog(this);
        }

        /// <summary>
        /// Once a second while connected: says once if the remote PC has an older TailRemote, and,
        /// when turned on, when the sound quality goes down or back up.
        /// </summary>
        private void WatchConnection(Client c)
        {
            if (_seenClient != c)
            {
                if (c.HostVersion is not Version hv) return; // its features have not come yet
                _seenClient = c;
                _qualitySaid = c.AudioQuality;
                _updateRemote.Text = "Update the remote P&C to this PC's version (it has " + hv + ", this PC has " + Updater.Current + ")";
                if (hv < Updater.Current && !c.ListenOnly && _versionTold.Add(FrontAddress + ":" + FrontPort + ":" + hv))
                    Speak("The remote PC has Kova " + hv + ", older than this PC's " + Updater.Current + ". File, Update the remote PC brings it up to date.");
                return;
            }
            int q = c.AudioQuality;
            if (q == _qualitySaid) return;
            bool lower = q > _qualitySaid; // higher steps are lower bitrates
            _qualitySaid = q;
            if (_announceQuality.Checked && !c.Muted) // muted, it is lowered on purpose: nothing to say
                Speak(q == 0 ? "Sound back to full quality." : "Sound " + (lower ? "lowered" : "raised") + " to " + Protocol.OpusSteps[q].Kbps + " kilobits.");
        }

        /// <summary>Control 1 to 9: connect to that saved PC, leaving the one connected now.</summary>
        // Here, not OnKeyDown: the window is blank with nothing in it to have focus, so its key
        // events never came and Control 1 to 9 did nothing at all. Command keys always arrive here.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if ((keyData & Keys.Modifiers) == Keys.Control && key >= Keys.D1 && key <= Keys.D9)
            {
                QuickConnect(key - Keys.D0);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void QuickConnect(int n)
        {
            if (HostMode) { Say("Control 1 to 9 switch between saved PCs, in Control another PC mode."); return; }
            if (n > _settings.SavedPcs.Count)
            {
                Say(_settings.SavedPcs.Count == 0 ? "There are no saved PCs yet. Use File, Save this PC." : "There is no saved PC " + n + ". There are " + _settings.SavedPcs.Count + ".");
                return;
            }
            var pc = _settings.SavedPcs[n - 1];
            SwitchTo(pc.Address, pc.Port, Settings.Unprotect(pc.PasswordEnc), pc.ToString());
        }

        // ---- Several PCs at once ----
        // Switching to another PC keeps the one in front connected, in the background: silent, with
        // no sound sent at all, its transfers still going. Switching back is instant, with no login.

        private static bool SamePc(string a, int portA, string b, int portB) => portA == portB && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        private int CurrentPort => int.TryParse(_port.Text.Trim(), out int p) ? p : 0;

        // The PC in front (or being reconnected to), as it was when connected to: Address, Port and Saved
        // PCs can be changed meanwhile, and must not rename it, park it under another PC's name or make a
        // reconnect go somewhere else.
        private string? _frontAddress, _frontPassword;
        private int _frontPort;
        private string FrontAddress => _frontAddress ?? _address.Text.Trim();
        private int FrontPort => _frontAddress != null ? _frontPort : CurrentPort;
        private string FrontPassword => _frontPassword ?? _password.Text;

        /// <summary>"pc:47120", "1.2.3.4:47120" or "[fe80::1]:47120": the address and the port typed with it; null if there is none.</summary>
        internal static (string Address, int Port)? SplitPort(string text)
        {
            text = text.Trim();
            if (text.StartsWith('[') && text.IndexOf("]:", StringComparison.Ordinal) is int end and > 0)
                return int.TryParse(text[(end + 2)..], out int p6) && p6 is > 0 and < 65536 ? (text[1..end], p6) : null;
            int colon = text.LastIndexOf(':');
            if (colon <= 0 || text.IndexOf(':') != colon) return null; // none, or a bare IPv6 address
            return int.TryParse(text[(colon + 1)..], out int p) && p is > 0 and < 65536 ? (text[..colon], p) : null;
        }

        private string NameFor(string address, int port) =>
            _settings.SavedPcs.Find(p => SamePc(p.Address, p.Port, address, port))?.ToString() ?? (port == Protocol.DefaultPort ? address : address + ", port " + port);

        private Parked? FindParked(string address, int port) => _parked.Find(p => SamePc(p.Address, p.Port, address, port));

        private void SwitchTo(string address, int port, string password, string name)
        {
            if (HostMode) { Say("Switching PCs works in Control another PC mode."); return; }
            if (_connecting && !_reconnecting) { Say("Still connecting. Try again in a moment."); return; }
            if ((_client != null || _reconnecting) && SamePc(FrontAddress, FrontPort, address, port)) { Say("Already on " + name + "."); return; }
            var target = FindParked(address, port);
            if (_client != null) Park(_client);
            else if (_reconnecting) { _attempt++; _reconnecting = false; _retryTimer.Stop(); _resumeRemote = false; _connecting = false; _go.Enabled = true; }
            _address.Text = address;
            _port.Text = port.ToString();
            _password.Text = password;
            SaveSettings();
            FillSaved();
            if (target != null) Unpark(target);
            else Connect(quiet: false);
        }

        private void Park(Client c)
        {
            if (_keys?.Remote == true) { _quietModeChange = true; _keys.Toggle(); } // keys come home first
            _keys?.SetClient(null);
            c.Muted = true;
            c.LockedStep = Protocol.OpusSteps.Length - 1; // a few kilobits, so it takes nothing from the one in front
            _parked.Add(new Parked(NameFor(FrontAddress, FrontPort), FrontAddress, FrontPort, FrontPassword, c));
            while (_parked.Count > MaxParked)
            {
                Speak(_parked[0].Name + ", the longest in the background, was disconnected: at most " + MaxParked + " wait there.");
                _parked[0].Client.Dispose();
                _parked.RemoveAt(0);
            }
            _client = null;
        }

        private void Unpark(Parked p)
        {
            _parked.Remove(p);
            var c = p.Client;
            _player?.ResetSoon(); // the sound starts fresh: nothing measured on the last PC carries over
            _client = c;
            (_frontAddress, _frontPort, _frontPassword) = (p.Address, p.Port, p.Password);
            c.Muted = false;
            c.LockedStep = _quality.SelectedIndex - 1;
            c.StartBest();
            ApplyMute();
            if (!c.ListenOnly) _keys?.SetClient(c);
            Tone(Sounds.Tone.Connected);
            Say("Switched to " + p.Name + "." + (c.ListenOnly ? " Listen only." : " Press Control Shift Enter to control it."));
            UpdateButtons();
            UpdateTitle();
        }

        /// <summary>A PC in the background dropped: it is let go, and switching to it connects again.</summary>
        private void BackgroundDropped(Client c, string why)
        {
            var p = _parked.Find(x => x.Client == c);
            if (p == null) return;
            _parked.Remove(p);
            c.Dispose();
            Speak(p.Name + ", in the background, disconnected: " + why + " Switching to it connects again.");
            UpdateButtons();
            UpdateTitle();
        }

        private void DropBackground()
        {
            foreach (var p in _parked) p.Client.Dispose();
            _parked.Clear();
        }

        /// <summary>The remote PC's battery, drive space, processor or memory: said if wanted, with its name when it waits in the background.</summary>
        private void HealthSaid(Client c, string text)
        {
            if (!_announceHealth.Checked) return;
            if (_parked.Find(p => p.Client == c) is Parked bg) Speak(bg.Name + ", in the background: " + text);
            else if (c == _client) Speak(text);
        }

        /// <summary>Keeps a saved PC's network cards (told by it while connected), so it can be woken later.</summary>
        private void RememberWake(string address, int port, string info)
        {
            if (info.Length == 0) return;
            var pc = _settings.SavedPcs.Find(p => SamePc(p.Address, p.Port, address, port));
            if (pc == null || pc.Wake == info) return;
            pc.Wake = info;
            try { _settings.Save(); } catch { }
        }

        /// <summary>File, Wake a saved PC and connect: every saved PC, and whether Kova knows how to wake it yet.</summary>
        private void FillWakeMenu()
        {
            var items = _wake.DropDownItems;
            items.Clear();
            foreach (var pc in _settings.SavedPcs)
            {
                bool known = Wake.Parse(pc.Wake).Count > 0;
                var item = new ToolStripMenuItem((pc + (known ? "" : ", not known yet: connect to it once while it is on")).Replace("&", "&&"));
                item.Click += (_, _) => Menus.AfterMenu(() => WakePc(pc));
                items.Add(item);
            }
            if (items.Count == 0) items.Add(new MenuStatus("There are no saved PCs yet. Connect to one, then File, Save this PC.").Item);
        }

        private void WakePc(SavedPc pc)
        {
            Doing("waking " + pc);
            if (HostMode) { Say("Waking PCs works in Control another PC mode."); return; }
            if (_connecting && !_reconnecting) { Say("Still connecting. Try again in a moment."); return; }
            var cards = Wake.Parse(pc.Wake);
            if (cards.Count == 0)
            {
                Say("Kova does not know how to wake " + pc + " yet. Connect to it once while it is on, and Kova learns it. Its network card must also allow Wake-on-LAN.");
                return;
            }
            // From every PC connected to as well: a wake signal only reaches its own network, so a PC on the
            // sleeping one's network (another PC at home) is what gets it there when this one is away.
            var relays = new System.Collections.Generic.List<Client>();
            if (_client is { CanWake: true } front) relays.Add(front);
            relays.AddRange(_parked.Select(p => p.Client).Where(c => c.CanWake));
            foreach (var c in relays) c.SendWake(cards.Select(x => x.Mac).ToArray());
            System.Threading.ThreadPool.QueueUserWorkItem(_ => { foreach (var (mac, broadcast) in cards) try { Wake.Send(mac, new[] { broadcast }); } catch { } });
            Say("Sent the wake signal to " + pc + " from this PC" + (relays.Count == 0 ? "" : " and from " + relays.Count + (relays.Count == 1 ? " connected PC" : " connected PCs")) + ".");
            _waking = pc.ToString();
            _wakingSince = Environment.TickCount64;
            SwitchTo(pc.Address, pc.Port, Settings.Unprotect(pc.PasswordEnc), pc.ToString());
        }

        // A PC just sent the wake signal: while it starts up, not answering is expected, not an error.
        private string? _waking;
        private long _wakingSince;
        private bool WakingUp(string address, int port) =>
            _waking != null && Environment.TickCount64 - _wakingSince < 5 * 60_000 && _settings.SavedPcs.Any(p => p.ToString() == _waking && SamePc(p.Address, p.Port, address, port));

        /// <summary>File, Switch to PC: every saved PC with its key and whether it is in front, in the background or not connected.</summary>
        private void FillSwitchMenu()
        {
            var items = _switchTo.DropDownItems;
            items.Clear();
            string State(string address, int port) =>
                (_client != null || _reconnecting || _connecting) && SamePc(FrontAddress, FrontPort, address, port) ? (_client != null ? "in front" : "connecting")
                : FindParked(address, port) != null ? "in the background" : "not connected";
            for (int i = 0; i < _settings.SavedPcs.Count; i++)
            {
                var pc = _settings.SavedPcs[i];
                var item = new ToolStripMenuItem((pc + ", " + State(pc.Address, pc.Port)).Replace("&", "&&"));
                if (i < 9) item.ShortcutKeyDisplayString = "Ctrl+" + (i + 1);
                item.Click += (_, _) => Menus.AfterMenu(() => SwitchTo(pc.Address, pc.Port, Settings.Unprotect(pc.PasswordEnc), pc.ToString()));
                items.Add(item);
            }
            // Connected ones that are not saved.
            foreach (var p in _parked.Where(p => !_settings.SavedPcs.Any(s => SamePc(s.Address, s.Port, p.Address, p.Port))).ToList())
            {
                var item = new ToolStripMenuItem((p.Name + ", in the background").Replace("&", "&&"));
                item.Click += (_, _) => Menus.AfterMenu(() => SwitchTo(p.Address, p.Port, p.Password, p.Name));
                items.Add(item);
            }
            if (_client != null && !_settings.SavedPcs.Any(s => SamePc(s.Address, s.Port, FrontAddress, FrontPort)))
                items.Add(new ToolStripMenuItem((NameFor(FrontAddress, FrontPort) + ", in front").Replace("&", "&&")));
            // Never an empty or all-greyed-out menu: with nothing NVDA can land on, it froze.
            if (items.Count == 0) items.Add(new MenuStatus("There are no saved PCs to switch to yet. Connect to one, then File, Save this PC.").Item);
            if (_parked.Count > 0)
            {
                items.Add(new ToolStripSeparator());
                items.Add(Menus.Action("&Disconnect the PCs in the background", () =>
                {
                    int n = _parked.Count;
                    DropBackground();
                    Say("Disconnected " + n + (n == 1 ? " PC" : " PCs") + " in the background.");
                    UpdateButtons();
                    UpdateTitle();
                }));
            }
        }

        // ---- Internet speed tests ----

        private async void SpeedTestHere()
        {
            Doing("testing this PC's internet speed");
            _speedHere.Enabled = false;
            Say("Testing this PC's internet speed. It takes about 20 seconds" + (_client != null || _host != null ? ", and the sound may break up meanwhile." : "."));
            try { ShowSpeed("This PC's internet speed", await SpeedTest.RunAsync()); }
            finally { _speedHere.Enabled = true; }
        }

        private bool _speedRemoteRunning;

        private async void SpeedTestRemote()
        {
            if (!CanUseRemote(out var c)) return;
            Doing("testing the remote PC's internet speed");
            _speedRemoteRunning = true;
            UpdateButtons();
            Say("The remote PC is testing its internet speed. It takes about 20 seconds, and its sound may break up meanwhile.");
            string text;
            try { text = await c.RequestSpeedTestAsync(); }
            catch { text = "Problem: the remote PC did not answer."; }
            finally { _speedRemoteRunning = false; UpdateButtons(); }
            ShowSpeed("The remote PC's internet speed", text);
        }

        /// <summary>The results in a list, read with the arrows, brought up in front.</summary>
        private void ShowSpeed(string title, string text)
        {
            if (!Visible) RestoreFromTray();
            Speak(text.StartsWith("Problem: ") ? text["Problem: ".Length..] : title + ": the test is done.");
            if (text.StartsWith("Problem: ")) return;
            Activate();
            using var f = new ListViewerForm(title, text.Split('\n'), _ => false);
            f.ShowDialog(this);
        }

        // ---- Settings backups ----

        /// <summary>A save or open box on a thread of its own, like the file pickers. Null: cancelled.</summary>
        private static System.Threading.Tasks.Task<string?> PickBackupFile(bool save)
        {
            var done = new System.Threading.Tasks.TaskCompletionSource<string?>();
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    using FileDialog d = save
                        ? new SaveFileDialog { Title = "Back up Kova's settings to", FileName = "Kova settings.trbackup", DefaultExt = "trbackup" }
                        : new OpenFileDialog { Title = "Restore Kova's settings from" };
                    d.Filter = "Kova settings backups (*.trbackup)|*.trbackup|All files|*.*";
                    done.TrySetResult(d.ShowDialog() == DialogResult.OK ? d.FileName : null);
                }
                catch (Exception e) { done.TrySetException(e); }
            }) { IsBackground = true, Name = "Kova backup picker" };
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            return done.Task;
        }

        /// <summary>
        /// Asks until it is long enough (saying why first), or null when cancelled. minimum: a
        /// restore takes any length, since a backup made before 2.2.0 may have a 5-character one.
        /// </summary>
        private string? AskBackupPassword(string prompt, int minimum = MinPasswordLength)
        {
            string? problem = null;
            while (true)
            {
                using var f = new TextForm("Backup password", problem == null ? prompt : problem + " " + prompt, "", secret: true);
                if (f.ShowDialog(this) != DialogResult.OK) return null;
                if (f.Value.Length >= minimum) return f.Value;
                problem = "That has " + f.Value.Length + (f.Value.Length == 1 ? " character" : " characters") + ": it needs at least " + minimum + ".";
            }
        }

        private bool Busy => _client != null || _host != null || _reconnecting || _connecting || _parked.Count > 0;

        private async void BackupSettings()
        {
            Doing("backing up settings");
            SaveSettings();
            string? path;
            try { path = await PickBackupFile(save: true); }
            catch (Exception e) { Say("Could not open the save box: " + e.Message); return; }
            if (path == null) return;
            string? pw = AskBackupPassword("A password for the backup, at least " + MinPasswordLength + " characters. It locks the saved PCs' passwords inside, and you need it to restore:");
            if (pw == null) return;
            try
            {
                System.IO.File.WriteAllBytes(path, _settings.Export(pw));
                int n = _settings.SavedPcs.Count;
                Say("Backed up every setting and " + (n == 0 ? "no" : n.ToString()) + (n == 1 ? " saved PC" : " saved PCs") + " to " + path + ".");
            }
            catch (Exception e) { Say("Could not write the backup: " + e.Message); }
        }

        private async void RestoreSettings()
        {
            Doing("restoring settings");
            if (Busy) { Say("Disconnect, or stop hosting, first."); return; }
            string? path;
            try { path = await PickBackupFile(save: false); }
            catch (Exception e) { Say("Could not open the file box: " + e.Message); return; }
            if (path == null) return;
            byte[] data;
            try
            {
                // A backup is a few kilobytes: anything big is some other file, not read whole.
                if (new System.IO.FileInfo(path).Length > 4 << 20) { Say("That is not a Kova settings backup: it is far too big."); return; }
                data = System.IO.File.ReadAllBytes(path);
            }
            catch (Exception e) { Say("Could not read the file: " + e.Message); return; }
            // Checked before asking for a password: typing one only to hear the file was something else entirely wastes it.
            if (!Settings.LooksLikeBackup(data)) { Say("That is not a Kova settings backup."); return; }
            Settings? restored = null;
            string? problem = null;
            while (restored == null)
            {
                string? pw = AskBackupPassword((problem != null ? problem + " " : "") + "The backup's password:", minimum: 1);
                if (pw == null) return;
                try { restored = Settings.Import(data, pw); }
                catch (System.Security.Cryptography.CryptographicException) { problem = "That password is not the backup's."; }
                catch (Exception e) when (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase)) { problem = e.Message; }
                catch (Exception e) { Say(e.Message); return; }
            }
            int n = restored.SavedPcs.Count;
            if (MessageBox.Show(this, "Replace Kova's settings on this PC with the backup's? It has " + n + (n == 1 ? " saved PC" : " saved PCs") + ".",
                    "Restore settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            // Again: the window could be used while the boxes were open (a connection started meanwhile).
            if (Busy) { Say("Not restored: disconnect, or stop hosting, first."); return; }
            _settings.CopyFrom(restored);
            _settings.Save();
            LoadIntoWindow();
            Say("Restored every setting and " + n + (n == 1 ? " saved PC." : " saved PCs.") + (_service.Checked ? " Use Apply settings to the service to give the service them too." : ""));
        }

        /// <summary>Puts the settings into the menus (a restored backup), saving nothing until it is all in.</summary>
        private void LoadIntoWindow()
        {
            _loadingSettings = true;
            try
            {
                _mode.SelectedIndex = _settings.HostMode ? 1 : 0;
                _address.Text = _settings.Address;
                _port.Text = _settings.Port.ToString();
                _password.Text = _settings.Password;
                _listenPassword.Text = _settings.ListenPassword;
                _logging.Checked = _settings.Logging;
                DiagLog.Enabled = _settings.Logging;
                _speedUp.Checked = _settings.CatchUpBySpeed;
                _announceQuality.Checked = _settings.AnnounceQuality;
                _announceHealth.Checked = _settings.AnnounceHealth;
                _muteLocal.Checked = _settings.MuteWhenNotControlling;
                _rideOut.Checked = _settings.RideOutStalls;
                _quality.SelectedIndex = Math.Clamp(_settings.SoundQuality + 1, 0, _quality.Count - 1);
                foreach (var (id, role) in new[] { (_settings.OutputDevice, "output"), (_settings.CaptureDevice, "capture") })
                    if (!string.IsNullOrEmpty(id) && !_devices.Exists(d => d.Id == id))
                    {
                        _devices.Add((id, NotConnected(role)));
                        _device.Add(NotConnected(role));
                        _captureFrom.Add(NotConnected(role));
                    }
                int sel = _devices.FindIndex(d => d.Id == _settings.OutputDevice);
                _device.SelectedIndex = sel < 0 ? 0 : sel;
                _fillingCapture = true;
                int cap = _devices.FindIndex(d => d.Id == _settings.CaptureDevice);
                _captureFrom.SelectedIndex = cap < 0 ? 0 : cap;
                _fillingCapture = false;
                Sounds.Key = Math.Clamp(_settings.SoundKey, 0, 11);
                ApplyReceiveFolder();
                FillSaved();
            }
            finally { _loadingSettings = false; }
            UpdateMode();
        }

        // ---- Restart ----

        private void RestartRemote()
        {
            var c = _client;
            if (c == null || c.ListenOnly) { Say("Connect first."); return; }
            if (!c.CanRestart) { Say("The remote PC's Kova is too old to restart it. Update it first."); return; }
            // A checkbox must be ticked before Restart turns on, so a reflexive Enter cannot restart the remote PC.
            using (var dlg = new RestartConfirmForm())
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (_client != c) { Say("Not restarted: the PC in front changed meanwhile."); return; }
            _expectRestart = true;
            c.RestartHost();
            Say("Asked the remote PC to restart.");
        }

        // ---- The Files line: how a transfer is going ----

        private const string TransferIdle = "Nothing being sent or received. Use Send the clipboard or Send files.";
        private FileChannel.Transfer? _transferShown;

        /// <summary>A finished line stays 15 seconds, then the Files line goes back to saying nothing is going.</summary>
        // Every send and receive that ended, newest first, until Clear the history. The Files line
        // keeps the last one too: it used to go back to "nothing being sent" after 15 seconds, so
        // whoever was away or busy elsewhere never found out how a transfer had gone.
        private readonly System.Collections.Generic.List<string> _historyLines = new();
        private readonly MenuItem _history = new("&History of transfers");
        private const int MaxHistory = 100;

        private void Remember(string what)
        {
            _historyLines.Insert(0, DateTime.Now.ToString("t") + ": " + what);
            if (_historyLines.Count > MaxHistory) _historyLines.RemoveAt(_historyLines.Count - 1);
            FillHistory();
        }

        private void FillHistory()
        {
            var items = _history.DropDownItems;
            items.Clear();
            _history.Text = "&History of transfers" + (_historyLines.Count > 0 ? " (" + _historyLines.Count + ")" : "");
            // Never an empty menu: with nothing to land on, NVDA froze.
            if (_historyLines.Count == 0) { items.Add(new MenuStatus("Nothing has been sent or received yet.").Item); return; }
            foreach (var line in _historyLines) items.Add(new MenuStatus(line).Item);
            items.Add(new ToolStripSeparator());
            items.Add(Menus.Action("&Clear the history", () =>
            {
                _historyLines.Clear();
                if (_transferShown == null) _transferStatus.Text = TransferIdle;
                FillHistory();
                Say("Cleared.");
            }));
        }

        /// <summary>
        /// While several files move, the Files line opens a list with a line per file: its name,
        /// how much has moved, and the percent. Nothing to open when one or none is moving.
        /// </summary>
        private void ShowFileBars(System.Collections.Generic.IReadOnlyList<(string Name, long Done, long Total)> files)
        {
            var items = _transferStatus.Item.DropDownItems;
            int n = Math.Min(files.Count, 32);
            // One file alone is the Files line itself: no list to open.
            if (n < 2) n = 0;
            while (items.Count > n) items.RemoveAt(items.Count - 1);
            while (items.Count < n) items.Add(new ToolStripMenuItem { Tag = Menus.KeepOpenTag });
            for (int i = 0; i < n; i++)
            {
                var (name, done, total) = files[i];
                int percent = total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 100);
                string text = (name + ": " + FileChannel.Size(done) + " of " + FileChannel.Size(total) + ", " + percent + " percent").Replace("&", "&&");
                if (items[i].Text != text) items[i].Text = text;
            }
        }

        /// <summary>The connection closed: whatever was going has stopped, and the Files line says so.</summary>
        private void TransferEnded(string why)
        {
            if (_transferShown == null) return;
            _transferShown = null;
            _going.Clear();
            _ended.Clear();
            ShowFileBars(Array.Empty<(string, long, long)>());
            _transferStatus.Text = why;
            _transferStop.Enabled = false;
            Remember(why);
        }

        // Every transfer going right now (several when this PC hosts many controlling PCs), and
        // those that ended while others still went: the Files line sums them up once all are done.
        private readonly System.Collections.Generic.Dictionary<int, FileChannel.Transfer> _going = new();
        private readonly System.Collections.Generic.List<FileChannel.Transfer> _ended = new();

        /// <summary>How transfers are going, either way: the Files line and bars. Never spoken, so a lot of them cannot flood NVDA; the sounds say they started and ended.</summary>
        /// <summary>A transfer of files coming to this PC ended (RemoteFilesForm marks what it asked for).</summary>
        internal static event Action<FileChannel.Transfer>? IncomingFilesEnded;
        /// <summary>Files sent from this PC (not the clipboard) finished going (RemoteFilesForm shows what it sent into a folder).</summary>
        internal static event Action<FileChannel.Transfer>? OutgoingFilesEnded;

        private void ShowTransfer(FileChannel.Transfer t)
        {
            if (t.Finished) { _going.Remove(t.Serial); if (!_ended.Contains(t)) _ended.Add(t); }
            else _going[t.Serial] = t;
            _transferShown = _going.Values.FirstOrDefault();
            if (_going.Count == 0)
            {
                // All ended: one line for all of them.
                var ends = _ended.ToList();
                _ended.Clear();
                if (ends.Count == 0) return;
                ShowFileBars(Array.Empty<(string, long, long)>());
                _transferStatus.Text = Summary(ends);
                // Files coming here (Get files, or Send files from the other PC): said, not only a sound,
                // as you may be busy in another window (Get files) when they finish.
                var arrived = ends.Where(e => !e.Outgoing && !e.Clipboard).ToList();
                if (arrived.Count > 0) Say(Summary(arrived));
                foreach (var e in arrived) IncomingFilesEnded?.Invoke(e);
                foreach (var e in ends.Where(e => e.Outgoing && !e.Clipboard)) OutgoingFilesEnded?.Invoke(e);
                Remember(_transferStatus.Text);
                _transferStop.Enabled = false;
                // The clipboard's own sounds play when it is sent and when it arrives; file sounds are
                // for Send files only; and stopping (or replacing) on purpose is never an error.
                if (ends.Any(e => e.Failed && !e.Cancelled)) Tone(Sounds.Tone.Error);
                else if (ends.Any(e => !e.Failed && !e.Clipboard)) Tone(ends[0].Outgoing ? Sounds.Tone.FileSent : Sounds.Tone.FileReceived);
                return;
            }
            var going = _going.Values.ToList();
            if (going.Count == 1 && _ended.Count == 0)
            {
                var g = going[0];
                string left = g.Left is TimeSpan l ? ", about " + Duration(l) + " left" : "";
                _transferStatus.Text = (g.Outgoing ? "Sending " : "Receiving ") + g.What + ": " + FileChannel.Size(g.Done) + " of " + FileChannel.Size(g.Total) +
                    (g.Waiting ? ", waiting for the connection to come back; it carries on from here." : ", " + FileChannel.Speed(g.BytesPerSecond) + left + ".");
                ShowFileBars(g.Files);
            }
            else
            {
                // Several at once (to or from several PCs): the totals, and one bar per PC.
                var all = going.Concat(_ended).ToList();
                long done = all.Sum(x => x.Finished ? x.Total : x.Done), total = all.Sum(x => x.Total);
                bool outgoing = going.All(x => x.Outgoing), incoming = going.All(x => !x.Outgoing);
                string names = string.Join(", ", going.Select(x => x.What).Distinct().Take(3));
                _transferStatus.Text = (outgoing ? "Sending " + names + " to " + all.Count + " PCs" : incoming ? "Receiving " + names + " from " + all.Count + " PCs" : "Moving " + all.Count + " transfers") +
                    (_ended.Count > 0 ? ", " + _ended.Count + " finished" : "") + ": " + FileChannel.Size(done) + " of " + FileChannel.Size(total) + ", " + FileChannel.Speed(going.Sum(x => x.BytesPerSecond)) +
                    // They go side by side, so the slowest one says when all are done.
                    (going.Select(x => x.Left).Where(x => x != null).Max() is TimeSpan most ? ", about " + Duration(most) + " left" : "") + ".";
                ShowFileBars(going.Select(x => ((x.Peer.Length > 0 ? x.Peer + ", " : "") + x.What, x.Done, x.Total)).ToList());
            }
            _transferStop.Enabled = true;
        }

        /// <summary>
        /// How long, in words, with the two largest parts that matter: "2 days 3 hours",
        /// "1 hour 20 minutes", "4 minutes 10 seconds", "12 seconds".
        /// </summary>
        internal static string Duration(TimeSpan t)
        {
            static string Part(long n, string unit) => n + " " + unit + (n == 1 ? "" : "s");
            long seconds = Math.Max(1, (long)Math.Round(t.TotalSeconds));
            long days = seconds / 86_400, hours = seconds / 3600 % 24, minutes = seconds / 60 % 60, secs = seconds % 60;
            if (days > 0) return Part(days, "day") + (hours > 0 ? " " + Part(hours, "hour") : "");
            if (hours > 0) return Part(hours, "hour") + (minutes > 0 ? " " + Part(minutes, "minute") : "");
            if (minutes > 0) return Part(minutes, "minute") + (secs > 0 ? " " + Part(secs, "second") : "");
            return Part(secs, "second");
        }

        /// <summary>One line for transfers that all ended: the one result, or how many PCs got it and what went wrong.</summary>
        private static string Summary(System.Collections.Generic.List<FileChannel.Transfer> ends)
        {
            if (ends.Count == 1) return ends[0].Result ?? TransferIdle;
            var good = ends.Where(e => !e.Failed).ToList();
            var bad = ends.Where(e => e.Failed).ToList();
            string what = string.Join(", ", ends.Select(e => e.What).Distinct().Take(3));
            string head = ends.All(e => e.Outgoing)
                ? (bad.Count == 0 ? "Sent " + what + " to all " + ends.Count + " PCs" : "Sent " + what + " to " + good.Count + " of " + ends.Count + " PCs")
                : (bad.Count == 0 ? "Received " + what + " from all " + ends.Count + " PCs" : "Received from " + good.Count + " of " + ends.Count + " PCs");
            if (good.Count > 0) head += ", " + FileChannel.Size(good.Sum(e => e.Total)) + " in all";
            if (bad.Count > 0) head += (bad[0].Outgoing ? ". Not sent to: " : ". Did not arrive from: ") + string.Join("; ", bad.Take(5).Select(e => (e.Peer.Length > 0 ? e.Peer + ": " : "") + e.Result));
            return head + ".";
        }

        // ---- Saved PCs ----

        private bool _filling;

        private void FillSaved()
        {
            _filling = true;
            _saved.Clear();
            _saved.Add("None: type the address");
            for (int n = 0; n < _settings.SavedPcs.Count; n++)
            {
                _saved.Add(_settings.SavedPcs[n].ToString()); // (Control 1 to 9 belong to Switch to PC, which connects)
            }
            int i = _settings.SavedPcs.FindIndex(p => p.Address == _settings.Address && p.Port == _settings.Port);
            _saved.SelectedIndex = i + 1;
            _filling = false;
            _forgetPc.Enabled = _saved.SelectedIndex > 0;
        }

        private SavedPc? ChosenSaved() => _saved.SelectedIndex > 0 && _saved.SelectedIndex <= _settings.SavedPcs.Count ? _settings.SavedPcs[_saved.SelectedIndex - 1] : null;

        private void UseSaved()
        {
            _forgetPc.Enabled = _saved.SelectedIndex > 0;
            if (_filling || ChosenSaved() is not SavedPc pc) return;
            _address.Text = pc.Address;
            _port.Text = pc.Port.ToString();
            _password.Text = Settings.Unprotect(pc.PasswordEnc);
            SaveSettings();
        }

        private void SavePc()
        {
            string address = _address.Text.Trim();
            if (address.Length == 0) { Later(() => _address.Ask(this, "Type the address first.")); return; }
            if (!CheckPassword(hosting: false) || !CheckPort(out int port)) return;
            var pc = _settings.SavedPcs.Find(p => string.Equals(p.Address, address, StringComparison.OrdinalIgnoreCase) && p.Port == port);
            // Already there with the same password: nothing to do. (The stored form is re-encrypted
            // differently each time, so compare the password itself, not the stored bytes.)
            if (pc != null && Settings.Unprotect(pc.PasswordEnc) == _password.Text) { Say(pc + " is already saved."); return; }
            bool isNew = pc == null;
            pc ??= new SavedPc { Address = address, Port = port };
            pc.PasswordEnc = Settings.Protect(_password.Text);
            // Connected to it now (in front or in the background): how to wake it is known already.
            string? wake = _client != null && SamePc(FrontAddress, FrontPort, address, port) ? _client.WakeInfo
                : _parked.Find(x => SamePc(x.Address, x.Port, address, port))?.Client.WakeInfo;
            if (!string.IsNullOrEmpty(wake)) pc.Wake = wake;
            if (isNew) _settings.SavedPcs.Add(pc);
            SaveSettings();
            FillSaved();
            Say(isNew ? "Saved " + pc + "." : "Updated the password for " + pc + ".");
        }

        private void ForgetPc()
        {
            if (ChosenSaved() is not SavedPc pc) return;
            _settings.SavedPcs.Remove(pc);
            _settings.Save();
            FillSaved();
            Say("Forgot " + pc + ".");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _shown = true;
            StartWatchdog();
            _keys = new KeyCapture(() => _handle);
            _keys.ModeChanged += remote => Later(() => ModeChanged(remote));
            _keys.NotConnected += () => Later(() => Say("Not connected."));
            _titleTimer.Start();
            if (!_autoHost)
            {
                // The window is blank, with nothing in it to take focus, so NVDA said nothing when
                // TailRemote opened: you had to Alt Tab away and back to know where you were.
                Activate();
                Speech.Speak(Text + ". Alt opens the menus.", interrupt: false);
            }
            if (_updated) Say("Updated to version " + Updater.Current + ".");
            OfferServiceUpdate();
            if (_autoHost && _service.Checked) Log("The Kova service is hosting this PC, so this window does not.");
            else if (_autoHost)
            {
                _mode.SelectedIndex = 1;
                _hostingByItself = true;
                try { Go(); }
                finally { _hostingByItself = false; }
                BeginInvoke(() => HideToTray(announce: false)); // started with Windows: out of the way
            }
            else if (_autoConnect && !HostMode && _address.Text.Trim().Length > 0) Go();
        }

        /// <summary>
        /// Settings, Mute the remote PC while you are not controlling it: the PC in front is
        /// silent while the keys are on this PC, the host sending it no sound at all (which saves
        /// data on a phone connection), and the connection stays up. Controlling it again brings
        /// the sound straight back at its best. Listen-only connections are always heard.
        /// </summary>
        private void ApplyMute()
        {
            if (_client is not Client c || c.ListenOnly) return;
            bool mute = _muteLocal.Checked && _keys?.Remote != true;
            if (mute == c.Muted) return;
            c.Muted = mute;
            if (mute) c.LockedStep = Protocol.OpusSteps.Length - 1;
            else
            {
                c.LockedStep = _quality.SelectedIndex - 1;
                c.StartBest();
            }
            // Muting and unmuting move the quality on purpose: nothing to announce.
            _qualitySaid = c.AudioQuality;
        }

        private void ModeChanged(bool remote)
        {
            _toggle.Text = remote ? "Stop controlling the &remote PC" : "Control &remote PC";
            ApplyMute();
            if (_quietModeChange) _quietModeChange = false; // the reconnect message says it
            else { Say(remote ? "Controlling remote PC." : "Controlling this PC."); Tone(remote ? Sounds.Tone.ControlRemote : Sounds.Tone.ControlLocal); }
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            string t;
            if (_client != null)
            {
                t = "Kova - " + (_client.ListenOnly ? "listening" : _keys?.Remote == true ? "controlling remote" : "connected");
                if (_client.LastPingMs >= 0) t += ", ping " + _client.LastPingMs + " ms";
                int audio = _client.AudioDelayMs;
                if (audio >= 0) t += ", audio " + (audio + _client.PingForAudio / 2) + " ms";
                // Muted on purpose (Mute the remote PC while you are not controlling it) is said as that:
                // its few kilobits read as "sound at 6 kbit/s", as if the quality lock were not working.
                if (_client.Muted) t += ", sound muted while you are not controlling it";
                else if (_client.ReducedSound is string reduced) t += ", sound at " + reduced;
                if (_parked.Count > 0) t += ", " + _parked.Count + " more in the background";
                if (_screen is { IsDisposed: false } screen) t += ", screen " + VideoForm.Rate(screen.PerSecond);
                WatchConnection(_client);
            }
            else if (_reconnecting) t = "Kova - reconnecting";
            else if ((_host?.Connected ?? (_serviceLink?.Connected == true ? _serviceLink.Counts : null)) is (int controlling, int listening))
            {
                t = "Kova - hosting" + (_host == null ? " as the service" : "");
                // More than one PC: how many, and what they are doing.
                if (controlling + listening > 1)
                    t += ", " + (controlling + listening) + " PCs connected (" + controlling + " controlling" + (listening > 0 ? ", " + listening + " listening" : "") + ")";
            }
            else t = "Kova";
            if (Text != t) Text = t;
            // The Streaming line, read with Tab: what is coming in right now.
            string st = _client == null ? (_reconnecting ? "Not connected: trying again every second" : "Not connected")
                : (_client.Muted
                    ? "Muted while you are not controlling it. Controlling it brings the sound back, " +
                      (_quality.SelectedIndex > 0 ? "locked at " + Protocol.OpusSteps[_quality.SelectedIndex - 1].Kbps + " kilobits per second" : "variable")
                    : "Streaming at " + Protocol.OpusSteps[_client.AudioQuality].Kbps + " kilobits per second" +
                      (_client.LockedStep >= 0 ? ", locked" : ", variable")) +
                  (_client.AudioDelayMs >= 0 ? ", audio delay " + (_client.AudioDelayMs + _client.PingForAudio / 2) + " ms" : "") +
                  (_player?.RidingOutMs > 0 ? ", riding out stalls (" + _player.RidingOutMs + " ms of it)" : "") +
                  (WlanStreaming.State is string wifi ? ". " + wifi : "") +
                  (_client.UdpBlocked ? ". NO SOUND: UDP port " + _client.Port + " is blocked between the PCs. On the remote PC, open it with Port editor, or check its firewall" : "");
            if (_streaming.Text != st) _streaming.Text = st;
        }

        private void SaveSettings()
        {
            if (_loadingSettings) return;
            _settings.HostMode = HostMode;
            _settings.Address = _address.Text.Trim();
            if (int.TryParse(_port.Text.Trim(), out int port)) _settings.Port = port;
            _settings.Password = _password.Text;
            _settings.OutputDevice = _devices[Math.Max(0, _device.SelectedIndex)].Id;
            _settings.CaptureDevice = _devices[Math.Max(0, _captureFrom.SelectedIndex)].Id;
            _settings.ListenPassword = _listenPassword.Text;
            _settings.Logging = _logging.Checked;
            _settings.CatchUpBySpeed = _speedUp.Checked;
            _settings.AnnounceQuality = _announceQuality.Checked;
            _settings.AnnounceHealth = _announceHealth.Checked;
            _settings.MuteWhenNotControlling = _muteLocal.Checked;
            _settings.RideOutStalls = _rideOut.Checked;
            _settings.SoundQuality = _quality.SelectedIndex - 1;
            _settings.Save();
        }

        // ---- Hang watchdog ----
        // Once a second a background thread asks the window to answer. If it has not for
        // 3 seconds, what it was doing goes to TailRemote-crash.txt, so a freeze can be found.
        private static volatile string _doing = "starting";
        private long _answeredAt = Environment.TickCount64;

        /// <summary>Notes what the window is doing, for the hang watchdog.</summary>
        private static void Doing(string what) => _doing = what;

        private void StartWatchdog()
        {
            new System.Threading.Thread(() =>
            {
                bool reported = false;
                long lastPass = Environment.TickCount64;
                while (!IsDisposed)
                {
                    System.Threading.Thread.Sleep(1000);
                    // Back from sleep: the clock moved on while nothing ran at all, which is not the window hanging.
                    long now = Environment.TickCount64;
                    bool slept = now - lastPass > 5000;
                    lastPass = now;
                    if (slept) System.Threading.Interlocked.Exchange(ref _answeredAt, now);
                    // Answering clears the label too: a report then always names what was really going on.
                    try { if (IsHandleCreated) BeginInvoke(() => { _answeredAt = Environment.TickCount64; _doing = "nothing in particular"; }); } catch { return; }
                    long stuck = Environment.TickCount64 - System.Threading.Interlocked.Read(ref _answeredAt);
                    if (stuck > 3000 && !reported)
                    {
                        reported = true;
                        Program.CrashNote("the window stopped responding for " + stuck / 1000 + " seconds while " + _doing);
                    }
                    else if (stuck < 1500) reported = false;
                }
            }) { IsBackground = true, Name = "Kova watchdog" }.Start();
        }

        private void Go()
        {
            Doing("pressing " + (_go.Text ?? "").Replace("&", ""));
            if (_connecting && !_reconnecting) return; // a second press while connecting would start a second connection
            SaveSettings();
            if (HostMode && _service.Checked) ApplyService();
            else if (HostMode) { if (_host == null) StartHost(); else StopHost(); }
            else if (_reconnecting) StopReconnecting("Disconnected.");
            else if (_client == null) Connect(quiet: false);
            else Disconnect("Disconnected.", byUser: true);
        }

        private async void StartHost()
        {
            Doing("starting hosting");
            if (ServiceHost.IsInstalled()) { Say("The Kova service is hosting this PC. Use Apply settings to the service instead."); return; }
            if (!CheckPassword()) return;
            if (!CheckListenPassword()) return;
            string listen = _listenPassword.Text;
            if (!CheckPort(out int port)) return;
            PortEditorForm.Remember(_settings, port);
            try
            {
                _host = new Host(port, _password.Text, listen, msg => Later(() => Log(msg)),
                    _settings.CaptureDevice.Length == 0 ? null : _settings.CaptureDevice);
                _host.ClipboardReceived += text => Later(() => ClipboardArrived(text));
                _host.ClipboardFilesReceived += paths => Later(() => ClipboardFilesArrived(paths));
                var hosting = _host;
                _host.ClipboardRequested += () => SendClipboardToControllers(hosting); // a controller pulled this host's clipboard
                _host.TransferProgress += t => Later(() => ShowTransfer(t));
                _host.UpdateRequested += (version, reply) => Later(() => RemoteUpdate(version, reply));
                Say("Hosting on port " + port + ". Waiting for a connection." + (listen.Length > 0 ? " Listening with the listen-only password is on." : ""));
                // Audio settings and device names are never changed by themselves (they are other
                // programs' too): only Set up audio device does that, when pressed.
                if (Wasapi.OutputDevices().Count == 0)
                    Say("This PC has no sound output, so the other PC will hear nothing. Press Set up audio device if you want Kova to add one.");
                if (!Startup.IsElevated()) Log("Not running as administrator, so keys cannot reach administrator windows. Start hosting when Windows starts runs it as administrator.");
            }
            catch (Exception e) { Say("Could not start hosting: " + e.Message); }
            UpdateButtons();
            UpdateTitle();
            if (_host != null) await OfferToOpenPort(port);
        }

        /// <summary>If Windows Firewall would keep other PCs out, open the port (asking first unless already administrator).</summary>
        private async System.Threading.Tasks.Task OfferToOpenPort(int port)
        {
            if (await System.Threading.Tasks.Task.Run(() => Firewall.IsOpen(port))) return;
            if (Startup.IsElevated())
            {
                if (await System.Threading.Tasks.Task.Run(() => Firewall.Apply(true, port)) == 0) Log("Opened port " + port + " in Windows Firewall.");
                return;
            }
            var answer = MessageBox.Show(this, "Port " + port + " is not open in Windows Firewall, so other PCs may not be able to connect. Open it now? Windows asks for administrator permission. You can close it later in Port editor.",
                "Open port " + port, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            Say(await Firewall.ChangeAsync(port, true) switch
            {
                ChangeResult.Done => "Port " + port + " is open.",
                ChangeResult.Declined => "Port " + port + " was not opened: administrator permission was not given.",
                _ => "Port " + port + " was not opened: Windows did not accept the change.",
            });
        }

        public const int MinPasswordLength = 6;

        // Hosting started by itself (at sign-in, or the new copy after an update): nobody is there to
        // type a longer password, and refusing would leave the PC unreachable, so a short one still hosts.
        private bool _hostingByItself;

        /// <summary>
        /// Refuses a password shorter than 6 characters for hosting. For connecting (or saving a PC
        /// to connect to) only an empty one is refused: the other PC decides, and one still on an
        /// older 5-character password must stay reachable, or it could never be updated from here.
        /// </summary>
        private bool CheckPassword(bool hosting = true)
        {
            int n = _password.Text.Length;
            if (n >= MinPasswordLength || (n > 0 && !hosting)) return true;
            if (n > 0 && _hostingByItself)
            {
                Speak("This PC's password has only " + n + " characters. Choose one of at least " + MinPasswordLength + ", on this PC and the ones that connect to it.");
                return true;
            }
            string why = n == 0 ? "Type a password first." : "The password needs at least " + MinPasswordLength + " characters. It has " + n + ".";
            Log(why);
            Later(() => _password.Ask(this, why));
            return false;
        }

        /// <summary>
        /// Refuses a listen-only password shorter than 6 characters, or the same as the main
        /// password. Empty is fine: it means no listen-only password. Shared by StartHost and the
        /// service paths (ServiceChanged/ApplyService), so the service cannot be enabled or applied
        /// with a listen-only password that hosting itself would refuse.
        /// </summary>
        private bool CheckListenPassword()
        {
            string listen = _listenPassword.Text;
            if (listen.Length == 0 || (listen.Length >= MinPasswordLength && listen != _password.Text)) return true;
            if (_hostingByItself && listen != _password.Text)
            {
                // As with the main password: refusing here would leave a PC that hosts by itself unreachable.
                Speak("The listen-only password has only " + listen.Length + " characters. Choose one of at least " + MinPasswordLength + ".");
                return true;
            }
            string why = listen == _password.Text
                ? "The listen-only password must be different from the main password, or empty."
                : "The listen-only password needs at least " + MinPasswordLength + " characters, or leave it empty.";
            Log(why);
            Later(() => _listenPassword.Ask(this, why));
            return false;
        }

        /// <summary>Refuses a port that is not a number, or that would clash with something else.</summary>
        private bool CheckPort(out int port)
        {
            string? problem = Protocol.PortProblem(_port.Text.Trim(), out port);
            if (problem == null) return true;
            Log(problem);
            Later(() => _port.Ask(this, problem));
            return false;
        }

        private void StopHost()
        {
            TransferEnded("Stopped: hosting stopped.");
            Doing("stopping hosting");
            _host?.Leave(Protocol.LeavingStopped);
            _host?.Dispose();
            _host = null;
            Say("Stopped hosting.");
            UpdateButtons();
            UpdateTitle();
        }

        /// <summary>Connects with the saved settings. Quiet attempts are reconnects: only success is spoken.</summary>
        private async void Connect(bool quiet)
        {
            if (HostMode) { if (_reconnecting) StopReconnecting("Stopped trying to reconnect: this PC is hosting now."); return; }
            string address;
            int port;
            string pw;
            if (quiet && _frontAddress != null)
            {
                // Trying again: the PC that was in front, never a box asking for something (one would
                // open every second, each inside the last).
                (address, port, pw) = (_frontAddress, _frontPort, _frontPassword ?? "");
            }
            else if (quiet)
            {
                address = _address.Text.Trim();
                pw = _password.Text;
                if (address.Length == 0 || pw.Length == 0 || !int.TryParse(_port.Text.Trim(), out port) || port is <= 0 or > 65535)
                {
                    StopReconnecting("Stopped trying: the address, port or password is empty or not valid. Fix it, then press Connect.");
                    return;
                }
            }
            else
            {
                address = _address.Text.Trim();
                if (SplitPort(address) is (string bare, int typedPort))
                {
                    // "pc:47120" typed into Address: the port goes where it belongs.
                    address = bare;
                    _address.Text = bare;
                    _port.Text = typedPort.ToString();
                    SaveSettings();
                }
                if (address.Length == 0) { Later(() => _address.Ask(this, "Type the address first.")); return; }
                if (!CheckPassword(hosting: false)) return;
                if (!CheckPort(out port)) return;
                pw = _password.Text;
            }
            if (!quiet && FindParked(address, port) is Parked waiting) { Unpark(waiting); return; } // already connected, in the background
            (_frontAddress, _frontPort, _frontPassword) = (address, port, pw);
            _connecting = true;
            int attempt = ++_attempt;
            if (!quiet) { _go.Enabled = false; Say("Connecting to " + address + "."); }
            UpdateButtons();
            string device = _devices[Math.Max(0, _device.SelectedIndex)].Id;
            try
            {
                if (_player == null || _playerDevice != device)
                {
                    _player?.Dispose();
                    _player = new Player(device, msg => Later(() => Say(msg)));
                    _playerDevice = device;
                }
                _player.SpeedUp = _speedUp.Checked;
                _player.RideOut = _rideOut.Checked;
                var player = _player;
                int locked = _quality.SelectedIndex - 1; // from the very first sound
                // Trying again: a short wait for an answer, so a PC that is starting up is caught the
                // moment it hosts, instead of every 8 seconds or more.
                int answerMs = _reconnecting ? 2500 : 8000;
                Client? made = null;
                // What a PC says while it waits in the background is said with its name: "Host audio is not
                // available" from a background PC sounded as if it came from the one in front.
                void Told(string msg) => Later(() => Say(_parked.Find(x => x.Client == made) is Parked bg ? bg.Name + ", in the background: " + msg : msg));
                var c = await System.Threading.Tasks.Task.Run(() =>
                    Client.Connect(address, port, pw, player, Told, locked, answerMs));
                made = c;
                if (attempt != _attempt || HostMode || _client != null)
                {
                    // Stopped, switched to hosting, or already connected while this was under way.
                    c.Dispose();
                    return;
                }
                c.Disconnected += why => Later(() => { if (c == _client) Disconnect(why, byUser: false); else BackgroundDropped(c, why); });
                c.ClipboardReceived += text => Later(() => ClipboardArrived(text));
                c.ClipboardFilesReceived += paths => Later(() => ClipboardFilesArrived(paths));
                c.TransferProgress += t => Later(() => ShowTransfer(t));
                c.HealthAlert += text => Later(() => HealthSaid(c, text));
                c.WakeInfoKnown += info => Later(() => RememberWake(address, port, info));
                if (c.WakeInfo is string knownNow) RememberWake(address, port, knownNow); // it can come before the line above
                _client = c;
                if (!c.ListenOnly) _keys?.SetClient(c); // a listener never sends keys
                ApplyMute();
                bool wasReconnecting = _reconnecting;
                _reconnecting = false;
                _retryTimer.Stop();
                string start = _expectUpdate != null ? "The remote PC is back" + (_expectUpdate.Length > 0 ? ", updated to " + _expectUpdate : "") + "."
                    : _expectRestart ? "The remote PC is back." : wasReconnecting ? "Reconnected." : "Connected.";
                _expectRestart = false;
                _expectUpdate = null;
                bool resume = _resumeRemote && !c.ListenOnly;
                _resumeRemote = false;
                if (resume)
                {
                    _quietModeChange = true;
                    _keys?.Toggle(); // straight back to controlling the remote PC
                }
                Tone(Sounds.Tone.Connected);
                Say(c.ListenOnly ? start + " Listen only: you hear the remote PC, but cannot control it."
                    : resume ? start + " Controlling the remote PC."
                    : start + " Press Control Shift Enter to control the remote PC.");
            }
            catch (Exception e) when (attempt == _attempt)
            {
                // Keeps trying every second until Disconnect, except when trying again
                // cannot help (and a wrong password tried again gets this PC blocked).
                // Only a wrong password stops it: the login is checked for damage, so that is real. A
                // "different version" can also be a damaged first message, so it keeps trying.
                bool hopeless = e.Message.StartsWith("Wrong password");
                if (hopeless) { if (_reconnecting) StopReconnecting("Stopped trying: " + e.Message); else Say("Could not connect: " + e.Message); }
                else if (!_reconnecting)
                {
                    _reconnecting = true;
                    _retryTimer.Start();
                    if (WakingUp(address, port)) Say("Waiting for " + _waking + " to wake up. Kova connects as soon as it answers; press Disconnect to stop.");
                    else
                    {
                        Tone(Sounds.Tone.Error);
                        Say("Could not connect: " + e.Message + " Trying again every second until you press Disconnect.");
                    }
                }
            }
            catch { } // an abandoned attempt failing: nobody is waiting for it
            finally
            {
                // Only the latest attempt: an abandoned one finishing late (after Control 2 started
                // another) must not say the new one is done connecting.
                if (attempt == _attempt)
                {
                    _connecting = false;
                    _go.Enabled = true;
                }
                UpdateButtons();
                UpdateTitle();
            }
        }

        private void Disconnect(string why, bool byUser)
        {
            // Transfers never end with the connection, dropped or disconnected: they wait (the Files
            // line says so) and carry on where they were on the next connection to this PC. Only
            // Stop the file transfer ends them.
            Doing("disconnecting");
            if (_client == null) return;
            // Keys come back to this PC while the connection is down, so nothing is
            // typed into nowhere; remote control resumes by itself on reconnecting.
            _resumeRemote = !byUser && _keys?.Remote == true;
            _quietModeChange = _resumeRemote;
            byte leaving = _client.Leaving;
            string leavingDetail = _client.LeavingDetail;
            _keys?.SetClient(null);
            _client.Dispose();
            _client = null;
            Tone(Sounds.Tone.Disconnected);
            if (!byUser && leaving is Protocol.LeavingRestarting or Protocol.LeavingShutdown) _expectRestart = true; // whoever asked for it
            if (byUser)
            {
                _frontAddress = _frontPassword = null; // the next Connect goes wherever Address says
                _expectRestart = false;
                _expectUpdate = null;
                Say(why);
                if (_parked.Count > 0) Speak("Still connected in the background to " + string.Join(", ", _parked.Select(x => x.Name)) + ". File, Switch to PC brings one to the front.");
            }
            else if (leaving == Protocol.LeavingUpdating)
            {
                // The host said it is updating: no error, just wait for it to come back.
                _expectUpdate = leavingDetail;
                _reconnecting = true;
                _retryTimer.Start();
                Say(why + " Kova reconnects as soon as it is back.");
            }
            else if (_expectRestart)
            {
                // Expected: keep trying until it is back, however long the restart takes.
                _reconnecting = true;
                _retryTimer.Start();
                Say(leaving == Protocol.LeavingShutdown ? why + " Kova reconnects if it comes back."
                    : "The remote PC is restarting. Kova reconnects as soon as it is back.");
            }
            else if (leaving == Protocol.LeavingStopped)
            {
                _reconnecting = true;
                _retryTimer.Start();
                Say(why + " Trying again every second until you press Disconnect.");
            }
            else
            {
                // Dropped, or the host restarted after an update: try at once, then every second.
                _reconnecting = true;
                _retryTimer.Start();
                Say(why + " Reconnecting.");
                Connect(quiet: true);
            }
            UpdateButtons();
            UpdateTitle();
        }

        /// <summary>A piano tone for an event, on the controlling PC only (a host's would be sent along with its sound).</summary>
        private void Tone(Sounds.Tone t)
        {
            if (!HostMode) Sounds.Play(t);
        }

        private void StopReconnecting(string why)
        {
            Tone(why.StartsWith("Disconnected") ? Sounds.Tone.Disconnected : Sounds.Tone.Error);
            _resumeRemote = false;
            _expectRestart = false;
            _expectUpdate = null;
            _attempt++;
            _reconnecting = false;
            _connecting = false; // the abandoned attempt's own finally no longer counts, so it is cleared here
            _frontAddress = _frontPassword = null;
            _go.Enabled = true;
            _retryTimer.Stop();
            Say(why);
            UpdateButtons();
            UpdateTitle();
        }

        private bool _updating;

        private async void CheckForUpdates()
        {
            Doing("checking for updates");
            if (_updating) { Say("Kova is already updating."); return; }
            _updating = true; // from the start: a remote request during the question box must not start a second download
            _update.Enabled = false;
            Say("Checking for updates.");
            bool checkedOk = false;
            try
            {
                var r = await Updater.CheckAsync();
                checkedOk = true;
                if (r == null) { Say("Kova " + Updater.Current + " is the latest version."); return; }
                // A big release's notes made a box taller than the screen, its buttons out of reach, and
                // NVDA read all of it before the question. The first lines are enough to decide; the
                // changelog has the rest once the new version is running.
                var noteLines = r.Notes.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
                const int shown = 12;
                string notes = noteLines.Count == 0 ? "No notes."
                    : string.Join(Environment.NewLine, noteLines.Take(shown)) +
                      (noteLines.Count > shown ? Environment.NewLine + "and " + (noteLines.Count - shown) + " more lines; About, View the changelog shows them all after updating." : "");
                var answer = MessageBox.Show(this, "Version " + r.Version + " is available. You have " + Updater.Current + "." +
                    Environment.NewLine + Environment.NewLine + notes + Environment.NewLine + Environment.NewLine + "Update now? Kova restarts and carries on where it was.",
                    "Kova update", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2); // default No: a stray Enter must not kick off an update + restart
                if (answer != DialogResult.Yes) return;
                Say("Downloading version " + r.Version + ". Everything carries on until it is ready.");
                await InstallUpdate(r);
            }
            catch (Exception e) { Say((checkedOk ? "Update failed: " : "Could not check for updates: ") + e.Message); }
            finally
            {
                if (!_reallyExit) _updating = false;
                if (!IsDisposed) _update.Enabled = true;
            }
        }

        /// <summary>
        /// The controlling PC chose File, Update the remote PC: update to that version, which must
        /// be GitHub's newest (never anything else), with no questions here. reply goes back to it.
        /// </summary>
        private async void RemoteUpdate(string requested, Action<string> reply)
        {
            Doing("updating for the controlling PC");
            if (!Version.TryParse(requested, out var want)) { reply("Kova on the remote PC did not understand the version " + requested + "."); return; }
            if (_updating) { reply("Kova on the remote PC is already updating."); return; }
            if (want <= Updater.Current) { reply("The remote PC already has Kova " + Updater.Current + "."); return; }
            // Taken before the first wait, so two controllers asking at once cannot both download
            // into the same file: the second is told it is already updating.
            _updating = true;
            try
            {
                var r = await Updater.CheckAsync();
                if (r == null || r.Version != want)
                {
                    reply("The remote PC did not update: the newest Kova on GitHub is " + (r?.Version ?? Updater.Current) + ", not " + want + ".");
                    return;
                }
                reply("The remote PC is downloading Kova " + r.Version + ". Everything carries on until it is ready.");
                Log("The controlling PC asked to update Kova to " + r.Version + ".");
                await InstallUpdate(r);
            }
            catch (Exception e) { reply("The remote PC could not update: " + e.Message); }
            finally { if (!_reallyExit) _updating = false; }
        }

        /// <summary>
        /// Downloads and swaps in the new version, then closes. Hosting and the connection carry on
        /// until the new copy is in place. On failure, whatever was running is put back, then it throws.
        /// </summary>
        private async System.Threading.Tasks.Task InstallUpdate(Updater.Release r)
        {
            _updating = true;
            string args = "";
            try
            {
                SaveSettings();
                await Updater.InstallAsync(r, () =>
                {
                    // Only now, with the new copy in place: the connected PCs are told it is an
                    // update (they wait for it quietly), then the port and keyboard are let go
                    // before the new copy starts.
                    args = _host != null ? "--host" : _client != null || _reconnecting ? "--connect" : "";
                    _host?.Leave(Protocol.LeavingUpdating, r.Version.ToString());
                    _keys?.SetClient(null);
                    _client?.Dispose(); _client = null;
                    DropBackground();
                    _host?.Dispose(); _host = null;
                    return args;
                });
            }
            catch
            {
                _updating = false;
                if (args == "--host" && _host == null) StartHost();
                else if (args == "--connect" && _client == null) Connect(quiet: false);
                throw;
            }
            _reallyExit = true;
            Close();
        }

        private async void SetUpAudio()
        {
            if (AudioSetup.IsReady()) { Say("The Kova audio device is already set up."); return; }
            bool have = AudioSetup.FindCable() != null;
            string plan = "Set up audio device gives this PC an output called Kova, so everything this PC plays can be heard on the other PC. It will:" + Environment.NewLine + Environment.NewLine +
                (have ? "" :
                "1. Download VB-Cable, a free virtual audio device by VB-Audio, from vb-audio.com, and check it is genuine." + Environment.NewLine +
                "2. Open VB-Cable's installer. You press Install Driver." + Environment.NewLine) +
                (have ? "1. " : "3. ") + "Name VB-Cable's output Kova and make it this PC's default output. Your speakers stop being the default." + Environment.NewLine +
                (have ? "2. " : "4. ") + "Turn off VB-Cable's extra output, CABLE In 16 Ch." + Environment.NewLine + Environment.NewLine +
                "Windows asks for administrator permission first. Remove audio device undoes all of it.";
            if (MessageBox.Show(this, plan, "Set up audio device", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            _audioSetup.Enabled = _audioRemove.Enabled = false;
            bool ok = await AudioSetup.RunElevatedAsync();
            Say(ok ? "The Kova audio device is set up." : "Audio device setup did not finish.");
            _audioSetup.Enabled = _audioRemove.Enabled = true;
        }

        private async void RemoveAudio()
        {
            if (!AudioSetup.Installed()) { Say("There is no Kova audio device on this PC."); return; }
            var other = AudioSetup.OtherOutput();
            string plan = "Remove audio device will:" + Environment.NewLine + Environment.NewLine +
                "1. " + (other != null ? "Make " + other.Value.Name + " the default output." : "There is no other output on this PC, so it will have no sound output afterwards.") + Environment.NewLine +
                "2. Move programs off the Kova device. Any program still using it is cut off." + Environment.NewLine +
                "3. Remove VB-Cable completely, including its driver. If you use VB-Cable for anything else, that stops working too." + Environment.NewLine + Environment.NewLine +
                "Windows asks for administrator permission first.";
            if (MessageBox.Show(this, plan, "Remove audio device", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return; // default Cancel: removing the driver is destructive
            _audioSetup.Enabled = _audioRemove.Enabled = false;
            bool ok = await AudioSetup.RemoveElevatedAsync();
            Say(ok ? "The Kova audio device is removed." : "Removing the audio device did not finish.");
            _audioSetup.Enabled = _audioRemove.Enabled = true;
        }

        // ---- The Windows service ----

        private async void ServiceChanged()
        {
            bool want = _service.Checked;
            if (want == ServiceHost.IsInstalled()) return;
            string plan = want
                ? "Run Kova as a Windows service?" + Environment.NewLine + Environment.NewLine +
                  "The service starts with Windows, before anyone signs in, and has full system access. Whoever knows the Kova password can then use the lock screen, sign in, and approve administrator prompts on this PC, and Control Alt End sends Control Alt Delete." + Environment.NewLine + Environment.NewLine +
                  "It uses this window's port and passwords, opens the port in Windows Firewall, and replaces Start hosting when Windows starts. Windows asks for administrator permission."
                : "Remove the Kova service? This PC stops hosting until you start hosting here again. Windows asks for administrator permission.";
            if (MessageBox.Show(this, plan, want ? "Run as a Windows service" : "Remove the service", MessageBoxButtons.OKCancel,
                    want ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                    want ? MessageBoxDefaultButton.Button1 : MessageBoxDefaultButton.Button2) != DialogResult.OK // removing defaults to Cancel; installing (what the user just asked for) keeps OK
                || (want && (!CheckPassword() || !CheckPort(out _) || !CheckListenPassword())))
            {
                SetServiceBox(!want);
                return;
            }
            if (want && _host != null) StopHost(); // the service takes the port
            SaveSettings();
            Say("Windows asks for administrator permission.");
            bool ok = await ServiceHost.SetAsync(want);
            bool now = ServiceHost.IsInstalled();
            SetServiceBox(now);
            _startup.Checked = Startup.IsEnabled();
            if (ok && now == want)
                Say(want ? "The Kova service is running and hosting this PC on port " + _settings.Port + "." : "The Kova service is removed.");
            else
                Say((want ? "The service was not set up: " : "The service was not removed: ") + ServiceHost.LastError());
            UpdateMode();
        }

        private async void ApplyService()
        {
            if (!CheckPassword() || !CheckPort(out _) || !CheckListenPassword()) return;
            SaveSettings();
            Say("Giving the service these settings. Windows asks for administrator permission.");
            Say(await ServiceHost.SetAsync(true)
                ? "The service is hosting with these settings on port " + _settings.Port + "."
                : "The service was not changed: " + ServiceHost.LastError());
        }

        private void SetServiceBox(bool on)
        {
            _settingService = true;
            _service.Checked = on;
            _settingService = false;
        }

        /// <summary>
        /// The service never updates on its own. After this copy was updated with
        /// Check for updates, nudge it to follow, to the same version. No questions.
        /// </summary>
        // When the service was last nudged: every Send pressed while it is on an older version used to
        // nudge it again, each time it was still busy downloading.
        private long _serviceNudgedMs = -60_000;

        /// <summary>
        /// fromSend: a Send found the service on an older version (CanSend). Then exactly one sentence
        /// is said, once the nudge's outcome is known, instead of "updating itself" said at once and
        /// "could not ask it" contradicting it a moment later.
        /// </summary>
        private void OfferServiceUpdate(bool fromSend = false)
        {
            var v = ServiceHost.InstalledVersion();
            if (v == null || !_service.Checked) return;
            // The file's version says nothing just after an update when this window runs the
            // installed copy itself: updating put the new version in that file, while the service
            // still runs the old one in memory, so it was never told. Right after an update, always
            // tell it: the service compares GitHub with what it really runs, so if it is already
            // current, nothing happens.
            if (!fromSend && !_updated && v >= Updater.Current) return;
            long now = Environment.TickCount64;
            if (now - _serviceNudgedMs < 60_000)
            {
                if (fromSend) Say("The Kova service is still on version " + v + " and is updating itself. Try again in a minute.");
                return;
            }
            _serviceNudgedMs = now;
            // Off the window's thread: the pipe waits up to 2 seconds for a service that is not
            // there. The problem is spoken after "Updated to version", never over it. No version is
            // promised: the service goes to GitHub's newest only if it matches this window's.
            System.Threading.Tasks.Task.Run(() =>
            {
                bool asked = ServiceHost.NudgeUpdate();
                Later(() =>
                {
                    if (!asked)
                    {
                        string problem = "Could not ask the Kova service to update itself. If it is still on an older version, press Apply settings to the service.";
                        if (fromSend) Say(problem); else Speak(problem);
                    }
                    else if (fromSend) Say("The Kova service is still on version " + v + " and has been asked to update itself. Try again in a minute.");
                    else Log("The Kova service was asked to update itself.");
                });
            });
        }

        private async void StartupChanged()
        {
            bool want = _startup.Checked;
            if (want == Startup.IsEnabled()) return;
            SaveSettings();
            Say("Windows asks for administrator permission.");
            var result = await Startup.ChangeAsync(want);
            if (result == ChangeResult.Done) Say(want ? "Kova will start hosting when you sign in." : "Kova will no longer start with Windows.");
            else
            {
                Say(result == ChangeResult.Declined ? "That needs administrator permission, and it was not given." : "Windows did not accept the change.");
                _startup.Checked = Startup.IsEnabled();
            }
        }

        private void Log(string line)
        {
            DiagLog.Write("window: " + line); // NVDA speaks it (Say); the log file keeps it when logging is on
        }

        /// <summary>Runs on the window's thread, from any thread; dropped once the window is gone.</summary>
        private void Later(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        /// <summary>Logs a line and speaks it after whatever is being said, without cutting it off.</summary>
        private void Speak(string line)
        {
            Log(line);
            Speech.Speak(line, interrupt: false);
        }

        /// <summary>Logs a line and speaks it.</summary>
        private void Say(string line)
        {
            Log(line);
            Speech.Speak(line);
        }

        private ToolStripMenuItem? _trayExit;

        /// <summary>
        /// While hosting or connected, closing the window only hides it: hosting, or the connection and
        /// its sound, carry on from the notification area.
        /// </summary>
        private void HideToTray(bool announce)
        {
            bool connected = _host == null && (_client != null || _reconnecting || _parked.Count > 0);
            if (connected && _keys?.Remote == true)
            {
                // The keyboard comes back to this PC first: nothing is typed into a window you cannot see.
                _quietModeChange = true;
                _keys.Toggle();
            }
            if (_trayExit != null) _trayExit.Text = connected ? "&Disconnect and exit" : "&Stop hosting and exit";
            _tray.Text = connected ? "Kova, connected" : "Kova, hosting";
            _tray.Visible = true;
            Hide();
            if (announce)
                Speech.Speak(connected
                    ? "Kova is still connected, and you still hear the other PC. It is in the notification area: press Windows B to find it. Use its menu to disconnect and exit."
                    : "Kova is still hosting, in the notification area. Press Windows B to find it. Use its menu to stop hosting and exit.");
        }

        /// <summary>File, Exit: closes for real, ending hosting or the connection (the X only hides to the tray then).</summary>
        private void ExitForGood()
        {
            _reallyExit = true;
            Close();
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            _tray.Visible = false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && (_host != null || _client != null || _reconnecting || _parked.Count > 0) && !_reallyExit)
            {
                e.Cancel = true;
                HideToTray(announce: true);
                return;
            }
            _tray.Visible = false;
            _settings.ResumeState = _host != null ? "host" : _client != null || _reconnecting ? "connect" : "";
            SaveSettings();
            _retryTimer.Stop();
            _retryTimer.Dispose();
            _titleTimer.Stop();
            _titleTimer.Dispose();
            _keys?.SetClient(null);
            _client?.Dispose();
            DropBackground();
            _player?.Dispose();
            _host?.Leave(e.CloseReason == CloseReason.WindowsShutDown ? Protocol.LeavingShutdown : Protocol.LeavingStopped);
            _host?.Dispose();
            _keys?.Dispose();
            _serviceLink?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
