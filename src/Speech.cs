using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        private readonly record struct Utterance(string Message, bool Interrupt, bool Remote = false);
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
            if (TestLog != null && Environment.GetEnvironmentVariable("KOVA_SPEECH_SILENT") == "1") return;
            Load();
            if (_speakText == null) return;
            new Thread(Pump) { IsBackground = true, Name = "Kova speech" }.Start();
        }

        // Tests only: every line handed to NVDA is also written here, so a test run can check what was said.
        private static readonly string? TestLog = Environment.GetEnvironmentVariable("KOVA_SPEECH_LOG") is { Length: > 0 } f ? f : null;

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

        /// <summary>
        /// Lines the remote PC's NVDA spoke, said by this PC's NVDA. interrupt: a key was pressed since the last lines,
        /// so these answer it and cut off whatever is still being said (as NVDA does when you move on); otherwise they
        /// wait their turn, like a remote NVDA reading on by itself. Kova's own news waiting is kept.
        /// </summary>
        public static void Remote(string[] lines, bool interrupt)
        {
            if (TestLog != null)
                lock (Gate) try { System.IO.File.AppendAllText(TestLog, DateTime.Now.ToString("HH:mm:ss ") + "remote NVDA" + (interrupt ? " (cuts in)" : "") + ": " + string.Join(" / ", lines) + Environment.NewLine); } catch { }
            if (_speakText == null || lines.Length == 0) return;
            lock (Gate)
            {
                if (interrupt) { var keep = Pending.Where(u => !u.Interrupt && !u.Remote).ToList(); Pending.Clear(); foreach (var u in keep) Pending.Enqueue(u); }
                for (int i = 0; i < lines.Length; i++) Pending.Enqueue(new Utterance(lines[i], interrupt && i == 0, Remote: true));
                // A remote NVDA reading a long page must not build up minutes here: the oldest of it goes.
                while (Pending.Count > 64) Pending.Dequeue();
                Monitor.Pulse(Gate);
            }
        }

        private static delegate* unmanaged[Stdcall]<char*, int> _brailleMessage;

        /// <summary>What the remote PC's braille display shows, on this PC's braille display (NVDA shows it as a message).</summary>
        public static void Braille(string text)
        {
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

        private static void Load()
        {
            string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "Arm64" : "64";
            string exact = "nvdaControllerClient" + arch + ".dll";
            var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, exact) };

            // A single-file build unpacks native libraries to its own folder.
            if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string search)
                foreach (var dir in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    candidates.Add(Path.Combine(dir, exact));
            candidates.Add(exact);

            foreach (string c in candidates)
            {
                if (!NativeLibrary.TryLoad(c, out IntPtr lib)) continue;
                if (!NativeLibrary.TryGetExport(lib, "nvdaController_speakText", out IntPtr speak)) continue;
                NativeLibrary.TryGetExport(lib, "nvdaController_cancelSpeech", out IntPtr cancel);
                if (NativeLibrary.TryGetExport(lib, "nvdaController_brailleMessage", out IntPtr braille)) _brailleMessage = (delegate* unmanaged[Stdcall]<char*, int>)braille;
                _speakText = (delegate* unmanaged[Stdcall]<char*, int>)speak;
                _cancelSpeech = cancel == IntPtr.Zero ? null : (delegate* unmanaged[Stdcall]<int>)cancel;
                return;
            }
        }
    }
}
