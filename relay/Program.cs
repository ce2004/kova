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
            // The service itself, and the elevated helpers that install and change it.
            if (RelayService.HandleSwitch(args) is int code) return code;

            string folder = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            // One relay per exe folder: the folder's files (port.txt, hosts.txt) belong to one copy.
            string tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.TrimEnd('\\').ToLowerInvariant())))[..16];
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

            var files = new RelayFiles(folder);
            using var log = new RelayLog(folder);
            string lastWindowError = "";
            long lastWindowErrorAt = 0;
            Application.ThreadException += (_, e) =>
            {
                // The same error over and over (a paint or accessibility loop) is written once a minute, not hundreds of times.
                string what = e.Exception.ToString();
                long now = Environment.TickCount64;
                if (what == lastWindowError && now - lastWindowErrorAt < 60_000) return;
                lastWindowError = what; lastWindowErrorAt = now;
                log.Write("Something unexpected went wrong in the window: " + what.Replace(Environment.NewLine, " | "));
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
            Server = new RelayServer(files, log);
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

        public void Begin()
        {
            if (ServiceMode)
            {
                Log.Write($"Kova Relay {Program.Version} opened its tray icon; the relay itself runs as the service {ServiceName}.");
                UpdateTip();
                _tipTimer.Start();
                return;
            }
            Log.Write($"Kova Relay {Program.Version} is starting from {Files.Folder}.");
            int port = Files.ReadPort(out bool made);
            string? why = Server.Start(port);
            UpdateTip();
            _tipTimer.Start();
            if (why is not null)
                _tray.Notify($"Kova Relay could not start. {why} Open the relay from its tray icon to choose another port.", warning: true);
            else if (made)
                _tray.Notify($"Kova Relay made port.txt with port {port}. Change it there or in the relay's window.");
            else if (Files.Problem is not null)
                _tray.Notify("Kova Relay is running, but it cannot save files in its folder, so its settings last only until it closes.", warning: true);
        }

        /// <summary>What the window and tooltip show: the relay in this process, or the service.</summary>
        public sealed record View(RelaySnapshot Snap, bool AllowNew, string Text, bool Service);

        public View Current(string? note)
        {
            if (!ServiceMode)
            {
                RelaySnapshot s = Server.Snapshot();
                return new View(s, Server.AllowNewNames, OptionsForm.StatusText(s, Files.Problem, note), false);
            }
            int state = RelayService.State(ServiceName);
            var status = RelayService.ReadStatus(ServiceName);
            string where = $"Its host list, settings and log are kept in {RelayService.DataDir(ServiceName)}, because the service's own account cannot write the relay's folder.";
            var parts = new System.Collections.Generic.List<string>();
            RelaySnapshot snap;
            bool allow = true;
            if (state == RelayService.Running && status is { } st)
            {
                snap = st.Snap;
                allow = st.AllowNew;
                if (snap.Running)
                {
                    parts.Add($"Running as a Windows service on port {snap.Port}.");
                    parts.Add(OptionsForm.CountsText(snap));
                }
                else parts.Add("The relay service is running, but it is not relaying. " + (snap.Problem ?? ""));
                if (st.FilesProblem is not null) parts.Add(st.FilesProblem);
            }
            else
            {
                snap = new RelaySnapshot(state == RelayService.Running || state == RelayService.StartPending, Files.ReadPortQuietly(), null, 0, 0, 0, Array.Empty<HostRow>());
                parts.Add(state switch
                {
                    RelayService.Running or RelayService.StartPending => "The relay service is starting.",
                    RelayService.StopPending => "The relay service is stopping.",
                    _ => "The relay service is stopped. Press Start the relay to start it.",
                });
            }
            parts.Add(where);
            if (note is not null) parts.Add(note);
            return new View(snap, allow, string.Join(" ", parts), true);
        }

        public void UpdateTip()
        {
            View v = Current(null);
            RelaySnapshot s = v.Snap;
            string state = !s.Running ? "stopped"
                : $"port {s.Port}, " + (s.Hosts switch { 0 => "no hosts", 1 => "1 host", _ => $"{s.Hosts} hosts" });
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
