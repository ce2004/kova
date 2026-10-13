using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// Get files from the remote PC: its folders and files in a list. Enter opens a folder,
    /// Backspace goes up, Space (or Enter on a file) checks files and folders, in as many
    /// folders as you like, and only Get brings the checked ones here, all at once, into the
    /// received files folder. Each one then says ", getting", then ", got" (or ", failed"), and
    /// TailRemote says when they have arrived. The other way, Send files here and Send a folder
    /// here send files from this PC into the remote folder that is open.
    /// </summary>
    internal sealed class RemoteFilesForm : Form
    {
        private readonly Func<Client?> _client; // the connection now: a reconnect makes a new one
        private readonly Action<string> _say;
        private readonly Action<string> _tell; // news, said after whatever is being said (see Speech)
        private readonly TextBox _where = new() { ReadOnly = true, Width = 640, AccessibleName = "Folder on the remote PC" };
        private readonly ListView _list = new()
        {
            View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, MultiSelect = false,
            Width = 640, Height = 360, AccessibleName = "Files and folders",
        };
        private readonly Button _get = new() { Text = "&Get the checked files and folders", AutoSize = true };
        private readonly Button _sendHere = new() { Text = "&Send files here...", AutoSize = true };
        private readonly Button _sendFolderHere = new() { Text = "Send a fol&der here...", AutoSize = true };
        private readonly Button _up = new() { Text = "&Up one folder (Backspace)", AutoSize = true };
        private readonly Button _close = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private string _path = "";
        private bool _loading, _filling;
        // Checked, in any folder (checks stay when you move between folders), by path.
        private readonly Dictionary<string, Entry> _chosen = new(StringComparer.OrdinalIgnoreCase);
        // What happened to each one asked for: "getting", "got" or "failed", by path.
        private readonly Dictionary<string, string> _state = new(StringComparer.OrdinalIgnoreCase);
        // The ones on their way now (one Get at a time: the remote PC replaces a sending with a newer one).
        private List<Entry>? _pending;
        // The remote folder files are being sent into now (one at a time: a newer sending replaces it).
        private string? _sentInto;
        // Which transfer is this window's: begun after it asked (a higher serial) and named as it asked. Any end
        // used to count, so the host's own Send files ending marked a Get as "got" while it was still coming.
        private int _askedAfter, _sentAfter;
        private string? _sentWhat;
        private bool _picking;
        // Which connection each was asked on, and when: see ForgetStale.
        private Client? _pendingOn, _sentOn;
        private long _pendingAt, _sentAt;

        private sealed record Entry(bool Folder, string Path, string Name, long Bytes, long Ticks);

        public RemoteFilesForm(Func<Client?> client, Action<string> say, Action<string> tell)
        {
            _client = client;
            _say = say;
            _tell = tell;
            Text = "Get files from the remote PC";
            Font = new System.Drawing.Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimizeBox = MaximizeBox = false;
            _list.Columns.Add("Name", 330);
            _list.Columns.Add("Size", 120);
            _list.Columns.Add("Modified", 170);
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(10), WrapContents = false };
            flow.Controls.Add(new Label { Text = "Folder on the remote PC", AutoSize = true });
            flow.Controls.Add(_where);
            flow.Controls.Add(new Label { Text = "&Files and folders (Enter opens a folder or checks a file, Space checks, Backspace goes up, Alt G gets what is checked)", AutoSize = true });
            flow.Controls.Add(_list);
            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.Add(_get);
            buttons.Controls.Add(_sendHere);
            buttons.Controls.Add(_sendFolderHere);
            buttons.Controls.Add(_up);
            buttons.Controls.Add(_close);
            flow.Controls.Add(buttons);
            Controls.Add(flow);
            CancelButton = _close;
            _get.Click += (_, _) => Get();
            _up.Click += (_, _) => Up();
            _sendHere.Click += (_, _) => SendHere(folder: false);
            _sendFolderHere.Click += (_, _) => SendHere(folder: true);
            _list.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Open(); }
                else if (e.KeyCode == Keys.Back) { e.Handled = e.SuppressKeyPress = true; Up(); }
            };
            _list.DoubleClick += (_, _) => Open();
            _list.ItemChecked += (_, e) =>
            {
                if (_filling || e.Item.Tag is not Entry en) return;
                if (e.Item.Checked) _chosen[en.Path] = en; else _chosen.Remove(en.Path);
            };
            MainForm.IncomingFilesEnded += Arrived;
            MainForm.OutgoingFilesEnded += Sent;
            FormClosed += (_, _) => { MainForm.IncomingFilesEnded -= Arrived; MainForm.OutgoingFilesEnded -= Sent; };
            Shown += async (_, _) => await LoadFolder("", null);
            Menus.FocusWhenShown(this, () => _list);
        }

        private Entry? Current => _list.FocusedItem?.Tag as Entry ?? _list.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.Tag as Entry;

        private async void Open()
        {
            if (Current is not Entry e) return;
            if (!e.Folder)
            {
                // A file is checked, never fetched on the spot: Get brings everything checked at once.
                if (_list.FocusedItem is ListViewItem item) item.Checked = !item.Checked;
                return;
            }
            await LoadFolder(e.Path, null);
        }

        private string Label(Entry e) =>
            e.Name + (e.Folder ? ", folder" : "") + (_state.TryGetValue(e.Path, out var st) ? ", " + st : "");

        private void Relabel()
        {
            foreach (ListViewItem i in _list.Items)
                if (i.Tag is Entry e && i.Text != Label(e)) i.Text = Label(e);
        }

        /// <summary>A transfer coming here ended (MainForm says what arrived): the ones asked for are marked.</summary>
        private void Arrived(FileChannel.Transfer t)
        {
            if (IsDisposed || _pending == null || t.Outgoing || t.Clipboard || t.Serial <= _askedAfter || !Asked(_pending, t.What)) return;
            foreach (var e in _pending) _state[e.Path] = t.Failed ? "failed" : "got";
            _pending = null;
            Relabel();
        }

        /// <summary>
        /// Is this what was asked for? The remote PC calls a batch by the name of the one thing in it, or
        /// "3 items" (fewer if some were no longer there), and a name already taken here becomes "name (2)".
        /// </summary>
        private static bool Asked(List<Entry> asked, string what)
        {
            var names = asked.Select(e => FileChannel.TopName(e.Path)).ToList();
            if (names.Any(n => SameName(n, what))) return true;
            return names.Count > 1 && what.EndsWith(" items") && int.TryParse(what[..^6], out int k) && k >= 2 && k <= names.Count;
        }

        private static bool SameName(string asked, string what)
        {
            if (string.Equals(asked, what, StringComparison.OrdinalIgnoreCase)) return true;
            string stem = System.IO.Path.GetFileNameWithoutExtension(asked), ext = System.IO.Path.GetExtension(asked);
            if (!what.StartsWith(stem + " (", StringComparison.OrdinalIgnoreCase) || !what.EndsWith(")" + ext, StringComparison.OrdinalIgnoreCase)) return false;
            int digits = what.Length - stem.Length - 3 - ext.Length;
            return digits > 0 && what.Substring(stem.Length + 2, digits).All(char.IsAsciiDigit);
        }

        /// <summary>Send files here / Send a folder here: chosen on this PC, sent into the remote folder open now.</summary>
        private async void SendHere(bool folder)
        {
            // The drives and usual folders are a list, not a folder anything can go into.
            if (_path.Length == 0) { _say("Open a folder on the remote PC first."); return; }
            if (_client() is not Client c) { _say("Not connected to the remote PC."); return; }
            if (!c.CanSendTo) { _say("The remote PC has an older Kova that cannot take files into a folder. Update it first."); return; }
            if (_picking) { _say("The file picker is already open."); return; }
            ForgetStale();
            if (_sentInto != null) { _say("Still sending the last ones into " + _sentInto + ". Send more when they have gone."); return; }
            string into = _path;
            string[]? paths;
            _picking = true;
            try { paths = await MainForm.PickFiles(folder); } // its own thread: this window keeps answering
            catch (Exception e) { if (!IsDisposed) _say("Could not choose what to send: " + e.Message); return; }
            finally { _picking = false; }
            if (IsDisposed || paths == null || paths.Length == 0) return;
            string what = paths.Length == 1 ? FileChannel.TopName(paths[0]) : paths.Length + " items"; // as the transfer will be called
            _sentAfter = FileChannel.Transfer.Latest;
            _sentWhat = what;
            if (_client() is not Client now || !now.SendFilesTo(paths, into)) { _say("Not connected to the remote PC, so nothing was sent."); return; }
            _sentInto = into;
            _sentOn = now;
            _sentAt = Environment.TickCount64;
            _say("Sending " + what + " into " + into + " on the remote PC. Kova says when it has gone.");
        }

        /// <summary>Files sent from here finished going: said, and the folder they went into shows them.</summary>
        private async void Sent(FileChannel.Transfer t)
        {
            if (IsDisposed || _sentInto is not string into || t.Serial <= _sentAfter || !string.Equals(t.What, _sentWhat, StringComparison.OrdinalIgnoreCase)) return;
            _sentInto = null;
            // News, queued: it ends by itself, often while something else is being said.
            if (t.Result != null) _tell(t.Result);
            if (!t.Failed && string.Equals(_path, into, StringComparison.OrdinalIgnoreCase)) await LoadFolder(_path, Current?.Path);
        }

        private async void Up()
        {
            // Backspace here used to do nothing at all, which sounds like a key that did not register.
            if (_path.Length == 0) { _say("Already at the list of drives."); return; }
            // A drive's root goes back to the drives and usual folders; the folder just left is where you land.
            string trimmed = _path.TrimEnd('\\');
            string parent = trimmed.Length <= 2 ? "" : System.IO.Path.GetDirectoryName(trimmed) ?? "";
            await LoadFolder(parent, _path);
        }

        private async System.Threading.Tasks.Task LoadFolder(string path, string? focus)
        {
            if (_loading) return;
            _loading = true;
            UseWaitCursor = true;
            try
            {
                if (_client() is not Client c) { _say("Not connected to the remote PC."); return; }
                string text = await c.ListFolderAsync(path);
                if (IsDisposed) return; // closed while waiting
                if (text.StartsWith("E\t")) { _say(text[2..].Trim()); return; }
                var entries = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Parse).OfType<Entry>().ToList();
                _path = path;
                _where.Text = path.Length == 0 ? "Drives and usual folders" : path;
                // T: the remote PC had more than it listed (it lists at most RemoteTools.MaxEntries).
                // Its count is how many it really listed: fewer than MaxEntries when a long listing was cut short by size.
                string? tLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("T\t"));
                bool more = tLine != null;
                int listed = tLine != null && int.TryParse(tLine[2..].Trim(), out int count) && count > 0 ? count : RemoteTools.MaxEntries;
                _list.BeginUpdate();
                // EndUpdate in finally: a problem while filling must not leave the list frozen, never redrawn.
                try
                {
                    _filling = true;
                    _list.Items.Clear();
                    foreach (var e in entries)
                    {
                        // "Desktop, folder", not the Size column saying "folder"; then how getting it went.
                        var item = new ListViewItem(Label(e)) { Tag = e, Checked = _chosen.ContainsKey(e.Path) };
                        item.SubItems.Add(e.Folder ? "" : FileChannel.Size(e.Bytes));
                        item.SubItems.Add(e.Ticks > 0 ? new DateTime(e.Ticks, DateTimeKind.Utc).ToLocalTime().ToString(MainForm.DateWords, System.Globalization.CultureInfo.InvariantCulture) : "");
                        _list.Items.Add(item);
                    }
                }
                finally
                {
                    _filling = false;
                    _list.EndUpdate();
                }
                if (_list.Items.Count == 0) { _say("This folder is empty."); _list.Focus(); return; }
                var land = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => focus != null && string.Equals(((Entry)i.Tag!).Path.TrimEnd('\\'), focus.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    ?? _list.Items[0];
                land.Selected = land.Focused = true;
                land.EnsureVisible();
                _list.Focus();
                if (more) _say("Only the first " + listed + " are shown.");
            }
            catch (TimeoutException) { if (!IsDisposed) _say("The remote PC did not answer. Try again."); }
            catch (System.IO.IOException e) { if (!IsDisposed) _say("Could not list the folder: " + e.Message); }
            catch (Exception e) when (!IsDisposed) { _say("Could not list the folder: " + e.Message); }
            catch { } // closed while waiting: nobody to tell
            finally
            {
                _loading = false;
                _filling = false;
                if (!IsDisposed) UseWaitCursor = false;
            }
        }

        private static Entry? Parse(string line)
        {
            var p = line.Split('\t');
            if (p.Length < 5 || (p[0] != "D" && p[0] != "F")) return null;
            long.TryParse(p[3], out long bytes);
            long.TryParse(p[4], out long ticks);
            return new Entry(p[0] == "D", p[1], p[2], bytes, ticks);
        }

        /// <summary>
        /// A Get or a sending whose end this window can no longer hear about must not block the next
        /// one for as long as the window is open: after a reconnect (a new connection), or when no
        /// transfer at all began within a minute of asking (the remote PC never answered).
        /// </summary>
        private void ForgetStale()
        {
            var now = _client();
            long ms = Environment.TickCount64;
            if (_pending != null && (now != _pendingOn || (FileChannel.Transfer.Latest == _askedAfter && ms - _pendingAt > 60_000)))
            {
                foreach (var e in _pending)
                    if (_state.TryGetValue(e.Path, out var st) && st == "getting") _state.Remove(e.Path); // no longer known
                _pending = null;
                Relabel();
            }
            if (_sentInto != null && (now != _sentOn || (FileChannel.Transfer.Latest == _sentAfter && ms - _sentAt > 60_000)))
                _sentInto = null;
        }

        private void Get()
        {
            ForgetStale();
            if (_pending != null) { _say("Still getting the last ones. Check more meanwhile, and Get them when those have arrived."); return; }
            var chosen = _chosen.Values.ToList();
            if (chosen.Count == 0) { _say("Nothing is checked. Check files and folders with Space, or Enter on a file, then Get."); return; }
            // Whole drives are too much to mean: a folder on one is fine.
            if (chosen.Any(c => c.Path.TrimEnd('\\').Length <= 2)) { _say("A whole drive is too much: open it and check folders or files in it."); return; }
            _askedAfter = FileChannel.Transfer.Latest; // only a transfer begun after this can be the answer
            if (_client() is not Client client || !client.Fetch(chosen.Select(c => c.Path))) { _say("Not connected to the remote PC, so nothing was asked for."); return; }
            _pending = chosen;
            _pendingOn = client;
            _pendingAt = Environment.TickCount64;
            foreach (var c in chosen) _state[c.Path] = "getting";
            _chosen.Clear();
            _filling = true;
            foreach (ListViewItem i in _list.Items) i.Checked = false;
            _filling = false;
            Relabel();
            _say("Getting " + (chosen.Count == 1 ? chosen[0].Name : chosen.Count + " items") + " into " + FileChannel.Downloads + ". Kova says when they have arrived; each one says getting, then got.");
        }
    }

    /// <summary>Remote PC info: one line per fact, read with the arrows. Refresh asks again.</summary>
    internal sealed class RemoteInfoForm : Form
    {
        private readonly ListBox _lines = new() { Width = 560, Height = 260, AccessibleName = "About the remote PC" };

        public RemoteInfoForm(Func<Client?> client, string first, Action<string> say)
        {
            Text = "Remote PC info";
            Font = new System.Drawing.Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimizeBox = MaximizeBox = false;
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(10), WrapContents = false };
            flow.Controls.Add(_lines);
            // Tab goes between the lines and OK, nothing else; F5 asks the remote PC again.
            var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
            flow.Controls.Add(ok);
            Controls.Add(flow);
            AcceptButton = ok;
            CancelButton = ok;
            _lines.AccessibleDescription = "F5 refreshes";
            Fill(first);
            _lines.KeyDown += async (_, e) =>
            {
                if (e.KeyCode != Keys.F5) return;
                e.Handled = true;
                try
                {
                    if (client() is not Client c) { say("Not connected to the remote PC."); return; }
                    string text = await c.RequestInfoAsync();
                    if (IsDisposed) return;
                    Fill(text);
                    say("Refreshed.");
                }
                catch { if (!IsDisposed) say("The remote PC did not answer."); }
            };
            Menus.FocusWhenShown(this, () => _lines);
        }

        private void Fill(string text)
        {
            int at = Math.Max(0, _lines.SelectedIndex);
            _lines.Items.Clear();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) _lines.Items.Add(line);
            if (_lines.Items.Count > 0) _lines.SelectedIndex = Math.Min(at, _lines.Items.Count - 1);
        }
    }
}
