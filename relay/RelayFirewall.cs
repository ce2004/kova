using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using TailRemote;

namespace KovaRelay
{
    /// <summary>
    /// Kova Relay's side of Kova's firewall code and port editor (src\Firewall.cs and src\PortEditorForm.cs, linked
    /// into this project). Its rules are "Kova Relay port N" and its exe is KovaRelay.exe, names Kova's rules ("Kova
    /// port N", kova.exe) never match, so neither app's clean-up can touch the other's rules.
    /// </summary>
    internal static class RelayFirewall
    {
        /// <summary>A test service's rules carry its name (KOVA_RELAY_SERVICE), so a test never touches a real rule.</summary>
        private static string Word => RelayService.Name == RelayService.DefaultName ? "" : " " + RelayService.Name;

        public static FirewallApp Profile => new()
        {
            Name = "Kova Relay",
            RuleName = port => "Kova Relay port " + port.ToString(CultureInfo.InvariantCulture) + Word,
            RulePrefix = "Kova Relay port ",
            RuleNamePattern = @"^Kova Relay port \d+( [A-Za-z0-9]+)?$",
            ExeNamePattern = @"^kovarelay(-(x64|arm64))?\.exe$",
            ProcessNames = new[] { "kovarelay" },
            OwnExe = Environment.ProcessPath!,
            ElevatedArgsPrefix = RelayService.Name == RelayService.DefaultName ? "" : "--name " + RelayService.Name + " ",
            ServiceExe = () => ServiceExe(RelayService.Name),
            // The port the relay (in the tray or as the service) listens on: the one in settings.json, as an
            // administrator saved it. The elevated half never closes it.
            PortInUse = () => new RelayFiles(RelayService.DataDir(RelayService.Name), RelayService.DataDir(RelayService.Name), RelayService.ExeFolder).LoadSettings().Port,
            PortProblem = PortProblem,
            ListFile = "kova-relay-firewall.txt",
        };

        public static string? PortProblem(string text) =>
            int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p >= 1 && p <= 65535 ? null : "The port must be a number from 1 to 65535.";

        /// <summary>The exe the relay service runs, from its ImagePath ("exe" --service NAME), or null when there is no service.</summary>
        public static string? ServiceExe(string name)
        {
            try
            {
                using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
                if (k?.GetValue("ImagePath") is not string image) return null;
                image = Environment.ExpandEnvironmentVariables(image.Trim());
                if (image.StartsWith('"')) { int end = image.IndexOf('"', 1); return end > 1 ? image[1..end] : null; }
                int space = image.IndexOf(' ');
                return space > 0 ? image[..space] : image;
            }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------- the ports this user's relay has used

        /// <summary>ports.txt in LocalAppData\KovaRelay: one port a line. Only a list for the port editor, nothing listens because of it.</summary>
        private static string PortsFile => Path.Combine(RelayService.TrayFolder(RelayService.Name), "ports.txt");
        private static string? _saveProblem;

        public static List<int> KnownPorts()
        {
            var ports = new List<int>();
            try
            {
                if (File.Exists(PortsFile))
                    foreach (string line in File.ReadAllLines(PortsFile))
                        if (int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p >= 1 && p <= 65535 && !ports.Contains(p)) ports.Add(p);
            }
            catch (Exception) { }
            return ports;
        }

        private static bool SavePorts(List<int> ports)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PortsFile)!);
                string tmp = PortsFile + ".tmp";
                File.WriteAllText(tmp, string.Concat(ports.Select(p => p.ToString(CultureInfo.InvariantCulture) + "\r\n")), new UTF8Encoding(false));
                File.Move(tmp, PortsFile, overwrite: true);
                _saveProblem = null;
                return true;
            }
            catch (Exception e)
            {
                _saveProblem = "Kova Relay could not save its list of ports (" + e.Message + ").";
                return false;
            }
        }

        public static bool Remember(int port)
        {
            var ports = KnownPorts();
            if (ports.Contains(port)) return true;
            ports.Add(port);
            return SavePorts(ports);
        }

        public static bool Forget(int port)
        {
            var ports = KnownPorts();
            return !ports.Remove(port) || SavePorts(ports);
        }

        public static PortBook Book(Action<string> note) => new()
        {
            Known = KnownPorts,
            Remember = Remember,
            Forget = Forget,
            SaveProblem = () => _saveProblem,
            Speak = (text, interrupt) => { note(text); Speak(text, interrupt); },
            ChangePortHint = "change the port in the relay window first",
        };

        /// <summary>
        /// Says something through the screen reader: a UI Automation notification from the focused control of the
        /// relay's active window, which NVDA reads out (the relay has no speech of its own, unlike Kova).
        /// </summary>
        public static void Speak(string text, bool interrupt = true)
        {
            // Tests only: KOVA_RELAY_SPEECH_LOG gets every line too.
            if (Environment.GetEnvironmentVariable("KOVA_RELAY_SPEECH_LOG") is { Length: > 0 } log)
                try { File.AppendAllText(log, text + Environment.NewLine); } catch (Exception) { }
            try
            {
                Control? from = Form.ActiveForm is { } f ? (f.ActiveControl ?? f) : null;
                from ??= Application.OpenForms.Cast<Form>().LastOrDefault(x => x.Visible);
                from?.AccessibilityObject.RaiseAutomationNotification(
                    System.Windows.Forms.Automation.AutomationNotificationKind.Other,
                    interrupt ? System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent
                              : System.Windows.Forms.Automation.AutomationNotificationProcessing.All,
                    text);
            }
            catch (Exception) { }
        }
    }
}
