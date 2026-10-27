using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// NVDA output through the controller client. NVDA only, no SAPI fallback.
    /// Calls go through a background thread because the client is a synchronous
    /// RPC into NVDA, and the keyboard hook must never wait on it.
    /// </summary>
    internal static unsafe class Speech
    {
        private static int _init; // 0 = not yet started; CompareExchange below makes the check-then-set atomic
        private static delegate* unmanaged[Stdcall]<char*, int> _speakText;
        private static delegate* unmanaged[Stdcall]<int> _cancelSpeech;

        // Interrupt: cut off whatever NVDA is saying (and anything waiting) before this is said.
        // Ssml: say this instead, where this PC's NVDA can (pitch changes, spelled letters from the remote NVDA); CancelOnly:
        // not words at all, the remote NVDA cut itself off, so this one does too, in order with what came before.
        private readonly record struct Utterance(string Message, bool Interrupt, string? Ssml = null, bool CancelOnly = false);
        private static delegate* unmanaged[Stdcall]<char*, int, int, int, int> _speakSsml;
        private static readonly Queue<Utterance> Pending = new();
        private static readonly object Gate = new();

        // The one rule for who may cut whom off:
        //  - An answer to something you pressed (interrupt: true) cuts off whatever is being said, as
        //    you have moved on and want to hear the answer now.
        //  - News that happened by itself (interrupt: false: a transfer ended, a health alert, the other
        //    PC dropped or is updating) never cuts anything off: it waits its turn.
        //  - But news less than a second old is never thrown away by an answer: the answer is said
        //    right after it instead. In a burst (a health alert, then "Received ...", then a drop) only
        //    the last line used to survive, each one cutting the one before after a few milliseconds.
        //  - Only once per burst, and not behind long news: an answer then waited for the whole of a long
        //    line, and on a line that kept dropping and coming back no answer ever cut anything off, so
        //    NVDA's own queue only grew.
        private const int FreshNewsMs = 1000;
        private const int LongNews = 100; // characters: news longer than this is cut off by an answer as before
        private static long _newsAt = long.MinValue / 2; // when the latest news was handed over (cleared once an answer waited for it)
        private static bool _newsLong;

        public static void Init()
        {
            // Atomic check-then-set: two near-simultaneous callers must never both pass this,
            // which would double-load the native DLL and start two Pump threads on one queue.
            if (Interlocked.CompareExchange(ref _init, 1, 0) != 0) return;
            // Tests only: a test copy on the PC being tested says nothing aloud (only into KOVA_SPEECH_LOG). Testing the
            // remote NVDA on one PC, its lines said by this PC's NVDA would be read back by the host: round and round.
            // TestLog is null in an elevated or SYSTEM copy, so this switch is ignored there too.
            if (TestLog != null && Environment.GetEnvironmentVariable("KOVA_SPEECH_SILENT") == "1") return;
            Load();
            if (_speakText == null) return;
            new Thread(Pump) { IsBackground = true, Name = "Kova speech" }.Start();
            new Thread(RemotePump) { IsBackground = true, Name = "Kova remote NVDA" }.Start();
            // The Kova Helper's cut-offs and pitch changes from the remote NVDA come here (NvdaEvent.Perform).
            NvdaEvent.RemoteCancel = RemoteCancel;
            NvdaEvent.RemoteSsml = RemoteSsml;
        }

        // Tests only: every line handed to NVDA is also written here, so a test run can check what was said.
        // Never in an elevated or SYSTEM copy: such a copy takes its environment from HKCU\Environment, which any
        // program the user runs can write, so the switch would let it append text anywhere as administrator.
        private static string? TestLog = ReadTestLog();

        /// <summary>Tests only: treat this process as elevated (true) or not (false) instead of asking Windows.</summary>
        internal static bool? PrivilegedForTest;

        internal static bool Privileged()
        {
            if (PrivilegedForTest is bool b) return b;
            try
            {
                using var me = WindowsIdentity.GetCurrent();
                return me.IsSystem || new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return true; } // unsure: the safe answer is to ignore the test switches
        }

        private static string? ReadTestLog() =>
            !Privileged() && Environment.GetEnvironmentVariable("KOVA_SPEECH_LOG") is { Length: > 0 } f ? f : null;

        /// <summary>Tests only: read KOVA_SPEECH_LOG again (after changing it or PrivilegedForTest).</summary>
        internal static void ReloadTestLogForTest() => TestLog = ReadTestLog();

        public static void Speak(string message, bool interrupt = true)
        {
            if (TestLog != null && !string.IsNullOrEmpty(message))
                lock (Gate) try { System.IO.File.AppendAllText(TestLog, DateTime.Now.ToString("HH:mm:ss ") + message + Environment.NewLine); } catch { }
            if (_speakText == null || string.IsNullOrEmpty(message)) return;
            lock (Gate)
            {
                long now = Environment.TickCount64;
                if (!interrupt) { _newsAt = now; _newsLong = message.Length > LongNews; }
                else if (now - _newsAt < FreshNewsMs && !_newsLong)
                {
                    _newsAt = long.MinValue / 2; // the next answer cuts in as usual, until new news comes
                    // Fresh news is still being said (or waiting): the answer goes after it, cutting
                    // nothing. Older answers still waiting are dropped: this one replaces them.
                    var keep = Pending.Where(u => !u.Interrupt).ToList();
                    Pending.Clear();
                    foreach (var u in keep) Pending.Enqueue(u);
                    interrupt = false;
                }
                else Pending.Clear();
                while (Pending.Count >= 8) Pending.Dequeue();
                Pending.Enqueue(new Utterance(message, interrupt));
                Monitor.Pulse(Gate);
            }
        }

        // What the other PC sends is said in this PC's NVDA, so a hostile or broken host must not be able to swamp
        // it (an 8 MB line can hang NVDA) or slip in characters that change how text reads. Normal NVDA text never
        // comes near these limits: a line is a few words, a burst a few dozen lines.
        internal const int RemoteLineMax = 1000;   // characters in one spoken line
        internal const int BrailleMax = 2000;      // characters on the braille display
        internal const int RemoteLinesPerMessage = 50;
        internal const int RemoteLinesPerSecond = 100; // past this, the rest of a message is joined into one line, never lost
        internal const int RemoteJoinedPerSecond = 20; // joined lines on top of that before anything is dropped
        private const int RemoteQueueMax = 64;
        private const int RemoteCallMax = 4 * RemoteLineMax; // characters handed to NVDA in one call when lines have piled up

        /// <summary>
        /// Text from the other PC, made safe to hand to NVDA: control characters (other than tab) and the invisible
        /// characters that reverse or reorder text are removed, and it is cut to max characters.
        /// </summary>
        internal static string Clean(string? text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(Math.Min(text.Length, max));
            foreach (char c in text)
            {
                if (sb.Length >= max) break;
                if (c < 0x20 && c != '\t') continue;
                if (c >= 0x7F && c <= 0x9F) continue;
                if (c is '‎' or '‏' or '؜' || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩')) continue;
                sb.Append(c);
            }
            // Never leave half of a two-part character at the cut.
            if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
            return sb.ToString();
        }

        private static readonly object RemoteGate = new();
        private static readonly Queue<Utterance> RemotePending = new();
        private static long _rateStart = long.MinValue / 2;
        private static int _rateLines, _rateJoined;
        private static long _droppedNoteAt = long.MinValue / 2;

        /// <summary>
        /// The lines of one remote NVDA message as they will be said: cleaned, cut, at most RemoteLinesPerMessage, and
        /// past RemoteLinesPerSecond the rest joined into one line. Empty when even joined lines are past their limit
        /// (dropped is then true). now is Environment.TickCount64; internal so the tests can drive the clock.
        /// </summary>
        internal static List<string> PrepareRemote(string[] lines, long now, out bool dropped)
        {
            dropped = false;
            var said = new List<string>(Math.Min(lines.Length, RemoteLinesPerMessage));
            StringBuilder? joined = null;
            lock (RemoteGate)
            {
                if (now - _rateStart >= 1000) { _rateStart = now; _rateLines = 0; _rateJoined = 0; }
                foreach (string raw in lines)
                {
                    string line = Clean(raw, RemoteLineMax);
                    if (line.Length == 0 || string.IsNullOrWhiteSpace(line)) continue;
                    if (joined == null && said.Count < RemoteLinesPerMessage - 1 && _rateLines < RemoteLinesPerSecond)
                    {
                        said.Add(line);
                        _rateLines++;
                        continue;
                    }
                    joined ??= new StringBuilder();
                    if (joined.Length >= RemoteLineMax) break; // the rest cannot fit: stop reading a huge message
                    if (joined.Length > 0) joined.Append(' ');
                    joined.Append(line);
                }
                if (joined != null)
                {
                    if (said.Count < RemoteLinesPerMessage - 1 || _rateLines >= RemoteLinesPerSecond)
                    {
                        // Over the rate: one joined line, and only so many of those a second.
                        if (_rateJoined < RemoteJoinedPerSecond) { _rateJoined++; said.Add(Clean(joined.ToString(), RemoteLineMax)); }
                        else dropped = true;
                    }
                    else { _rateLines++; said.Add(Clean(joined.ToString(), RemoteLineMax)); } // only the per-message limit: the last line holds the rest
                }
            }
            return said;
        }

        /// <summary>
        /// Lines the remote PC's NVDA spoke, said by this PC's NVDA. interrupt: a key was pressed since the last lines,
        /// so these answer it and cut off whatever is still being said (as NVDA does when you move on); otherwise they
        /// queue up inside NVDA, like a remote NVDA reading on by itself. They never wait behind Kova's own lines:
        /// they have their own thread, which hands each line to NVDA the moment it arrives.
        /// </summary>
        public static void Remote(string[] lines, bool interrupt)
        {
            long now = Environment.TickCount64;
            var said = PrepareRemote(lines, now, out bool dropped);
            if (TestLog != null)
                lock (Gate) try { System.IO.File.AppendAllText(TestLog, DateTime.Now.ToString("HH:mm:ss ") + "remote NVDA" + (interrupt ? " (cuts in)" : "") + ": " + string.Join(" / ", said) + Environment.NewLine); } catch { }
            if (dropped && now - _droppedNoteAt > 30_000)
            {
                _droppedNoteAt = now;
                DiagLog.Write("speech: the other PC sent more NVDA speech than can be read, so some was skipped");
                Speak("The other PC sent more speech than could be read, so some was skipped.", interrupt: false);
            }
            if (_speakText == null || said.Count == 0) return;
            lock (RemoteGate)
            {
                // A key was pressed: what the remote NVDA had not said yet is old news, as in NVDA itself.
                if (interrupt) RemotePending.Clear();
                for (int i = 0; i < said.Count; i++)
                {
                    if (RemotePending.Count >= RemoteQueueMax)
                    {
                        // NVDA is not keeping up (frozen?): add to the last waiting line while it has room, rather
                        // than letting this queue grow without end.
                        var last = RemotePending.Last();
                        if (!last.CancelOnly && last.Ssml == null && last.Message.Length + said[i].Length < RemoteLineMax)
                        {
                            var all = RemotePending.ToArray();
                            all[^1] = last with { Message = last.Message + " " + said[i] };
                            RemotePending.Clear();
                            foreach (var u in all) RemotePending.Enqueue(u);
                        }
                        continue;
                    }
                    RemotePending.Enqueue(new Utterance(said[i], interrupt && i == 0));
                }
                Monitor.Pulse(RemoteGate);
            }
        }

        private static void RemotePump()
        {
            while (true)
            {
                Utterance u;
                lock (RemoteGate)
                {
                    while (RemotePending.Count == 0) Monitor.Wait(RemoteGate);
                    u = RemotePending.Dequeue();
                    // Plain lines that piled up while NVDA took the last one go over as one call: each call into NVDA
                    // costs a round trip and an entry in its speech queue, and lots of text fell behind that way.
                    if (!u.CancelOnly && u.Ssml == null)
                    {
                        StringBuilder? joined = null;
                        while (RemotePending.Count > 0 && RemotePending.Peek() is { CancelOnly: false, Ssml: null, Interrupt: false } next
                            && (joined?.Length ?? u.Message.Length) + 1 + next.Message.Length <= RemoteCallMax)
                        {
                            joined ??= new StringBuilder(u.Message);
                            joined.Append(' ').Append(RemotePending.Dequeue().Message);
                        }
                        if (joined != null) u = u with { Message = joined.ToString() };
                    }
                }
                try
                {
                    if (u.CancelOnly) { if (_cancelSpeech != null) _cancelSpeech(); continue; }
                    if (u.Interrupt && _cancelSpeech != null) _cancelSpeech();
                    // SSML (symbol level unchanged, normal priority, not waiting), else the plain words.
                    int rc = -1;
                    if (u.Ssml != null && _speakSsml != null) fixed (char* s = u.Ssml) rc = _speakSsml(s, -1, 0, 1);
                    if (rc != 0) fixed (char* p = u.Message) _speakText(p);
                }
                catch { }
            }
        }

        /// <summary>The remote NVDA cut itself off (a key was pressed there, or focus moved): so does this one, and what it had not said yet goes.</summary>
        public static void RemoteCancel()
        {
            if (_speakText == null) return;
            lock (RemoteGate)
            {
                RemotePending.Clear();
                RemotePending.Enqueue(new Utterance("", false, CancelOnly: true));
                Monitor.Pulse(RemoteGate);
            }
        }

        /// <summary>Words from the remote NVDA with a pitch change or spelled letters: SSML where this NVDA takes it, else plain, in order with the rest.</summary>
        public static void RemoteSsml(string ssml, string plain)
        {
            var said = PrepareRemote(new[] { plain }, Environment.TickCount64, out _);
            if (_speakText == null || said.Count == 0) return;
            lock (RemoteGate)
            {
                if (RemotePending.Count >= RemoteQueueMax) return; // NVDA is not keeping up: the plain lines' joining takes care of the rest
                RemotePending.Enqueue(new Utterance(said[0], false, ssml.Length <= 4 * RemoteLineMax ? ssml : null));
                Monitor.Pulse(RemoteGate);
            }
        }

        private static delegate* unmanaged[Stdcall]<char*, int> _brailleMessage;

        /// <summary>What the remote PC's braille display shows, on this PC's braille display (NVDA shows it as a message).</summary>
        public static void Braille(string text)
        {
            text = Clean(text, BrailleMax);
            if (TestLog != null) lock (Gate) try { System.IO.File.AppendAllText(TestLog, DateTime.Now.ToString("HH:mm:ss ") + "remote braille: " + text + Environment.NewLine); } catch { }
            if (_brailleMessage == null || string.IsNullOrEmpty(text)) return;
            // Never on the caller's thread (the client is a call into NVDA that can wait), and only the newest: one sender,
            // so an older line can never land after a newer one and stay on the display.
            Volatile.Write(ref _brailleNext, text);
            if (Interlocked.CompareExchange(ref _brailleSending, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                while (true)
                {
                    while (Interlocked.Exchange(ref _brailleNext, null) is string next) { try { fixed (char* p = next) _brailleMessage(p); } catch { } }
                    Volatile.Write(ref _brailleSending, 0);
                    if (Volatile.Read(ref _brailleNext) == null || Interlocked.CompareExchange(ref _brailleSending, 1, 0) != 0) return;
                }
            });
        }
        private static string? _brailleNext;
        private static int _brailleSending;

        private static void Pump()
        {
            while (true)
            {
                Utterance u;
                lock (Gate)
                {
                    while (Pending.Count == 0) Monitor.Wait(Gate);
                    u = Pending.Dequeue();
                }
                try
                {
                    if (u.Interrupt && _cancelSpeech != null) _cancelSpeech();
                    fixed (char* p = u.Message) _speakText(p);
                }
                catch { }
            }
        }

        // SHA-256 of the NVDA controller clients Kova ships (native\win-x64 and native\win-arm64). Only these are
        // loaded: the folders a DLL could come from (next to the exe, or where a single-file copy unpacks itself) are
        // often writable by the user, and an elevated Kova loading a DLL dropped there would run it as administrator.
        internal const string ControllerSha256X64 = "796b9342eff8ab3f99a9c551ca0d615ab7de4944f99a69ce055f93a3abeaf377";
        internal const string ControllerSha256Arm64 = "c7f83cb2e23aad0df0ac1176e317069c6e7af26edc2494c6ad24c2ec10d81a40";

        /// <summary>
        /// Opens path so nobody can change or replace it while it is open, and checks it is the controller client Kova
        /// ships. On success the open file is handed back: load the DLL while still holding it, then dispose it.
        /// </summary>
        internal static FileStream? OpenTrustedController(string path, string wantSha256, out string why)
        {
            FileStream? fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                string got = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                if (got == wantSha256) { why = ""; return fs; }
                why = path + " is not the NVDA controller client Kova ships (SHA-256 " + got + ")";
            }
            catch (FileNotFoundException) { why = path + " is not there"; }
            catch (DirectoryNotFoundException) { why = path + " is not there"; }
            catch (Exception e) { why = path + " could not be checked: " + e.Message; }
            fs?.Dispose();
            return null;
        }

        private static void Load()
        {
            bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            string exact = "nvdaControllerClient" + (arm ? "Arm64" : "64") + ".dll";
            string want = arm ? ControllerSha256Arm64 : ControllerSha256X64;

            // Only full paths from the runtime's own native library folders (a single-file build's unpack folder; a plain
            // build's own folder). Never a bare name, which Windows would look for along PATH.
            var candidates = new List<string>();
            if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string search)
                foreach (var dir in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    if (Path.IsPathFullyQualified(dir)) candidates.Add(Path.Combine(dir, exact));

            var refused = new List<string>();
            foreach (string c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var held = OpenTrustedController(c, want, out string why);
                if (held == null) { if (File.Exists(c)) refused.Add(why); continue; }
                if (!NativeLibrary.TryLoad(c, out IntPtr lib)) { refused.Add(c + " would not load"); continue; }
                if (!NativeLibrary.TryGetExport(lib, "nvdaController_speakText", out IntPtr speak)) continue;
                NativeLibrary.TryGetExport(lib, "nvdaController_cancelSpeech", out IntPtr cancel);
                if (NativeLibrary.TryGetExport(lib, "nvdaController_brailleMessage", out IntPtr braille)) _brailleMessage = (delegate* unmanaged[Stdcall]<char*, int>)braille;
                if (NativeLibrary.TryGetExport(lib, "nvdaController_speakSsml", out IntPtr ssml)) _speakSsml = (delegate* unmanaged[Stdcall]<char*, int, int, int, int>)ssml;
                _speakText = (delegate* unmanaged[Stdcall]<char*, int>)speak;
                _cancelSpeech = cancel == IntPtr.Zero ? null : (delegate* unmanaged[Stdcall]<int>)cancel;
                return;
            }
            DiagLog.Write("speech: NVDA controller client not loaded, so Kova cannot speak through NVDA" + (refused.Count > 0 ? ": " + string.Join("; ", refused) : " (not found)"));
        }
    }
}
