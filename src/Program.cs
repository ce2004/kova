using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

// DLLs Kova names without a path come from System32 only (4.4): never from beside the exe, where a program
// without administrator rights may have put one (a Downloads folder), and Kova sometimes runs as administrator
// or as the service. Kova's own native library (NVDA's) is loaded by its full path after a fingerprint check.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace TailRemote
{
    internal static class Program
    {
        /// <summary>Writes an unexpected error to TailRemote-crash.txt next to the exe (and the log, if on).</summary>
        internal static void Crash(Exception? e) => CrashNote(e?.ToString() ?? "unknown error");

        /// <summary>Writes a line to TailRemote-crash.txt next to the exe (and the log, if on).</summary>
        internal static void CrashNote(string what)
        {
            try
            {
                string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  Kova " + Updater.Current + ": " + what + Environment.NewLine;
                // Next to Kova; where that can't be written (Program Files without administrator), in
                // AppData\Local\Kova like the log, rather than nowhere.
                if (!CrashAppend(AppContext.BaseDirectory, text)) CrashAppend(Names.LocalDir, text);
                DiagLog.Write("problem: " + what);
                // At once, with every line before it: a crash can end the process before the next timed write.
                DiagLog.Flush();
            }
            catch { }
        }

        private static bool CrashAppend(string dir, string text)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "Kova-crash.txt"), old = Path.Combine(dir, "Kova-crash.old.txt");
                // Kept to about a megabyte (the one before it becomes Kova-crash.old.txt): a problem that keeps
                // happening for months must never fill the drive.
                var file = new FileInfo(path);
                if (file.Exists && file.Length > 1 << 20)
                    try
                    {
                        // A read-only old file refused to be replaced, so the big one grew for ever.
                        if (File.Exists(old)) File.SetAttributes(old, File.GetAttributes(old) & ~FileAttributes.ReadOnly);
                        File.Move(path, old, overwrite: true);
                    }
                    catch { } // the old one locked: this line is still written, to the big file
                File.AppendAllText(path, text);
                return true;
            }
            catch { return false; }
        }

        // Main is not [STAThread] (a stray one sat on Crash above for a long time, doing nothing): the
        // window and the audio have always run this way, and the sound code shares Windows audio
        // objects between threads. Anything that needs STA (the clipboard, file and folder pickers)
        // runs on a thread of its own that is: MainForm.ClipboardJobs, PickFiles, Agent.ClipboardSetter.
        /// <summary>
        /// --capturetest FILE: records Windows' default output for 10 seconds and writes what it heard
        /// (which session and account it ran as, how much sound, the loudest moment). For finding out
        /// whether the service itself, in session 0 before anyone signs in, can capture the sound.
        /// </summary>
        private static int CaptureTest(string file)
        {
            var lines = new System.Collections.Generic.List<string>();
            int ticks = 0, sound = 0, peak = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long firstSound = -1;
            var cap = new LoopbackCapture((seq, pcm) =>
            {
                ticks++;
                if (pcm == null) return;
                sound++;
                foreach (short s in pcm) peak = Math.Max(peak, Math.Abs((int)s));
                if (firstSound < 0) firstSound = clock.ElapsedMilliseconds;
            }, msg => { lock (lines) lines.Add(msg); }, null, 0);
            System.Threading.Thread.Sleep(10_000);
            cap.Stop();
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            var report = "session " + System.Diagnostics.Process.GetCurrentProcess().SessionId + ", account " + me.Name +
                Environment.NewLine + "device: " + LoopbackCapture.DeviceInfo +
                Environment.NewLine + "ticks " + ticks + ", with sound " + sound + ", loudest " + peak + " of 32767" +
                (firstSound >= 0 ? ", first sound after " + firstSound + " ms" : "") +
                Environment.NewLine + "messages: " + string.Join(" | ", lines);
            File.WriteAllText(file, report);
            return 0;
        }

        /// <summary>A command-line word: true for yes (on, open), false for no (off, close), in any case; null for anything else.</summary>
        private static bool? OnOff(string word, string yes, string no) =>
            word.Equals(yes, StringComparison.OrdinalIgnoreCase) ? true : word.Equals(no, StringComparison.OrdinalIgnoreCase) ? false : null;

        private static int Main(string[] args)
        {
            // A test copy's elevated half (Names.InstanceArgs): taken before anything reads a name.
            if (args.Length >= 2 && args[0] == "--instance")
            {
                Environment.SetEnvironmentVariable("KOVA_INSTANCE", args[1]);
                args = args[2..];
            }
            // The service counts its start before anything else can go wrong: a new version that failed
            // before it got as far as the service itself was never counted, so it was never rolled back.
            if (args.Length == 1 && args[0] == "--service") ServiceHost.CountStart();
            // Never a crash box: on a PC reached only through TailRemote, Windows' "has stopped working"
            // or System Error box grabbed the keyboard until someone closed it. A crash now just ends
            // the process, and the service or Windows starts it again.
            SetErrorMode(0x0001 | 0x0002 | 0x8000); // SEM_FAILCRITICALERRORS, NOGPFAULTERRORBOX, NOOPENFILEERRORBOX
            // And for anything Windows itself loads later by name (4.4, as above): System32 only, never the exe's folder.
            try { SetDefaultDllDirectories(0x00000800); } catch { } // LOAD_LIBRARY_SEARCH_SYSTEM32
            // A test copy's settings switch, in a copy running as administrator or SYSTEM, which must not honour it
            // (Names.TestSwitch): it stops here, rather than quietly use (and save over) the real settings.
            if (Environment.GetEnvironmentVariable("KOVA_SETTINGS_DIR") is { Length: > 0 } && Names.TestSwitch("KOVA_SETTINGS_DIR") == null
                && !(args.Length >= 1 && args[0] is "--securitytest" or "--selftest" or "--chaostest" or "--testhost" or "--soaktest" or "--testcontrol"))
                return 3;
            try { WerSetFlags(0x20); } catch { } // WER_FAULT_REPORTING_NO_UI
            Native.FullSpeed(); // never on power-saving cores: that makes the sound run dry
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                Updater.CleanLeftovers(); // old copies from earlier updates, once nothing holds them
                Updater.CleanUnpackedCopies(); // and the libraries each build unpacked to the temp folder
            });
            // Nothing may ever close TailRemote by surprise: a problem in the window is
            // written down and the app carries on.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Crash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
            // Anything but on or off (or open or close below) is refused: a mistyped word used to mean
            // off, and quietly removed the sign-in task or closed the port.
            // --startup on DOMAIN\user: the account signed in where it was asked, which may not be the
            // administrator whose password was typed.
            // A wrong number of words is refused too: it used to fall through and open the whole Kova
            // window, as administrator, in the elevated copy.
            if (args.Length >= 1 && args[0] == "--startup")
                return (args.Length == 2 || args.Length == 3) && OnOff(args[1], "on", "off") is bool startupOn ? Startup.Apply(startupOn, args.Length == 3 ? args[2] : null) : 2;

            if (args.Length == 1 && args[0] == "--setup-audio")
            {
                Speech.Init();
                SetupForm? setup = null;
                RunWindow(() => setup = new SetupForm("Setting up the Kova audio device", async (report, ct) =>
                {
                    await AudioSetup.RunAsync(report, ct);
                    return "Done. The Kova audio device is ready and is the default output.";
                }));
                return setup!.Result;
            }

            if (args.Length >= 1 && args[0] == "--firewall")
                return Firewall.Command(args[1..]);

            if (args.Length is >= 2 and <= 4 && args[0] == "--service" && args[1] is "install" or "remove")
            {
                // The window that asked, so waiting for TailRemote to stop does not wait on it.
                if (args.Length >= 3 && int.TryParse(args[2], out int windowPid)) ServiceHost.WindowPid = windowPid;
                // And its settings, handed over (this copy may run as another account: an administrator's password).
                if (args.Length == 4) ServiceHost.ReceiveHandover(args[3]);
                int result = args[1] == "install" ? ServiceHost.Install() : ServiceHost.Remove();
                ServiceHost.Answer(result);
                return result;
            }
            if (args.Length == 1 && args[0] == "--screentest-service") return ServiceScreenTest();
            if (args.Length == 1 && args[0] == "--service") return ServiceHost.RunService();
            if (args.Length == 1 && args[0] == "--agent") return Agent.Run();
            if (args.Length == 2 && args[0] == "--agent" && int.TryParse(args[1], out int servicePid)) return Agent.Run(servicePid);
            if (args.Length == 2 && args[0] == "--capturetest") return CaptureTest(args[1]);

            if (args.Length == 1 && args[0] == "--remove-audio")
            {
                Speech.Init();
                SetupForm? remove = null;
                RunWindow(() => remove = new SetupForm("Removing the Kova audio device", AudioSetup.RemoveAsync));
                return remove!.Result;
            }

            if (args.Length == 1 && args[0] == "--licence")
            {
                // The licences of what is built in: the NVDA controller client and Concentus (Opus).
                foreach (var name in new[] { "NVDA-controllerClient-LICENSE.txt", "Concentus-LICENSE.txt", "Sounds-CREDITS.txt" })
                {
                    using var s = typeof(Program).Assembly.GetManifestResourceStream(name)!;
                    using var f = File.Create(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, name));
                    s.CopyTo(f);
                }
                return 0;
            }

            if (args.Length >= 1 && args[0] == "--sampler")
            {
                // A tour of the sounds: --sampler [pattern | classic | android | bells] [key 0-11]
                string what = args.Length > 1 ? args[1] : "rising";
                if (args.Length > 2 && int.TryParse(args[2], out int key)) Sounds.Key = Math.Clamp(key, 0, 11);
                var names = what switch
                {
                    "classic" => Sounds.All.Where(n => n.StartsWith("Classic phone: ")).ToList(),
                    "android" => Sounds.All.Where(n => n.StartsWith("Android")).ToList(),
                    "bells" => Sounds.All.Where(n => n.StartsWith("Nepalese")).ToList(),
                    _ => Sounds.All.Where(n => n.EndsWith(", " + what)).ToList(),
                };
                foreach (string name in names)
                {
                    Console.WriteLine(name);
                    Sounds.PlayNamedAndWait(name);
                    System.Threading.Thread.Sleep(350);
                }
                return 0;
            }
            if (args.Length == 1 && args[0] == "--tones")
            {
                // Every event tone in turn, to hear them: TailRemote.exe --tones
                foreach (Sounds.Tone t in Enum.GetValues<Sounds.Tone>())
                {
                    Sounds.PlayAndWait(t);
                    System.Threading.Thread.Sleep(500);
                }
                return 0;
            }
            if (args.Length == 1 && args[0] == "--selftest") return SelfTest();
            if (args.Length >= 1 && args[0] == "--securitytest")
            {
                // The security fixes (4.4), each part checked in-process: %TEMP%\kova-securitytest.txt says "ok" or what failed.
                // --securitytest Files (or Media, Nvda, Lead) runs one part.
                var lines = new System.Collections.Generic.List<string>();
                bool all = true;
                foreach (var (name, run) in new (string, Func<string?>)[] { ("Files", SecTestFiles.Run), ("Media", SecTestMedia.Run), ("Nvda", SecTestNvda.Run), ("NvdaHelper", SecTestNvdaHelper.Run), ("Lead", SecTestLead.Run) })
                {
                    if (args.Length > 1 && !name.Equals(args[1], StringComparison.OrdinalIgnoreCase)) continue;
                    string? why;
                    try { why = run(); } catch (Exception e) { why = "threw " + e; }
                    lines.Add(name + ": " + (why ?? "ok"));
                    all &= why == null;
                }
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "kova-securitytest.txt"), lines);
                return all ? 0 : 1;
            }
            if (args.Length == 1 && args[0] == "--speedtest")
            {
                // The internet speed test from File, without the window: the result goes to the temp folder.
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "tailremote-speedtest.txt"), SpeedTest.RunAsync().GetAwaiter().GetResult());
                return 0;
            }
            if (args.Length == 1 && args[0] == "--chaostest") return ChaosTest.Run();
            // --relaytest RELAYPORT [HOSTPORT]: end to end through a Kova Relay running on this PC (RelayTest.cs).
            if (args.Length >= 2 && args[0] == "--relaytest" && int.TryParse(args[1], out int relayTestPort))
                return RelayTest.Run(relayTestPort, args.Length > 2 && int.TryParse(args[2], out int rtHost) ? rtHost : 47371);
            if (args.Length == 1 && args[0] == "--cliptest") return ClipTest();
            // --soaktest [minutes]: months of use in a few minutes; handles, threads and memory must stay put.
            if (args.Length >= 1 && args[0] == "--soaktest") return SoakTest.Run(args.Length > 1 && int.TryParse(args[1], out int soakMinutes) ? soakMinutes : 10);
            if (args.Length >= 3 && args[0] == "--testhost" && int.TryParse(args[1], out int testPort))
            {
                // A host with no window, for testing a controlling window against (until it is stopped).
                // Optional fourth: a listen-only password. What it says goes to %TEMP%\tailremote-testhost.txt.
                string said = Path.Combine(Path.GetTempPath(), "tailremote-testhost.txt");
                using var host = new Host(testPort, args[2], args.Length > 3 ? args[3] : null, s => { try { File.AppendAllText(said, s + Environment.NewLine); } catch { } });
                // What arrives is written down too, so a test can check a window's Send the clipboard end to end.
                host.ClipboardReceived += t => { try { File.AppendAllText(said, "clipboard text: " + t + Environment.NewLine); } catch { } };
                host.ClipboardFilesReceived += p => { try { File.AppendAllText(said, "clipboard files: " + string.Join(" | ", p) + Environment.NewLine); } catch { } };
                // The Kova Helper NVDA add-on, as a hosting window listens for it (its pipe has this run's KOVA_INSTANCE word in its name).
                host.StartNvdaHelper();
                if (Environment.GetEnvironmentVariable("KOVA_TEST_HELPER") == "1") host.TestHelperConnected = true; // tests: as if the Kova Helper were talking to it
                host.NvdaEventSeen += ev => { try { File.AppendAllText(said, DateTime.Now.ToString("HH:mm:ss.fff ") + "NVDA: " + ev + Environment.NewLine); } catch { } };
                // KOVA_TEST_RELAY=name@relay:port: also reachable through a Kova Relay (4.4), as Host through a relay does.
                if (Environment.GetEnvironmentVariable("KOVA_TEST_RELAY") is { Length: > 0 } viaRelay)
                {
                    var target = Relay.Parse(viaRelay, 47200, out string? relayProblem);
                    if (target != null) host.UseRelay(target);
                    else try { File.AppendAllText(said, "relay: " + relayProblem + Environment.NewLine); } catch { }
                }
                // KOVA_MIC_TEST=1: the controlling PC's microphone is counted, not played (a test PC may have no second cable).
                if (Environment.GetEnvironmentVariable("KOVA_MIC_TEST") == "1")
                {
                    int ticks = 0;
                    MicIn.TestHeard = (_, t) => System.Threading.Interlocked.Add(ref ticks, t);
                    new System.Threading.Thread(() =>
                    {
                        while (true)
                        {
                            System.Threading.Thread.Sleep(1000);
                            int n = System.Threading.Interlocked.Exchange(ref ticks, 0);
                            if (n > 0) try { File.AppendAllText(said, "microphone ticks this second: " + n + Environment.NewLine); } catch { }
                        }
                    }) { IsBackground = true }.Start();
                }
                System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                return 0;
            }
            if (args.Length >= 4 && args[0] == "--testcontrol" && int.TryParse(args[1], out int controlPort) && int.TryParse(args[3], out int controlSecs))
            {
                // A controlling PC with no window, for testing a host's microphone and NVDA text against (4.3):
                // --testcontrol PORT PASSWORD SECONDS [mic] [nvda] [micoff=N]. Connects to this PC, turns on the
                // microphone (Windows' default recording device) and asks for the remote NVDA as text with no sound,
                // the way the window does; never sends a key. What it hears goes to %TEMP%\kova-testcontrol.txt.
                string said = Path.Combine(Path.GetTempPath(), "kova-testcontrol.txt");
                void Note(string s) { try { File.AppendAllText(said, DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { } }
                Speech.Init();
                bool mic = Array.IndexOf(args, "mic") >= 0, nvda = Array.IndexOf(args, "nvda") >= 0;
                int micOff = 0, soundOffAt = 0, soundOnAt = 0;
                string to = "127.0.0.1";
                foreach (var a in args) if (a.StartsWith("micoff=") && int.TryParse(a[7..], out int mo)) micOff = mo;
                // 4.4: to=ADDRESS (a relay address, name@relay:port, works too); soundoff=N / soundon=N turn the sound off and on
                // at those seconds (text-only mode), and every 5 seconds the bytes received over UDP are noted, to measure both.
                foreach (var a in args) if (a.StartsWith("to=")) to = a[3..];
                foreach (var a in args) if (a.StartsWith("soundoff=") && int.TryParse(a[9..], out int so)) soundOffAt = so;
                foreach (var a in args) if (a.StartsWith("soundon=") && int.TryParse(a[8..], out int sn)) soundOnAt = sn;
                using var player = new Player(Player.NoDevice, Note);
                using var c = Client.Connect(to, controlPort, args[2], player, s => Note("status: " + s));
                Note("connected" + (c.ListenOnly ? ", listen only" : ""));
                // Everything the remote NVDA did, acted on as the window does (NvdaEvent.Perform), and logged.
                c.NvdaEventArrived += ev => { Note(ev.Kind == NvdaEventKind.Speak ? "remote NVDA: " + ev.SpokenText : "remote NVDA " + ev); NvdaEvent.Perform(ev, sounds: true); };
                c.NvdaHelperThere += there => Note(there ? "the remote PC has the Kova Helper" : "the remote PC has no Kova Helper");
                c.NvdaNote += n => Note("NVDA note: " + n);
                c.MicNote += n => Note("microphone note: " + n);
                c.Disconnected += why => Note("disconnected: " + why);
                c.SoundOff = nvda;
                c.NvdaText = nvda;
                c.SetMic(mic, null);
                for (int i = 0; i < 50 && c.HostVersion == null; i++) System.Threading.Thread.Sleep(20);
                Note("host " + c.HostVersion + ": microphone " + (c.CanMic ? "taken" : "not taken") + ", NVDA text " + (c.CanNvda ? "offered" : "not offered") + ", sending microphone " + c.MicSending);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool offDone = false;
                long lastBytes = 0, lastBytesAt = 0;
                while (sw.Elapsed.TotalSeconds < controlSecs)
                {
                    System.Threading.Thread.Sleep(200);
                    if (micOff > 0 && !offDone && sw.Elapsed.TotalSeconds >= micOff) { offDone = true; c.SetMic(false, null); Note("microphone turned off"); }
                    if (soundOffAt > 0 && sw.Elapsed.TotalSeconds >= soundOffAt) { soundOffAt = 0; c.SoundOff = true; Note("sound turned off"); }
                    if (soundOnAt > 0 && sw.Elapsed.TotalSeconds >= soundOnAt) { soundOnAt = 0; c.SoundOff = false; Note("sound turned on"); }
                    if (sw.ElapsedMilliseconds / 5000 != lastBytesAt) { lastBytesAt = sw.ElapsedMilliseconds / 5000; long b = c.UdpBytesReceived; Note("UDP bytes received in the last 5 seconds: " + (b - lastBytes)); lastBytes = b; }
                }
                Note("done, microphone was " + (c.MicSending ? "on" : "off") + " at the end");
                return 0;
            }
            if (args.Length >= 1 && args[0] == "--dialogtest")
            {
                // A blank window with a menu bar like the real one, opening a list and a typing box,
                // to check with NVDA what is read. "direct" opens them straight from the click, and
                // "mta" runs it on the program's own thread, the way 2.1.0 did both.
                bool direct = args.Length > 1 && args[1] == "direct";
                if (args.Length > 1 && args[1] == "mta") Application.Run(MenuTestWindow(direct));
                else RunWindow(() => MenuTestWindow(direct));
                return 0;
            }
            if (args.Length >= 1 && args[0] == "--audiotest")
            {
                if (args.Length > 2 && int.TryParse(args[2], out int jitter)) Client.TestJitterMs = jitter;
                Client.TestHoldQuality = Array.IndexOf(args, "steps") >= 0;
                foreach (var a in args) if (a.StartsWith("drop") && int.TryParse(a[4..], out int drop)) Client.TestDropPercent = drop;
                foreach (var a in args) if (a.StartsWith("lag") && int.TryParse(a[3..], out int lag)) Client.TestLagMs = lag;
                foreach (var a in args) if (a.StartsWith("stall") && int.TryParse(a[5..], out int stall)) Client.TestStallMs = stall;
                foreach (var a in args) if (a.StartsWith("bw") && int.TryParse(a[2..], out int kbps)) Client.TestKbps = kbps;
                foreach (var a in args) if (a.StartsWith("lock") && int.TryParse(a[4..], out int lk)) Client.TestLockStep = lk;
                foreach (var a in args) if (a.StartsWith("until") && int.TryParse(a[5..], out int un)) Client.TestKbpsUntil = un;
                foreach (var a in args) if (a.StartsWith("dialup") && int.TryParse(a[6..], out int du)) Client.TestDialupKbps = du;
                foreach (var a in args) if (a.StartsWith("cell") && int.TryParse(a[4..], out int cell)) Client.TestCellMs = cell;
                foreach (var a in args) if (a.StartsWith("scan") && int.TryParse(a[4..], out int scanMs)) Client.TestScanMs = scanMs;
                foreach (var a in args) if (a.StartsWith("secs") && int.TryParse(a[4..], out int secs)) AudioTestSeconds = secs;
                return AudioTest(args.Length > 1 ? args[1] : null);
            }

            // Speech first: waiting for the old copy to close (up to 15 seconds) used to come before it,
            // so right after an update NVDA heard nothing at all until the window appeared. Now it hears
            // "Finishing the update." if there is a wait. Settings are still read only after the wait:
            // the old copy may write them as it closes.
            // One window per person: opening Kova again brings the open one forward instead. Not a copy
            // started after an update or a rebuild (--after-update, --resume): the old one is still
            // closing then, so it waits its turn below rather than giving up.
            int after = Array.IndexOf(args, "--after-update");
            bool takingOver = after >= 0 || Array.IndexOf(args, "--resume") >= 0;
            // --host (the sign-in task) with a window already open asks that window to host instead.
            if (!takingOver && !TakeWindow(wait: false, host: Array.IndexOf(args, "--host") >= 0)) return 0;

            Speech.Init();
            if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out int oldPid))
                Updater.FinishUpdate(oldPid, () => Speech.Speak("Finishing the update."));
            // Still not free after half a minute (the old one hung, or another copy of Kova is open): this
            // one opens anyway, rather than leave nothing open after an update.
            // If another window took the place meanwhile and answers, that is the one window: this copy ends.
            if (takingOver && !TakeWindow(wait: true)) return 0;

            ClearOldCopies();

            // --resume (used by build.ps1 after swapping in a new build): carry on
            // hosting or connected, whichever was running when the old copy closed.
            string resume = Array.IndexOf(args, "--resume") >= 0 ? Settings.Load().ResumeState : "";

            RunWindow(() => new MainForm(
                autoHost: Array.IndexOf(args, "--host") >= 0 || resume == "host",
                autoConnect: Array.IndexOf(args, "--connect") >= 0 || resume == "connect",
                updated: after >= 0));
            ReleaseWindow();
            return 0;
        }

        /// <summary>
        /// Raised (on a background thread) when Kova is opened again while this window is open: the
        /// window should come forward. Handlers must move to the window's own thread themselves, and return
        /// true only once it has really done so (a hung window must not answer, so the new copy opens instead).
        /// </summary>
        public static event Func<bool>? ShowRequested;

        /// <summary>
        /// Raised (on a background thread) when Kova is started with --host (the sign-in task) while this
        /// window is open: the window should start hosting if it is doing nothing else. Handlers must move
        /// to the window's own thread themselves, and return true once it has taken the request.
        /// </summary>
        public static event Func<bool>? HostRequested;

        private static volatile System.Threading.Mutex? _windowMutex;

        // Per person (their account's SID), per session (Local\), per copy of Kova (a short hash of the
        // exe's full path, so Documents\apps\Kova-x64.exe, a build in bin and Program Files\Kova each have
        // their own window) and apart for test runs (TestPipes).
        private static string WindowName(string what, string sid) =>
            @"Local\Kova" + what + "-" + sid + "-" + ExeHash() + Names.TestPipes;

        private static string ExeHash()
        {
            string path = (Environment.ProcessPath ?? AppContext.BaseDirectory).ToUpperInvariant();
            byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));
            return Convert.ToHexString(hash, 0, 6);
        }

        // Elevated and plain copies of the same person must open each other's lock and knocks: the sign-in
        // task runs Kova with the highest rights, and an object it made with Windows' defaults (Administrators
        // and SYSTEM only) could not be opened by a plain copy, which then opened a second window. So the
        // person's own SID (the same in both) gets full rights; full, because opening an existing object asks for them.
        private static System.Threading.Mutex MakeMutex(string name, System.Security.Principal.SecurityIdentifier me)
        {
            var acl = new System.Security.AccessControl.MutexSecurity();
            acl.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(me, System.Security.AccessControl.MutexRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            return System.Threading.MutexAcl.Create(false, name, out _, acl);
        }

        private static System.Threading.EventWaitHandle MakeEvent(string name, System.Security.Principal.SecurityIdentifier me)
        {
            var acl = new System.Security.AccessControl.EventWaitHandleSecurity();
            acl.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(me, System.Security.AccessControl.EventWaitHandleRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            return System.Threading.EventWaitHandleAcl.Create(false, System.Threading.EventResetMode.AutoReset, name, out _, acl);
        }

        /// <summary>
        /// Takes the one-window place. False only when another window has it and has answered that it came
        /// forward (or, with host, took the request to host): this copy should just end. With wait, waits up
        /// to 30 seconds for it, then knocks once, and goes ahead if nobody answers.
        /// </summary>
        private static bool TakeWindow(bool wait, bool host = false)
        {
            System.Threading.EventWaitHandle show, hostKnock, ack;
            System.Threading.Mutex mutex;
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var me = id.User!;
                // The events first: a second copy can then always leave its knock, even before this one waits for it.
                show = MakeEvent(WindowName("WindowShow", me.Value), me);
                hostKnock = MakeEvent(WindowName("WindowHost", me.Value), me);
                ack = MakeEvent(WindowName("WindowAck", me.Value), me);
                mutex = MakeMutex(WindowName("Window", me.Value), me);
            }
            catch (Exception e)
            {
                // Something else holds the names, or the rights were refused: open as before rather than not at all.
                DiagLog.Write("one window: could not take part (" + e.Message + ")");
                return true;
            }
            void DropAll() { mutex.Dispose(); show.Dispose(); hostKnock.Dispose(); ack.Dispose(); }
            bool Take(int ms)
            {
                try { return mutex.WaitOne(ms); }
                catch (System.Threading.AbandonedMutexException) { return true; } // the last window ended without letting go: it is free
            }

            bool mine = Take(wait ? 30_000 : 0);
            if (!mine && wait)
            {
                // Restarting after an update, but another window took the place meanwhile (opened while the
                // update finished): if it answers, it is the one window, and this copy ends rather than make two.
                try
                {
                    ack.Reset();
                    show.Set();
                    if (ack.WaitOne(2000)) { DropAll(); return false; }
                }
                catch { }
            }
            if (!mine && !wait)
            {
                // This copy was just started by the user, so it may hand the foreground on; without this Windows can
                // refuse to let the running window come forward, and NVDA stays where it was.
                try { AllowSetForegroundWindow(-1); } catch { }
                // The open window answers only once it has really taken the knock. It may not: it may still be
                // starting (not listening yet), or closing (no longer listening, but still holding the place). So
                // the knock is repeated while the place is waited for, and exiting and at once opening Kova again
                // always ends with a window.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!mine)
                {
                    try
                    {
                        ack.Reset(); // an answer left over from a knock nobody waited for is not this one's
                        (host ? hostKnock : show).Set();
                        if (ack.WaitOne(2000)) { DropAll(); return false; }
                    }
                    catch { break; }
                    mine = Take(1000);
                    if (clock.ElapsedMilliseconds > 30_000) break;
                }
            }
            if (!mine)
            {
                // Still held after half a minute (that window hung, or will not answer): this one opens anyway.
                DropAll();
                return true;
            }
            // Knocks left by copies that gave up are not for this window.
            try { show.Reset(); hostKnock.Reset(); ack.Reset(); } catch { }
            _windowMutex = mutex;
            new System.Threading.Thread(() =>
            {
                var knocks = new System.Threading.WaitHandle[] { show, hostKnock };
                while (true)
                {
                    int which;
                    try { which = System.Threading.WaitHandle.WaitAny(knocks); } catch { return; }
                    // No window listening (still starting, or closing): no answer, so the new copy keeps knocking
                    // and then waits for the place, rather than ending with no window at all.
                    var handler = _windowMutex == null ? null : which == 0 ? ShowRequested : HostRequested;
                    if (handler == null) continue;
                    try { if (handler()) ack.Set(); } catch (Exception e) { Crash(e); }
                }
            }) { IsBackground = true, Name = "Kova show requests" }.Start();
            return true;
        }

        /// <summary>Lets go of the one-window place, on the thread that took it, as the window closes.</summary>
        private static void ReleaseWindow()
        {
            var mutex = _windowMutex;
            _windowMutex = null; // the knock listener stops answering first
            try { mutex?.ReleaseMutex(); } catch { }
            mutex?.Dispose();
        }

        /// <summary>
        /// Every TailRemote window runs on a thread of the kind Windows' accessibility expects
        /// (STA). On the program's own thread NVDA read a window's first control, then heard
        /// nothing more: Tab, the arrows, other windows, all silent. The sound code is unaffected:
        /// playback and capture make their Windows audio objects on threads of their own.
        /// </summary>
        private static void RunWindow(Func<Form> make) => RunOnWindowThread(() =>
        {
            // The same safety net as Main's, on this thread too: WinForms keeps it per thread, and
            // without it a problem in the window showed WinForms' own box, whose Quit closed TailRemote.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Crash(e.Exception);
            ApplicationConfiguration.Initialize();
            Application.Run(make());
        });

        private static Form MenuTestWindow(bool direct)
        {
            var form = new Form { Text = "Kova menu test", ClientSize = new System.Drawing.Size(420, 120), Font = new System.Drawing.Font("Segoe UI", 10f) };
            var bar = new MenuStrip { Dock = DockStyle.Top, LayoutStyle = ToolStripLayoutStyle.Flow };
            var about = new ToolStripMenuItem("&About");
            void List() { using var f = new ListViewerForm("Test results", new[] { "Download: 100 megabits per second", "Upload: 20 megabits per second" }, _ => false); f.ShowDialog(form); }
            var address = new MenuText("&Address", "The other PC's name:");
            var list = new ToolStripMenuItem("View the &results...");
            list.Click += (_, _) => { if (direct) List(); else Menus.AfterMenu(List); };
            about.DropDownItems.Add(list);
            about.DropDownItems.Add(address.Item);
            // Submenus like the real ones, for arrowing past them: a choice, and one filled as it opens.
            var quality = new MenuChoice("Sound &quality");
            quality.Add("Variable");
            quality.Add("Locked at 64 kbit/s");
            quality.SelectedIndex = 0;
            about.DropDownItems.Add(quality.Menu);
            var switchTo = new ToolStripMenuItem("Switch &to PC");
            switchTo.DropDownItems.Add("(filled in when opened)");
            switchTo.DropDownOpening += (_, _) =>
            {
                switchTo.DropDownItems.Clear();
                switchTo.DropDownItems.Add(new MenuStatus("There are no saved PCs to switch to yet. Connect to one, then File, Save this PC.").Item);
            };
            about.DropDownItems.Add(switchTo);
            about.DropDownItems.Add(Menus.Check("&Logging"));
            bar.Items.Add(about);
            Menus.Attach(bar);
            form.Controls.Add(bar);
            form.MainMenuStrip = bar;
            return form;
        }

        private static void RunOnWindowThread(Action run)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? failed = null;
            var t = new System.Threading.Thread(() =>
            {
                try { run(); }
                catch (Exception e) { failed = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
            }) { Name = "Kova window" };
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            t.Join();
            failed?.Throw();
        }

        /// <summary>
        /// --screentest-service, as administrator: the installed Kova service shows the real screen. Logs in
        /// on this PC with the service's own saved password (never written anywhere), watches, and checks
        /// that a real picture comes from the agent at the screen and changes are followed.
        /// Writes %TEMP%\kova-servicescreen.txt ("ok. ..." or "FAIL: ...").
        /// </summary>
        private static int ServiceScreenTest()
        {
            string outPath = Path.Combine(Path.GetTempPath(), "kova-servicescreen.txt");
            int Done(string text, int code) { File.WriteAllText(outPath, text); return code; }
            try
            {
                var cfg = ServiceHost.LoadConfig();
                if (cfg == null) return Done("FAIL: no Kova service settings here (run as administrator, with the service installed).", 1);
                string password = ServiceHost.Config.Open(cfg.PasswordEnc);
                using var p = new Player(Player.NoDevice, _ => { });
                using var c = Client.Connect("127.0.0.1", cfg.Port, password, p, _ => { });
                for (int i = 0; i < 100 && c.HostVersion == null; i++) System.Threading.Thread.Sleep(20);
                if (!c.CanWatch) return Done("FAIL: the service (version " + c.HostVersion + ") does not offer its screen.", 1);
                using var watch = c.WatchScreen()!;
                string? note = null;
                watch.Note += t => note = t;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 800 && watch.Pictures < 1; i++) System.Threading.Thread.Sleep(25);
                if (watch.Pictures < 1) return Done("FAIL: no picture from the service in 20 seconds" + (note != null ? "; it said: " + note : "") + ".", 1);
                long firstMs = clock.ElapsedMilliseconds;
                int width, height, colours;
                lock (watch.Canvas.Gate)
                {
                    var pic = watch.Canvas.Picture!;
                    width = pic.Width; height = pic.Height;
                    var seen = new System.Collections.Generic.HashSet<int>();
                    for (int y = 0; y < height; y += Math.Max(1, height / 40))
                        for (int x = 0; x < width; x += Math.Max(1, width / 40))
                            seen.Add(pic.GetPixel(x, y).ToArgb());
                    colours = seen.Count;
                }
                if (width < 320 || height < 200) return Done("FAIL: the picture was only " + width + " by " + height + ".", 1);
                if (colours < 3) return Done("FAIL: the picture was one flat colour (" + width + " by " + height + "): the screen was not really taken.", 1);
                // Something moving on the real screen for 4 seconds (a small test window, then gone): the
                // service's agent takes it with Desktop Duplication and it comes as H.264, if this PC can.
                int framesBefore = watch.VideoFrames, peak = 0;
                var shown = new System.Threading.ManualResetEventSlim();
                var ui = new System.Threading.Thread(() =>
                {
                    using var f = new Form { Text = "Kova screen test", FormBorderStyle = FormBorderStyle.None, WindowState = FormWindowState.Maximized, TopMost = true, BackColor = System.Drawing.Color.Black };
                    int tick = 0;
                    var timer = new System.Windows.Forms.Timer { Interval = 15 };
                    timer.Tick += (_, _) => { tick++; f.Invalidate(); if (tick > 260) f.Close(); };
                    f.Paint += (_, e) =>
                    {
                        int x = tick * 13 % Math.Max(1, f.Width - 200), y = tick * 7 % Math.Max(1, f.Height - 200);
                        e.Graphics.FillRectangle(System.Drawing.Brushes.Orange, x, y, 200, 200);
                        e.Graphics.FillEllipse(System.Drawing.Brushes.SkyBlue, f.Width - x - 200, f.Height - y - 200, 200, 200);
                    };
                    f.Shown += (_, _) => { timer.Start(); shown.Set(); };
                    Application.Run(f);
                    timer.Dispose();
                });
                ui.SetApartmentState(System.Threading.ApartmentState.STA);
                ui.Start();
                shown.Wait(5000);
                for (int i = 0; i < 40; i++) { System.Threading.Thread.Sleep(100); peak = Math.Max(peak, watch.PerSecond); }
                ui.Join(10_000);
                int videoFrames = watch.VideoFrames - framesBefore;
                System.Threading.Thread.Sleep(3000); // and still again: an exact picture
                return Done("ok. The service at version " + c.HostVersion + " showed the real screen, " + width + " by " + height + ", " + colours +
                    " different colours sampled, first picture after " + firstMs + " ms; with things moving: " + videoFrames + " H.264 frames, up to " + peak +
                    " pictures a second; " + watch.Pictures + " pictures and " + (watch.Bytes / 1024) + " kB in all.", 0);
            }
            catch (Exception e) { return Done("FAIL: " + e.Message, 1); }
        }

        /// <summary>Copies that build.ps1 moved aside while they were running; gone once they have exited.</summary>
        private static void ClearOldCopies()
        {
            try
            {
                string dir = Path.Combine(Names.LocalDir, "old");
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir)) { try { File.Delete(f); } catch { } }
                if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
                string parent = Path.GetDirectoryName(dir)!;
                if (Directory.GetFileSystemEntries(parent).Length == 0) Directory.Delete(parent);
            }
            catch { }
        }

        /// <summary>
        /// The whole audio path on this PC, measured: a 440 Hz tone is played into
        /// one output (default: the first one that is not the default output), a
        /// host captures it, a client receives it and plays it, muted, through the
        /// default output with real device timing. Once a second it writes how
        /// often the buffer ran dry or skipped, and how full it was, to
        /// %TEMP%\tailremote-audiotest.txt.
        /// </summary>
        /// <summary>
        /// --cliptest: the Windows clipboard, the way TailRemote really uses it when clipboard text or
        /// files arrive and when Send the clipboard reads it, each step written down before it runs
        /// (so a crash shows where). The self-test moves clipboard data between PCs but never touched
        /// the real clipboard. Writes %TEMP%\tailremote-cliptest.txt.
        /// </summary>
        private static int ClipTest()
        {
            string report = Path.Combine(Path.GetTempPath(), "tailremote-cliptest.txt");
            File.WriteAllText(report, "");
            void Step(string s) => File.AppendAllText(report, s + Environment.NewLine);
            int result = 0;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    Step("set text (SetDataObject, copy, as when text arrives)");
                    Clipboard.SetDataObject("Kova clipboard test", true, 2, 50);
                    Step("read text: " + Clipboard.GetText());
                    Step("set text (DataObject UnicodeText, as the service's agent does)");
                    Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, "agent test"), true, 2, 50);
                    Step("read text: " + Clipboard.GetText());
                    Step("set files (file drop list with Preferred DropEffect, as when files arrive)");
                    var list = new System.Collections.Specialized.StringCollection { Environment.ProcessPath! };
                    var data = new DataObject();
                    data.SetFileDropList(list);
                    data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(2)));
                    Clipboard.SetDataObject(data, true, 2, 50);
                    Step("contains files: " + Clipboard.ContainsFileDropList() + ", " + Clipboard.GetFileDropList().Count);
                    Step("formats: " + string.Join(", ", Clipboard.GetDataObject()?.GetFormats() ?? Array.Empty<string>()));
                    Step("clear");
                    Clipboard.Clear();
                    Step("formats when empty: " + (Clipboard.GetDataObject()?.GetFormats()?.Length ?? -1));
                    Step("ok");
                }
                catch (Exception e) { Step("FAIL: " + e); result = 1; }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            t.Join();
            return result;
        }

        private static int AudioTestSeconds = 15;

        private static int AudioTest(string? captureName)
        {
            string report = Path.Combine(Path.GetTempPath(), "tailremote-audiotest.txt");
            var lines = new System.Collections.Generic.List<string>();
            void Say(string s) { lines.Add(s); File.WriteAllLines(report, lines); }
            try
            {
                var outputs = Wasapi.OutputDevices();
                Wasapi.Enumerator().GetDefaultAudioEndpoint(Wasapi.eRender, Wasapi.eConsole, out var def);
                def.GetId(out string defId);
                var src = captureName != null
                    ? outputs.Find(d => d.Name.Contains(captureName, StringComparison.OrdinalIgnoreCase))
                    : outputs.Find(d => d.Id != defId);
                if (src.Id == null) { Say("No output to play the test tone into."); return 1; }
                Say("tone into: " + src.Name + "; playing (muted) through the default output; simulated network delay 0 to " + Client.TestJitterMs + " ms per packet");
                timeBeginPeriod(1);

                // Into the default output (speakers), quiet enough not to hear: loopback records it before the volume.
                using var tone = new ToneSource(src.Id, src.Id == defId ? 0.0003 : 0.3);
                Host.WrongPasswordDelayMs = 0;
                using var host = new Host(47998, "audiotest", null, _ => { }, src.Id);
                using var player = new Player("", s => Say("player: " + s)) { Mute = true, SpeedUp = Array.IndexOf(Environment.GetCommandLineArgs(), "speed") >= 0,
                    RideOut = Array.IndexOf(Environment.GetCommandLineArgs(), "noride") < 0 };
                using var c = Client.Connect("127.0.0.1", 47998, "audiotest", player, s => Say("client: " + s), Client.TestLockStep);
                System.Threading.Thread.Sleep(1500);
                player.Diagnose();
                for (int i = 1; i <= AudioTestSeconds; i++)
                {
                    if (Client.TestHoldQuality) c.TestSetQuality(i % Protocol.OpusSteps.Length); // every bitrate in turn, down and back up
                    System.Threading.Thread.Sleep(1000);
                    int fills = System.Threading.Interlocked.Exchange(ref LoopbackCapture.TestGapFills, 0);
                    int fillMs = System.Threading.Interlocked.Exchange(ref LoopbackCapture.TestGapFillMs, 0);
                    int seqSkips = System.Threading.Interlocked.Exchange(ref LoopbackCapture.TestSeqSkips, 0);
                    int chunk = System.Threading.Interlocked.Exchange(ref LoopbackCapture.TestChunkMax, 0);
                    if (Client.TestLockStep >= 0 && i == AudioTestSeconds) Say("locked test: first packet " + Client.TestFirstTicks + " ticks, other lengths " + Client.TestOtherTicks);
                    Say($"{i,2}s  {player.Diagnose()}, quality step {c.AudioQuality} | host: biggest chunk {chunk} ms, silence added {fills}x ({fillMs} ms), count skips {seqSkips}");
                }
                return 0;
            }
            catch (Exception e) { Say("failed: " + e); return 1; }
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool SetDefaultDllDirectories(uint flags);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern int WerSetFlags(uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

        /// <summary>Test only: plays a steady 440 Hz tone into one output.</summary>
        private sealed unsafe class ToneSource : IDisposable
        {
            private volatile bool _stop;
            private readonly System.Threading.Thread _t;

            private readonly double _amplitude;

            public ToneSource(string deviceId, double amplitude)
            {
                _amplitude = amplitude;
                _t = new System.Threading.Thread(() => Run(deviceId)) { IsBackground = true };
                _t.Start();
            }

            public void Dispose() { _stop = true; _t.Join(2000); }

            private void Run(string id)
            {
                var dev = Wasapi.OutputDevice(id);
                var client = Wasapi.Activate(dev);
                client.GetMixFormat(out IntPtr fmtPtr);
                var fmt = Wasapi.ReadFormat(fmtPtr);
                client.Initialize(0, Wasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, fmtPtr, IntPtr.Zero);
                using var ev = new System.Threading.AutoResetEvent(false);
                client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                client.GetBufferSize(out uint buf);
                var iid = Wasapi.IID_IAudioRenderClient;
                client.GetService(ref iid, out object o);
                var render = (Wasapi.IAudioRenderClient)o;
                client.Start();
                double phase = 0, step = 2 * Math.PI * 440 / fmt.Rate;
                while (!_stop)
                {
                    ev.WaitOne(50);
                    client.GetCurrentPadding(out uint pad);
                    uint n = buf - pad;
                    if (n == 0 || render.GetBuffer(n, out IntPtr data) < 0) continue;
                    for (int f = 0; f < n; f++)
                    {
                        float v = (float)(_amplitude * Math.Sin(phase));
                        phase += step;
                        for (int ch = 0; ch < fmt.Channels; ch++)
                        {
                            if (fmt.IsFloat) ((float*)data)[f * fmt.Channels + ch] = v;
                            else ((short*)data)[f * fmt.Channels + ch] = (short)(v * 32767);
                        }
                    }
                    render.ReleaseBuffer(n, 0);
                }
                client.Stop();
            }
        }

        /// <summary>Host and client on this PC: handshake, wrong password, ping. No audio is played.</summary>
        private static int SelfTest()
        {
            Host.TestLocked = false; // this VPS's console is usually locked: Get files is tested as if signed in (the lock refusal has its own test)
            string lossless = "";
            // Windows tools run from System32 by full path, never a same-named exe next to Kova.exe.
            {
                string sys = Environment.SystemDirectory;
                if (Native.SystemTool("netsh") != Path.Combine(sys, "netsh.exe") || Native.SystemTool("sc.exe") != Path.Combine(sys, "sc.exe")
                    || Native.SystemTool(@"C:\x\y.exe") != @"C:\x\y.exe")
                    return Fail("a Windows tool was not given its full System32 path");
            }
            // VB-Cable's signer: exactly VB-Audio's name, not any name that holds it.
            if (!AudioSetup.SignerIsVbAudio("CN=BUREL VINCENT, O=BUREL VINCENT, L=Paris, C=FR")
                || !AudioSetup.SignerIsVbAudio("CN=BUREL VINCENT Entrepreneur individuel, O=BUREL VINCENT Entrepreneur individuel, STREET=21 avenue des Marguerites, L=Eymet, S=Dordogne, C=FR, OID.1.3.6.1.4.1.311.60.2.1.3=FR, SERIALNUMBER=423 734 177, OID.2.5.4.15=Private Organization")
                || AudioSetup.SignerIsVbAudio("CN=NOT BUREL VINCENT LTD, O=Someone, C=US")
                || AudioSetup.SignerIsVbAudio("CN=Someone, OU=BUREL VINCENT, C=US"))
                return Fail("VB-Cable's signer check took the wrong certificate names");
            // The remote PC's health: each thing said once, at the right moment, and again only after it got better.
            {
                var w = new HealthWatch();
                long t = 0;
                HealthSample S(bool onBattery, int battery, long freeGb = 100, int cpu = 10, int mem = 40) =>
                    new(true, onBattery, battery, "C:", freeGb << 30, 200L << 30, cpu, mem);
                var said = new System.Collections.Generic.List<string>();
                void Look(HealthSample s) { said.AddRange(w.Next(s, t)); t += HealthWatch.EveryMs; }
                Look(S(false, 90));                           // plugged in, all well: nothing
                Look(S(true, 89));                            // unplugged
                Look(S(true, 20)); Look(S(true, 19));         // low, said once
                Look(S(true, 9));                             // very low
                Look(S(false, 9));                            // plugged in again
                Look(S(false, 50, freeGb: 1)); Look(S(false, 50, freeGb: 1)); // drive nearly full, said once
                for (int i = 0; i < 5; i++) Look(S(false, 50, cpu: 99));     // busy, but not yet a minute
                int beforeMinute = said.Count;
                Look(S(false, 50, cpu: 99));                  // a whole minute busy
                Look(S(false, 50, cpu: 99));                  // still busy: not again
                string[] want = { "now running on its battery, 89", "battery is low, 20", "very low, 9", "plugged in again, 9", "nearly full: 1.0 gigabytes", "processor has been busy" };
                if (said.Count != want.Length || !said.Zip(want).All(p => p.First.Contains(p.Second)) || beforeMinute != 5)
                    return Fail("health alerts said: " + string.Join(" / ", said));
                if (HealthWatch.Standing(S(true, 8, freeGb: 1)).Count != 2 || HealthWatch.Standing(S(false, 80)).Count != 0)
                    return Fail("health on connecting was wrong");
            }
            // Wake-on-LAN: the packet, and the broadcast address of the network the card was on.
            {
                byte[] mac = Convert.FromHexString("0A1B2C3D4E5F");
                byte[] p = Wake.MagicPacket(mac);
                if (p.Length != 102 || p.Take(6).Any(b => b != 0xFF) || !p.AsSpan(96, 6).SequenceEqual(mac)) return Fail("the wake packet is wrong");
                var cards = Wake.Parse("0A1B2C3D4E5F 192.168.1.20/24\n0A1B2C3D4E60 10.0.5.9/16\nnonsense\n0A1B 1.2.3.4/24");
                if (cards.Count != 2 || cards[0].Broadcast?.ToString() != "192.168.1.255" || cards[1].Broadcast?.ToString() != "10.0.255.255")
                    return Fail("wake broadcast addresses: " + string.Join(", ", cards.Select(c => c.Broadcast)));
            }
            // Release tags: a v or V, a -beta or +build, two or four parts all read as a three-part
            // version (Version.Parse failed on all but "v4.0.0"), and 3.10.0 is newer than 3.9.0.
            {
                var want = new Version(4, 0, 0);
                foreach (var tag in new[] { "v4.0.0", "V4.0.0", "4.0.0", "v4.0.0-beta", "4.0.0+build.7", "v4.0", "4.0.0.1", " v4.0.0 " })
                    if (Updater.NormalizeTag(tag) != want) return Fail("the release tag " + tag + " read as " + Updater.NormalizeTag(tag) + ", not 4.0.0");
                if (Updater.NormalizeTag("latest") != null || Updater.NormalizeTag("") != null || Updater.NormalizeTag(null) != null)
                    return Fail("a release tag that is no version was taken as one");
                if (!(Updater.NormalizeTag("v3.10.0") > Updater.NormalizeTag("v3.9.0"))) return Fail("3.10.0 was not newer than 3.9.0");
            }
            // GitHub's answer: a Wi-Fi sign-in page, an empty object, a release with no files and notes
            // that are null are each a plain sentence or handled, never a raw exception.
            {
                string ParseProblem(string text)
                {
                    try { Updater.ParseRelease(text, "Kova-x64.exe"); return ""; }
                    catch (InvalidOperationException e) { return e.Message; }
                    catch (Exception e) { return "raw " + e.GetType().Name; }
                }
                if (!ParseProblem("<!DOCTYPE html><html><body>Sign in to the hotel Wi-Fi</body></html>").Contains("web page")) return Fail("a sign-in page from GitHub's address was not recognised");
                if (!ParseProblem("not json at all").Contains("web page")) return Fail("an answer that is not JSON was not recognised");
                if (!ParseProblem("{}").Contains("could not be understood")) return Fail("an empty answer from GitHub was not a plain sentence: " + ParseProblem("{}"));
                var none = Updater.ParseRelease("{\"tag_name\":\"v9.1.0\",\"body\":null}", "Kova-x64.exe");
                if (none.Version != new Version(9, 1, 0) || none.Notes != "" || none.Url != null) return Fail("a release with no files and null notes was read wrongly");
                var full = Updater.ParseRelease("{\"tag_name\":\"v9.1.0-beta\",\"body\":\"Notes\",\"assets\":[{\"name\":\"Kova-arm64.exe\",\"browser_download_url\":\"https://a\"},{\"name\":\"Kova-x64.exe\",\"browser_download_url\":\"https://b\",\"digest\":\"sha256:ABCD\"}]}", "Kova-x64.exe");
                if (full.Version != new Version(9, 1, 0) || full.Notes != "Notes" || full.Url != "https://b" || full.Sha256 != "ABCD") return Fail("a normal release was read wrongly");
            }
            // A remote folder of exactly the most it lists says nothing is left out; one more, and it does.
            {
                string many = Path.Combine(Path.GetTempPath(), "kova-selftest-many");
                try
                {
                    if (Directory.Exists(many)) Directory.Delete(many, true);
                    Directory.CreateDirectory(many);
                    for (int i = 0; i < RemoteTools.MaxEntries; i++) File.WriteAllBytes(Path.Combine(many, "f" + i + ".txt"), Array.Empty<byte>());
                    string exact = RemoteTools.ListFolder(many);
                    if (exact.Split('\n').Any(l => l.StartsWith("T\t")) || exact.Split('\n').Count(l => l.StartsWith("F\t")) != RemoteTools.MaxEntries)
                        return Fail("a folder of exactly " + RemoteTools.MaxEntries + " files was listed as cut short, or not all listed");
                    Directory.CreateDirectory(Path.Combine(many, "a folder"));
                    string over = RemoteTools.ListFolder(many);
                    if (!over.Split('\n').Any(l => l.StartsWith("T\t")) || over.Split('\n').Count(l => l.StartsWith("F\t") || l.StartsWith("D\t")) != RemoteTools.MaxEntries || !over.StartsWith("D\t"))
                        return Fail("a folder of more than " + RemoteTools.MaxEntries + " entries was not cut short properly");
                }
                finally { try { Directory.Delete(many, true); } catch { } }
            }
            // The sign-in task's account: a real one comes back as Windows spells it, a made-up one is refused.
            if (Startup.Account(System.Security.Principal.WindowsIdentity.GetCurrent().Name) == null || Startup.Account(@"NoSuchDomain\no such user 'x") != null)
                return Fail("the sign-in task's account was not checked properly");
            // The service's agent restarts: one failing at once, again and again, ends up retried once a
            // minute (it used to stop at 16 seconds), and one that ran a while is retried in 2 seconds.
            {
                long w = 2000;
                for (int i = 0; i < 10; i++) w = ServiceHost.NextWait(w, 0, true);
                if (w != 60_000) return Fail("agent restart wait after 10 instant failures: " + w + " ms, not 60000");
                if (ServiceHost.NextWait(60_000, 30_000, true) != 2000 || ServiceHost.NextWait(60_000, 0, false) != 2000)
                    return Fail("agent restart wait did not go back to 2 seconds after a long run or a new session");
            }
            // Damaged settings: nulls load without a crash, and a file that cannot be read is kept as .bad.
            {
                string? before = Environment.GetEnvironmentVariable("KOVA_SETTINGS_DIR");
                string settingsDir = Path.Combine(Path.GetTempPath(), "kova-selftest-settings");
                try
                {
                    if (Directory.Exists(settingsDir)) Directory.Delete(settingsDir, true);
                    Directory.CreateDirectory(settingsDir);
                    Settings.TestDir = settingsDir; // in-process, never the environment: honoured even when this test runs elevated (on CI it does)
                    File.WriteAllText(Settings.FilePath, "{\"PasswordEnc\":null,\"SavedPcs\":null}");
                    try
                    {
                        var s = Settings.Load();
                        if (s.Password != "" || s.SavedPcs == null || s.SavedPcs.Count != 0) return Fail("settings with nulls loaded wrongly");
                    }
                    catch (Exception e) { return Fail("settings with nulls: " + e.GetType().Name + " " + e.Message); }
                    File.WriteAllText(Settings.FilePath, "{\"Port\":\"abc\"}");
                    var fresh = Settings.Load();
                    if (fresh.Port != Protocol.DefaultPort || !File.Exists(Settings.FilePath + ".bad") || File.ReadAllText(Settings.FilePath + ".bad") != "{\"Port\":\"abc\"}")
                        return Fail("damaged settings were not kept as settings.json.bad");
                    // Damaged again: the first copy stays as it was, the new one is kept beside it.
                    File.WriteAllText(Settings.FilePath, "{\"Port\":\"xyz\"}");
                    Settings.Load();
                    if (File.ReadAllText(Settings.FilePath + ".bad") != "{\"Port\":\"abc\"}" || Directory.GetFiles(settingsDir, "settings.json.bad-*").Length != 1)
                        return Fail("damaged settings wrote over the earlier settings.json.bad");
                    // A read-only settings.json is still saved to.
                    new Settings { Address = "first" }.Save();
                    File.SetAttributes(Settings.FilePath, FileAttributes.ReadOnly);
                    if (!new Settings { Address = "second" }.Save() || Settings.Load().Address != "second")
                        return Fail("settings were not saved over a read-only settings.json");
                    byte[] exported = new Settings().Export("selftest password");
                    if (!Settings.LooksLikeBackup(exported) || Settings.LooksLikeBackup(new byte[60]) || Settings.LooksLikeBackup("TRBK1"u8.ToArray()))
                        return Fail("a settings backup was not told apart from other files");
                }
                finally
                {
                    Settings.TestDir = null;
                    try { Directory.Delete(settingsDir, true); } catch { }
                }
            }
            // A 1 kHz tone through 48k -> 44.1k -> 48k must keep its level and pitch.
            foreach (var (from, to) in new[] { (48000, 44100), (44100, 48000) })
            {
                var rs = new Resampler(from, to);
                var tone = new float[from * 2];
                for (int i = 0; i < from; i++) tone[i * 2] = tone[i * 2 + 1] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / from);
                var outL = new System.Collections.Generic.List<float>();
                for (int i = 0; i < tone.Length; i += 512) rs.Process(tone.AsSpan(i, Math.Min(512, tone.Length - i)), (l, r) => outL.Add(l));
                var mid = outL.GetRange(1000, to / 2);
                double rms = Math.Sqrt(mid.Average(v => (double)v * v));
                int crossings = 0;
                for (int i = 1; i < mid.Count; i++) if (mid[i - 1] < 0 && mid[i] >= 0) crossings++;
                if (Math.Abs(rms - 0.3536) > 0.005 || Math.Abs(crossings - 500) > 1 || Math.Abs(outL.Count - to) > 40)
                    return Fail($"resampler {from}->{to}: rms {rms:F4}, crossings {crossings}, frames {outL.Count}");
            }

            // Every Opus step: a 1 kHz tone comes back at the same pitch and about the same
            // level, in packets of the step's length, near the step's bitrate.
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int step = 0; step < Protocol.OpusSteps.Length; step++)
                {
                    var (kbps, ms) = Protocol.OpusSteps[step];
                    var enc = OpusBank.CreateEncoder(step);
                    var dec = Concentus.OpusCodecFactory.CreateDecoder(Protocol.AudioRate, 2);
                    int frame = Protocol.AudioRate * ms / 1000;
                    short[] pcm = new short[frame * 2], back = new short[5760 * 2];
                    byte[] packet = new byte[Protocol.MaxOpusBytes];
                    var output = new System.Collections.Generic.List<short>();
                    long bytes = 0;
                    int packets = 1200 / ms, pos = 0;
                    for (int n = 0; n < packets; n++)
                    {
                        for (int f = 0; f < frame; f++, pos++)
                            pcm[f * 2] = pcm[f * 2 + 1] = (short)(0.3 * 32767 * Math.Sin(2 * Math.PI * 1000 * pos / Protocol.AudioRate));
                        int len = enc.Encode(pcm, frame, packet, packet.Length);
                        if (len <= 0 || len > 1200) return Fail("opus " + kbps + ": packet of " + len + " bytes");
                        bytes += len;
                        int decoded = dec.Decode(packet.AsSpan(0, len), back, back.Length / 2, false);
                        if (decoded != frame) return Fail("opus " + kbps + ": decoded " + decoded + " frames, not " + frame);
                        for (int f = 0; f < decoded; f++) output.Add(back[f * 2]);
                    }
                    var mid = output.GetRange(Protocol.AudioRate / 5, Protocol.AudioRate); // one second, after the start
                    double rms = Math.Sqrt(mid.Average(v => (double)v * v)) / 32768;
                    int crossings = 0;
                    for (int k = 1; k < mid.Count; k++) if (mid[k - 1] < 0 && mid[k] >= 0) crossings++;
                    double actual = bytes * 8.0 / (packets * ms);
                    if (Math.Abs(crossings - 1000) > 5 || rms < 0.212 * 0.6 || rms > 0.212 * 1.4 || actual > kbps * 1.3 + 2)
                        return Fail($"opus {kbps} kbit/s: rms {rms:F3}, crossings {crossings}, {actual:0} kbit/s");
                }
                lossless = " | Opus: all " + Protocol.OpusSteps.Length + " steps clean, " + watch.ElapsedMilliseconds + " ms";
            }

            // Audio encryption: a sealed packet opens on the other side; a changed one does not.
            {
                byte[] k = Protocol.DeriveKey("secret", new byte[Protocol.SaltBytes]), n1 = new byte[16], n2 = new byte[16];
                n2[0] = 1;
                using var hostLink = new SecureLink(k, n1, n2, isHost: true);
                using var clientLink = new SecureLink(k, n1, n2, isHost: false);
                byte[] pkt = new byte[5 + 256 + SecureLink.TagSize];
                pkt[0] = Protocol.UdpOpus; pkt[1] = 7; pkt[10] = 42;
                hostLink.SealAudio(pkt, 256);
                if (pkt[10] == 42 && pkt[9] == 0 && pkt[11] == 0) return Fail("audio was not encrypted");
                byte[] bad = (byte[])pkt.Clone();
                bad[100] ^= 1;
                if (!clientLink.OpenAudio(pkt, pkt.Length) || pkt[10] != 42) return Fail("audio did not decrypt");
                if (clientLink.OpenAudio(bad, bad.Length)) return Fail("a tampered audio packet was accepted");
            }

            Host.WrongPasswordDelayMs = 0;
            var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
            string Dump() => string.Join(" | ", log);
            using var host = new Host(47999, "secret", "listen", log.Enqueue);
            string? toHost = null;
            host.ClipboardReceived += t => toHost = t;
            if (Protocol.PortProblem("3389", out _) == null || Protocol.PortProblem("abc", out _) == null || Protocol.PortProblem("47120", out _) != null)
                return Fail("port rules");
            try { new Host(47999, "x", null, log.Enqueue).Dispose(); return Fail("a busy port was accepted"); }
            catch (InvalidOperationException e) when (e.Message.Contains("already used")) { }
            using var silent = new Player(Player.NoDevice, log.Enqueue);
            using var silent2 = new Player(Player.NoDevice, log.Enqueue);
            try { Client.Connect("127.0.0.1", 47999, "nope", silent, log.Enqueue).Dispose(); return Fail("wrong password accepted"); }
            catch (InvalidOperationException e) when (e.Message == "Wrong password.") { }
            using var c = Client.Connect("127.0.0.1", 47999, "secret", silent, log.Enqueue);
            for (int i = 0; i < 40 && c.LastPingMs < 0; i++) System.Threading.Thread.Sleep(100);
            if (c.LastPingMs < 0) return Fail("no ping reply. " + Dump());
            if (c.ListenOnly) return Fail("the main password gave a listener");

            // Clipboard both ways, and a listener's clipboard is ignored.
            string? toClient = null;
            c.ClipboardReceived += t => toClient = t;
            for (int i = 0; i < 100 && host.ControllerFiles == null; i++) System.Threading.Thread.Sleep(20); // the first file lane
            c.SendClipboard("from client ✓");
            c.Files!.ExpectClipboard(); // 4.4: a controller takes the host's clipboard only when it asked for it (Get the remote clipboard)
            host.SendClipboard("from host ✓");
            using var listener = Client.Connect("127.0.0.1", 47999, "listen", silent2, log.Enqueue);
            if (!listener.ListenOnly) return Fail("the listen password gave control");
            listener.SendClipboard("from listener");
            for (int i = 0; i < 40 && (toHost == null || toClient == null); i++) System.Threading.Thread.Sleep(50);
            System.Threading.Thread.Sleep(100);
            if (toHost != "from client ✓" || toClient != "from host ✓") return Fail("clipboard: host got " + toHost + ", client got " + toClient);

            // The microphone (4.3): a controller's reaches the host under its own key; a listener's is never played;
            // a packet played back later, or tampered with, is dropped.
            {
                int heard = 0;
                MicIn.TestHeard = (_, t) => System.Threading.Interlocked.Add(ref heard, t);
                try
                {
                    for (int i = 0; i < 100 && (c.HostVersion == null || listener.HostVersion == null); i++) System.Threading.Thread.Sleep(20);
                    if (!c.CanMic) return Fail("the host does not take a microphone");
                    if (listener.CanMic) return Fail("a listen-only connection was offered the microphone");
                    short[] tone = new short[Protocol.TickFrames * 2];
                    for (int i = 0; i < Protocol.TickFrames; i++) tone[2 * i] = tone[2 * i + 1] = (short)(8000 * Math.Sin(i * 2 * Math.PI * 440 / Protocol.AudioRate));
                    for (uint s = 0; s < 100; s++) { c.TestMicTick(s, tone); if (s % 10 == 9) System.Threading.Thread.Sleep(5); }
                    for (int i = 0; i < 100 && System.Threading.Volatile.Read(ref heard) < 90; i++) System.Threading.Thread.Sleep(20);
                    if (heard < 90) return Fail("microphone: the host heard " + heard + " of 100 ticks");
                    if (!host.TestMicPlaying) return Fail("microphone: the host is not playing it");
                    int before = heard;
                    for (uint s = 0; s < 50; s++) listener.TestMicTick(s, tone);
                    for (uint s = 0; s < 20; s++) c.TestMicTick(s, tone, replay: true); // the controller's oldest numbers again: a replay
                    System.Threading.Thread.Sleep(300);
                    if (heard != before) return Fail("microphone: a listener's or a replayed packet was played (" + (heard - before) + " ticks)");
                    lossless += " | microphone: " + heard + " ticks heard, listener and replay refused";
                }
                finally { MicIn.TestHeard = null; }
            }

            // The remote NVDA as text (4.3): lines go to a PC that asked, and to nobody else.
            {
                var nvdaGot = new System.Collections.Concurrent.ConcurrentQueue<string>();
                c.NvdaSpoke += lines => { foreach (var l in lines) nvdaGot.Enqueue("c " + l); };
                listener.NvdaSpoke += lines => { foreach (var l in lines) nvdaGot.Enqueue("l " + l); };
                c.NvdaText = true;
                System.Threading.Thread.Sleep(200);
                host.NvdaLines(new[] { "Desktop list", "Recycle Bin 1 of 4" });
                for (int i = 0; i < 50 && nvdaGot.Count < 2; i++) System.Threading.Thread.Sleep(20);
                System.Threading.Thread.Sleep(100);
                string all = string.Join("|", nvdaGot);
                if (all != "c Desktop list|c Recycle Bin 1 of 4") return Fail("remote NVDA text: " + all);
                c.NvdaText = false;
                System.Threading.Thread.Sleep(200);
                host.NvdaLines(new[] { "not wanted" });
                System.Threading.Thread.Sleep(200);
                if (nvdaGot.Count != 2) return Fail("remote NVDA text came after it was turned off");
            }

            // A flood of logins that never finish must not touch a session that is
            // already running, and someone else must still be able to log in.
            {
                var flood = new System.Collections.Generic.List<System.Net.Sockets.TcpClient>();
                for (int i = 0; i < 200; i++)
                {
                    var t = new System.Net.Sockets.TcpClient();
                    try { t.Connect("127.0.0.1", 47999); } catch { }
                    flood.Add(t);
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                toClient = null;
                c.Files!.ExpectClipboard(); // 4.4: asked for, as Get the remote clipboard does
                host.SendClipboard("during the flood");
                while (toClient == null && sw.ElapsedMilliseconds < 3000) System.Threading.Thread.Sleep(10);
                long clipMs = sw.ElapsedMilliseconds;
                foreach (var t in flood) t.Dispose();
                if (toClient != "during the flood") return Fail("the running session froze during a login flood");
                if (clipMs > 500) return Fail("the running session was slow during a login flood: " + clipMs + " ms");
                System.Threading.Thread.Sleep(200);
                using var after = new Player(Player.NoDevice, log.Enqueue);
                using var late = Client.Connect("127.0.0.1", 47999, "listen", after, log.Enqueue);
                if (!late.ListenOnly) return Fail("could not log in after a flood");
                lossless += " | login flood: session answered in " + clipMs + " ms";
            }

            // A damaged login (one byte flipped by a relay, like Clumsy's tamper) is reported as
            // damage, never as a wrong password, and never counts toward blocking this PC.
            {
                var helloHit = TamperedLogin(47999, toClient: true, at: 10);
                if (helloHit?.Message != Protocol.DamagedLogin) return Fail("damaged hello: " + (helloHit?.Message ?? "connected"));
                for (int i = 0; i < 6; i++)
                {
                    var answerHit = TamperedLogin(47999, toClient: false, at: 30);
                    if (answerHit?.Message != Protocol.DamagedLogin) return Fail("damaged answer: " + (answerHit?.Message ?? "connected"));
                }
                using var clean = Client.Connect("127.0.0.1", 47999, "listen", new Player(Player.NoDevice, _ => { }), _ => { }); // as a listener, so the test controller stays connected
                lossless += " | damaged logins: retried, never blocked";
            }

            // The 2.2 login, on a host of its own so its wrong guesses cannot block the one above:
            // one salt per start of hosting, and a client gets in again after the host restarts with
            // a new one; a key exchange that is not a real point is a wrong password (padded, never a
            // crash); an older host gets "update both"; the UDP hello's MAC holds.
            {
                static byte[] Salt(int port)
                {
                    using var t = new System.Net.Sockets.TcpClient("127.0.0.1", port);
                    var s = t.GetStream();
                    s.ReadTimeout = 3000;
                    byte[] h = new byte[Protocol.HelloBytes];
                    Protocol.ReadExactly(s, h);
                    return h[Protocol.HelloSaltAt..(Protocol.HelloSaltAt + Protocol.SaltBytes)];
                }
                byte[] firstSalt;
                using (var h1 = new Host(47998, "second", null, _ => { }))
                {
                    firstSalt = Salt(47998);
                    if (!Salt(47998).AsSpan().SequenceEqual(firstSalt)) return Fail("a host changed its salt between logins");
                    using (var p1 = new Player(Player.NoDevice, _ => { }))
                    using (Client.Connect("127.0.0.1", 47998, "second", p1, _ => { })) { }
                    using var t = new System.Net.Sockets.TcpClient("127.0.0.1", 47998);
                    var s = t.GetStream();
                    s.ReadTimeout = 5000;
                    byte[] hello = new byte[Protocol.HelloBytes];
                    Protocol.ReadExactly(s, hello);
                    byte[] answer = new byte[Protocol.AnswerBytes];
                    Protocol.Magic.CopyTo(answer, 0);
                    answer[Protocol.AnswerPublicAt] = 4; // the rest of the point is zeros: not on the curve
                    Protocol.AddCheck(answer);
                    s.Write(answer);
                    byte[] reply = new byte[Protocol.ReplyBytes];
                    try { Protocol.ReadExactly(s, reply); }
                    catch (Exception e) { return Fail("a forged key exchange got no refusal: " + e.Message); }
                    if (reply[0] != 0 || !Protocol.CheckOk(reply)) return Fail("a forged key exchange was not refused like a wrong password");
                }
                using (var h2 = new Host(47998, "second", null, _ => { }))
                {
                    if (Salt(47998).AsSpan().SequenceEqual(firstSalt)) return Fail("hosting again kept the same salt");
                    using var p2 = new Player(Player.NoDevice, _ => { });
                    try { using (Client.Connect("127.0.0.1", 47998, "second", p2, _ => { })) { } }
                    catch (Exception e) { return Fail("could not log in again after the host restarted: " + e.Message); }
                }

                var oldHost = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                oldHost.Start();
                int oldPort = ((System.Net.IPEndPoint)oldHost.LocalEndpoint).Port;
                var oldServe = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        using var a = oldHost.AcceptTcpClient();
                        byte[] b = new byte[24];
                        "TRM7"u8.CopyTo(b);
                        a.GetStream().Write(b);
                        System.Threading.Thread.Sleep(1000);
                    }
                    catch { }
                });
                try
                {
                    using var p3 = new Player(Player.NoDevice, _ => { });
                    using (Client.Connect("127.0.0.1", oldPort, "second", p3, _ => { })) { }
                    return Fail("an older host was accepted");
                }
                catch (Exception e) when (e.Message.Contains("different Kova version")) { }
                catch (Exception e) { return Fail("an older host gave the wrong message: " + e.Message); }
                finally { oldHost.Stop(); }

                byte[] k1 = new byte[32], k2 = new byte[32], n1 = new byte[16], n2 = new byte[16];
                k2[0] = 1;
                using var sender = new SecureLink(k1, n1, n2, isHost: false);
                using var receiver = new SecureLink(k1, n1, n2, isHost: true);
                using var stranger = new SecureLink(k2, n1, n2, isHost: true);
                byte[] udpHello = new byte[Protocol.UdpHelloBytes];
                udpHello[0] = Protocol.UdpHello;
                udpHello[3] = 7;
                sender.SignHello(udpHello);
                if (!receiver.HelloOk(udpHello)) return Fail("a signed UDP hello was refused");
                if (stranger.HelloOk(udpHello)) return Fail("a UDP hello from another login was accepted");
                udpHello[12] ^= 1;
                if (receiver.HelloOk(udpHello)) return Fail("a changed UDP hello was accepted");
                lossless += " | 2.2 login: new salt per start, re-login after restart, forged key refused, old host told to update, UDP hello MAC";
            }

            // Clipboard files and folders both ways, into a temporary holding folder, checked byte for byte.
            string dir = Path.Combine(Path.GetTempPath(), "tailremote-selftest-files");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            FileChannel.StagingOverride = Path.Combine(dir, "stage");
            string album = Path.Combine(dir, "src", "Album");
            Directory.CreateDirectory(Path.Combine(album, "sub"));
            Directory.CreateDirectory(Path.Combine(album, "empty"));
            byte[] content = new byte[700_000];
            new Random(5).NextBytes(content);
            File.WriteAllBytes(Path.Combine(album, "sub", "a.bin"), content);
            File.WriteAllText(Path.Combine(album, "b.txt"), "hello");
            string loose = Path.Combine(dir, "src", "loose file.bin");
            File.WriteAllBytes(loose, content);
            for (int i = 0; i < 100 && (c.Files == null || host.ControllerFiles == null); i++) System.Threading.Thread.Sleep(50);
            if (c.Files == null || host.ControllerFiles == null) return Fail("the file connection never opened");

            string[]? landed = null;
            host.ClipboardFilesReceived += paths => landed = paths;
            c.Files.SendFiles(new[] { album, loose });
            for (int i = 0; i < 200 && landed == null; i++) System.Threading.Thread.Sleep(20);
            if (landed == null || landed.Length != 2) return Fail("clipboard files: " + (landed == null ? "nothing arrived" : landed.Length + " items"));
            string at = Path.GetDirectoryName(landed[0])!;
            if (!File.ReadAllBytes(Path.Combine(at, "Album", "sub", "a.bin")).AsSpan().SequenceEqual(content) || File.ReadAllText(Path.Combine(at, "Album", "b.txt")) != "hello"
                || !Directory.Exists(Path.Combine(at, "Album", "empty")) || !File.ReadAllBytes(Path.Combine(at, "loose file.bin")).AsSpan().SequenceEqual(content))
                return Fail("clipboard files arrived wrong");

            // Big clipboard text the other way, over the file connection.
            string bigText = string.Concat(Enumerable.Repeat("Kova clipboard, the long way round. ", 120_000)); // about 5 MB
            string? gotText = null;
            c.ClipboardReceived += t => gotText = t;
            c.Files!.ExpectClipboard(); // 4.4: a controller takes the host's clipboard only when it asked for it (Get the remote clipboard)
            host.ControllerFiles.SendText(bigText);
            for (int i = 0; i < 200 && gotText == null; i++) System.Threading.Thread.Sleep(20);
            if (gotText != bigText) return Fail("big clipboard text: " + (gotText == null ? "nothing arrived" : gotText.Length + " characters"));

            // Speed: 128 MB over this PC's own connection, paced from the audio ping, checked byte for byte.
            byte[] big = new byte[128 << 20];
            new Random(9).NextBytes(big);
            string bigSrc = Path.Combine(dir, "src", "big.bin");
            File.WriteAllBytes(bigSrc, big);
            landed = null;
            var bigClock = System.Diagnostics.Stopwatch.StartNew();
            c.Files.SendFiles(new[] { bigSrc });
            for (int i = 0; i < 1000 && landed == null; i++) System.Threading.Thread.Sleep(20);
            double bigSecs = bigClock.Elapsed.TotalSeconds;
            if (landed == null || !File.ReadAllBytes(landed[0]).AsSpan().SequenceEqual(big)) return Fail("128 MB file did not arrive whole");
            lossless += " | clipboard files, folders and 5 MB of text both ways | 128 MB at " + (128 / bigSecs).ToString("0") + " MB/s";

            // The audio path's own ping (UDP hello and pong) gets measured.
            for (int i = 0; i < 60 && c.AudioPingMs < 0; i++) System.Threading.Thread.Sleep(50);
            if (c.AudioPingMs < 0) return Fail("the audio ping was never measured");
            lossless += " | audio ping " + c.AudioPingMs + " ms";

            // The remote PC from the controlling one: its version, a folder, its info, getting a file, asking it to update.
            if (c.HostVersion != Updater.Current || !c.CanRemoteTools) return Fail("remote tools: the host said version " + c.HostVersion);
            string listing = c.ListFolderAsync(Path.Combine(dir, "src")).GetAwaiter().GetResult();
            if (!listing.Contains("\tloose file.bin\t")) return Fail("remote folder listing: " + listing);
            if (!c.ListFolderAsync("").GetAwaiter().GetResult().Contains("D\t")) return Fail("remote drives were not listed");
            string info = c.RequestInfoAsync().GetAwaiter().GetResult();
            if (!info.Contains("Name: " + Environment.MachineName) || !info.Contains("Kova: " + Updater.Current)) return Fail("remote info: " + info);
            FileChannel.DownloadsOverride = Path.Combine(dir, "got"); // never the real Downloads
            FileChannel.Transfer? fetched = null;
            c.TransferProgress += t => { if (t.Finished && !t.Outgoing && !t.Clipboard) fetched = t; };
            c.Fetch(new[] { loose });
            for (int i = 0; i < 300 && fetched == null; i++) System.Threading.Thread.Sleep(20);
            string got = Path.Combine(dir, "got", "loose file.bin");
            if (fetched == null || fetched.Failed || !File.Exists(got) || !File.ReadAllBytes(got).AsSpan().SequenceEqual(content))
                return Fail("get files from the remote PC: " + (fetched?.Result ?? "nothing arrived"));

            // Send files here: into the remote folder open in Get files, never over what is there;
            // a destination that is not an existing folder is refused, and nothing is written.
            if (!c.CanSendTo) return Fail("send files here: the host did not say it takes them");
            string into = Path.Combine(dir, "into");
            Directory.CreateDirectory(into);
            File.WriteAllText(Path.Combine(into, "loose file.bin"), "already here");
            FileChannel.Transfer? sentTo = null;
            Action<FileChannel.Transfer> noteSent = t => { if (t.Finished && t.Outgoing && !t.Clipboard) sentTo = t; };
            c.TransferProgress += noteSent;
            FileChannel.Transfer? SendTo(string folder)
            {
                sentTo = null;
                if (!c.SendFilesTo(new[] { loose }, folder)) return null;
                for (int i = 0; i < 300 && sentTo == null; i++) System.Threading.Thread.Sleep(20);
                return sentTo;
            }
            var toFolder = SendTo(into);
            string arrivedTo = Path.Combine(into, "loose file (2).bin");
            if (toFolder == null || toFolder.Failed || !File.Exists(arrivedTo) || !File.ReadAllBytes(arrivedTo).AsSpan().SequenceEqual(content)
                || File.ReadAllText(Path.Combine(into, "loose file.bin")) != "already here")
                return Fail("send files into a remote folder: " + (toFolder?.Result ?? "nothing happened"));
            foreach (string bad in new[] { Path.Combine(dir, "no such folder"), Path.Combine(into, "loose file.bin"), "relative\\folder", @"\\?\" + into, @"\\.\" + into, Path.Combine(into, "CON") })
            {
                var refused = SendTo(bad);
                if (refused == null || !refused.Failed) return Fail("send files into " + bad + ": " + (refused?.Result ?? "no answer") + " (it should have been refused)");
            }
            if (Directory.GetFileSystemEntries(into).Length != 2 || Directory.Exists(Path.Combine(dir, "no such folder")))
                return Fail("a refused send files here still wrote something");
            c.TransferProgress -= noteSent;
            c.RequestUpdate(new Version(99, 0, 0));
            for (int i = 0; i < 100 && !log.Any(l => l.Contains("cannot update itself")); i++) System.Threading.Thread.Sleep(20);
            if (!log.Any(l => l.Contains("cannot update itself"))) return Fail("the update request got no answer");
            lossless += " | remote folders, info, get files, send files into a folder, update answer";

            // The remote screen. The encoder: a still screen sends nothing, a small change only a few
            // strips, and lossless comes back exact. Then for real, host to client: nothing is taken
            // until someone watches; the picture is right and follows a change; pausing stops it; on a
            // fast line it climbs to every pixel exact; a host with no picture says why; a listener
            // cannot watch; and nothing is kept once watching stops.
            {
                var test = new TestScreen();
                var enc = new ScreenVideo(test.Take);
                var exact = new VideoSettings(0, VideoSettings.Lossless, 15);
                byte[] whole = enc.Update(1, true, exact)!;
                using (var canvas = new VideoCanvas())
                {
                    canvas.Apply(whole, 0);
                    foreach (var (x, y) in new[] { (0, 0), (115, 120), (639, 359), (300, 50) })
                        if (canvas.At(x, y) != test.At(x, y)) return Fail("a lossless picture is not exact at " + x + "," + y);
                    if (enc.Update(1, false, exact)!.Length != 0) return Fail("a still screen sent a picture");
                    test.SquareX = 400; test.SquareY = 200;
                    byte[] change = enc.Update(1, false, exact)!;
                    int strips = canvas.Apply(change, 0);
                    if (strips == 0 || strips > 4) return Fail("moving one square sent " + strips + " strips");
                    if (canvas.At(410, 210) != test.At(410, 210) || canvas.At(110, 110) != test.At(110, 110)) return Fail("the change did not land exactly");
                    if (change.Length * 5 > whole.Length) return Fail("a small change cost " + change.Length + " bytes, the whole screen " + whole.Length);
                }

                test.SquareX = 100; test.SquareY = 100;
                var screen = new ScreenVideo(test.Take);
                host.VideoUpdate = screen.Update;
                host.VideoForget = screen.Forget;
                if (host.TestViewers != 0 || screen.Viewers != 0) return Fail("the screen was being taken before anyone watched");
                if (!c.CanWatch) return Fail("the host did not offer its screen");
                using (var watch = c.WatchScreen()!)
                {
                    for (int i = 0; i < 200 && watch.Pictures < 1; i++) System.Threading.Thread.Sleep(25);
                    if (watch.Pictures < 1) return Fail("no picture of the screen arrived");
                    foreach (var (x, y) in new[] { (5, 5), (320, 180), (110, 110), (630, 350) })
                        if (!TestScreen.Near(watch.Canvas.At(x, y), test.At(x, y), 40)) return Fail("the picture is wrong at " + x + "," + y);
                    test.SquareX = 400; test.SquareY = 200;
                    bool Shows() => TestScreen.Near(watch.Canvas.At(410, 210), test.At(410, 210), 40) && TestScreen.Near(watch.Canvas.At(110, 110), test.At(110, 110), 40);
                    for (int i = 0; i < 200 && !Shows(); i++) System.Threading.Thread.Sleep(25);
                    if (!Shows()) return Fail("a change on the screen did not show");
                    // On a fast line, still: it climbs to every pixel exact by itself.
                    bool Exact() => watch.Canvas.At(410, 210) == test.At(410, 210) && watch.Canvas.At(300, 50) == test.At(300, 50) && watch.Canvas.At(5, 300) == test.At(5, 300);
                    for (int i = 0; i < 600 && !Exact(); i++) System.Threading.Thread.Sleep(25);
                    if (!Exact()) return Fail("on a fast line the picture never became exact");
                    watch.Pause(true);
                    System.Threading.Thread.Sleep(300);
                    int paused = watch.Pictures;
                    test.SquareX = 200; test.SquareY = 40;
                    System.Threading.Thread.Sleep(1200);
                    if (watch.Pictures != paused) return Fail("pictures kept coming while paused");
                    watch.Pause(false);
                    bool Moved() => TestScreen.Near(watch.Canvas.At(210, 50), test.At(210, 50), 40);
                    for (int i = 0; i < 200 && !Moved(); i++) System.Threading.Thread.Sleep(25);
                    if (!Moved()) return Fail("the picture did not come back after a pause");
                    if (host.TestViewers != 1 || screen.Viewers != 1) return Fail("watching once counted " + host.TestViewers + " viewers");
                }
                for (int i = 0; i < 200 && (host.TestViewers != 0 || screen.Viewers != 0); i++) System.Threading.Thread.Sleep(25);
                if (host.TestViewers != 0 || screen.Viewers != 0) return Fail("the host kept taking pictures after watching stopped");

                host.VideoUpdate = (_, _, _) => null; // nothing to show (the lock screen, from the window)
                string? note = null;
                using (var watch = c.WatchScreen()!)
                {
                    watch.Note += t => note = t;
                    for (int i = 0; i < 200 && note == null; i++) System.Threading.Thread.Sleep(25);
                }
                if (note == null || !note.StartsWith("No picture")) return Fail("a host with no picture did not say why");
                using (var onlyListening = Client.Connect("127.0.0.1", 47999, "listen", new Player(Player.NoDevice, _ => { }), _ => { }))
                {
                    for (int i = 0; i < 100 && onlyListening.HostVersion == null; i++) System.Threading.Thread.Sleep(20);
                    if (onlyListening.CanWatch || onlyListening.WatchScreen() != null) return Fail("a listener could watch the screen");
                }
                // H.264, where this PC has an encoder: things moving go as video, many a second, and once
                // still, an exact picture again. (Some Windows have none that works: still pictures then.)
                bool hasH264;
                using (var probe = H264Encoder.Create(64, 64, 30, 500)) hasH264 = probe != null;
                string h264Note = "no H.264 encoder on this PC, still pictures only";
                if (hasH264)
                {
                    var moving = new TestScreen();
                    var video = new ScreenVideo(moving.Take);
                    host.VideoUpdate = video.Update;
                    host.VideoForget = video.Forget;
                    bool go = true;
                    var mover = new System.Threading.Thread(() => { int i = 0; while (go) { i++; moving.SquareX = 20 + i * 9 % 560; moving.SquareY = 40 + i * 5 % 260; System.Threading.Thread.Sleep(16); } }) { IsBackground = true };
                    using (var watch = c.WatchScreen()!)
                    {
                        mover.Start();
                        for (int i = 0; i < 400 && watch.VideoFrames < 30; i++) System.Threading.Thread.Sleep(25);
                        if (watch.VideoFrames < 30) { go = false; return Fail("moving things did not go as H.264 (" + watch.VideoFrames + " frames)"); }
                        int peak = 0;
                        for (int i = 0; i < 20; i++) { System.Threading.Thread.Sleep(100); peak = Math.Max(peak, watch.PerSecond); }
                        go = false;
                        mover.Join();
                        bool Exact() => watch.Canvas.At(moving.SquareX + 5, moving.SquareY + 5) == moving.At(moving.SquareX + 5, moving.SquareY + 5)
                            && watch.Canvas.At(5, 5) == moving.At(5, 5) && watch.Canvas.At(600, 300) == moving.At(600, 300);
                        for (int i = 0; i < 400 && !Exact(); i++) System.Threading.Thread.Sleep(25);
                        if (!Exact()) return Fail("once things stopped moving, the picture did not become exact again");
                        if (peak < 25) return Fail("only " + peak + " frames a second while things moved");
                        h264Note = "H.264 at " + peak + " frames a second while moving, exact once still";
                    }
                }
                // An odd-size screen (641 by 361; H.264 only takes even sizes): moving, then still, exact again.
                {
                    var odd = new TestScreen { Width = 641, Height = 361 };
                    var oddVideo = new ScreenVideo(odd.Take);
                    var oddSettings = new VideoSettings(0, VideoSettings.Lossless, 15, 4000);
                    using var oddCanvas = new VideoCanvas();
                    bool again = true, wasMoving = false;
                    void OddStep()
                    {
                        byte[]? u = oddVideo.Update(1, again, oddSettings);
                        again = false;
                        if (u != null && u.Length > 0 && oddCanvas.Apply(u, 0) < 0) again = true;
                        wasMoving |= oddVideo.IsMoving(1);
                    }
                    for (int i = 0; i < 60; i++) { odd.SquareX = 20 + i * 37 % 560; odd.SquareY = 30 + i * 23 % 280; OddStep(); System.Threading.Thread.Sleep(16); }
                    bool OddExact() => oddCanvas.At(odd.SquareX + 5, odd.SquareY + 5) == odd.At(odd.SquareX + 5, odd.SquareY + 5)
                        && oddCanvas.At(640, 360) == odd.At(640, 360) && oddCanvas.At(5, 5) == odd.At(5, 5);
                    for (int i = 0; i < 200 && !OddExact(); i++) { OddStep(); System.Threading.Thread.Sleep(20); }
                    if (!OddExact()) return Fail("a 641 by 361 screen was not exact once it stopped moving");
                    if (hasH264 && !wasMoving) return Fail("a 641 by 361 screen moving never went as H.264");
                }
                // A key frame asked of an encoder already running is a fresh start: a new decoder shows it.
                if (hasH264)
                {
                    var kf = new TestScreen();
                    using var running = H264Encoder.Create(640, 360, 30, 4000)!;
                    for (int i = 0; i < 6; i++) { kf.SquareX = 50 + i * 20; running.Encode(kf.Take(0)!, i == 0, out _); }
                    kf.SquareX = 300; kf.SquareY = 150;
                    byte[]? key = running.Encode(kf.Take(0)!, true, out bool isKey);
                    if (key == null || key.Length == 0) return Fail("a key frame asked of a running encoder did not come");
                    using var fresh = H264Decoder.Create();
                    var px = new byte[640 * 360 * 4];
                    bool kgot = false;
                    if (fresh == null || !fresh.Decode(key, 0, key.Length, 640, 360, px, out kgot) || !kgot)
                        return Fail("a fresh decoder could not show a key frame from a running encoder (marked key " + isKey + ", decoded " + kgot + ")");
                    foreach (var (x, y) in new[] { (5, 5), (310, 160), (600, 300), (100, 300) })
                    {
                        int i = (y * 640 + x) * 4;
                        if (!TestScreen.Near(System.Drawing.Color.FromArgb(px[i + 2], px[i + 1], px[i]), kf.At(x, y), 40)) return Fail("a key frame from a running encoder decoded wrong at " + x + "," + y);
                    }
                }
                // Windows N (no Media Foundation): no encoder or decoder, no exception, and still pictures
                // follow a moving square.
                Mf.TestUnavailable = true;
                try
                {
                    if (H264Encoder.Create(640, 360, 30, 2000) != null || H264Decoder.Create() != null) return Fail("without Media Foundation an H.264 encoder or decoder was made");
                    var noMf = new TestScreen();
                    var noMfVideo = new ScreenVideo(noMf.Take);
                    var noMfSettings = new VideoSettings(0, 82, 15, 2000);
                    using var noMfCanvas = new VideoCanvas();
                    for (int i = 0; i < 40; i++)
                    {
                        noMf.SquareX = 20 + i * 13 % 560; noMf.SquareY = 30 + i * 7 % 280;
                        byte[]? u = noMfVideo.Update(1, i == 0, noMfSettings);
                        if (u is { Length: > 0 })
                        {
                            if (u[0] != ScreenVideo.StillPicture) return Fail("without Media Foundation something other than still pictures was sent");
                            noMfCanvas.Apply(u, 0);
                        }
                        System.Threading.Thread.Sleep(16);
                    }
                    if (!TestScreen.Near(noMfCanvas.At(noMf.SquareX + 5, noMf.SquareY + 5), noMf.At(noMf.SquareX + 5, noMf.SquareY + 5), 40) || !TestScreen.Near(noMfCanvas.At(5, 5), noMf.At(5, 5), 40))
                        return Fail("without Media Foundation the picture did not follow a moving square");
                }
                finally { Mf.TestUnavailable = false; }
                h264Note += "; odd sizes exact once still" + (hasH264 ? ", a forced key frame shows on a fresh decoder" : "") + ", without Media Foundation still pictures follow";
                var real = ScreenCapture.Capture(0);
                // Which screen: a PC with 3 says so first, then shows the one chosen at its own size; one it
                // does not have is refused and the one shown stays. And this PC's own list has a main screen.
                {
                    var three = new TestScreens();
                    var several = new ScreenVideo(three.Take, three.Screens);
                    var exact3 = new VideoSettings(0, VideoSettings.Lossless, 15);
                    byte[]? said = several.Update(1, true, exact3);
                    var first = said is { Length: > 1 } && said[0] == ScreenVideo.ScreenNote ? ScreenChoice.From(said, 1, said.Length - 1) : null;
                    if (first == null || first.Count != 3 || first.Shown != 0 || !first.Screen.Main) return Fail("a PC with 3 screens did not say so first");
                    if (several.Update(1, true, exact3) is not { Length: > 0 } mainPicture || mainPicture[0] != ScreenVideo.StillPicture || BitConverter.ToUInt16(mainPicture, 1) != 640)
                        return Fail("after saying which screen, the main one was not shown");
                    said = several.Update(1, false, exact3 with { Screen = 2 });
                    var chose = said is { Length: > 1 } && said[0] == ScreenVideo.ScreenNote ? ScreenChoice.From(said, 1, said.Length - 1) : null;
                    if (chose == null || chose.Shown != 2 || chose.Refused || chose.Say() != "Screen 3 of 3, 320 by 480, left of the main screen.")
                        return Fail("choosing screen 3 said " + (chose?.Say() ?? "nothing"));
                    using (var canvas3 = new VideoCanvas())
                    {
                        byte[]? third = several.Update(1, false, exact3 with { Screen = 2 });
                        if (third is not { Length: > 0 } || canvas3.Apply(third, 0) <= 0) return Fail("screen 3 was not shown after choosing it");
                        var t3 = three.All[2];
                        if (canvas3.At(319, 479) != t3.At(319, 479) || canvas3.At(t3.SquareX + 5, t3.SquareY + 5) != t3.At(t3.SquareX + 5, t3.SquareY + 5) || canvas3.At(320, 0) != System.Drawing.Color.Empty)
                            return Fail("screen 3 was not shown exactly at its own size");
                    }
                    said = several.Update(1, false, exact3 with { Screen = 8 });
                    var no = said is { Length: > 1 } && said[0] == ScreenVideo.ScreenNote ? ScreenChoice.From(said, 1, said.Length - 1) : null;
                    if (no == null || !no.Refused || no.Shown != 2 || no.Asked != 8) return Fail("screen 9 of 3 was not refused (" + (no?.Say() ?? "nothing said") + ")");
                    var mine = ScreenCapture.Screens();
                    if (mine.Count(s => s.Main) != 1 || !mine[0].Main || mine.Any(s => s.Width <= 0 || s.Height <= 0)) return Fail("this PC's screens have no main one first (" + mine.Count + " found)");
                    lossless += " | which screen: told, chosen, shown exactly, 9 of 3 refused; this PC has " + mine.Count + (mine.Count == 1 ? " screen" : " screens");
                }
                lossless += " | remote screen: exact, follows changes, pauses, climbs to lossless, says why, listeners refused; " + h264Note + "; this screen " +
                    (real == null ? "cannot be taken here" : real.Width + " by " + real.Height);
            }

            // A host that updates says so: the controlling PC reports the update, never a broken connection.
            string? goneWhy = null;
            c.Disconnected += why => goneWhy = why;
            host.Leave(Protocol.LeavingUpdating, "9.9.9");
            host.Dispose();
            for (int i = 0; i < 60 && goneWhy == null; i++) System.Threading.Thread.Sleep(50);
            if (goneWhy != "The remote PC is updating Kova to version 9.9.9." || c.Leaving != Protocol.LeavingUpdating)
                return Fail("updating host: the controller said " + (goneWhy ?? "nothing"));
            lossless += " | update goodbye";

            // A settings backup comes back whole with its password, and not at all with a wrong one.
            var original = new Settings { Address = "backup-test", Port = 47555, Password = "main secret", ListenPassword = "listen secret" };
            original.SavedPcs.Add(new SavedPc { Address = "saved-one", Port = 47120, PasswordEnc = Settings.Protect("saved secret") });
            byte[] backup = original.Export("backup password");
            var restored = Settings.Import(backup, "backup password");
            if (restored.Address != "backup-test" || restored.Port != 47555 || restored.Password != "main secret" || restored.ListenPassword != "listen secret"
                || restored.SavedPcs.Count != 1 || Settings.Unprotect(restored.SavedPcs[0].PasswordEnc) != "saved secret")
                return Fail("a settings backup did not come back whole");
            if (System.Text.Encoding.UTF8.GetString(backup).Contains("secret")) return Fail("a settings backup has a password in plain sight");
            try { Settings.Import(backup, "wrong password"); return Fail("a settings backup opened with the wrong password"); }
            catch (WrongBackupPasswordException) { }
            lossless += " | settings backup";

            // The real Windows clipboard, as when clipboard text and files arrive. Trimmed too far, the
            // release build hung or crashed here (2.0.0 to 2.1.3), and nothing else noticed.
            {
                string before = Path.Combine(Path.GetTempPath(), "tailremote-cliptest.txt");
                var clip = new System.Threading.Thread(() => ClipTest()) { IsBackground = true };
                clip.Start();
                if (!clip.Join(20_000)) return Fail("putting text on the Windows clipboard hung (the release build is trimmed too far)");
                string said = File.Exists(before) ? File.ReadAllText(before) : "";
                if (said.Contains("FAIL") && !said.Contains("ExternalException") && !said.Contains("COMException"))
                    return Fail("the Windows clipboard: " + said.Trim().Split('\n').Last());
                lossless += said.Contains("\nok") || said.EndsWith("ok" + Environment.NewLine) ? " | Windows clipboard" : " | Windows clipboard not reachable here";
            }

            // The window and its menu bar still build in the trimmed exe (never shown, so nothing is saved).
            using (var form = new MainForm(false, false, false))
            {
                form.CreateControl();
                int items = 0;
                void Count(ToolStripItemCollection list) { foreach (ToolStripItem i in list) { items++; if (i is ToolStripMenuItem m) Count(m.DropDownItems); } }
                if (form.MainMenuStrip == null) return Fail("the window has no menu bar");
                Count(form.MainMenuStrip.Items);
                if (items < 30) return Fail("the menu bar has only " + items + " items");
                // Each menu really on the bar, even with Windows' largest text: a bar hides what does not fit.
                form.Font = new System.Drawing.Font(form.Font.FontFamily, form.Font.Size * 2.5f);
                form.MainMenuStrip.PerformLayout();
                foreach (ToolStripItem top in form.MainMenuStrip.Items)
                    if (top.Placement != ToolStripItemPlacement.Main) return Fail("the " + (top.Text ?? "").Replace("&", "") + " menu is hidden off the menu bar");
                lossless += " | window with " + items + " menu items";
                if (!AboutMenu.Resource("CHANGES.txt").Split('\n').Any(l => l.Trim() == Updater.Current.ToString()) || AboutMenu.Resource("README.txt").Length == 0)
                    return Fail("the changelog inside Kova has no section for " + Updater.Current + ", or the guide is missing");
            }
            Directory.Delete(dir, true);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "tailremote-selftest.txt"), "ok, ping " + c.LastPingMs + " ms. " + Dump() + lossless + CableReport());
            return 0;

            // Connects through a relay that flips one byte at 'at' in one direction; the error, or null.
            static Exception? TamperedLogin(int port, bool toClient, int at)
            {
                var relay = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                relay.Start();
                int relayPort = ((System.Net.IPEndPoint)relay.LocalEndpoint).Port;
                new System.Threading.Thread(() =>
                {
                    try
                    {
                        using var a = relay.AcceptTcpClient();
                        using var b = new System.Net.Sockets.TcpClient("127.0.0.1", port);
                        var sa = a.GetStream();
                        var sb = b.GetStream();
                        void Pump(Stream from, Stream to, bool flip)
                        {
                            var buf = new byte[4096];
                            long pos = 0;
                            int n;
                            try
                            {
                                while ((n = from.Read(buf)) > 0)
                                {
                                    if (flip && at >= pos && at < pos + n) buf[at - pos] ^= 0x40;
                                    pos += n;
                                    to.Write(buf, 0, n);
                                }
                            }
                            catch { }
                            try { to.Close(); } catch { }
                        }
                        var back = new System.Threading.Thread(() => Pump(sb, sa, toClient)) { IsBackground = true };
                        back.Start();
                        Pump(sa, sb, !toClient);
                        back.Join(3000);
                    }
                    catch { }
                    finally { relay.Stop(); }
                }) { IsBackground = true }.Start();
                try
                {
                    using var p = new Player(Player.NoDevice, _ => { });
                    using var c = Client.Connect("127.0.0.1", relayPort, "secret", p, _ => { });
                    return null;
                }
                catch (Exception e) { return e; }
            }

            // Read-only: which virtual cable output setup would rename.
            static string CableReport()
            {
                var d = AudioSetup.FindCable();
                string name = "none";
                if (d != null) { d.GetId(out string id); name = id; }
                string sig = "";
                // TAILREMOTE_VBCHECK: a real VB-Cable installer, and something that is not one.
                if (Environment.GetEnvironmentVariable("TAILREMOTE_VBCHECK") is string vb && File.Exists(vb))
                    sig = ", VB-Audio signature: genuine " + AudioSetup.SignedByVbAudio(vb) + ", other exe " + AudioSetup.SignedByVbAudio(Environment.ProcessPath!);
                return " | cable output: " + name + sig;
            }

            int Fail(string why)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "tailremote-selftest.txt"), "FAIL: " + why);
                return 1;
            }
        }
    }
}
