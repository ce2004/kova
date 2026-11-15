using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace KovaRelay
{
    /// <summary>
    /// kovarelay: Kova Relay on Linux. The relay itself is the Windows relay's own code (RelayServer.cs, RelayWire.cs,
    /// RelayFiles.cs and SelfTest.cs in the folder above); this file is only what Linux needs instead of the tray
    /// and the Windows service: the command line, where the settings and the log are, and stopping on a signal.
    ///
    /// There is no window, no tray and no firewall handling. It runs in the foreground until SIGTERM (systemd's stop)
    /// or SIGINT (Control C), which is all systemd wants of a service (linux\kovarelay.service).
    ///
    /// The port, most specific first: --port, then KOVA_RELAY_PORT, then "Port" in /etc/kovarelay/settings.json (the
    /// same file the Windows relay keeps in ProgramData\KovaRelay), then 47200.
    ///
    /// The log goes to relay-log.txt in the data folder and is printed as well, so systemd's journal has it. The data
    /// folder: --data, then KOVA_RELAY_DATA, then the folder systemd made for the service (STATE_DIRECTORY), then
    /// /var/lib/kovarelay if this user may write there, then this user's own ~/.local/share/kovarelay.
    ///
    /// Exit codes: 0 stopped as asked, 1 could not start (the port is taken, for one), 2 a mistake in the command
    /// line or the settings file (the service file tells systemd not to keep restarting after a 2).
    /// </summary>
    internal static class Program
    {
        private const string SettingsFile = "/etc/kovarelay/settings.json";
        private const string SystemData = "/var/lib/kovarelay";

        private static int Main(string[] args)
        {
            int? portArg = null;
            string? dataArg = null, settingsArg = null;
            bool loopback = Environment.GetEnvironmentVariable("KOVA_RELAY_LOOPBACK") == "1";
            bool selfTest = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string? value = null;
                int eq = a.IndexOf('=');
                if (a.StartsWith("--", StringComparison.Ordinal) && eq > 0) { value = a[(eq + 1)..]; a = a[..eq]; }
                switch (a.ToLowerInvariant())
                {
                    case "--help" or "-h" or "-?":
                        Console.Out.Write(Help);
                        return 0;
                    case "--version":
                        Console.Out.WriteLine("Kova Relay " + Version);
                        return 0;
                    case "--selftest":
                        selfTest = true;
                        break;
                    case "--loopback":
                        loopback = true;
                        break;
                    case "--port":
                        value ??= ++i < args.Length ? args[i] : null;
                        if (PortOf(value) is not int p) return Mistake("--port needs a number from 1 to 65535 after it, for example --port 47200.");
                        portArg = p;
                        break;
                    case "--data":
                        value ??= ++i < args.Length ? args[i] : null;
                        if (string.IsNullOrWhiteSpace(value)) return Mistake("--data needs a folder after it, for example --data /var/lib/kovarelay.");
                        dataArg = value;
                        break;
                    case "--settings":
                        value ??= ++i < args.Length ? args[i] : null;
                        if (string.IsNullOrWhiteSpace(value)) return Mistake("--settings needs a file after it, for example --settings " + SettingsFile + ".");
                        settingsArg = value;
                        break;
                    default:
                        return Mistake($"Kova Relay does not know the option {args[i]}.");
                }
            }

            if (selfTest)
            {
                // The same self-test as KovaRelay.exe --selftest. Its result file is in /tmp; printed too, since a
                // Linux server is usually looked at through a terminal.
                int code = SelfTest.Run();
                try { Console.Out.Write(File.ReadAllText(SelfTest.ResultFile)); } catch (Exception) { }
                return code;
            }

            // ---- the port
            int port;
            string portFrom;
            string settings = settingsArg ?? SettingsFile;
            int? filePort = RelayFiles.ReadPort(settings, out string? settingsProblem);
            if (settingsArg is not null && !File.Exists(settingsArg)) settingsProblem = $"There is no file {settingsArg}.";
            string? envPort = Environment.GetEnvironmentVariable("KOVA_RELAY_PORT");
            if (portArg is int fromArg) { port = fromArg; portFrom = "given with --port"; }
            else if (!string.IsNullOrWhiteSpace(envPort))
            {
                if (PortOf(envPort) is not int fromEnv) return Mistake($"KOVA_RELAY_PORT is \"{envPort}\". It must be a number from 1 to 65535.");
                port = fromEnv; portFrom = "from KOVA_RELAY_PORT";
            }
            else if (settingsProblem is not null)
                // Carrying on with port 47200 would leave the hosts knocking on a port nobody listens on, with no
                // window here to show why. Better not to start, and to say what is wrong.
                return Mistake(settingsProblem);
            else if (filePort is int fromFile) { port = fromFile; portFrom = "from " + settings; }
            else { port = RelayFiles.DefaultPort; portFrom = "the usual port, since no other was set"; }

            // ---- the data folder, and the log in it
            string? data = DataFolder(dataArg, out string? dataProblem);
            if (dataProblem is not null) return Mistake(dataProblem, code: 1);

            // Printing is done by a thread of its own with a queue that never grows: a terminal that stops reading
            // (a pager, a stuck pipe) must not hold up the relay's log, and through it the relay.
            // systemd's journal stamps every line itself, so there the log's own date and time are left off.
            bool journal = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JOURNAL_STREAM"));
            using var toPrint = new BlockingCollection<string>(2000);
            var printer = new Thread(() =>
            {
                try
                {
                    foreach (string line in toPrint.GetConsumingEnumerable())
                        Console.Out.WriteLine(journal && line.Length > RelayLog.StampLength ? line[RelayLog.StampLength..] : line);
                }
                catch (Exception) { }   // nobody is reading any more: the file still has the log
            }) { IsBackground = true, Name = "Relay print" };
            printer.Start();

            var log = new RelayLog(data) { Echo = line => { try { toPrint.TryAdd(line); } catch (Exception) { } } };
            int Finish(int code)
            {
                log.Dispose();
                toPrint.CompleteAdding();
                printer.Join(3000);
                return code;
            }

            AppDomain.CurrentDomain.UnhandledException += (_, e) => { log.Write("The relay stopped because of an unexpected error: " + e.ExceptionObject); log.Flush(); };

            // Asked to stop: systemd sends SIGTERM, Control C sends SIGINT, Control \ sends SIGQUIT. All three mean
            // "close every connection, write the log, leave", never a half-written log or a core dump.
            using var stop = new ManualResetEventSlim(false);
            string stoppedBy = "";
            void OnSignal(PosixSignalContext c)
            {
                c.Cancel = true;   // this program ends itself, below, once everything is closed
                stoppedBy = c.Signal switch { PosixSignal.SIGTERM => "it was asked to stop", PosixSignal.SIGINT => "Control C was pressed", _ => "it was asked to quit" };
                stop.Set();
            }
            using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
            using var onInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
            using var onQuit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnSignal);

            log.Write($"Kova Relay {Version} is starting. Port {port} ({portFrom})."
                + (log.Path.Length > 0 ? $" The log is kept in {log.Path}." : " No folder for the log could be written, so the log is only printed."));
            if (settingsProblem is not null) log.Write("The settings file was not used: " + settingsProblem);

            var server = new RelayServer(log) { LoopbackOnly = loopback };
            string? why = server.Start(port);
            if (why is not null)
            {
                // The relay's own words, for whoever started it by hand; the log (and the journal) has them too.
                server.Dispose();
                log.Flush();
                Console.Error.WriteLine("Kova Relay could not start. " + ForLinux(why));
                return Finish(1);
            }

            stop.Wait();
            server.Dispose();
            log.Write($"Kova Relay closed, because {stoppedBy}.");
            return Finish(0);
        }

        /// <summary>"Choose another port." is what the Windows window can act on; here the port comes from the command line or a file.</summary>
        private static string ForLinux(string why) =>
            why.Replace("Choose another port.", "Stop the other program, or start the relay on another port with --port.");

        private static int? PortOf(string? text) =>
            text is not null && int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p >= 1 && p <= 65535 ? p : null;

        /// <summary>A mistake in how the relay was started: one sentence on the error stream, and how to see the options.</summary>
        private static int Mistake(string what, int code = 2)
        {
            Console.Error.WriteLine(what);
            if (code == 2) Console.Error.WriteLine("Run kovarelay --help to see how to start it.");
            return code;
        }

        /// <summary>
        /// The folder the log is kept in. One that was asked for (--data, KOVA_RELAY_DATA) must work, or the relay
        /// says why and does not start. Otherwise the first of the usual places that can be written; null when none
        /// can (the log is then only printed, and the relay runs all the same).
        /// </summary>
        private static string? DataFolder(string? asked, out string? problem)
        {
            problem = null;
            string? env = Environment.GetEnvironmentVariable("KOVA_RELAY_DATA");
            string? wanted = !string.IsNullOrWhiteSpace(asked) ? asked : !string.IsNullOrWhiteSpace(env) ? env : null;
            if (wanted is not null)
            {
                string full = Path.GetFullPath(wanted);
                if (CanWriteIn(full)) return full;
                problem = $"Kova Relay cannot write in {full}, the folder it was told to keep its log in. Give it a folder this user may write to.";
                return null;
            }
            // systemd makes this folder for the service and names it here (StateDirectory= in kovarelay.service).
            // Several folders would be separated by colons; the relay asks for one.
            string? state = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(state))
            {
                string first = state.Split(':')[0];
                if (first.Length > 0 && CanWriteIn(first)) return first;
            }
            if (CanWriteIn(SystemData)) return SystemData;
            // An ordinary user starting it by hand: ~/.local/share/kovarelay (or XDG_DATA_HOME, which .NET follows).
            // (DoNotVerify: asked the plain way, .NET answers with nothing when ~/.local/share does not exist yet,
            // which on a fresh server it does not.)
            string home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            if (home.Length > 0)
            {
                string own = Path.Combine(home, "kovarelay");
                if (CanWriteIn(own)) return own;
            }
            return null;
        }

        /// <summary>Whether a file can be made in the folder (making the folder first if it is not there).</summary>
        private static bool CanWriteIn(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".kovarelay-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                using (new FileStream(probe, FileMode.Create, FileAccess.Write)) { }
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
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

        private static string Help =>
            "Kova Relay " + Version + "\n" +
            "\n" +
            "Kova Relay pairs Kova hosts with the PCs that control them, so a PC behind a router can be reached without\n" +
            "opening a port on it. This is the relay for Linux. It has no window: it runs until you press Control C, or\n" +
            "until systemd stops it.\n" +
            "\n" +
            "Start it with: kovarelay\n" +
            "\n" +
            "Options:\n" +
            "  --port N         Listen on port N, for both TCP and UDP. Without this, the relay uses the port in the\n" +
            "                   KOVA_RELAY_PORT environment variable, then the port in " + SettingsFile + ",\n" +
            "                   then 47200.\n" +
            "  --settings FILE  Read the settings from FILE instead of " + SettingsFile + ".\n" +
            "                   The file looks like this: { \"Port\": 47200 }\n" +
            "  --data DIR       Keep the log, relay-log.txt, in DIR. Without this, the relay uses the folder in the\n" +
            "                   KOVA_RELAY_DATA environment variable, then the folder systemd made for it, then\n" +
            "                   " + SystemData + " if it may write there, then .local/share/kovarelay in your home folder.\n" +
            "  --loopback       Listen on 127.0.0.1 only, so nothing outside this computer can reach it. For testing.\n" +
            "  --selftest       Test the relay on this computer and print the result. It starts with ok or FAIL.\n" +
            "  --version        Print the version.\n" +
            "  --help           Print this.\n" +
            "\n" +
            "The relay listens on IPv4 and IPv6. Open its port in your firewall for both TCP and UDP.\n" +
            "Everything it writes to its log it also prints, so under systemd the journal has it: journalctl -u kovarelay\n" +
            "The log never contains passwords or anything that passes between the PCs.\n";
    }
}
