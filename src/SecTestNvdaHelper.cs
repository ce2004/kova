using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Security tests (--securitytest NvdaHelper): the Kova Helper NVDA add-on and Kova's side of it. Returns null when
    /// every check passes, else what failed. Never touches the real NVDA, its settings or its pipe name.
    /// </summary>
    internal static class SecTestNvdaHelper
    {
        internal static string? Run()
        {
            var args = Environment.GetCommandLineArgs();
            int live = Array.IndexOf(args, "live");
            if (live >= 0) return Live(args.Skip(live + 1).ToArray());
            return Tests();
        }

        private static string? Tests() => Codec() ?? Perform() ?? Package() ?? Installer() ?? Pipe() ?? Squat() ?? AddonEndToEnd();

        // ---- Live tools for this PC's real NVDA (never run by the plain test) ----
        // --securitytest NvdaHelper live state | install | update | remove | listen SECONDS
        // Results go to %TEMP%\kova-nvdahelper-live.txt.
        private static string? Live(string[] a)
        {
            string file = Path.Combine(Path.GetTempPath(), "kova-nvdahelper-live.txt");
            var lines = new List<string>();
            void Note(string s) { lock (lines) lines.Add(DateTime.Now.ToString("HH:mm:ss.fff ") + s); }
            string mode = a.Length > 0 ? a[0] : "state";
            try
            {
                switch (mode)
                {
                    case "state":
                        Note(NvdaHelper.GetState().ToString());
                        break;
                    case "install": case "update": case "remove":
                        var action = mode == "install" ? NvdaHelperAction.Install : mode == "update" ? NvdaHelperAction.Update : NvdaHelperAction.Remove;
                        Note("before: " + NvdaHelper.GetState());
                        Note("said: " + NvdaHelper.Apply(action, false, Note));
                        Thread.Sleep(6000); // the restart happens 3 seconds later
                        Note("after: " + NvdaHelper.GetState());
                        break;
                    case "listen":
                    {
                        int secs = a.Length > 1 && int.TryParse(a[1], out int s) ? s : 30;
                        var delays = new List<double>();
                        var rx = new System.Text.RegularExpressions.Regex(@"kova latency (\d\d):(\d\d):(\d\d)\.(\d\d\d)");
                        using var server = new NvdaHelper.Server(Note);
                        server.ConnectedChanged += on => Note("connected: " + on + " (" + server.AddonVersion + ")");
                        server.Event += e =>
                        {
                            var now = DateTime.Now;
                            Note(e.ToString());
                            if (e.Kind != NvdaEventKind.Speak) return;
                            var m = rx.Match(e.SpokenText);
                            if (!m.Success) return;
                            var sent = now.Date + new TimeSpan(0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
                            lock (delays) delays.Add((now - sent).TotalMilliseconds);
                        };
                        var sw = Stopwatch.StartNew();
                        while (sw.Elapsed.TotalSeconds < secs) Thread.Sleep(100);
                        Note("listening: " + server.Listening + " on " + server.ListeningName + ", refused " + server.Refused + ", dropped " + server.Dropped);
                        lock (delays)
                        {
                            delays.Sort();
                            if (delays.Count > 0)
                                Note("latency: " + delays.Count + " lines, median " + delays[delays.Count / 2].ToString("0.#") + " ms, best " + delays[0].ToString("0.#") + " ms, worst " + delays[^1].ToString("0.#") + " ms; all: " + string.Join(" ", delays.Select(d => d.ToString("0"))));
                        }
                        break;
                    }
                    default:
                        return "unknown live mode " + mode;
                }
                return null;
            }
            finally { lock (lines) File.WriteAllLines(file, lines); }
        }

        // ---- NvdaEvent encoding ----

        private static string? Codec()
        {
            var evs = new[]
            {
                NvdaEvent.Speak(new NvdaSpeechPart("Hello world"), new NvdaSpeechPart("A", 30, Capital: true), new NvdaSpeechPart("é ✓ 😀", -100, Spelled: true)),
                NvdaEvent.Cancel(),
                NvdaEvent.Beep(440, 50, 0, 100), NvdaEvent.Beep(20, 1, 100, 0), NvdaEvent.Beep(20000, 5000, 50, 50),
                NvdaEvent.Wave("error"), NvdaEvent.Wave("browse_Mode-2"),
                NvdaEvent.Braille(""), NvdaEvent.Braille("⠓⠑ hello"),
                NvdaEvent.Speak(new NvdaSpeechPart(new string('x', NvdaEvent.MaxText))),
            };
            foreach (var ev in evs)
            {
                var back = NvdaEvent.Decode(ev.Encode());
                if (back == null || back.ToString() != ev.ToString() || back.Kind != ev.Kind) return "a " + ev.Kind + " did not come back the same: " + ev;
                if (ev.Kind == NvdaEventKind.Speak && !back.Parts.SequenceEqual(ev.Parts)) return "speech parts did not come back the same";
                if (ev.Kind == NvdaEventKind.Beep && (back.Hz != ev.Hz || back.Ms != ev.Ms || back.Left != ev.Left || back.Right != ev.Right)) return "a beep did not come back the same";
                // Every shorter body, and the body with a byte added, is refused.
                byte[] b = ev.Encode();
                for (int n = 0; n < b.Length; n++) if (NvdaEvent.Decode(b.AsSpan(0, n)) != null && !(ev.Kind == NvdaEventKind.Cancel && n == 1)) return "a cut-short " + ev.Kind + " (" + n + " of " + b.Length + " bytes) was accepted";
                if (NvdaEvent.Decode(b.Concat(new byte[] { 0 }).ToArray()) != null) return "a " + ev.Kind + " with a byte too many was accepted";
            }
            byte[] Speak(int flags, int pitch, byte[] text) => new byte[] { 1, 1, (byte)flags, (byte)(sbyte)pitch, (byte)text.Length, (byte)(text.Length >> 8) }.Concat(text).ToArray();
            var bad = new (string What, byte[] Body)[]
            {
                ("empty", Array.Empty<byte>()), ("kind 0", new byte[] { 0 }), ("kind 9", new byte[] { 9 }),
                ("no parts", new byte[] { 1, 0 }), ("65 parts", new byte[] { 1, 65 }),
                ("unknown flag", Speak(4, 0, Encoding.UTF8.GetBytes("a"))), ("pitch 101", Speak(0, 101, Encoding.UTF8.GetBytes("a"))),
                ("pitch -101", Speak(0, -101, Encoding.UTF8.GetBytes("a"))), ("empty part", Speak(0, 0, Array.Empty<byte>())),
                ("broken UTF-8", Speak(0, 0, new byte[] { 0xC3, 0x28 })), ("half a two-part character", Speak(0, 0, new byte[] { 0xED, 0xA0, 0x80 })),
                ("4001 characters", Speak(0, 0, Encoding.UTF8.GetBytes(new string('y', 4001)))),
                ("hz 19", new byte[] { 3, 19, 0, 50, 0, 50, 50 }), ("hz 20001", new byte[] { 3, 0x21, 0x4E, 50, 0, 50, 50 }),
                ("ms 0", new byte[] { 3, 0xB8, 1, 0, 0, 50, 50 }), ("ms 5001", new byte[] { 3, 0xB8, 1, 0x89, 0x13, 50, 50 }),
                ("volume 101", new byte[] { 3, 0xB8, 1, 50, 0, 101, 50 }),
                ("wave length 0", new byte[] { 4, 0 }), ("wave with a path", new byte[] { 4, 5, (byte)'.', (byte)'.', (byte)'\\', (byte)'x', (byte)'y' }),
                ("wave with a dot", new byte[] { 4, 9 }.Concat(Encoding.ASCII.GetBytes("error.wav")).ToArray()),
                ("wave with a space", new byte[] { 4, 3, (byte)'a', (byte)' ', (byte)'b' }),
                ("wave of 41", new byte[] { 4, 41 }.Concat(Encoding.ASCII.GetBytes(new string('w', 41))).ToArray()),
                ("wave not ASCII", new byte[] { 4, 2, 0xC3, 0xA9 }),
                ("braille of 4001", new byte[] { 5, 0xA1, 0x0F }.Concat(Encoding.UTF8.GetBytes(new string('b', 4001))).ToArray()),
                ("cancel with a byte", new byte[] { 2, 0 }),
                ("huge", new byte[NvdaEvent.MaxBody + 1]),
            };
            foreach (var (what, body) in bad) if (NvdaEvent.Decode(body) != null) return "a body with " + what + " was accepted";
            foreach (string n in new[] { "", "../x", "a/b", "a b", "error.wav", new string('w', 41), "é" })
            {
                if (NvdaEvent.IsWaveName(n)) return "'" + n + "' counted as a sound name";
                try { NvdaEvent.Wave(n); return "a Wave event could be made named '" + n + "'"; } catch (ArgumentException) { }
            }
            if (NvdaHelper.Server.Hello(new byte[] { 0, 5 }.Concat(Encoding.ASCII.GetBytes("1.0.0")).ToArray()) != "1.0.0") return "a good hello was refused";
            foreach (var h in new[] { new byte[] { 0, 0 }, new byte[] { 1, 1, (byte)'1' }, new byte[] { 0, 2, (byte)'1', (byte)'x' }, new byte[] { 0, 3, (byte)'1' } })
                if (NvdaHelper.Server.Hello(h) != null) return "a bad hello was accepted";
            return null;
        }

        // ---- The controlling PC's side ----

        private static string? Perform()
        {
            string ssml = NvdaEvent.Ssml(new[] { new NvdaSpeechPart("cap <a&b>"), new NvdaSpeechPart("A", 30, true), new NvdaSpeechPart("x", 0, false, true) });
            if (ssml != "<speak>cap &lt;a&amp;b&gt; <prosody pitch=\"160%\">A</prosody> <say-as interpret-as=\"characters\">x</say-as> </speak>") return "the SSML was " + ssml;
            if (NvdaEvent.Ssml(new[] { new NvdaSpeechPart("\u202Eevil\u0007") }) != "<speak>evil </speak>") return "text in SSML was not cleaned";
            var chunks = NvdaEvent.Chunks(string.Join(" ", Enumerable.Repeat("word", 600)), 1000);
            if (chunks.Length != 3 || chunks.Any(c => c.Length > 1000) || string.Join(" ", chunks) != string.Join(" ", Enumerable.Repeat("word", 600))) return "long speech was not split into whole lines";
            var seen = new List<string>();
            string? ssmlGot = null; int cancels = 0;
            NvdaEvent.PerformedForTest = seen.Add;
            NvdaEvent.RemoteSsml = (s, p) => ssmlGot = s + "|" + p;
            NvdaEvent.RemoteCancel = () => cancels++;
            try
            {
                NvdaEvent.Perform(NvdaEvent.Speak(new NvdaSpeechPart("cap"), new NvdaSpeechPart("A", 30, true)));
                NvdaEvent.Perform(NvdaEvent.Cancel());
                NvdaEvent.Perform(NvdaEvent.Beep(440, 5, 10, 10), sounds: false);
            }
            finally { NvdaEvent.PerformedForTest = null; NvdaEvent.RemoteSsml = null; NvdaEvent.RemoteCancel = null; }
            if (ssmlGot != "<speak>cap <prosody pitch=\"160%\">A</prosody> </speak>|cap A") return "a capital with pitch did not go to SSML: " + ssmlGot;
            if (cancels != 1 || seen.Count != 3) return "Perform did not act on each event once";
            var (pcm, rate) = NvdaSounds.MakeBeep(1000, 100, 100, 0);
            if (rate != 44100 || pcm.Length != 4410 * 2 || pcm.Where((_, i) => i % 2 == 1).Any(s => s != 0) || pcm.Max() < 5000) return "a beep was not made as asked";
            string err = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVDA", "waves", "error.wav");
            if (File.Exists(err) && NvdaSounds.ParseWav(File.ReadAllBytes(err)) is not { Pcm.Length: > 1000 }) return "NVDA's own error.wav could not be read";
            if (NvdaSounds.ParseWav(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVEjunk")) != null) return "a junk wave file was accepted";
            return null;
        }

        // ---- The add-on inside the exe ----

        private static string? Package()
        {
            byte[]? p = NvdaHelper.Package();
            if (p == null) return "the Kova Helper add-on is not inside the exe";
            using (var zip = new ZipArchive(new MemoryStream(p)))
            {
                var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
                if (!names.Contains("globalPlugins/kovaHelper/__init__.py")) return "the add-on has no globalPlugins/kovaHelper/__init__.py: " + string.Join(", ", names);
                if (names.Any(n => n.Contains("__pycache__") || n.EndsWith(".pyc"))) return "the add-on carries compiled Python";
            }
            var m = NvdaHelper.ReadManifest(p);
            if (m == null) return "the add-on's manifest could not be read";
            if (m.GetValueOrDefault("name") != NvdaHelper.AddonName) return "the add-on is named " + m.GetValueOrDefault("name");
            if (m.GetValueOrDefault("summary") != "Kova Helper") return "the add-on's summary is " + m.GetValueOrDefault("summary");
            if (string.IsNullOrEmpty(m.GetValueOrDefault("description")) || string.IsNullOrEmpty(m.GetValueOrDefault("author"))) return "the add-on has no description or author";
            if (!System.Text.RegularExpressions.Regex.IsMatch(m.GetValueOrDefault("version") ?? "", @"^\d+\.\d+\.\d+$")) return "the add-on's version is " + m.GetValueOrDefault("version");
            var api = new System.Text.RegularExpressions.Regex(@"^(0|\d{4})\.(\d)(?:\.(\d))?$"); // NVDA's own rule (addonAPIVersion)
            string min = m.GetValueOrDefault("minimumnvdaversion") ?? "", last = m.GetValueOrDefault("lasttestednvdaversion") ?? "";
            if (!api.IsMatch(min) || !api.IsMatch(last)) return "the add-on's NVDA versions are not in NVDA's form: " + min + ", " + last;
            // NVDA 2026.1 broke add-ons tested only on older versions: the last tested must be at least that, or NVDA blocks it.
            if (NvdaHelper.CompareVersions(last, "2026.1") < 0 || NvdaHelper.CompareVersions(min, last) > 0) return "the add-on would be blocked by NVDA 2026: last tested " + last;
            if (NvdaHelper.CarriedVersion != m["version"]) return "CarriedVersion is " + NvdaHelper.CarriedVersion;
            var parsed = NvdaHelper.ParseManifest("name = x\r\nsummary = \"A b\"\r\ndescription = \"\"\"one\r\ntwo\"\"\"\r\nversion = 1.2 # note\r\n");
            if (parsed["summary"] != "A b" || parsed["description"] != "one\ntwo" || parsed["version"] != "1.2") return "a manifest was parsed wrongly";
            if (NvdaHelper.CompareVersions("1.0.10", "1.0.9") <= 0 || NvdaHelper.CompareVersions("1.0", "1.0.0") != 0) return "versions compared wrongly";
            return null;
        }

        // ---- Installing into a scratch NVDA settings folder (never the real one) ----

        private static string? Installer()
        {
            string cfg = Path.Combine(Path.GetTempPath(), "kova-nvdahelper-test-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                byte[] p = NvdaHelper.Package()!;
                string carried = NvdaHelper.CarriedVersion!;
                string addons = Path.Combine(cfg, "addons"), final = Path.Combine(addons, NvdaHelper.AddonName);
                if (NvdaHelper.InstalledVersion(cfg) != null) return "an empty folder had an add-on";
                Directory.CreateDirectory(Path.Combine(addons, "kovaHelper.kova-1234.delete", "x"));
                Directory.CreateDirectory(Path.Combine(addons, "someoneElse"));
                if (NvdaHelper.InstallInto(cfg, p) is string w1) return "installing failed: " + w1;
                if (NvdaHelper.InstalledVersion(cfg) != carried) return "after installing, the version was " + NvdaHelper.InstalledVersion(cfg);
                if (!File.Exists(Path.Combine(final, "globalPlugins", "kovaHelper", "__init__.py"))) return "the plugin was not unpacked";
                var left = Directory.GetDirectories(addons).Select(Path.GetFileName).OrderBy(x => x).ToArray();
                if (!left.SequenceEqual(new[] { "kovaHelper", "someoneElse" })) return "after installing the add-ons folder held " + string.Join(", ", left);
                // An older copy is replaced whole (a file only it had goes too).
                File.WriteAllText(Path.Combine(final, "manifest.ini"), "name = kovaHelper\nversion = 0.9.0\n");
                File.WriteAllText(Path.Combine(final, "stale.py"), "x");
                var st = new NvdaHelperState(cfg, NvdaHelper.InstalledVersion(cfg), carried, true, false, null);
                if (!st.UpdateAvailable || st.Offer != NvdaHelperAction.Update || NvdaHelper.MenuText(st) != "Update the Kova Helper NVDA add-on...") return "an older add-on was not offered an update";
                if (NvdaHelper.InstallInto(cfg, p) is string w2) return "updating failed: " + w2;
                if (NvdaHelper.InstalledVersion(cfg) != carried || File.Exists(Path.Combine(final, "stale.py"))) return "updating did not replace the old copy";
                st = st with { Installed = NvdaHelper.InstalledVersion(cfg) };
                if (st.UpdateAvailable || st.Offer != NvdaHelperAction.Remove) return "an up-to-date add-on was not offered removal";
                if (NvdaHelper.RemoveFrom(cfg) is string w3) return "removing failed: " + w3;
                if (Directory.Exists(final) || NvdaHelper.InstalledVersion(cfg) != null) return "removing left the add-on";
                if (Directory.GetDirectories(addons).Select(Path.GetFileName).Single() != "someoneElse") return "removing touched another add-on or left something";
                if (NvdaHelper.RemoveFrom(cfg) != null) return "removing twice failed";
                if ((st with { Installed = null }).Offer != NvdaHelperAction.Install) return "a missing add-on was not offered install";
                // A package that tries to climb out is refused and writes nothing outside.
                foreach (string evil in new[] { "../evil.txt", "a/../../evil.txt", "C:/evil.txt", "a/con:x", "/evil.txt" })
                {
                    var ms = new MemoryStream();
                    using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) using (var s = new StreamWriter(z.CreateEntry(evil).Open())) s.Write("x");
                    string to = Path.Combine(cfg, "unpack-" + Guid.NewGuid().ToString("N")[..6]);
                    if (NvdaHelper.Extract(ms.ToArray(), to) == null) return "a package entry named " + evil + " was unpacked";
                    if (File.Exists(Path.Combine(cfg, "evil.txt")) || File.Exists(Path.Combine(Path.GetTempPath(), "evil.txt"))) return "a package entry climbed out: " + evil;
                }
                if (NvdaHelper.Extract(Encoding.ASCII.GetBytes("not a zip"), Path.Combine(cfg, "junk")) == null) return "a junk package was unpacked";
                return null;
            }
            finally { try { Directory.Delete(cfg, true); } catch { } }
        }

        // ---- The pipe ----

        private static readonly ConcurrentQueue<string> _log = new();
        private static void Log(string s) => _log.Enqueue(s);

        private static string TestPipe() => "KovaNvdaTest-" + Guid.NewGuid().ToString("N")[..8];

        private static NamedPipeClientStream? Open(string name)
        {
            var c = new NamedPipeClientStream(".", name, PipeDirection.Out);
            try { c.Connect(3000); return c; } catch { c.Dispose(); return null; }
        }

        private static void Frame(Stream s, byte[] body) { s.Write(BitConverter.GetBytes(body.Length)); s.Write(body); s.Flush(); }
        private static byte[] HelloBody(string v) => new byte[] { 0, (byte)v.Length }.Concat(Encoding.ASCII.GetBytes(v)).ToArray();

        private static bool Until(Func<bool> ok, int ms = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (ok()) return true; Thread.Sleep(20); }
            return ok();
        }

        private static string? Pipe()
        {
            // The real rule: this test process is not NVDA, so it is refused before anything is read.
            string n1 = TestPipe();
            using (var real = new NvdaHelper.Server(Log, n1))
            {
                if (!Until(() => real.Listening)) return "the pipe did not come up: " + string.Join(" / ", _log);
                using var c = Open(n1);
                if (c == null) return "could not connect to the pipe";
                try { Frame(c, HelloBody("1.0.0")); } catch (IOException) { }
                if (!Until(() => real.Refused >= 1)) return "a program that is not NVDA was not refused";
                Thread.Sleep(200);
                if (real.Connected) return "a program that is not NVDA counted as the add-on";
            }

            // With this process allowed in (tests only): events come through in order; junk ends only that connection.
            string n2 = TestPipe();
            var got = new ConcurrentQueue<string>();
            var changes = new ConcurrentQueue<bool>();
            using var server = new NvdaHelper.Server(null, n2, pid => pid == Environment.ProcessId);
            server.Event += e => got.Enqueue(e.ToString());
            server.ConnectedChanged += on => changes.Enqueue(on);
            if (!Until(() => server.Listening)) return "the test pipe did not come up";
            var evs = new[] { NvdaEvent.Speak(new NvdaSpeechPart("one")), NvdaEvent.Cancel(), NvdaEvent.Beep(880, 40, 50, 50), NvdaEvent.Wave("error"), NvdaEvent.Braille("two") };
            using (var c = Open(n2))
            {
                if (c == null) return "could not connect to the test pipe";
                Frame(c, HelloBody("1.0.0"));
                Frame(c, new byte[] { 0 }); // a keepalive is passed over, not counted as junk
                foreach (var e in evs) Frame(c, e.Encode());
                if (!Until(() => got.Count == evs.Length)) return "only " + got.Count + " of " + evs.Length + " events came through";
                if (!got.SequenceEqual(evs.Select(e => e.ToString()))) return "events came through wrong: " + string.Join(" | ", got);
                if (!server.Connected || server.AddonVersion != "1.0.0") return "the add-on did not count as connected";
            }
            if (!Until(() => !server.Connected)) return "the add-on still counted as connected after it went";
            if (!changes.SequenceEqual(new[] { true, false })) return "ConnectedChanged said " + string.Join(",", changes);

            int dropped = server.Dropped;
            foreach (var junk in new Func<Stream, bool>[]
            {
                s => { s.Write(BitConverter.GetBytes(0)); return true; },                 // a zero length
                s => { s.Write(BitConverter.GetBytes(1 << 30)); return true; },           // a huge length
                s => { s.Write(BitConverter.GetBytes(-5)); return true; },                // a negative length
                s => { Frame(s, NvdaEvent.Cancel().Encode()); return true; },             // an event before the hello
                s => { Frame(s, HelloBody("1.0")); Frame(s, new byte[] { 9, 9, 9 }); return true; }, // a bad event after it
            })
            {
                using var c = Open(n2);
                if (c == null) return "the pipe stopped taking connections after junk";
                try { junk(c); c.Flush(); } catch (IOException) { }
                if (!Until(() => server.Dropped > dropped)) return "junk on the pipe did not end its connection";
                dropped = server.Dropped;
            }
            // Still working.
            got.Clear();
            using (var c = Open(n2))
            {
                if (c == null) return "the pipe stopped taking connections";
                Frame(c, HelloBody("1.0.0"));
                Frame(c, NvdaEvent.Speak(new NvdaSpeechPart("still here")).Encode());
                if (!Until(() => got.Count == 1)) return "the pipe stopped working after junk";
            }
            // A half-sent frame that never finishes holds only its own connection.
            using (var stuck = Open(n2))
            using (var c = Open(n2))
            {
                if (stuck == null || c == null) return "could not open two connections";
                stuck.Write(BitConverter.GetBytes(100)); stuck.Flush();
                got.Clear();
                Frame(c, HelloBody("1.0.0"));
                Frame(c, NvdaEvent.Wave("start").Encode());
                if (!Until(() => got.Count == 1)) return "a stuck connection held up another";
            }
            return null;
        }

        /// <summary>A program that makes the pipe first gets it, and Kova then refuses to share the name (FirstPipeInstance).</summary>
        private static string? Squat()
        {
            string n = TestPipe();
            using var squatter = new NamedPipeServerStream(n, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var server = new NvdaHelper.Server(null, n, _ => true);
            Thread.Sleep(1000);
            if (server.Listening) return "Kova made its pipe under a name another program already had";
            return null;
        }

        // ---- The add-on itself, run in a real Python with stand-ins for NVDA's modules (skipped if there is no Python) ----

        private static string? AddonEndToEnd()
        {
            string? python = FindPython();
            if (python == null) return null; // no Python on this PC: the add-on is tested live in NVDA instead
            string root = Path.Combine(Path.GetTempPath(), "kova-nvdahelper-py-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                string addon = Path.Combine(root, "addon"), driverDir = Path.Combine(root, "driver"), app = Path.Combine(root, "nvda");
                if (NvdaHelper.Extract(NvdaHelper.Package()!, addon) is string bad) return "unpacking for the Python test failed: " + bad;
                Directory.CreateDirectory(driverDir);
                Directory.CreateDirectory(Path.Combine(app, "waves"));
                string driver = Path.Combine(driverDir, "drive.py");
                File.WriteAllText(driver, Driver);
                uint session = NvdaHelper.MySession();
                string pipe = "KovaNvdaPyTest" + Guid.NewGuid().ToString("N")[..6];
                var got = new ConcurrentQueue<string>();
                using var server = new NvdaHelper.Server(null, pipe + session, _ => true);
                server.Event += e => got.Enqueue(e.Kind == NvdaEventKind.Speak ? "speak: " + string.Join(" | ", e.Parts.Select(p => p.Text + "/" + p.Pitch + (p.Capital ? "/cap" : "") + (p.Spelled ? "/spelled" : ""))) : e.ToString());
                if (!Until(() => server.Listening)) return "the pipe for the Python test did not come up";
                var psi = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "-I", "-B", driver, Path.Combine(addon, "globalPlugins", "kovaHelper", "__init__.py"), "\\\\.\\pipe\\" + pipe, app }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } return "the add-on's Python test did not finish"; }
                string said = outTask.Result + errTask.Result;
                if (p.ExitCode != 0) return "the add-on's Python test failed: " + said.Trim();
                var want = new[]
                {
                    "speak: Hello world/0 | A/30/cap | b/0/spelled",
                    "cancel",
                    "beep 440 Hz 50 ms 50/60",
                    "sound error",
                    "braille: braille here",
                    // 5000 characters: two events within Kova's limit of 4000.
                    "speak: " + new string('z', 4000) + "/0",
                    "speak: " + new string('z', 1000) + "/0",
                };
                if (!Until(() => got.Count >= want.Length)) return "the add-on handed over " + got.Count + " of " + want.Length + " things: " + Short(string.Join(" || ", got)) + " " + said;
                if (!got.Take(want.Length).SequenceEqual(want)) return "the add-on handed over: " + Short(string.Join(" || ", got));
                if (got.Count != want.Length) return "the add-on handed over more than it should have (a sound from outside NVDA's waves folder, or a cut-off with nothing said?): " + Short(string.Join(" || ", got));
                return null;
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static string Short(string s) => s.Length > 600 ? s[..600] + "..." : s;

        private static string? FindPython()
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    if (dir.Length == 0 || dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue; // the Store's stand-in only opens the Store
                    string p = Path.Combine(dir.Trim(), "python.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        // Stand-ins for the parts of NVDA the add-on uses, then everything it hooks, in order.
        private const string Driver = @"
import sys, types, time, importlib.util, os
plugin_path, pipe_base, app_dir = sys.argv[1], sys.argv[2], sys.argv[3]

class Point:
    def __init__(self): self.handlers = []
    def register(self, h): self.handlers.append(h)
    def unregister(self, h): self.handlers.remove(h)
    def notify(self, **kw):
        for h in list(self.handlers): h(**kw)
    def decide(self, **kw):
        return all(h(**kw) for h in list(self.handlers))

def mod(name, **attrs):
    m = types.ModuleType(name)
    m.__dict__.update(attrs)
    sys.modules[name] = m
    return m

class GP:
    def __init__(self, *a, **k): pass
    def terminate(self): pass
class Log:
    def info(self, *a, **k): pass
    def warning(self, *a, **k): print('warning', a)
    def debugWarning(self, *a, **k): print('debugWarning', a, k)
mod('globalPluginHandler', GlobalPlugin=GP)
mod('logHandler', log=Log())
mod('globalVars', appDir=app_dir, appArgs=types.SimpleNamespace(secure=False))
class PitchCommand:
    def __init__(self, offset=0, multiplier=1): self.offset, self.multiplier = offset, multiplier
class CharacterModeCommand:
    def __init__(self, state): self.state = state
class BeepCommand:
    def __init__(self, *a): pass
speech = mod('speech')
speech.extensions = mod('speech.extensions', pre_speech=Point(), speechCanceled=Point())
speech.commands = mod('speech.commands', PitchCommand=PitchCommand, CharacterModeCommand=CharacterModeCommand)
speech.speech = mod('speech.speech')
tones = mod('tones', decide_beep=Point())
nvwave = mod('nvwave', decide_playWaveFile=Point())
braille = mod('braille', pre_writeCells=Point())

spec = importlib.util.spec_from_file_location('kovaHelper', plugin_path)
k = importlib.util.module_from_spec(spec)
spec.loader.exec_module(k)
k.PIPE_BASE = pipe_base
k.RETRY_SECONDS = 0.2
p = k.GlobalPlugin()
end = time.time() + 10
while not p._link.connected and time.time() < end: time.sleep(0.05)
if not p._link.connected:
    print('never connected'); sys.exit(2)
speech.extensions.speechCanceled.notify()  # nothing said yet: no cut-off is sent
speech.extensions.pre_speech.notify(speechSequence=['Hello world', PitchCommand(offset=30), 'A', PitchCommand(), CharacterModeCommand(True), 'b', CharacterModeCommand(False), BeepCommand(2000, 50)], symbolLevel=None, priority=None)
speech.extensions.speechCanceled.notify()
speech.extensions.speechCanceled.notify()  # twice in a row: sent once
if tones.decide_beep.decide(hz=440.0, length=50, left=50, right=60, isSpeechBeepCommand=False) is not True: print('a beep was blocked'); sys.exit(3)
if nvwave.decide_playWaveFile.decide(fileName=os.path.join(app_dir, 'waves', 'error.wav'), asynchronous=True, isSpeechWaveFileCommand=False) is not True: print('a sound was blocked'); sys.exit(3)
nvwave.decide_playWaveFile.decide(fileName=os.path.join(app_dir, 'other', 'secret.wav'), asynchronous=True)
braille.pre_writeCells.notify(cells=[1, 2], rawText='braille here  ', currentCellCount=40)
braille.pre_writeCells.notify(cells=[1, 3], rawText='braille here', currentCellCount=40)
speech.extensions.pre_speech.notify(speechSequence=['z' * 5000])
time.sleep(1.0)
p.terminate()
if speech.extensions.pre_speech.handlers or tones.decide_beep.handlers or braille.pre_writeCells.handlers: print('hooks left behind'); sys.exit(4)
print('done')
";
    }
}
