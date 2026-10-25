using System;
using System.Drawing;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// Asks before Kova installs, updates or removes the Kova Helper NVDA add-on on this PC (or, for AskRemote, on the
    /// remote PC). The explanation is a read-only text box with the focus, so NVDA reads all of it; Tab goes to the
    /// buttons, Escape cancels.
    /// </summary>
    internal sealed class NvdaHelperForm : Form
    {
        internal const string InstallText =
            "The Kova Helper is a small NVDA add-on. It lets the PC controlling this one get this PC's NVDA speech, braille, beeps and sounds straight away, as text and in its own voice.\r\n\r\n" +
            "It only talks to Kova on this PC, and it sends nothing over the internet by itself.\r\n\r\n" +
            "NVDA will restart to finish installing, so it goes quiet for a few seconds.\r\n\r\n" +
            "You can remove it any time, from NVDA's add-on store or with the same menu item in Kova.";

        internal const string UpdateText =
            "This Kova carries a newer Kova Helper NVDA add-on than the one NVDA has now (version {0}, newer one {1}).\r\n\r\n" +
            "It still only talks to Kova on this PC, and it sends nothing over the internet by itself.\r\n\r\n" +
            "NVDA will restart to finish updating, so it goes quiet for a few seconds.";

        internal const string RemoveText =
            "This removes the Kova Helper NVDA add-on from this PC.\r\n\r\n" +
            "A PC controlling this one then hears this PC's NVDA only through its sound.\r\n\r\n" +
            "NVDA will restart to finish removing it, so it goes quiet for a few seconds.";

        internal const string RemoteText =
            "This installs the Kova Helper NVDA add-on on the remote PC. It lets this PC get the remote PC's NVDA speech, braille, beeps and sounds straight away, as text and in your own voice.\r\n\r\n" +
            "It only talks to Kova on that PC, and it sends nothing over the internet by itself.\r\n\r\n" +
            "The remote PC's NVDA will restart to finish installing, so it goes quiet for a few seconds.\r\n\r\n" +
            "It can be removed any time, from NVDA's add-on store on that PC or from Kova's Settings menu there.";

        private NvdaHelperForm(string title, string text, string yes)
        {
            Text = title;
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(12) };
            var box = new TextBox
            {
                Multiline = true, ReadOnly = true, Text = text, Width = 520, Height = 190, ScrollBars = ScrollBars.Vertical,
                AccessibleName = title, TabIndex = 0, BackColor = SystemColors.Window,
            };
            panel.Controls.Add(box);
            var row = new FlowLayoutPanel { AutoSize = true };
            var ok = new Button { Text = yes, AutoSize = true, DialogResult = DialogResult.OK, TabIndex = 1 };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, TabIndex = 2 };
            row.Controls.Add(ok);
            row.Controls.Add(cancel);
            panel.Controls.Add(row);
            Controls.Add(panel);
            CancelButton = cancel;
            // No AcceptButton: Enter in the text box must not install by surprise; Tab to the button, then Space or Enter.
            Menus.FocusWhenShown(this, () => box);
            Shown += (_, _) => { box.SelectionStart = 0; box.SelectionLength = 0; };
        }

        /// <summary>
        /// Asks about the action the state offers (install, update or remove). Returns that action if the person agreed,
        /// else None. Call on the window's thread.
        /// </summary>
        public static NvdaHelperAction Ask(IWin32Window? owner, NvdaHelperState state)
        {
            var action = state.Offer;
            (string title, string text, string yes) = action switch
            {
                NvdaHelperAction.Install => ("Install the Kova Helper", InstallText, "&Install"),
                NvdaHelperAction.Update => ("Update the Kova Helper", string.Format(UpdateText, state.Installed, state.Carried), "&Update"),
                NvdaHelperAction.Remove => ("Remove the Kova Helper", RemoveText, "&Remove"),
                _ => ("", "", ""),
            };
            if (action == NvdaHelperAction.None) return NvdaHelperAction.None;
            using var f = new NvdaHelperForm(title, text, yes);
            return f.ShowDialog(owner) == DialogResult.OK ? action : NvdaHelperAction.None;
        }

        /// <summary>The controlling PC's File, Install the Kova Helper on the remote PC: true if the person agreed.</summary>
        public static bool AskRemote(IWin32Window? owner)
        {
            using var f = new NvdaHelperForm("Install the Kova Helper on the remote PC", RemoteText, "&Install");
            return f.ShowDialog(owner) == DialogResult.OK;
        }
    }
}
