using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// The About menu: Check for updates, the changelog, the guide, keyboard shortcuts, about
    /// this copy, licences, the website and reporting a problem. Everything to read opens in
    /// ListViewerForm: one line per item, read with the arrows.
    /// </summary>
    internal static class AboutMenu
    {
        public const string Website = "https://github.com/ce2004/kova";

        public static ToolStripMenuItem Build(Form owner, MenuStrip bar, ToolStripMenuItem update)
        {
            var about = new MenuItem("&About");
            about.DropDownItems.AddRange(new ToolStripItem[]
            {
                update,
                Menus.Action("View the &changelog...", () => ShowChangelog(owner), Keys.F1 | Keys.Shift),
                Menus.Action("Read the &guide...", () => ShowGuide(owner), Keys.F1),
                Menus.Action("&Keyboard shortcuts...", () => Show(owner, "Keyboard shortcuts", Shortcuts(bar), l => !l.Contains(':'))),
                new ToolStripSeparator(),
                Menus.Action("&About Kova...", () => Show(owner, "About Kova", AboutLines(), _ => false)),
                Menus.Action("&Licences and credits...", () => Show(owner, "Licences and credits", Licences(), l => l.StartsWith("== "))),
                Menus.Action("Open the Kova &website", () => Open(Website)),
                Menus.Action("&Report a problem on GitHub", () => Open(Website + "/issues/new")),
                Menus.Action("Open the l&og file", () =>
                {
                    if (File.Exists(DiagLog.FilePath)) Open(DiagLog.FilePath);
                    else Speech.Speak("There is no log file yet. Turn on Settings, Enable logging first.");
                }),
            });
            return about;
        }

        private static void Show(Form owner, string title, IReadOnlyList<string> lines, Func<string, bool> heading)
        {
            using var f = new ListViewerForm(title, lines, heading);
            f.ShowDialog(owner);
        }

        public static string Resource(string name)
        {
            using var s = typeof(AboutMenu).Assembly.GetManifestResourceStream(name);
            if (s == null) return "";
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        /// <summary>Every change, a line each, under "Version 1.9.0" headings (Control Up and Down jump between them).</summary>
        private static void ShowChangelog(Form owner)
        {
            var lines = new List<string>();
            foreach (var raw in Resource("CHANGES.txt").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                lines.Add(Version.TryParse(line, out _) ? "Version " + line + (line == Updater.Current.ToString() ? ", this one" : "") : line);
            }
            Show(owner, "Kova changelog", lines, l => l.StartsWith("Version "));
        }

        /// <summary>The README, a line each; its section names are the headings.</summary>
        private static void ShowGuide(Form owner)
        {
            var lines = Resource("README.txt").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            Show(owner, "Kova guide", lines, IsGuideHeading);
        }

        private static bool IsGuideHeading(string l) => !l.StartsWith("-") && !(l.Length > 1 && char.IsDigit(l[0]) && l.Contains(". ")) && l.Length < 60 && !l.EndsWith(".");

        private static IReadOnlyList<string> AboutLines()
        {
            var lines = new List<string>
            {
                "Kova " + Updater.Current + " for " + (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "ARM64" : "x64") + " Windows",
                "Running from " + Environment.ProcessPath,
                "Settings are in " + Path.GetDirectoryName(Settings.FilePath),
                "Received files go to " + FileChannel.Downloads,
                "Log file: " + DiagLog.FilePath + (DiagLog.Enabled ? ", logging is on" : ", logging is off"),
                "The Windows service: " + (ServiceHost.InstalledVersion() is Version v ? "installed, version " + v : "not installed"),
                "Website: " + Website,
                ".NET " + Environment.Version,
            };
            return lines;
        }

        private static IReadOnlyList<string> Licences()
        {
            var lines = new List<string>();
            foreach (var (name, title) in new[]
            {
                ("Sounds-CREDITS.txt", "Where the sounds come from"),
                ("Concentus-LICENSE.txt", "Concentus (Opus sound)"),
                ("NVDA-controllerClient-LICENSE.txt", "NVDA controller client"),
            })
            {
                lines.Add("== " + title);
                lines.AddRange(Resource(name).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            }
            return lines;
        }

        /// <summary>The keys that always work, then every menu item with a key of its own, read from the menus themselves.</summary>
        private static IReadOnlyList<string> Shortcuts(MenuStrip bar)
        {
            var lines = new List<string>
            {
                "Anywhere in Kova",
                "Open the menus: Alt, then Right and Left Arrow between them, Down Arrow through one, Escape to go back",
                "Turn a setting on or off: Enter, and the menu stays open",
                "Control the remote PC, or come back to this one: Control Shift Enter",
                "Control Alt Delete on the remote PC (when it runs the service): Control Alt End",
                "Connect to saved PC 1 to 9: Control 1 to Control 9",
                "Menu items with their own keys",
            };
            void Walk(ToolStripItemCollection items)
            {
                foreach (ToolStripItem i in items)
                {
                    if (i is not ToolStripMenuItem m || !m.Available) continue; // only what this mode has
                    if (m.ShortcutKeys != Keys.None)
                        lines.Add(m.Text!.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").TrimEnd('.') + ": " + KeyWords(m.ShortcutKeys));
                    Walk(m.DropDownItems);
                }
            }
            Walk(bar.Items);
            return lines;
        }

        private static string KeyWords(Keys k)
        {
            var parts = new List<string>();
            if ((k & Keys.Control) != 0) parts.Add("Control");
            if ((k & Keys.Shift) != 0) parts.Add("Shift");
            if ((k & Keys.Alt) != 0) parts.Add("Alt");
            parts.Add((k & Keys.KeyCode).ToString());
            return string.Join(" ", parts);
        }

        private static void Open(string target)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
            catch (Exception e) { Speech.Speak("Could not open it: " + e.Message); }
        }
    }

    /// <summary>
    /// Something to read, one line per item: the arrows go through it, Control Down and Control Up
    /// jump to the next or last heading (a version, a section), Control C copies a line.
    /// </summary>
    internal sealed class ListViewerForm : Form
    {
        private readonly ListBox _lines = new() { Width = 720, Height = 420, HorizontalScrollbar = true };

        public ListViewerForm(string title, IReadOnlyList<string> lines, Func<string, bool> heading)
        {
            Text = title;
            Font = new System.Drawing.Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimizeBox = MaximizeBox = false;
            _lines.AccessibleName = title;
            foreach (var l in lines) _lines.Items.Add(l);
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(10), WrapContents = false };
            flow.Controls.Add(_lines);
            // Tab goes between the lines and OK, nothing else; Control C copies a line.
            var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
            flow.Controls.Add(ok);
            Controls.Add(flow);
            AcceptButton = ok;
            CancelButton = ok;
            _lines.KeyDown += (_, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C) { e.Handled = e.SuppressKeyPress = true; Copy(); }
                else if (e.Control && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up))
                {
                    e.Handled = e.SuppressKeyPress = true;
                    int step = e.KeyCode == Keys.Down ? 1 : -1;
                    for (int i = _lines.SelectedIndex + step; i >= 0 && i < _lines.Items.Count; i += step)
                        if (heading((string)_lines.Items[i])) { _lines.SelectedIndex = i; return; }
                    Speech.Speak(step > 0 ? "No more headings." : "This is the first heading.");
                }
            };
            if (_lines.Items.Count > 0) _lines.SelectedIndex = 0;
            Menus.FocusWhenShown(this, () => _lines);
        }

        private void Copy()
        {
            if (_lines.SelectedItem is not string line) return;
            // The clipboard needs a thread of its own (the window's is not the kind it accepts).
            var t = new System.Threading.Thread(() =>
            {
                try { Clipboard.SetText(line); Speech.Speak("Copied."); }
                catch { Speech.Speak("Could not copy: another program has the clipboard open."); }
            }) { IsBackground = true };
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
        }
    }
}
