using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// A step-by-step window: one question a page, Back, Next and Cancel, plus any buttons a page adds (Edit PC,
    /// Delete PC). Each page is a group named by its question, so NVDA says the question as focus lands on the page's
    /// first control. A page can check what was typed before moving on; a problem is said and focus stays where it
    /// was. A check that only warns (the relay is reachable but the PC is not on it yet) lets a second press of the
    /// same button carry on anyway.
    /// </summary>
    internal sealed class WizardForm : Form
    {
        internal sealed class Page
        {
            public required string Question { get; init; }
            public required Control[] Controls { get; init; }
            /// <summary>Whether the page is shown at all, from what earlier pages chose.</summary>
            public Func<bool> Shown { get; init; } = () => true;
            /// <summary>Checks the page: null to go on, or (problem, canCarryOn). Runs on the window's thread; may await.</summary>
            public Func<Task<(string Problem, bool CanCarryOn)?>>? Check { get; init; }
            /// <summary>Said (without interrupting) once the next page is showing: what a check found.</summary>
            public string? Note { get; set; }
            /// <summary>The main button's words on this page when it is not Next or Finish.</summary>
            public Func<string>? NextText { get; init; }
            /// <summary>Finishing from this page (a saved PC chosen: nothing more to ask).</summary>
            public Func<bool> FinishesHere { get; init; } = () => false;
            /// <summary>Where focus goes when the page shows (otherwise its first control that takes focus).</summary>
            public Func<Control?>? Focus { get; init; }
            /// <summary>Buttons beside the main one, shown only on this page.</summary>
            public Button[] Buttons { get; init; } = Array.Empty<Button>();
            internal GroupBox? Box;
        }

        private readonly List<Page> _pages;
        private readonly Panel _host = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(480, 0) };
        private readonly Button _back = new() { Text = "&Back", AutoSize = true };
        private readonly Button _next = new() { Text = "&Next", AutoSize = true };
        private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        private int _at = -1;
        private bool _busy;
        private string? _warnedFor; // the page and values a warning was given for: the same press again carries on

        public WizardForm(string title, List<Page> pages)
        {
            _pages = pages;
            Text = title;
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(12) };
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            foreach (var p in pages)
            {
                var box = new GroupBox { Text = p.Question, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, Padding = new Padding(8), MinimumSize = new Size(480, 0) };
                var inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
                inner.Controls.AddRange(p.Controls);
                box.Controls.Add(inner);
                p.Box = box;
                _host.Controls.Add(box);
            }
            buttons.Controls.Add(_next);
            foreach (var p in pages) foreach (var b in p.Buttons) { b.AutoSize = true; b.Visible = false; buttons.Controls.Add(b); }
            buttons.Controls.Add(_back);
            buttons.Controls.Add(_cancel);
            flow.Controls.Add(_host);
            flow.Controls.Add(_status);
            flow.Controls.Add(buttons);
            Controls.Add(flow);
            AcceptButton = _next;
            CancelButton = _cancel;
            _back.Click += (_, _) => { if (!_busy) Go(-1); };
            _next.Click += async (_, _) => await NextPressed();
            Shown += (_, _) => Go(+1);
        }

        private bool IsLast(int i) => !Enumerable.Range(i + 1, Math.Max(0, _pages.Count - i - 1)).Any(j => _pages[j].Shown()) || _pages[i].FinishesHere();

        /// <summary>Moves to the next (or previous) page that is shown, and focuses its first control.</summary>
        private void Go(int step)
        {
            int i = _at + step;
            while (i >= 0 && i < _pages.Count && !_pages[i].Shown()) i += step;
            if (i < 0 || i >= _pages.Count) return;
            string? note = _at >= 0 && step > 0 ? _pages[_at].Note : null;
            var old = _at >= 0 ? _pages[_at] : null;
            _at = i;
            var page = _pages[i];
            page.Box!.Visible = true;
            foreach (var b in page.Buttons) b.Visible = true;
            _back.Enabled = Enumerable.Range(0, i).Any(j => _pages[j].Shown());
            ShowStatus("", speak: false);
            _warnedFor = null;
            UpdateNext();
            FocusPage();
            // The old page goes only once focus is on the new one, so focus never passes through the buttons.
            if (old != null && old != page)
            {
                old.Box!.Visible = false;
                foreach (var b in old.Buttons) b.Visible = false;
            }
            if (note != null) BeginInvoke(() => Speech.Speak(note, interrupt: false));
        }

        /// <summary>Focus to the page's first control (or its own choice); a chosen radio button rather than the first.</summary>
        internal void FocusPage()
        {
            if (_at < 0) return;
            var page = _pages[_at];
            var first = page.Focus?.Invoke() ?? page.Controls.FirstOrDefault(c => c.CanSelect && c.Enabled && c is not Label) ?? _next;
            if (first is RadioButton && first.Parent?.Controls.OfType<RadioButton>().FirstOrDefault(r => r.Checked) is RadioButton chosen) first = chosen;
            first.Focus();
        }

        /// <summary>The main button's words follow the page and the choice on it (a saved PC chosen connects at once).</summary>
        internal void UpdateNext()
        {
            if (_at < 0) return;
            var page = _pages[_at];
            _next.Text = page.NextText?.Invoke() ?? (IsLast(_at) ? "&Finish" : "&Next");
        }

        private async Task NextPressed()
        {
            if (_busy || _at < 0) return;
            var page = _pages[_at];
            page.Note = null;
            if (page.Check != null)
            {
                string values = _at + ":" + string.Join("\u0001", page.Controls.Select(c => c is CheckBox cb ? cb.Checked.ToString() : c is RadioButton rb ? rb.Checked.ToString() : c.Text));
                if (_warnedFor != values)
                {
                    _busy = true; // the buttons do nothing until the check ends (disabling the focused one sent focus to Cancel)
                    (string Problem, bool CanCarryOn)? found;
                    try { found = await page.Check(); }
                    finally { _busy = false; }
                    if (IsDisposed) return;
                    if (found is { } f)
                    {
                        _warnedFor = f.CanCarryOn ? values : null;
                        ShowStatus(f.Problem + (f.CanCarryOn ? " To continue anyway, press " + _next.Text.Replace("&", "") + " again." : ""), speak: true);
                        return;
                    }
                }
            }
            if (IsLast(_at)) { DialogResult = DialogResult.OK; Close(); return; }
            Go(+1);
        }

        /// <summary>What a check is doing or found, shown under the page and said.</summary>
        internal void ShowStatus(string text, bool speak)
        {
            _status.Text = text;
            _status.AccessibleName = text;
            if (speak && text.Length > 0) Speech.Speak(text);
        }

        // ---- Controls a page is made of: each named for NVDA ----

        internal static RadioButton Choice(string text, bool chosen = false) => new() { Text = text, AutoSize = true, Checked = chosen, MaximumSize = new Size(460, 0) };

        internal static (Label Label, TextBox Box) Field(string label, string text, bool secret = false)
        {
            var l = new Label { Text = label, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(3, 8, 3, 0) };
            var b = new TextBox { Text = text, Width = 440, UseSystemPasswordChar = secret, AccessibleName = label.Replace("&", "").TrimEnd(':') };
            return (l, b);
        }

        internal static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(3, 8, 3, 0) };

        internal static CheckBox ShowPassword(params TextBox[] boxes)
        {
            var c = new CheckBox { Text = boxes.Length == 1 ? "&Show password" : "&Show passwords", AutoSize = true };
            c.CheckedChanged += (_, _) => { foreach (var b in boxes) b.UseSystemPasswordChar = !c.Checked; };
            return c;
        }

        internal static TextBox Reading(string text) => new()
        {
            Text = text, ReadOnly = true, Multiline = true, Width = 460, Height = 160, ScrollBars = ScrollBars.Vertical,
            AccessibleName = "Summary", TabStop = true,
        };
    }

    /// <summary>
    /// Where a typed address leads (4.6): Kova finds out itself whether it is a Kova Relay or the PC, so nobody has to
    /// choose. Both are tried at once; a relay that answers wins (the PC on it is the one hosting with the password).
    /// </summary>
    internal static class Route
    {
        internal sealed record Found(string Address, int Port, string? Problem, bool CanCarryOn);

        public static async Task<Found> Find(string typed, string password)
        {
            typed = typed.Trim();
            if (typed.Length == 0) return new("", 0, "Enter the address of the PC or of a Kova Relay.", false);
            if (typed.Contains('@'))
            {
                // A relay address with a name in front, as Kova 4.4 and 4.5 used: kept as typed.
                if (Relay.Parse(typed, KovaRelayWire.Wire.DefaultPort, out string? why) == null) return new("", 0, why ?? "That address cannot be used.", false);
                return new(typed, Protocol.DefaultPort, null, false);
            }
            if (Relay.Parse("x@" + typed, KovaRelayWire.Wire.DefaultPort, out string? problem) is not Relay.Target t)
                return new("", 0, problem ?? "That address cannot be used.", false);
            bool portTyped = t.RelayPort != KovaRelayWire.Wire.DefaultPort || typed.EndsWith(":" + KovaRelayWire.Wire.DefaultPort);
            int directPort = portTyped ? t.RelayPort : Protocol.DefaultPort;
            string bare = t.RelayHost.Contains(':') ? "[" + t.RelayHost + "]" : t.RelayHost;
            string relayAt = bare + (t.RelayPort == KovaRelayWire.Wire.DefaultPort ? "" : ":" + t.RelayPort);
            string channel = await Task.Run(() => Relay.Channel(password));
            var relay = Task.Run(() => Relay.Check(relayAt, channel));
            var direct = Task.Run(() => Answers(t.RelayHost, directPort));
            // A PC that answers directly while nothing there is a relay needs no further wait.
            await Task.WhenAny(Task.WhenAll(relay, direct), direct.ContinueWith(d => d.Result && relay.IsCompleted));
            if (!relay.IsCompleted && direct.IsCompleted && direct.Result)
            {
                // The relay probe is still waiting (a firewall that drops its port): give it two more seconds at most.
                await Task.WhenAny(relay, Task.Delay(2000));
                if (!relay.IsCompleted) return new(t.RelayHost, directPort, null, false);
            }
            await Task.WhenAll(relay, direct);
            var r = relay.Result;
            if (r.Up && r.HostHere == true) return new(Relay.Tagged(relayAt, password), Protocol.DefaultPort, null, false);
            if (direct.Result) return new(t.RelayHost, directPort, null, false);
            if (r.Up) return new(Relay.Tagged(relayAt, password), Protocol.DefaultPort, r.HostHere == false ? r.Said : null, r.HostHere == false);
            return new(t.RelayHost, directPort,
                "Could not reach " + typed + ", either as a PC hosting Kova or as a Kova Relay. Check the address, and that the PC is turned on and hosting.", true);
        }

        /// <summary>
        /// Whether Kova is hosting at that address and port: it must greet with Kova's own first bytes ("TRM"). 4.6.0 took
        /// any answer as a PC, so a busy relay on that port was connected to as if it were the PC ("damaged first message").
        /// </summary>
        private static bool Answers(string host, int port)
        {
            try
            {
                using var tcp = new TcpClient(AddressFamily.InterNetworkV6);
                tcp.Client.DualMode = true;
                var addrs = System.Net.Dns.GetHostAddresses(host);
                if (addrs.Length == 0 || !tcp.ConnectAsync(addrs, port).Wait(5000) || !tcp.Connected) return false;
                var s = tcp.GetStream();
                s.ReadTimeout = 4000;
                byte[] head = new byte[3];
                int got = 0;
                while (got < 3) { int n = s.Read(head, got, 3 - got); if (n <= 0) return false; got += n; }
                return head[0] == (byte)'T' && head[1] == (byte)'R' && head[2] == (byte)'M';
            }
            catch { return false; }
        }
    }

    /// <summary>Connect to a PC: a saved PC (which can be edited or deleted here), or a new one by its address.</summary>
    internal static class ConnectWizard
    {
        internal sealed record Answer(string Address, int Port, string Password, bool Save, string Name, SavedPc? Saved);

        /// <param name="savedChanged">Called after a saved PC is edited or deleted here, to save the settings at once.</param>
        public static Answer? Run(IWin32Window owner, Settings settings, Action savedChanged)
        {
            var list = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            var radios = new List<(RadioButton Radio, SavedPc? Pc)>();
            var editPc = new Button { Text = "&Edit PC..." };
            var deletePc = new Button { Text = "&Delete PC" };

            var (addrLabel, addr) = WizardForm.Field("&Address: the PC's name or IP address, or the address of a Kova Relay. Add a colon and the port number only if it is not the standard one.", "");
            var (pwLabel, pwBox) = WizardForm.Field("Pass&word:", "", secret: true);
            var save = new CheckBox { Text = "Sa&ve this PC", AutoSize = true, Checked = true };
            var (nameLabel, nameBox) = WizardForm.Field("&Name for this PC (optional, for example Office PC):", "");
            save.CheckedChanged += (_, _) => nameBox.Enabled = save.Checked;

            WizardForm? form = null;
            Route.Found? found = null;
            SavedPc? Chosen() => radios.FirstOrDefault(r => r.Radio.Checked).Pc;

            void EditButtons() => editPc.Enabled = deletePc.Enabled = Chosen() != null;
            void Fill(SavedPc? select)
            {
                foreach (Control c in list.Controls.Cast<Control>().ToList()) { list.Controls.Remove(c); c.Dispose(); }
                radios.Clear();
                foreach (var pc in settings.SavedPcs) radios.Add((WizardForm.Choice(pc.ToString().Replace("&", "&&")), pc));
                radios.Add((WizardForm.Choice("&A new PC"), null));
                var pick = radios.FirstOrDefault(r => r.Pc != null && r.Pc == select).Radio ?? (select == null && settings.SavedPcs.Count > 0 ? radios[0].Radio : radios[^1].Radio);
                pick.Checked = true;
                foreach (var (r, _) in radios)
                {
                    r.CheckedChanged += (_, _) => { form?.UpdateNext(); EditButtons(); };
                    list.Controls.Add(r);
                }
                EditButtons();
            }

            var choosePage = new WizardForm.Page
            {
                Question = "Choose a PC",
                Controls = new Control[] { list },
                Shown = () => settings.SavedPcs.Count > 0,
                FinishesHere = () => Chosen() != null,
                NextText = () => Chosen() != null ? "&Connect" : "&Next",
                Focus = () => radios.FirstOrDefault(r => r.Radio.Checked).Radio,
                Buttons = new[] { editPc, deletePc },
            };
            var detailsPage = new WizardForm.Page
            {
                Question = "Connection details",
                Controls = new Control[] { addrLabel, addr, pwLabel, pwBox, WizardForm.ShowPassword(pwBox), save, nameLabel, nameBox },
                Shown = () => Chosen() == null,
                NextText = () => "&Connect",
                Check = async () =>
                {
                    if (addr.Text.Trim().Length == 0) return ("Enter the address of the PC or of a Kova Relay.", false);
                    if (pwBox.Text.Length == 0) return ("Enter the password.", false);
                    form!.ShowStatus("Looking for the PC.", speak: true);
                    found = await Route.Find(addr.Text, pwBox.Text);
                    if (found.Problem != null) return (found.Problem, found.CanCarryOn);
                    return null;
                },
            };
            form = new WizardForm("Connect to a PC", new List<WizardForm.Page> { choosePage, detailsPage });
            Fill(settings.SavedPcs.FirstOrDefault(p => p.Address == settings.Address && p.Port == settings.Port));

            editPc.Click += async (_, _) =>
            {
                if (Chosen() is not SavedPc pc) return;
                if (await EditPc.Run(form, pc))
                {
                    savedChanged();
                    Fill(pc);
                    form.UpdateNext();
                    Speech.Speak("Saved " + pc + ".");
                }
                form.FocusPage();
            };
            deletePc.Click += (_, _) =>
            {
                if (Chosen() is not SavedPc pc) return;
                if (MessageBox.Show(form, "Delete " + pc + " from your saved PCs?", "Delete PC", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    settings.SavedPcs.Remove(pc);
                    savedChanged();
                    Fill(null);
                    form.UpdateNext();
                    Speech.Speak("Deleted " + pc + ".");
                }
                form.FocusPage();
            };

            using (form)
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return null;
                if (Chosen() is SavedPc chosen) return new Answer(chosen.Address, chosen.Port, Settings.Unprotect(chosen.PasswordEnc), false, chosen.Name, chosen);
                var f = found!;
                return new Answer(f.Address, f.Port, pwBox.Text, save.Checked, save.Checked ? nameBox.Text.Trim() : "", null);
            }
        }
    }

    /// <summary>Edit PC: a saved PC's name, address and password. The address is checked the same way Connect to a PC checks it.</summary>
    internal static class EditPc
    {
        public static Task<bool> Run(IWin32Window owner, SavedPc pc)
        {
            string shownAddress = Relay.IsByPassword(pc.Address) ? Relay.Shown(pc.Address)
                : pc.Port == Protocol.DefaultPort ? pc.Address
                : (pc.Address.Contains(':') ? "[" + pc.Address + "]" : pc.Address) + ":" + pc.Port;
            var (nameLabel, nameBox) = WizardForm.Field("&Name (for example Office PC):", pc.Name);
            var (addrLabel, addr) = WizardForm.Field("&Address: the PC's name or IP address, or the address of a Kova Relay:", shownAddress);
            var (pwLabel, pwBox) = WizardForm.Field("Pass&word:", Settings.Unprotect(pc.PasswordEnc), secret: true);
            WizardForm? form = null;
            Route.Found? found = null;
            var page = new WizardForm.Page
            {
                Question = "PC details",
                Controls = new Control[] { nameLabel, nameBox, addrLabel, addr, pwLabel, pwBox, WizardForm.ShowPassword(pwBox) },
                NextText = () => "&Save",
                Check = async () =>
                {
                    if (addr.Text.Trim().Length == 0) return ("Enter the address of the PC or of a Kova Relay.", false);
                    if (pwBox.Text.Length == 0) return ("Enter the password.", false);
                    form!.ShowStatus("Checking the address.", speak: true);
                    found = await Route.Find(addr.Text, pwBox.Text);
                    return found.Problem != null ? (found.Problem, found.CanCarryOn) : null;
                },
            };
            form = new WizardForm("Edit PC", new List<WizardForm.Page> { page });
            using (form)
            {
                if (form.ShowDialog(owner) != DialogResult.OK || found == null) return Task.FromResult(false);
                pc.Name = nameBox.Text.Trim();
                pc.Address = found.Address;
                pc.Port = found.Port;
                pc.PasswordEnc = Settings.Protect(pwBox.Text);
                return Task.FromResult(true);
            }
        }
    }

    /// <summary>Host this PC: how other computers connect to it (through a relay, directly, or both), the relay, the passwords.</summary>
    internal static class HostWizard
    {
        internal sealed record Answer(bool Direct, bool Relay, int Port, string RelayAddress, int RelayPort, string Password, string ListenPassword);

        public static Answer? Run(IWin32Window owner, Settings settings, int port, string password, string listen, bool registeredNow, bool service, string? myAddresses)
        {
            int was = !settings.RelayHosting ? 1 : settings.RelayOnly ? 0 : 2;
            var viaRelay = WizardForm.Choice("Through a Kova &Relay. Recommended when this PC is behind a router or firewall. No ports are opened on this PC.", chosen: was == 0);
            var direct = WizardForm.Choice("&Directly, using this PC's network address. Kova opens a port in Windows Firewall.", chosen: was == 1);
            var both = WizardForm.Choice("B&oth: through a Kova Relay, and directly.", chosen: was == 2);
            bool UsesRelay() => viaRelay.Checked || both.Checked;
            bool UsesDirect() => direct.Checked || both.Checked;

            var (portLabel, portBox) = WizardForm.Field("&Port number (the standard port is 47120):", port.ToString());
            string relayText = settings.RelayAddress.Length == 0 ? "" : settings.RelayAddress + (settings.RelayPort == KovaRelayWire.Wire.DefaultPort ? "" : ":" + settings.RelayPort);
            var (relayLabel, relayBox) = WizardForm.Field("&Relay address. Add a colon and the port number only if it is not the standard one, 47200.", relayText);
            var (pwLabel, pwBox) = WizardForm.Field("Pass&word (at least 6 characters). Other computers need it to connect.", password, secret: true);
            var relayNote = WizardForm.Note("If you use a Kova Relay, the password also identifies this PC on the relay, so give each PC its own password.");
            var (listenLabel, listenBox) = WizardForm.Field("&Listen-only password (optional). It lets someone hear this PC without controlling it.", listen, secret: true);
            var summary = WizardForm.Reading("");

            WizardForm? form = null;
            string Summary()
            {
                var lines = new List<string>();
                if (UsesRelay())
                    lines.Add("To connect through the relay, other computers choose Connect to a PC, then enter the relay address " + relayBox.Text.Trim() + " and this PC's password.");
                if (UsesDirect())
                    lines.Add("To connect directly, other computers enter this PC's address" + (string.IsNullOrEmpty(myAddresses) ? "" : " (" + myAddresses + ")") +
                        (portBox.Text.Trim() == Protocol.DefaultPort.ToString() ? "" : " followed by :" + portBox.Text.Trim()) + " and the password. Kova opens port " + portBox.Text.Trim() + " in Windows Firewall.");
                if (viaRelay.Checked) lines.Add("No ports are opened on this PC. Every connection comes through the relay.");
                if (service) lines.Add("The Kova service is installed, so it will host this way from now on.");
                return string.Join(Environment.NewLine, lines);
            }

            var pages = new List<WizardForm.Page>
            {
                new()
                {
                    Question = "How will other computers connect to this PC?",
                    Controls = new Control[] { viaRelay, direct, both },
                },
                new()
                {
                    Question = "Port",
                    Controls = new Control[] { portLabel, portBox },
                    Shown = UsesDirect,
                    Check = () => Task.FromResult<(string, bool)?>(Protocol.PortProblem(portBox.Text.Trim(), out _) is string p ? (p, false) : null),
                },
                new()
                {
                    Question = "Kova Relay",
                    Controls = new Control[] { relayLabel, relayBox },
                    Shown = UsesRelay,
                    Check = async () =>
                    {
                        string r = relayBox.Text.Trim();
                        if (r.Length == 0) return ("Enter the relay address.", false);
                        if (Relay.Parse("x@" + r, KovaRelayWire.Wire.DefaultPort, out string? why) == null) return (why ?? "That relay address cannot be used.", false);
                        form!.ShowStatus("Checking the relay.", speak: true);
                        var found = await Task.Run(() => Relay.Check(r, null));
                        if (!found.Up) return (found.Said + " This PC will keep trying to reach it while it hosts.", true);
                        return null;
                    },
                },
                new()
                {
                    Question = "Passwords",
                    Controls = new Control[] { pwLabel, pwBox, relayNote, listenLabel, listenBox, WizardForm.ShowPassword(pwBox, listenBox) },
                    Check = async () =>
                    {
                        int n = new System.Globalization.StringInfo(pwBox.Text).LengthInTextElements;
                        if (n < MainForm.MinPasswordLength) return (n == 0 ? "Enter a password." : "The password must have at least " + MainForm.MinPasswordLength + " characters. It has " + n + ".", false);
                        string l = listenBox.Text;
                        if (l.Length > 0 && l == pwBox.Text) return ("The listen-only password must be different from the password, or empty.", false);
                        if (l.Length > 0 && new System.Globalization.StringInfo(l).LengthInTextElements < MainForm.MinPasswordLength)
                            return ("The listen-only password must have at least " + MainForm.MinPasswordLength + " characters, or be empty.", false);
                        if (UsesRelay())
                        {
                            // Another PC already hosting on this relay with this password would collide with this one.
                            form!.ShowStatus("Checking the relay.", speak: true);
                            string r = relayBox.Text.Trim(), pw = pwBox.Text;
                            var on = await Task.Run(() => Relay.Check(r, Relay.Channel(pw)));
                            bool thisPc = registeredNow && pw == password && string.Equals(r, relayText, StringComparison.OrdinalIgnoreCase);
                            if (on.HostHere == true && !thisPc)
                                return ("Another PC is already hosting on this relay with this password. Choose a different password for this PC.", true);
                        }
                        summary.Text = Summary();
                        return null;
                    },
                },
                new()
                {
                    Question = "Ready",
                    Controls = new Control[] { summary },
                    NextText = () => service ? "&Apply to the Kova service" : "Start &hosting",
                },
            };

            form = new WizardForm(service ? "Host this PC (Kova service)" : "Host this PC", pages);
            using (form)
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return null;
                Protocol.PortProblem(portBox.Text.Trim(), out int p);
                if (!UsesDirect()) p = port; // unchanged: through the relay alone, the port is not used
                string rh = "";
                int rp = KovaRelayWire.Wire.DefaultPort;
                if (Relay.Parse("x@" + relayBox.Text.Trim(), KovaRelayWire.Wire.DefaultPort, out _) is Relay.Target t) { rh = t.RelayHost; rp = t.RelayPort; }
                return new Answer(UsesDirect(), UsesRelay(), p, rh, rp, pwBox.Text, listenBox.Text);
            }
        }
    }
}
