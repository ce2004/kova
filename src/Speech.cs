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
        private readonly record struct Utterance(string Message, bool Interrupt);
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
            Load();
            if (_speakText == null) return;
            new Thread(Pump) { IsBackground = true, Name = "Kova speech" }.Start();
        }

        public static void Speak(string message, bool interrupt = true)
        {
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
                _speakText = (delegate* unmanaged[Stdcall]<char*, int>)speak;
                _cancelSpeech = cancel == IntPtr.Zero ? null : (delegate* unmanaged[Stdcall]<int>)cancel;
                return;
            }
        }
    }
}
