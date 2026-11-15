using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// Kova.exe --lookshot FOLDER: pictures of Kova's own windows, saved as PNG files, for checking how they look
    /// (4.11). The owner cannot see them and whoever works on this may be a program, so the look is checked from
    /// pictures. Nothing is put on the screen and nothing takes the keyboard: each window is made off screen, shown
    /// there without being made the active window (so a screen reader never hears of it), drawn into a picture and
    /// closed. It uses settings of its own, connects to nothing and hosts nothing.
    /// </summary>
    internal static class LookShot
    {
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int how);
        private const int ShowNoActivate = 4;

        public static int Run(string folder, bool? dark = null)
        {
            Look.TestDark = dark;
            int made = 0;
            Exception? failed = null;
            var thread = new Thread(() =>
            {
                string settingsDir = Path.Combine(Path.GetTempPath(), "kova-lookshot-" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    Directory.CreateDirectory(folder);
                    Directory.CreateDirectory(settingsDir);
                    Settings.TestDir = settingsDir;
                    Application.EnableVisualStyles();

                    // The real main window, as a PC that has never connected to anything opens it.
                    using (var main = new MainForm(false, false, false)) made += Shoot(main, Path.Combine(folder, "main.png"), shown: false);

                    // A question window as the wizards make them: the page's words, boxes to type in, choices, the buttons.
                    var address = new TextBox { Width = 320, Text = "laptop.example.net", AccessibleName = "Address" };
                    var password = new TextBox { Width = 320, UseSystemPasswordChar = true, Text = "secret password", AccessibleName = "Password" };
                    var direct = new RadioButton { Text = "&Directly, by its address", AutoSize = true, Checked = true };
                    var relay = new RadioButton { Text = "&Through a Kova Relay", AutoSize = true };
                    var keep = new CheckBox { Text = "&Save this PC for next time", AutoSize = true, Checked = true };
                    var pages = new List<WizardForm.Page>
                    {
                        new()
                        {
                            Question = "Which PC do you want to control?",
                            Controls = new Control[]
                            {
                                new Label { Text = "Its &address, as the other PC shows it:", AutoSize = true }, address,
                                new Label { Text = "Its &password:", AutoSize = true }, password, direct, relay, keep,
                            },
                        },
                    };
                    using (var wizard = new WizardForm("Connect to a PC", pages)) made += Shoot(wizard, Path.Combine(folder, "wizard.png"), shown: true);

                    using (var list = new ListViewerForm("What is new", new[]
                           {
                               "Version 4.11.0", "New: Kova has a home screen.", "Changed: the microphone is gone.", "Version 4.10.0",
                               "New: a copy can hide to the notification area.",
                           }, line => line.StartsWith("Version ", StringComparison.Ordinal)))
                        made += Shoot(list, Path.Combine(folder, "list.png"), shown: true);

                    using (var text = new TextForm("Listen-only password", "A second password that lets someone hear this PC but not control it. Leave it empty for none:", "", secret: true))
                        made += Shoot(text, Path.Combine(folder, "text.png"), shown: true);
                }
                catch (Exception ex) { failed = ex; }
                finally
                {
                    Settings.TestDir = null;
                    try { Directory.Delete(settingsDir, true); } catch { }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            try { File.WriteAllText(Path.Combine(folder, "lookshot.txt"), failed == null ? "ok, " + made + " pictures" : "FAIL: " + failed); } catch { }
            return failed == null ? 0 : 1;
        }

        /// <param name="shown">
        /// Also tell the window it has been shown (a wizard lays its first page out then). Never for the main window:
        /// being shown is when it would start hosting or connecting.
        /// </param>
        private static int Shoot(Form form, string file, bool shown)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.ShowInTaskbar = false;
            _ = form.Handle; // made now, still hidden
            // What Windows Forms does as a window first shows, by hand: showing it the usual way would make it the
            // active window, and a screen reader would start reading it.
            Call(form, "OnLoad");
            if (shown) Call(form, "OnShown");
            ShowWindow(form.Handle, ShowNoActivate);
            form.PerformLayout();
            Application.DoEvents();
            using var picture = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height), PixelFormat.Format24bppRgb);
            form.DrawToBitmap(picture, new Rectangle(0, 0, picture.Width, picture.Height));
            picture.Save(file, ImageFormat.Png);
            ShowWindow(form.Handle, 0);
            return 1;
        }

        private static void Call(Form form, string method) =>
            typeof(Form).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(EventArgs) }, null)!
                .Invoke(form, new object[] { EventArgs.Empty });
    }
}
