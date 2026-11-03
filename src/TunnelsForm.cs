using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// File, Reach a port on the remote PC (Control Shift P): which port there, which port here (the same one when it is free),
    /// and for a saved PC whether it opens again by itself each time it connects.
    /// </summary>
    internal static class TunnelWizard
    {
        internal sealed record Answer(int RemotePort, int LocalPort, bool Keep);

        /// <param name="pcName">The PC in front, as it is called here.</param>
        /// <param name="saved">Whether it is a saved PC (only then can it open again by itself).</param>
        /// <param name="probe">Whether something answers at that port on the remote PC: null if it does, else why not.</param>
        /// <param name="already">The local port a remote port is already reached at here, or null.</param>
        public static Answer? Run(IWin32Window owner, string pcName, bool saved, Func<int, Task<string?>> probe, Func<int, int?> already)
        {
            var (remoteLabel, remoteBox) = WizardForm.Field("&Port on the remote PC", "",
                help: "the number the program there listens on, for example 8888 for Resilio Sync's web page.");
            var localNote = WizardForm.Note("");
            var (localLabel, localBox) = WizardForm.Field("Port on &this PC", "",
                help: "where it opens here, as localhost and this number. Only this PC can use it.");
            var keep = new CheckBox { Text = "Open it a&gain each time I connect", AutoSize = true, Visible = saved };
            WizardForm? form = null;
            int remote = 0;

            static int? Number(string text) => int.TryParse(text.Trim(), out int p) && p is >= 1 and <= 65535 ? p : null;

            var first = new WizardForm.Page
            {
                Question = "Which port on " + pcName + " do you want to reach?",
                Controls = new Control[] { remoteLabel, remoteBox },
                Check = async () =>
                {
                    if (Number(remoteBox.Text) is not int p) return ("Type the port as a number from 1 to 65535.", false);
                    if (already(p) is int at) return ("Port " + p + " on " + pcName + " is already at localhost " + at + ". File, Remote ports open on this PC lists it.", false);
                    form!.ShowStatus("Checking that something answers at port " + p + " there.", speak: true);
                    string? problem = await probe(p);
                    remote = p;
                    // Here first: the same number when it is free on this PC, else the next free one, said on the next page.
                    int here = TunnelBook.FreeNear(p);
                    localBox.Text = here.ToString();
                    localNote.Text = here == p ? "Port " + p + " is free on this PC too, so it can be the same."
                        : "Port " + p + " is already used on this PC, so the next free one, " + here + ", is filled in.";
                    if (problem != null) return (problem + (problem.StartsWith("Nothing") ? " The program there may not be running yet." : ""), true);
                    return null;
                },
            };
            var second = new WizardForm.Page
            {
                Question = "Where should it open on this PC?",
                Controls = new Control[] { localNote, localLabel, localBox, keep },
                Focus = () => localBox,
                NextText = () => "&Open it",
                Check = () =>
                {
                    if (Number(localBox.Text) is not int p) return Task.FromResult<(string, bool)?>(("Type the port as a number from 1 to 65535.", false));
                    if (!TunnelBook.LocalFree(p))
                    {
                        int next = TunnelBook.FreeNear(p);
                        localBox.Text = next.ToString();
                        localBox.SelectAll();
                        return Task.FromResult<(string, bool)?>(("Port " + p + " is already used on this PC. The next free one, " + next + ", is filled in now: press Open it to use it.", false));
                    }
                    return Task.FromResult<(string, bool)?>(null);
                },
            };
            form = new WizardForm("Reach a port on the remote PC", new List<WizardForm.Page> { first, second });
            using (form)
            {
                if (form.ShowDialog(owner) != DialogResult.OK || Number(localBox.Text) is not int local || remote == 0) return null;
                return new Answer(remote, local, saved && keep.Checked);
            }
        }
    }

    /// <summary>
    /// File, Remote ports open on this PC: every tunnel, a line each ("localhost 8888, port 8888 on Office PC"). Delete closes
    /// the one you are on (and it no longer opens by itself), Copy its address puts it on the clipboard, Escape closes the window.
    /// </summary>
    internal sealed class TunnelsForm : Form
    {
        private readonly TunnelBook _book;
        private readonly Func<TunnelBook.Tunnel, bool> _kept;
        private readonly Func<TunnelBook.Tunnel, string> _close;
        private readonly Action<string> _copy, _say;
        private readonly ListView _list = new()
        {
            View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.None, Width = 640, Height = 260,
            AccessibleName = "Remote ports open on this PC", AccessibleRole = AccessibleRole.List,
        };
        private readonly Button _closeOne = new() { Text = "Close the &selected port (Delete)", AutoSize = true };
        private readonly Button _copyOne = new() { Text = "&Copy its address", AutoSize = true };
        private readonly Button _done = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private readonly Timer _timer = new() { Interval = 1000 };

        /// <param name="kept">Whether a tunnel opens again by itself (saved with its PC).</param>
        /// <param name="close">Closes a tunnel (and forgets it on its saved PC); returns what to say.</param>
        public TunnelsForm(TunnelBook book, Func<TunnelBook.Tunnel, bool> kept, Func<TunnelBook.Tunnel, string> close, Action<string> copy, Action<string> say)
        {
            _book = book;
            _kept = kept;
            _close = close;
            _copy = copy;
            _say = say;
            Text = "Remote ports open on this PC";
            // Let go of when the window closes: it is opened again and again over months.
            var font = new System.Drawing.Font("Segoe UI", 10f);
            Font = font;
            FormClosed += (_, _) => font.Dispose();
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            _list.Columns.Add("Port", 620);
            _list.AccessibleDescription = "Delete closes the one selected.";
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(10), WrapContents = false };
            flow.Controls.Add(new Label { Text = "&Ports on remote PCs you can reach here (Delete closes the one selected)", AutoSize = true });
            flow.Controls.Add(_list);
            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.Add(_closeOne);
            buttons.Controls.Add(_copyOne);
            buttons.Controls.Add(_done);
            flow.Controls.Add(buttons);
            Controls.Add(flow);
            CancelButton = _done;
            _closeOne.Click += (_, _) => CloseSelected();
            _copyOne.Click += (_, _) =>
            {
                if (Selected is not TunnelBook.Tunnel t) { _say("No ports are open here."); return; }
                _copy(t.Address);
                _say("Copied " + t.Address + ".");
            };
            _list.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Delete) { e.Handled = e.SuppressKeyPress = true; CloseSelected(); }
            };
            _timer.Tick += (_, _) => Fill();
            FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); };
            Fill();
            if (_list.Items.Count > 0) _list.Items[0].Selected = _list.Items[0].Focused = true;
            _timer.Start();
            Menus.FocusWhenShown(this, () => _list);
        }

        // The selected line first (the arrows move selection and focus together; a screen reader or mouse may select alone).
        private TunnelBook.Tunnel? Selected => (_list.SelectedItems.Cast<ListViewItem>().FirstOrDefault() ?? _list.FocusedItem)?.Tag as TunnelBook.Tunnel;

        private string Line(TunnelBook.Tunnel t)
        {
            string line = "localhost " + t.LocalPort + ", port " + t.RemotePort + " on " + t.Name;
            if (!_book.Ready(t)) line += ", waiting for " + t.Name + " to reconnect";
            if (_kept(t)) line += ", opens each time you connect";
            return line + ".";
        }

        /// <summary>
        /// The lines as they are now. Rebuilt only when a tunnel opens or closes, staying on the same one; otherwise only
        /// lines whose words changed, so NVDA does not read the line you are on again every second.
        /// </summary>
        private void Fill()
        {
            var all = _book.All.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.LocalPort).ToList();
            if (all.Count == 0)
            {
                // Never an empty list: with nothing to land on, NVDA says nothing at all.
                if (_list.Items.Count != 1 || _list.Items[0].Tag != null)
                {
                    _list.Items.Clear();
                    _list.Items.Add(new ListViewItem("No ports are open here. File, Reach a port on the remote PC opens one."));
                    _list.Items[0].Selected = _list.Items[0].Focused = true;
                }
                return;
            }
            bool same = _list.Items.Count == all.Count;
            for (int i = 0; same && i < all.Count; i++) same = _list.Items[i].Tag == all[i];
            if (!same)
            {
                var focused = Selected;
                _list.BeginUpdate();
                try
                {
                    _list.Items.Clear();
                    foreach (var t in all) _list.Items.Add(new ListViewItem(Line(t)) { Tag = t });
                }
                finally { _list.EndUpdate(); }
                var land = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag == focused) ?? _list.Items[0];
                land.Selected = land.Focused = true;
                land.EnsureVisible();
                return;
            }
            for (int i = 0; i < all.Count; i++)
            {
                string text = Line(all[i]);
                if (_list.Items[i].Text != text) _list.Items[i].Text = text;
            }
        }

        private void CloseSelected()
        {
            if (Selected is not TunnelBook.Tunnel t) { _say("No ports are open here."); return; }
            int at = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : _list.FocusedItem?.Index ?? 0;
            _say(_close(t));
            Fill();
            if (_list.Items.Count > 0)
            {
                var land = _list.Items[Math.Min(at, _list.Items.Count - 1)];
                land.Selected = land.Focused = true;
            }
        }
    }
}
