using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KovaRelay
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                return SelfTest.Run();
            // A test copy's elevated half: its service name, handed over (an elevated copy does not get this one's environment).
            if (args.Length >= 2 && args[0] == "--name" && RelayService.NameOk(args[1]))
            {
                Environment.SetEnvironmentVariable("KOVA_RELAY_SERVICE", args[1]);
                args = args[2..];
            }
            TailRemote.Firewall.App = RelayFirewall.Profile;
            // The elevated half of the port editor (Kova's Firewall.cs, with the relay's own rule names).
            if (args.Length >= 1 && args[0] == "--firewall")
                return TailRemote.Firewall.Command(args[1..]);
            // The service itself, and the elevated helpers that install and change it.
            if (RelayService.HandleSwitch(args) is int code) return code;

            string folder = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            string name = RelayService.Name;
            // One relay per service name (and so per settings.json), wherever its exe is: two copies would want one port.
            string tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..16];
            using var mutex = new Mutex(true, @"Local\KovaRelay-" + tag, out bool first);
            if (!first)
            {
                // Already running: ask that copy to open its window, and let it come to the front.
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(@"Local\KovaRelay-open-" + tag);
                    AllowSetForegroundWindow(-1);
                    ev.Set();
                }
                catch (Exception) { }
                return 0;
            }
            using var openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\KovaRelay-open-" + tag);

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // The log of a relay in the tray: this user's LocalAppData\KovaRelay. Nothing beside the exe.
            string own = RelayService.TrayFolder(name);
            try { Directory.CreateDirectory(own); } catch (Exception) { }
            RemoveOldFiles(folder, own);
            var files = new RelayFiles(RelayService.DataDir(name), folder);
            using var log = new RelayLog(own);
            string lastWindowError = "";
            long lastWindowErrorAt = 0;
            Application.ThreadException += (_, e) =>
            {
                // The same error over and over (a paint or accessibility loop) is written once a minute, not hundreds of times.
                string what = e.Exception.ToString();
                long now = Environment.TickCount64;
                if (what == lastWindowError && now - lastWindowErrorAt < 60_000) return;
                lastWindowError = what; lastWindowErrorAt = now;
                log.Write("An unexpected error occurred in the relay window: " + what.Replace(Environment.NewLine, " | "));
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => { log.Write("The relay stopped because of an unexpected error: " + e.ExceptionObject); log.Flush(); };

            using var app = new TrayApp(files, log);
            // Test switch: KOVA_RELAY_LOOPBACK=1 listens on 127.0.0.1 only (no firewall prompt, nothing reachable from outside).
            app.Server.LoopbackOnly = Environment.GetEnvironmentVariable("KOVA_RELAY_LOOPBACK") == "1";
            app.Begin();
            ThreadPool.RegisterWaitForSingleObject(openEvent, (_, _) => app.OpenWindowFromAnyThread(), null, -1, false);
            Application.Run(app);
            return 0;
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        /// <summary>
        /// Older versions kept a host name list (hosts.txt) beside the exe or in this user's folder, and 4.4.0 kept its log
        /// beside the exe. The relay no longer uses a host list: whatever can be deleted without administrator rights
        /// goes, quietly. Nothing here can fail the start.
        /// </summary>
        private static void RemoveOldFiles(string exeFolder, string own)
        {
            try { File.Delete(Path.Combine(own, "hosts.txt")); } catch (Exception) { }
            try { File.Delete(Path.Combine(own, "hosts.txt.tmp")); } catch (Exception) { }
            if (string.Equals(Path.GetFullPath(exeFolder).TrimEnd('\\'), Path.GetFullPath(own).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            foreach (string f in new[] { "hosts.txt", "relay-log.txt", "relay-log.old.txt" })
                try { File.Delete(Path.Combine(exeFolder, f)); } catch (Exception) { }
        }

        /// <summary>Whether 4.4.0 left anything beside the exe that only an administrator can move or delete.</summary>
        internal static bool OldFilesLeft(RelayFiles files)
        {
            if (files.HasOldSettings) return true;
            if (files.OldFolder is not string dir) return false;
            foreach (string f in new[] { "hosts.txt", "relay-log.txt", "relay-log.old.txt" })
                if (File.Exists(Path.Combine(dir, f))) return true;
            return false;
        }

        public static string Version
        {
            get
            {
                string? v = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (v is null) return typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
                int plus = v.IndexOf('+');
                return plus >= 0 ? v[..plus] : v;
            }
        }
    }

    /// <summary>The tray side: the icon, its tooltip, the options window, and starting and stopping the relay.</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        public RelayFiles Files { get; }
        public RelayLog Log { get; }
        public RelayServer Server { get; }
        public Icon AppIcon { get; }

        private readonly TrayIcon _tray;
        private readonly SynchronizationContext _ui;
        private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = 2000 };
        private OptionsForm? _form;
        private bool _exiting;

        public TrayApp(RelayFiles files, RelayLog log)
        {
            Files = files;
            Log = log;
            Server = new RelayServer(log);
            AppIcon = LoadIcon();
            // A WinForms control first, so this thread has the WinForms synchronization context.
            _form = new OptionsForm(this);
            _ = _form.Handle;
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _tray = new TrayIcon(AppIcon);
            _tray.OpenRequested += OpenWindow;
            _tray.ExitRequested += ExitAll;
            _tipTimer.Tick += (_, _) => UpdateTip();
        }

        private static Icon LoadIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
            catch (Exception) { return SystemIcons.Application; }
        }

        /// <summary>The Windows service this tray belongs to (KovaRelay; KOVA_RELAY_SERVICE for tests).</summary>
        public string ServiceName { get; } = RelayService.Name;

        /// <summary>When the service is installed, the tray shows and controls it instead of running a relay of its own.</summary>
        public bool ServiceMode => RelayService.IsInstalled(ServiceName);

        /// <summary>A word for the status box about moving 4.4.0's files (null once there is nothing to say).</summary>
        public string? MoveNote { get; private set; }

        public void Begin()
        {
            if (ServiceMode)
            {
                Log.Write($"Kova Relay {Program.Version} opened its tray icon. The relay itself runs as the Windows service {ServiceName}.");
                UpdateTip();
                _tipTimer.Start();
            }
            else
            {
                Log.Write($"Kova Relay {Program.Version} is starting from {RelayService.ExeFolder}.");
                int port = Files.LoadSettings().Port;
                string? why = Server.Start(port);
                UpdateTip();
                _tipTimer.Start();
                if (why is not null)
                    _tray.Notify($"Kova Relay could not start. {why} Open the relay window from its tray icon to choose another port.", warning: true);
            }
            if (Program.OldFilesLeft(Files)) _ = MoveOldSettingsAsync();
        }

        /// <summary>
        /// 4.4.0's port.txt (and relay-settings.txt) is moved into settings.json once and deleted, by an elevated copy:
        /// only administrators may write the relay's settings. If permission is not given, the relay keeps using what
        /// they say and asks again the next time it starts.
        /// </summary>
        private async System.Threading.Tasks.Task MoveOldSettingsAsync()
        {
            RelaySettings before = Files.LoadSettings();
            var r = await System.Threading.Tasks.Task.Run(() => RelayService.RunElevated($"--settings-migrate {ServiceName}"));
            if (r == RelayService.Result.Ok)
            {
                MoveNote = null;
                Log.Write($"Moved the port ({before.Port}) from 4.4.0's files into {Files.SettingsFile}, and deleted those files.");
                _tray.Notify($"Kova Relay moved its port setting, {before.Port}, from port.txt into its settings file. Change the port in the relay window from now on.");
            }
            else
            {
                MoveNote = $"Kova Relay still uses port {before.Port} from port.txt, the file older versions kept beside the relay. Moving it into the relay's settings file needs administrator permission, which was not given. The relay asks again the next time it starts.";
                Log.Write("Could not move 4.4.0's port.txt into settings.json: " + RelayService.Say(r, ""));
            }
        }

        /// <summary>What the window and tooltip show: the relay in this process, or the service.</summary>
        public sealed record View(RelaySnapshot Snap, string Text, bool Service);

        public View Current(string? note)
        {
            if (!ServiceMode)
            {
                RelaySnapshot s = Server.Snapshot();
                return new View(s, OptionsForm.StatusText(s, MoveNote is null ? note : note is null ? MoveNote : MoveNote + " " + note), false);
            }
            int state = RelayService.State(ServiceName);
            RelaySnapshot? status = RelayService.ReadStatus(ServiceName);
            string where = $"Its settings and log are kept in {RelayService.DataDir(ServiceName)}.";
            var parts = new System.Collections.Generic.List<string>();
            RelaySnapshot snap;
            if (state == RelayService.Running && status is not null)
            {
                snap = status;
                if (snap.Running)
                {
                    parts.Add($"The relay is running as a Windows service on port {snap.Port}.");
                    parts.Add(OptionsForm.CountsText(snap));
                }
                else parts.Add("The relay service is running, but it is not accepting connections. " + (snap.Problem ?? ""));
            }
            else
            {
                snap = new RelaySnapshot(state == RelayService.Running || state == RelayService.StartPending, Files.LoadSettings().Port, null, 0, 0, 0, Array.Empty<HostRow>());
                parts.Add(state switch
                {
                    RelayService.Running or RelayService.StartPending => "The relay service is starting.",
                    RelayService.StopPending => "The relay service is stopping.",
                    _ => "The relay service is stopped. Press Start the relay to start it.",
                });
            }
            parts.Add(where);
            if (MoveNote is not null) parts.Add(MoveNote);
            if (note is not null) parts.Add(note);
            return new View(snap, string.Join(" ", parts), true);
        }

        public void UpdateTip()
        {
            View v = Current(null);
            RelaySnapshot s = v.Snap;
            string state = !s.Running ? "stopped"
                : $"port {s.Port}, " + (s.Hosts switch { 0 => "no hosts connected", 1 => "1 host connected", _ => $"{s.Hosts} hosts connected" });
            if (v.Service) state = (s.Running ? "service, " : "service ") + state;
            // Windows 11 already puts the program's name in front of a tray tooltip (measured: UIA and NVDA read
            // "Kova Relay Kova Relay, port 47200" otherwise); Windows 10 shows the tip as it is.
            _tray.Tip = Environment.OSVersion.Version.Build >= 22000 ? state : "Kova Relay, " + state;
        }

        public void OpenWindowFromAnyThread() => _ui.Post(_ => OpenWindow(), null);

        public void OpenWindow()
        {
            if (_exiting) return;
            if (_form is null || _form.IsDisposed) _form = new OptionsForm(this);
            _form.ShowAndFocus();
        }

        public void ExitAll()
        {
            if (_exiting) return;
            _exiting = true;
            _tipTimer.Stop();
            Server.Dispose();
            Log.Write("Kova Relay closed.");
            if (_form is not null) { _form.ReallyClose = true; _form.Close(); _form.Dispose(); }
            _tray.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_exiting) { _exiting = true; Server.Dispose(); _tray.Dispose(); }
                _tipTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
