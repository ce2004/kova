using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// Clipboard, Stop just one transfer (Control Shift T): every transfer going or waiting its turn, to and from
    /// every PC (in front, in the background, or controlling this one), each with its progress, and under each the
    /// things chosen in it. Delete (or Alt S) stops the line selected, only that, while everything else carries on;
    /// Stop all stops everything; Escape closes. A transfer that ends stays listed, saying how it ended, until the
    /// window is closed, so the list never shifts under you.
    /// </summary>
    internal sealed class TransfersForm : Form
    {
        private readonly Func<IReadOnlyList<FileChannel.Transfer>> _going;
        private readonly Func<FileChannel.Transfer, int, string?, string> _stop; // the transfer, the item (-1: all of it) and its name
        private readonly Action<string> _say;
        private readonly ListView _list = new()
        {
            View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.None, Width = 760, Height = 340,
            AccessibleName = "Transfers", AccessibleRole = AccessibleRole.List,
        };
        private readonly Button _stopOne = new() { Text = "&Stop the selected one (Delete)", AutoSize = true };
        private readonly Button _stopAll = new() { Text = "Stop &all", AutoSize = true };
        private readonly Button _close = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private readonly Timer _timer = new() { Interval = 1000 };
        // Every transfer shown since the window opened, in the order first seen: one that ends stays, with its result.
        private readonly List<FileChannel.Transfer> _shown = new();
        private readonly List<string> _states = new(); // the state each line was last shown in, by line

        /// <summary>One line: a transfer (Item -1), or one of the things chosen in it.</summary>
        private sealed record Row(FileChannel.Transfer T, int Item);

        public TransfersForm(Func<IReadOnlyList<FileChannel.Transfer>> going, Func<FileChannel.Transfer, int, string?, string> stop, Action stopAll, Action<string> say)
        {
            _going = going;
            _stop = stop;
            _say = say;
            Text = "Transfers";
            // Let go of when the window closes: it is opened again and again over months.
            var font = new System.Drawing.Font("Segoe UI", 10f);
            Font = font;
            FormClosed += (_, _) => font.Dispose();
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimizeBox = MaximizeBox = false;
            _list.Columns.Add("Transfer", 740);
            _list.AccessibleDescription = "Delete stops the one selected. F5 says how it is going now.";
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(10), WrapContents = false };
            flow.Controls.Add(new Label { Text = "&Transfers going and waiting, with the things chosen in each (Delete stops just the one selected)", AutoSize = true });
            flow.Controls.Add(_list);
            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.Add(_stopOne);
            buttons.Controls.Add(_stopAll);
            buttons.Controls.Add(_close);
            flow.Controls.Add(buttons);
            Controls.Add(flow);
            CancelButton = _close;
            _stopOne.Click += (_, _) => StopSelected();
            _stopAll.Click += (_, _) =>
            {
                if (_shown.All(t => t.Finished)) { _say("Nothing is being sent or received."); return; }
                stopAll();
                Fill();
            };
            _list.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Delete) { e.Handled = e.SuppressKeyPress = true; StopSelected(); }
                else if (e.KeyCode == Keys.F5) { e.Handled = e.SuppressKeyPress = true; SayNow(); }
            };
            _timer.Tick += (_, _) => Fill();
            FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); };
            Fill();
            if (_list.Items.Count > 0) _list.Items[0].Selected = _list.Items[0].Focused = true;
            _timer.Start();
            Menus.FocusWhenShown(this, () => _list);
        }

        private Row? Selected => (_list.FocusedItem ?? _list.SelectedItems.Cast<ListViewItem>().FirstOrDefault())?.Tag as Row;

        /// <summary>
        /// The lines as they should be now: each with its words, and the kind of state it is in (waiting, going,
        /// ended, stopped...), whose change is worth hearing even on the line you are on.
        /// </summary>
        private List<(Row Row, string Text, string State)> Wanted()
        {
            foreach (var t in _going())
                if (!_shown.Contains(t)) _shown.Add(t);
            var rows = new List<(Row, string, string)>();
            foreach (var t in _shown)
            {
                string state = t.Finished ? "ended" : t.Queued ? "queued" : t.Waiting ? "waiting" : "going";
                rows.Add((new Row(t, -1), Line(t), state));
                // The things chosen in it, when there is more than one (one alone is the transfer itself).
                var items = t.Items;
                if (items.Count > 1)
                    for (int i = 0; i < items.Count; i++)
                    {
                        var (words, itemState) = ItemState(t, items[i]);
                        rows.Add((new Row(t, i), items[i].Name + ", in " + t.What + ": " + words + ".", itemState));
                    }
            }
            return rows;
        }

        private static string Line(FileChannel.Transfer t)
        {
            string what = (t.Outgoing ? "Sending " : "Receiving ") + t.What + (t.Peer.Length > 0 ? (t.Outgoing ? " to " : " from ") + t.Peer : "");
            if (t.Finished) return t.Result is string result ? (t.Peer.Length > 0 ? t.Peer + ": " : "") + result : what + ": ended.";
            if (t.Queued) return what + ": waiting its turn, " + FileChannel.Size(t.Total) + ".";
            int percent = t.Total <= 0 ? 0 : (int)Math.Clamp(t.Done * 100 / t.Total, 0, 100);
            string left = t.Left is TimeSpan l ? ", about " + MainForm.Duration(l) + " left" : "";
            return what + ": " + percent + " percent, " + FileChannel.Size(t.Done) + " of " + FileChannel.Size(t.Total) +
                (t.Waiting ? ", waiting for the connection to come back." : ", " + FileChannel.Speed(t.BytesPerSecond) + left + ".");
        }

        /// <summary>One thing chosen in a transfer: how it is going, in words, and the kind of state that is.</summary>
        private static (string Words, string State) ItemState(FileChannel.Transfer t, FileChannel.Item item)
        {
            if (item.Stopped) return ("stopped", "stopped");
            if (t.Finished) return t.Failed ? ("ended", "ended") : ("done", "done");
            if (t.Queued) return ("waiting its turn", "queued");
            if (item.Total <= 0 || item.Done >= item.Total) return ("done", "done");
            if (item.Done == 0) return ("not started yet", "not started");
            return ((int)Math.Clamp(item.Done * 100 / item.Total, 0, 100) + " percent, " + FileChannel.Size(item.Done) + " of " + FileChannel.Size(item.Total), "going");
        }

        /// <summary>
        /// Brings the lines up to date. The line you are on keeps its words unless its state changes (waiting, going,
        /// ended, stopped): NVDA reads out a change to the focused line, and progress every second would never stop
        /// talking. F5 says how it is going now; the other lines are always current when you arrow onto them.
        /// </summary>
        private void Fill()
        {
            var rows = Wanted();
            if (rows.Count == 0)
            {
                // Never an empty list: with nothing to land on, NVDA says nothing at all.
                if (_list.Items.Count != 1 || _list.Items[0].Tag != null)
                {
                    _list.Items.Clear();
                    _states.Clear();
                    _list.Items.Add(new ListViewItem("Nothing is being sent or received."));
                    _list.Items[0].Selected = _list.Items[0].Focused = true;
                }
                return;
            }
            bool same = _list.Items.Count == rows.Count && _states.Count == rows.Count;
            for (int i = 0; same && i < rows.Count; i++) same = _list.Items[i].Tag is Row r && r == rows[i].Row;
            var focused = Selected;
            if (!same)
            {
                // A new transfer or a new part: rebuilt, staying on the same line.
                _list.BeginUpdate();
                try
                {
                    _list.Items.Clear();
                    _states.Clear();
                    foreach (var (row, text, state) in rows) { _list.Items.Add(new ListViewItem(text) { Tag = row }); _states.Add(state); }
                }
                finally { _list.EndUpdate(); }
                var land = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => focused != null && i.Tag is Row r && r == focused) ?? _list.Items[0];
                land.Selected = land.Focused = true;
                land.EnsureVisible();
                return;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                var item = _list.Items[i];
                var (_, text, state) = rows[i];
                if (item.Text == text) continue;
                if (item.Focused && _list.Focused && _states[i] == state) continue;
                item.Text = text;
                _states[i] = state;
            }
        }

        /// <summary>F5: the selected line, as it is right now.</summary>
        private void SayNow()
        {
            var rows = Wanted();
            if (Selected is not Row r) { _say("Nothing is being sent or received."); return; }
            var now = rows.FirstOrDefault(x => x.Row == r);
            if (now.Text == null) return;
            var item = _list.FocusedItem;
            if (item != null) item.Text = now.Text;
            _say(now.Text);
        }

        private void StopSelected()
        {
            if (Selected is not Row r) { _say("Nothing is being sent or received."); return; }
            if (r.T.Finished) { _say("That one has already ended."); return; }
            if (r.Item >= 0 && r.Item < r.T.Items.Count && r.T.Items[r.Item].Stopped) { _say(r.T.Items[r.Item].Name + " is already stopped."); return; }
            // With its name: the list may have changed since it was last brought up to date, and the name finds it again.
            string? name = r.Item >= 0 && r.Item < r.T.Items.Count ? r.T.Items[r.Item].Name : null;
            _say(_stop(r.T, r.Item, name));
            Fill();
        }
    }
}
