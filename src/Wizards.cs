using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// A step-by-step window (4.5): one question a page, Back, Next and Cancel. Each page is a group named by its
    /// question, so NVDA says the question as focus lands on the page's first control. A page can check what was
    /// typed before moving on (the relay checks take a few seconds and say what they found); a problem is said
    /// and focus stays where it was. A check that only warns (the relay is down, the PC is not on it yet) lets a
    /// second Next carry on anyway.
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
            /// <summary>The Next button's words on this page when it is not "Next" (the last page: "Connect").</summary>
            public Func<string>? NextText { get; init; }
            /// <summary>Finishing from this page (a saved PC chosen: nothing more to ask).</summary>
            public Func<bool> FinishesHere { get; init; } = () => false;
            internal GroupBox? Box;
        }

        private readonly List<Page> _pages;
        private readonly Panel _host = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(460, 0) };
        private readonly Button _back = new() { Text = "&Back", AutoSize = true };
        private readonly Button _next = new() { Text = "&Next", AutoSize = true };
        private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        private int _at = -1;
        private bool _busy;
        private string? _warnedFor; // the page and values a warning was given for: Next again carries on

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
            foreach (var p in pages)
            {
                var box = new GroupBox { Text = p.Question, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, Padding = new Padding(8), MinimumSize = new Size(460, 0) };
                var inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
                inner.Controls.AddRange(p.Controls);
                box.Controls.Add(inner);
                p.Box = box;
                _host.Controls.Add(box);
            }
            flow.Controls.Add(_host);
            flow.Controls.Add(_status);
            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.AddRange(new Control[] { _back, _next, _cancel });
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
            var old = _at >= 0 ? _pages[_at].Box : null;
            _at = i;
            var page = _pages[i];
            page.Box!.Visible = true;
            _back.Enabled = Enumerable.Range(0, i).Any(j => _pages[j].Shown());
            ShowStatus("", speak: false);
            _warnedFor = null;
            UpdateNext();
            var first = page.Controls.FirstOrDefault(c => c.CanSelect && c.Enabled && c is not Label) ?? _next;
            if (first is RadioButton && page.Controls.OfType<RadioButton>().FirstOrDefault(r => r.Checked) is RadioButton chosen) first = chosen;
            first.Focus();
            if (old != null && old != page.Box) old.Visible = false;
            if (note != null) BeginInvoke(() => Speech.Speak(note, interrupt: false));
        }

        /// <summary>Next's words follow the page, and the choice on it (a saved PC chosen finishes at once).</summary>
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
                    _busy = true; // Next and Back do nothing until the check ends (disabling the focused Next sent focus to Cancel)
                    (string Problem, bool CanCarryOn)? found;
                    try { found = await page.Check(); }
                    finally { _busy = false; }
                    if (IsDisposed) return;
                    if (found is { } f)
                    {
                        _warnedFor = f.CanCarryOn ? values : null;
                        ShowStatus(f.Problem + (f.CanCarryOn ? " Press " + _next.Text.Replace("&", "") + " again to carry on anyway." : ""), speak: true);
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

        internal static RadioButton Choice(string text, bool chosen = false) => new() { Text = text, AutoSize = true, Checked = chosen, MaximumSize = new Size(440, 0) };

        internal static (Label Label, TextBox Box) Field(string label, string text, bool secret = false)
        {
            var l = new Label { Text = label, AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 8, 3, 0) };
            var b = new TextBox { Text = text, Width = 420, UseSystemPasswordChar = secret, AccessibleName = label.Replace("&", "") };
            return (l, b);
        }

        internal static CheckBox ShowPassword(params TextBox[] boxes)
        {
            var c = new CheckBox { Text = "&Show passwords", AutoSize = true };
            if (boxes.Length == 1) c.Text = "&Show password";
            c.CheckedChanged += (_, _) => { foreach (var b in boxes) b.UseSystemPasswordChar = !c.Checked; };
            return c;
        }

        internal static TextBox Reading(string text) => new()
        {
            Text = text, ReadOnly = true, Multiline = true, Width = 440, Height = 150, ScrollBars = ScrollBars.Vertical,
            AccessibleName = "Summary", TabStop = true,
        };
    }

    /// <summary>Connect to a PC, step by step: a saved PC, or a new one by its address or through a Kova Relay.</summary>
    internal static class ConnectWizard
    {
        internal sealed record Answer(string Address, int Port, string Password, bool Save, SavedPc? Saved);

        public static Answer? Run(IWin32Window owner, Settings settings, string address, int port, string password)
        {
            bool haveSaved = settings.SavedPcs.Count > 0;
            var saved = settings.SavedPcs.Select(p => WizardForm.Choice(p.ToString().Replace("&", "&&"))).ToList();
            var newPc = WizardForm.Choice("A n&ew PC", chosen: true);
            if (haveSaved && settings.SavedPcs.FindIndex(p => p.Address == address && p.Port == port) is int was and >= 0) { saved[was].Checked = true; newPc.Checked = false; }

            var relayTyped = Relay.Parse(address, KovaRelayWire.Wire.DefaultPort, out _);
            var direct = WizardForm.Choice("By its &address: its Tailscale name, IP address or internet address", chosen: relayTyped == null);
            var viaRelay = WizardForm.Choice("Through a Kova &Relay, for a PC that hosts through one", chosen: relayTyped != null);

            var (addrLabel, addr) = WizardForm.Field("&Address of the PC (its Tailscale name, 100 address, home network address or internet address):", relayTyped == null ? address : "");
            var (portLabel, portBox) = WizardForm.Field("&Port (the same as on that PC; 47120 unless it was changed):", port.ToString());
            var (relayLabel, relayBox) = WizardForm.Field("The &relay's address, with :port after it if it is not 47200 (like relay.example.com):",
                relayTyped == null ? settings.RelayAddress : relayTyped.RelayHost + (relayTyped.RelayPort == KovaRelayWire.Wire.DefaultPort ? "" : ":" + relayTyped.RelayPort));
            var (nameLabel, nameBox) = WizardForm.Field("The PC's na&me on the relay (like office-pc):", relayTyped?.Name ?? "");
            var (pwLabel, pwBox) = WizardForm.Field("The PC's pass&word:", password, secret: true);
            var save = new CheckBox { Text = "Sa&ve this PC, so it is in Saved PCs next time", AutoSize = true, Checked = true };

            WizardForm? form = null;
            bool UsingSaved() => haveSaved && !newPc.Checked;
            var pages = new List<WizardForm.Page>();
            if (haveSaved)
            {
                pages.Add(new WizardForm.Page
                {
                    Question = "Which PC do you want to connect to?",
                    Controls = saved.Cast<Control>().Append(newPc).ToArray(),
                    FinishesHere = UsingSaved,
                    NextText = () => UsingSaved() ? "&Connect" : "&Next",
                });
            }
            pages.Add(new WizardForm.Page
            {
                Question = "How do you reach the PC?",
                Controls = new Control[] { direct, viaRelay },
                Shown = () => !UsingSaved(),
            });
            pages.Add(new WizardForm.Page
            {
                Question = "Where is the PC?",
                Controls = new Control[] { addrLabel, addr, portLabel, portBox },
                Shown = () => !UsingSaved() && direct.Checked,
                Check = () =>
                {
                    string a = addr.Text.Trim();
                    if (a.Length == 0) return Task.FromResult<(string, bool)?>(("Type the PC's address.", false));
                    if (a.Contains('@')) return Task.FromResult<(string, bool)?>(("That looks like a relay address. Go Back and choose Through a Kova Relay.", false));
                    if (Protocol.PortProblem(portBox.Text.Trim(), out _) is string p) return Task.FromResult<(string, bool)?>((p, false));
                    return Task.FromResult<(string, bool)?>(null);
                },
            });
            WizardForm.Page? relayPage = null;
            pages.Add(relayPage = new WizardForm.Page
            {
                Question = "Which relay, and the PC's name on it?",
                Controls = new Control[] { relayLabel, relayBox, nameLabel, nameBox },
                Shown = () => !UsingSaved() && viaRelay.Checked,
                Check = async () =>
                {
                    string r = relayBox.Text.Trim(), n = nameBox.Text.Trim();
                    if (r.Length == 0) return ("Type the relay's address.", false);
                    if (n.Length == 0) return ("Type the PC's name on the relay.", false);
                    if (Relay.Parse(n + "@" + r, KovaRelayWire.Wire.DefaultPort, out string? why) == null) return (why ?? "That is not a relay address.", false);
                    form!.ShowStatus("Checking the relay.", speak: true);
                    var found = await Task.Run(() => Relay.Check(r, n));
                    if (!found.Up || found.HostHere == false) return (found.Said, true);
                    relayPage!.Note = found.Said;
                    return null;
                },
            });
            pages.Add(new WizardForm.Page
            {
                Question = "The password",
                Controls = new Control[] { pwLabel, pwBox, WizardForm.ShowPassword(pwBox), save },
                Shown = () => !UsingSaved(),
                NextText = () => "&Connect",
                Check = () => Task.FromResult<(string, bool)?>(pwBox.Text.Length == 0 ? ("Type the PC's password.", false) : null),
            });
            form = new WizardForm("Connect to a PC", pages);
            foreach (var r in saved.Append(newPc)) r.CheckedChanged += (_, _) => form.UpdateNext();
            using (form)
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return null;
                if (UsingSaved())
                {
                    var pc = settings.SavedPcs[saved.FindIndex(r => r.Checked)];
                    return new Answer(pc.Address, pc.Port, Settings.Unprotect(pc.PasswordEnc), false, pc);
                }
                if (viaRelay.Checked)
                {
                    var t = Relay.Parse(nameBox.Text.Trim() + "@" + relayBox.Text.Trim(), KovaRelayWire.Wire.DefaultPort, out _)!;
                    return new Answer(t.ToString(), port, pwBox.Text, save.Checked, null);
                }
                Protocol.PortProblem(portBox.Text.Trim(), out int p);
                return new Answer(addr.Text.Trim(), p, pwBox.Text, save.Checked, null);
            }
        }
    }

    /// <summary>Host this PC, step by step: how other PCs reach it (directly, through a relay only, or both), the relay, the passwords.</summary>
    internal static class HostWizard
    {
        internal sealed record Answer(bool Direct, bool Relay, int Port, string RelayAddress, int RelayPort, string RelayName, string Password, string ListenPassword);

        public static Answer? Run(IWin32Window owner, Settings settings, int port, string password, string listen, bool registeredNow, bool service)
        {
            int was = !settings.RelayHosting ? 1 : settings.RelayOnly ? 0 : 2;
            var relayOnly = WizardForm.Choice("Through a Kova &Relay only. Nothing on this PC is opened to the internet, and it works behind any home router. Needs a Kova Relay that both PCs can reach.", chosen: was == 0);
            var direct = WizardForm.Choice("&Directly, by this PC's Tailscale name or internet address. Kova opens its port in Windows Firewall.", chosen: was == 1);
            var both = WizardForm.Choice("B&oth: directly, and through a Kova Relay.", chosen: was == 2);
            bool UsesRelay() => relayOnly.Checked || both.Checked;
            bool UsesDirect() => direct.Checked || both.Checked;

            var (portLabel, portBox) = WizardForm.Field("&Port (the controlling PCs use the same one; 47120 unless you change it):", port.ToString());
            string relayText = settings.RelayAddress.Length == 0 ? "" : settings.RelayAddress + (settings.RelayPort == KovaRelayWire.Wire.DefaultPort ? "" : ":" + settings.RelayPort);
            var (relayLabel, relayBox) = WizardForm.Field("The &relay's address, with :port after it if it is not 47200 (like relay.example.com):", relayText);
            var (nameLabel, nameBox) = WizardForm.Field("This PC's na&me on the relay: 1 to 40 letters, digits, dashes, dots or underscores (like office-pc):",
                settings.RelayName.Length > 0 ? settings.RelayName : SuggestName());
            var (pwLabel, pwBox) = WizardForm.Field("Pass&word for controlling this PC (at least 6 characters):", password, secret: true);
            var (listenLabel, listenBox) = WizardForm.Field("&Listen-only password: someone with it hears this PC but cannot control it. Leave it empty for none:", listen, secret: true);
            var summary = WizardForm.Reading("");

            WizardForm? form = null;
            WizardForm.Page? relayPage = null;
            var pages = new List<WizardForm.Page>
            {
                new()
                {
                    Question = "How should other PCs reach this PC?",
                    Controls = new Control[] { relayOnly, direct, both },
                },
                new()
                {
                    Question = "Which port?",
                    Controls = new Control[] { portLabel, portBox },
                    Shown = UsesDirect,
                    Check = () => Task.FromResult<(string, bool)?>(Protocol.PortProblem(portBox.Text.Trim(), out _) is string p ? (p, false) : null),
                },
                (relayPage = new()
                {
                    Question = "Which relay, and this PC's name on it?",
                    Controls = new Control[] { relayLabel, relayBox, nameLabel, nameBox },
                    Shown = UsesRelay,
                    Check = async () =>
                    {
                        string r = relayBox.Text.Trim(), n = nameBox.Text.Trim();
                        if (r.Length == 0) return ("Type the relay's address.", false);
                        if (!KovaRelayWire.Wire.NameOk(n)) return ("The name must be 1 to 40 letters, digits, dashes, dots or underscores, like office-pc.", false);
                        if (Relay.Parse(n + "@" + r, KovaRelayWire.Wire.DefaultPort, out string? why) == null) return (why ?? "That is not a relay address.", false);
                        form!.ShowStatus("Checking the relay.", speak: true);
                        var found = await Task.Run(() => Relay.Check(r, n));
                        if (!found.Up) return (found.Said + " This PC keeps trying to reach it while it hosts.", true);
                        bool same = string.Equals(n, settings.RelayName, StringComparison.OrdinalIgnoreCase) && string.Equals(r, relayText, StringComparison.OrdinalIgnoreCase);
                        if (found.HostHere == true && !(same && registeredNow))
                            return ("The relay is up, but a PC called " + n + " is already connected to it. If that is not this PC, choose another name.", true);
                        relayPage!.Note = "The relay is up.";
                        return null;
                    },
                }),
                new()
                {
                    Question = "Passwords",
                    Controls = new Control[] { pwLabel, pwBox, listenLabel, listenBox, WizardForm.ShowPassword(pwBox, listenBox) },
                    Check = () =>
                    {
                        int n = new System.Globalization.StringInfo(pwBox.Text).LengthInTextElements;
                        if (n < MainForm.MinPasswordLength) return Task.FromResult<(string, bool)?>((n == 0 ? "Type a password." : "The password needs at least " + MainForm.MinPasswordLength + " characters. It has " + n + ".", false));
                        string l = listenBox.Text;
                        if (l.Length > 0 && l == pwBox.Text) return Task.FromResult<(string, bool)?>(("The listen-only password must be different from the password, or empty.", false));
                        if (l.Length > 0 && new System.Globalization.StringInfo(l).LengthInTextElements < MainForm.MinPasswordLength)
                            return Task.FromResult<(string, bool)?>(("The listen-only password needs at least " + MainForm.MinPasswordLength + " characters, or leave it empty.", false));
                        summary.Text = Summary();
                        return Task.FromResult<(string, bool)?>(null);
                    },
                },
                new()
                {
                    Question = "Ready to host",
                    Controls = new Control[] { summary },
                    NextText = () => service ? "&Apply to the Kova service" : "Start &hosting",
                },
            };

            string Summary()
            {
                var lines = new List<string>();
                string name = nameBox.Text.Trim(), r = relayBox.Text.Trim();
                var t = Relay.Parse(name + "@" + r, KovaRelayWire.Wire.DefaultPort, out _);
                if (UsesRelay() && t != null)
                    lines.Add("Other PCs choose Connect to a PC, Through a Kova Relay, then type the relay " + r + " and the name " + name + ". Or they type " + t + " as the address.");
                if (UsesDirect())
                    lines.Add("Other PCs can also connect straight to this PC: its Tailscale name or internet address, port " + portBox.Text.Trim() + ". Kova opens that port in Windows Firewall.");
                if (relayOnly.Checked) lines.Add("Nothing on this PC is opened to the internet: every connection comes through the relay.");
                lines.Add("They need the password you chose.");
                if (service) lines.Add("The Kova service is installed, so it hosts this way from now on.");
                return string.Join(Environment.NewLine, lines);
            }

            form = new WizardForm(service ? "Host this PC (the Kova service)" : "Host this PC", pages);
            using (form)
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return null;
                Protocol.PortProblem(portBox.Text.Trim(), out int p);
                if (!UsesDirect()) p = port; // kept as it was: relay only takes it on this PC alone
                string rh = "", rn = nameBox.Text.Trim();
                int rp = KovaRelayWire.Wire.DefaultPort;
                if (Relay.Parse((rn.Length > 0 ? rn : "x") + "@" + relayBox.Text.Trim(), KovaRelayWire.Wire.DefaultPort, out _) is Relay.Target t) { rh = t.RelayHost; rp = t.RelayPort; }
                return new Answer(UsesDirect(), UsesRelay(), p, rh, rp, rn, pwBox.Text, listenBox.Text);
            }
        }

        /// <summary>This PC's name made fit for the relay: letters, digits and dashes, lower case.</summary>
        private static string SuggestName()
        {
            var chars = Environment.MachineName.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').Take(40).ToArray();
            return chars.Length > 0 ? new string(chars) : "my-pc";
        }
    }
}
