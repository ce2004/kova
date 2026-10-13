using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// Sounds: which sound each event makes. One list per event: Default (the
    /// event's own tune on the piano, always the same), Random sound, None,
    /// then every sound (Sounds.All). Moving through a list plays each as you reach it.
    /// </summary>
    internal sealed class SoundsForm : Form
    {
        private readonly Settings _settings;
        private readonly Dictionary<Sounds.Tone, ComboBox> _lists = new();
        private readonly ComboBox _key = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, AccessibleName = "Key" };
        private readonly int _keyBefore;
        private bool _ready;
        private bool _saved; // OK was pressed and the settings (and key) were saved

        public SoundsForm(Settings settings)
        {
            _settings = settings;
            _keyBefore = Math.Clamp(settings.SoundKey, 0, 11);
            Text = "Sounds";
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12) };
            // The key every pattern plays in.
            _key.Items.AddRange(Sounds.Keys);
            _key.SelectedIndex = Math.Clamp(settings.SoundKey, 0, 11);
            _key.SelectedIndexChanged += (_, _) =>
            {
                if (!_ready) return;
                Sounds.Key = _key.SelectedIndex;
                Sounds.PlayNamed(Sounds.Default(Sounds.Tone.Connected)); // hear the new key
            };
            table.Controls.Add(new Label { Text = "&Key", AutoSize = true, Anchor = AnchorStyles.Left });
            table.Controls.Add(_key);
            foreach (Sounds.Tone t in Enum.GetValues<Sounds.Tone>())
            {
                var label = new Label { Text = Sounds.Label(t), AutoSize = true, Anchor = AnchorStyles.Left };
                var list = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, AccessibleName = Sounds.Label(t).Replace("&", "") };
                // Default first (this event's own tune on the piano, always the same), then
                // Random sound (anything, each time), then None and every sound.
                list.Items.Add(Sounds.DefaultName);
                list.Items.Add(Sounds.RandomName);
                foreach (string name in Sounds.All) list.Items.Add(name);
                int at = settings.SoundChoices.TryGetValue(t.ToString(), out var c) ? list.Items.IndexOf(c) : -1;
                list.SelectedIndex = at < 0 ? 0 : at;
                // Hear each sound as you reach it.
                var tone = t;
                list.SelectedIndexChanged += (_, _) =>
                {
                    if (_ready && list.SelectedItem is string s) Sounds.PlayNamed(Sounds.Resolve(tone, s));
                };
                table.Controls.Add(label);
                table.Controls.Add(list);
                _lists[t] = list;
            }

            var buttons = new FlowLayoutPanel { AutoSize = true };
            var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += (_, _) => Save();
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            table.Controls.Add(buttons);
            table.SetColumnSpan(buttons, 2);
            Controls.Add(table);
            Menus.FocusWhenShown(this, () => _key);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _ready = true;
        }

        /// <summary>
        /// Changing the selected key plays a live preview by setting Sounds.Key at once. Closing
        /// without saving - Cancel, the title bar X, Alt+F4 - must put it back, so the static
        /// Sounds.Key stays in sync with the actually-saved setting for the rest of the session.
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (!_saved) Sounds.Key = _keyBefore;
        }

        private void Save()
        {
            _settings.SoundKey = _key.SelectedIndex;
            Sounds.Key = _key.SelectedIndex;
            _settings.SoundChoices = new Dictionary<string, string>();
            foreach (var (t, list) in _lists)
                if (list.SelectedItem is string s && s != Sounds.DefaultName) _settings.SoundChoices[t.ToString()] = s;
            bool saved;
            try { saved = _settings.Save(retries: 1); } catch { saved = false; }
            // In use until Kova closes either way; said, so it is not a surprise when they are gone next time.
            if (!saved) Speech.Speak((_settings.SaveProblem ?? "Kova could not save its settings.") + " The sounds you chose are only in use until Kova closes.", interrupt: false);
            _saved = true;
        }
    }
}
