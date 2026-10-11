using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// The menu bar's behaviour for NVDA: Enter on a setting turns it on or off and the menu
    /// stays open, saying "checked" or "unchecked", so several can be changed in a row; Escape
    /// goes back a level. Actions (anything that opens a window or asks first) close the menu
    /// as usual.
    /// </summary>
    internal static class Menus
    {
        // Set while a stay-open item is being clicked: every menu in the open chain refuses to close.
        private static bool _keepOpen;

        /// <summary>A setting that is on or off. Enter flips it, the menu stays open and says which.</summary>
        public static MenuItem Check(string text)
        {
            var item = new MenuItem(text) { Tag = KeepOpenTag };
            item.Click += (_, _) =>
            {
                // The setting's own handlers run inside this and often speak (interrupting). Said
                // first, "checked" was cut off by their message and NVDA often never said the state;
                // queued after them without interrupting, both are heard.
                item.Checked = !item.Checked;
                Speech.Speak(item.Checked ? "checked" : "unchecked", interrupt: false);
            };
            return item;
        }

        /// <summary>A setting that is on or off but asks first (a window, administrator permission): the menu closes.</summary>
        public static MenuItem CheckAsking(string text)
        {
            var item = new MenuItem(text);
            item.Click += (_, _) => item.Checked = !item.Checked;
            return item;
        }

        public static MenuItem Action(string text, Action clicked, Keys keys = Keys.None)
        {
            var item = new MenuItem(text) { ShortcutKeys = keys };
            item.Click += (_, _) => AfterMenu(clicked);
            return item;
        }

        /// <summary>
        /// Runs a menu's action once the menu has finished closing. A window opened straight from
        /// the click came up while Windows was still closing the menu, and NVDA, told "menu
        /// closed" after the window had appeared, went back to the main window and never read it.
        /// </summary>
        public static void AfterMenu(Action action)
        {
            if (SynchronizationContext.Current is { } ui) ui.Post(_ => action(), null);
            else action();
        }

        /// <summary>
        /// For every TailRemote window: the keyboard starts on 'first', set before the window
        /// appears. Moving it there afterwards made NVDA say it twice.
        /// </summary>
        public static void FocusWhenShown(Form form, Func<Control?> first) =>
            form.Load += (_, _) => { if (first() is { } c) form.ActiveControl = c; };

        internal static readonly object KeepOpenTag = new();

        /// <summary>
        /// Empties a menu for good. Clear() alone keeps every removed item's handles and drop-down alive (Windows
        /// Forms never lets go of them), and menus rebuilt on every transfer or switch added up over months.
        /// </summary>
        internal static void ClearAndDispose(ToolStripItemCollection items)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i];
                items.RemoveAt(i);
                item.Dispose();
            }
        }

        // When a key that means "open this" (Right, Enter, a letter) or a mouse click last came in.
        private static long _openAsked;

        /// <summary>
        /// Notes the keys and clicks that may open a submenu. With the mouse pointer resting where
        /// a menu dropped down, the item under it opened its submenu by itself as the arrows went past.
        /// </summary>
        private sealed class OpenFilter : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202;
                if (m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    var key = (Keys)(int)m.WParam & Keys.KeyCode;
                    if (key is not (Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Escape or Keys.Left or Keys.Tab
                        or Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.Insert or Keys.Capital))
                        _openAsked = Environment.TickCount64;
                }
                else if (m.Msg is WM_LBUTTONDOWN or WM_LBUTTONUP) _openAsked = Environment.TickCount64;
                return false;
            }
        }

        private static bool _filtering;

        /// <summary>Hooks every drop-down under the menu bar (and ones added later) so stay-open items keep it open.</summary>
        public static void Attach(MenuStrip bar)
        {
            if (!_filtering) { _filtering = true; Application.AddMessageFilter(new OpenFilter()); }
            foreach (ToolStripItem top in bar.Items)
                if (top is ToolStripMenuItem m) Attach(m);
        }

        public static void Attach(ToolStripMenuItem menu)
        {
            var dd = menu.DropDown;
            if (dd.Tag == KeepOpenTag) return;
            dd.Tag = KeepOpenTag;
            // A submenu (not File, Settings and the rest) opens only when asked, never by the pointer resting on it.
            dd.Opening += (_, e) =>
            {
                if (menu.Owner is ToolStripDropDown && Environment.TickCount64 - _openAsked > 500) e.Cancel = true;
            };
            dd.ItemClicked += (_, e) =>
            {
                if (e.ClickedItem?.Tag != KeepOpenTag && e.ClickedItem is not MenuItem { Usable: false }) return;
                _keepOpen = true;
                // Cleared once the click has been handled, whichever menus asked to close.
                dd.BeginInvoke(() => _keepOpen = false);
            };
            dd.Closing += (_, e) =>
            {
                if (_keepOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };
            // Only items with a submenu of their own: asking a plain item for its drop-down makes one (a whole
            // ToolStrip Windows Forms remembers for good), and items added later (history lines, saved PCs,
            // file bars) are added and replaced thousands of times over months.
            dd.ItemAdded += (_, e) => { if (e.Item is ToolStripMenuItem { HasDropDownItems: true } sub) Attach(sub); };
            foreach (ToolStripItem i in menu.DropDownItems)
                if (i is ToolStripMenuItem sub) Attach(sub);
        }
    }

    /// <summary>
    /// A submenu that works like a drop-down list: one choice checked. Its name shows the choice
    /// ("Sound quality: Variable"), so NVDA reads the current one on the way past. Enter picks
    /// one, the menu stays open and says "checked".
    /// </summary>
    internal sealed class MenuChoice
    {
        public readonly MenuItem Menu;
        private readonly string _title;
        private readonly List<string> _names = new();
        private int _index = -1;

        public event EventHandler? SelectedIndexChanged;

        public MenuChoice(string title)
        {
            _title = title;
            Menu = new MenuItem(title);
        }

        public int Count => _names.Count;

        /// <summary>Empties the list, choosing nothing (no SelectedIndexChanged).</summary>
        public void Clear()
        {
            _names.Clear();
            _index = -1;
            Menus.ClearAndDispose(Menu.DropDownItems);
            Menu.Text = _title;
        }

        public ToolStripMenuItem Add(string name)
        {
            int i = _names.Count;
            _names.Add(name);
            var item = new MenuItem(name.Replace("&", "&&")) { Tag = Menus.KeepOpenTag };
            item.Click += (_, _) =>
            {
                if (!Menu.Usable) { Speech.Speak(Menu.WhyNot); return; }
                Speech.Speak("checked");
                SelectedIndex = i;
            };
            Menu.DropDownItems.Add(item);
            return item;
        }

        public int SelectedIndex
        {
            get => _index;
            set
            {
                if (value < 0 || value >= _names.Count || value == _index) return;
                if (_index >= 0) ((ToolStripMenuItem)Menu.DropDownItems[_index]).Checked = false;
                _index = value;
                ((ToolStripMenuItem)Menu.DropDownItems[value]).Checked = true;
                Menu.Text = _title + ": " + _names[value].Replace("&", "&&");
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Something typed in (the address, port, a password) as a menu item that says it:
    /// "Address: myserver...". Enter opens a small box to change it. Passwords say only
    /// whether one is set.
    /// </summary>
    internal sealed class MenuText
    {
        public readonly ToolStripMenuItem Item;
        private readonly string _title, _prompt;
        private readonly bool _secret;
        private string _text = "";

        /// <summary>Changed by the person (not by code), after the box closed with OK.</summary>
        public event Action? Changed;

        public MenuText(string title, string prompt, bool secret = false)
        {
            _title = title;
            _prompt = prompt;
            _secret = secret;
            Item = new ToolStripMenuItem();
            Item.Click += (_, _) => Menus.AfterMenu(() => Ask(Form.ActiveForm, null));
            Show();
        }

        public string Text
        {
            get => _text;
            set { _text = value ?? ""; Show(); }
        }

        private void Show() =>
            Item.Text = _title + ": " + (_secret ? (_text.Length == 0 ? "not set" : "set") : _text.Length == 0 ? "empty" : _text.Replace("&", "&&")) + "...";

        /// <summary>Opens the box. problem: why it is asked for (said first), or null when chosen from the menu.</summary>
        public void Ask(IWin32Window? owner, string? problem)
        {
            using var f = new TextForm(_title.Replace("&", ""), problem == null ? _prompt : problem + " " + _prompt, _text, _secret);
            if (f.ShowDialog(owner) != DialogResult.OK || f.Value == _text) return;
            Text = f.Value;
            Changed?.Invoke();
        }
    }

    /// <summary>The small box MenuText opens: what to type, the text, and Show password for passwords.</summary>
    internal sealed class TextForm : Form
    {
        private readonly TextBox _box = new() { Width = 360 };
        public string Value => _box.Text;

        public TextForm(string title, string prompt, string text, bool secret)
        {
            Text = title;
            Font = new System.Drawing.Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(10), WrapContents = false };
            var label = new Label { Text = prompt, AutoSize = true, MaximumSize = new System.Drawing.Size(360, 0) };
            _box.Text = text;
            _box.AccessibleName = prompt; // NVDA reads it with the box, including why it was asked for
            _box.UseSystemPasswordChar = secret;
            flow.Controls.Add(label);
            flow.Controls.Add(_box);
            if (secret)
            {
                var show = new CheckBox { Text = "&Show password", AutoSize = true };
                show.CheckedChanged += (_, _) => _box.UseSystemPasswordChar = !show.Checked;
                flow.Controls.Add(show);
            }
            var buttons = new FlowLayoutPanel { AutoSize = true };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            flow.Controls.Add(buttons);
            Controls.Add(flow);
            AcceptButton = ok;
            CancelButton = cancel;
            _box.SelectAll();
            Menus.FocusWhenShown(this, () => _box);
        }
    }

    /// <summary>
    /// The confirmation for restarting the remote PC: a checkbox must be ticked before the Restart
    /// button turns on, so a reflexive Enter cannot restart the remote PC. There is deliberately no
    /// AcceptButton, so even once the box is ticked, confirming takes a real press of Restart rather
    /// than a stray Enter; Escape still cancels. Styled and made accessible like <see cref="TextForm"/>.
    /// </summary>
    internal sealed class RestartConfirmForm : Form
    {
        public RestartConfirmForm()
        {
            Text = "Restart remote PC";
            Font = new System.Drawing.Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(10), WrapContents = false };
            var label = new Label
            {
                Text = "Restart the remote PC now? Programs there close as in a normal restart, and may ask to save first." + Environment.NewLine + Environment.NewLine +
                       "Kova reconnects when it is back. That only happens if the remote PC starts hosting by itself: turn on Start hosting when Windows starts there, or the service.",
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(360, 0),
            };
            var understand = new CheckBox { Text = "&I understand this will restart the remote PC", AutoSize = true, Checked = false };
            understand.AccessibleName = "I understand this will restart the remote PC"; // read with the box so the stakes come through
            var buttons = new FlowLayoutPanel { AutoSize = true };
            var restart = new Button { Text = "&Restart", DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            understand.CheckedChanged += (_, _) => restart.Enabled = understand.Checked; // Restart turns on only once the box is ticked
            buttons.Controls.Add(restart);
            buttons.Controls.Add(cancel);
            flow.Controls.Add(label);
            flow.Controls.Add(understand);
            flow.Controls.Add(buttons);
            Controls.Add(flow);
            // Tab order follows the add order: checkbox, then Restart, then Cancel.
            CancelButton = cancel; // Escape cancels; no AcceptButton, so Enter never confirms
            Menus.FocusWhenShown(this, () => understand);
        }
    }

    /// <summary>A line of information in a menu (Streaming, Files): Enter says it again, the menu stays open.</summary>
    internal sealed class MenuStatus
    {
        public readonly MenuItem Item = new() { Tag = Menus.KeepOpenTag };
        private string _text = "";

        public MenuStatus(string text)
        {
            Item.Click += (_, _) => { if (!Item.HasDropDownItems) Speech.Speak(_text); };
            Text = text;
        }

        public string Text
        {
            get => _text;
            set
            {
                if (value == _text) return;
                _text = value;
                Item.Text = value.Replace("&", "&&");
            }
        }
    }

    /// <summary>
    /// A menu item that is never greyed out. A greyed-out item cannot be reached with the arrows
    /// at all, yet NVDA still counted it ("7 of 17" after "2 of 17"), and nobody could find out it
    /// was there. Setting Enabled to false leaves it in reach: Enter (or its shortcut) says
    /// WhyNot instead of doing it, and the menu stays open.
    /// </summary>
    internal class MenuItem : ToolStripMenuItem
    {
        public MenuItem() { }
        public MenuItem(string text) : base(text) { }

        public bool Usable { get; private set; } = true;

        /// <summary>Said when it is chosen while it cannot be used.</summary>
        public string WhyNot { get; set; } = "That cannot be used right now.";

        public override bool Enabled
        {
            get => base.Enabled;
            set => Usable = value; // base.Enabled stays true: always in reach
        }

        protected override void OnClick(EventArgs e)
        {
            if (!Usable) { Speech.Speak(WhyNot); return; }
            base.OnClick(e);
        }

        /// <summary>Does what choosing it does, without the menu starting to close.</summary>
        internal void ClickInPlace() => OnClick(EventArgs.Empty);

        protected override ToolStripDropDown CreateDefaultDropDown() => new StayOpenDropDown { OwnerItem = this };
    }

    /// <summary>
    /// The drop-down of a MenuItem. Enter on a setting, a status line or something that cannot be
    /// used right now is handled here, inside the menu: going through the usual click, the menu
    /// first began to close and gave the window focus (NVDA said "Kova window") before
    /// TailRemote kept it open.
    /// </summary>
    internal sealed class StayOpenDropDown : ToolStripDropDownMenu
    {
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Enter && Items.Cast<ToolStripItem>().FirstOrDefault(i => i.Selected) is MenuItem item && !item.HasDropDownItems
                && (item.Tag == Menus.KeepOpenTag || !item.Usable))
            {
                item.ClickInPlace();
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }
    }
}
