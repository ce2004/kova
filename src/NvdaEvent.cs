using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TailRemote
{
    internal enum NvdaEventKind : byte { Speak = 1, Cancel = 2, Beep = 3, Wave = 4, Braille = 5 }

    /// <summary>
    /// One piece of what NVDA spoke: its words, how far NVDA moved the voice's pitch for them (NVDA's own scale, where
    /// a capital letter is usually +30), whether it is a capital letter, and whether NVDA spelled it out letter by letter.
    /// </summary>
    internal readonly record struct NvdaSpeechPart(string Text, sbyte Pitch = 0, bool Capital = false, bool Spelled = false);

    /// <summary>
    /// Something the remote PC's NVDA did, as the Kova Helper NVDA add-on hands it over (NvdaHelper): words it spoke,
    /// a cut-off (NVDA stopped talking), a beep, one of its sounds (by name only, never a path or audio), or what its
    /// braille display shows. The host sends each one to the controlling PCs in one message; the controlling PC acts on
    /// it with Perform. The same bytes travel from the add-on to Kova on the host and from the host to the controller.
    ///
    /// Wire form (the body of one message, little-endian):
    ///   Speak    1, u8 parts (1-64), each: u8 flags (1 capital, 2 spelled), i8 pitch (-100..100), u16 bytes, UTF-8 (1+ bytes)
    ///   Cancel   2
    ///   Beep     3, u16 hz (20-20000), u16 ms (1-5000), u8 left (0-100), u8 right (0-100)
    ///   Wave     4, u8 length (1-40), ASCII name of letters, digits, _ and -
    ///   Braille  5, u16 bytes, UTF-8 (may be empty: the display was cleared)
    /// Decode refuses anything else: unknown kinds or flags, numbers out of range, broken UTF-8, more than MaxText
    /// characters of text, or bytes left over.
    /// </summary>
    internal sealed class NvdaEvent
    {
        public const int MaxText = 4000;   // characters (UTF-16, as .NET counts them) in one Speak or Braille
        public const int MaxParts = 64;
        public const int MaxWaveName = 40;
        public const int MaxBody = 2 + MaxParts * 4 + MaxText * 3; // the largest body Encode can make

        public NvdaEventKind Kind { get; private init; }
        public IReadOnlyList<NvdaSpeechPart> Parts { get; private init; } = Array.Empty<NvdaSpeechPart>();
        public int Hz { get; private init; }
        public int Ms { get; private init; }
        public int Left { get; private init; }
        public int Right { get; private init; }
        /// <summary>The sound's name (Wave) or the braille text (Braille).</summary>
        public string Name { get; private init; } = "";
        public string Text { get; private init; } = "";

        private NvdaEvent() { }

        public static NvdaEvent Speak(params NvdaSpeechPart[] parts)
        {
            if (parts.Length < 1 || parts.Length > MaxParts) throw new ArgumentException("1 to " + MaxParts + " parts");
            int total = 0;
            foreach (var p in parts)
            {
                if (string.IsNullOrEmpty(p.Text)) throw new ArgumentException("empty part");
                if (p.Pitch < -100 || p.Pitch > 100) throw new ArgumentException("pitch out of range");
                total += p.Text.Length;
            }
            if (total > MaxText) throw new ArgumentException("more than " + MaxText + " characters");
            return new NvdaEvent { Kind = NvdaEventKind.Speak, Parts = (NvdaSpeechPart[])parts.Clone() };
        }

        public static NvdaEvent Cancel() => new() { Kind = NvdaEventKind.Cancel };

        public static NvdaEvent Beep(int hz, int ms, int left, int right)
        {
            if (hz < 20 || hz > 20000 || ms < 1 || ms > 5000 || left < 0 || left > 100 || right < 0 || right > 100) throw new ArgumentException("beep out of range");
            return new NvdaEvent { Kind = NvdaEventKind.Beep, Hz = hz, Ms = ms, Left = left, Right = right };
        }

        public static NvdaEvent Wave(string name)
        {
            if (!IsWaveName(name)) throw new ArgumentException("not a sound name");
            return new NvdaEvent { Kind = NvdaEventKind.Wave, Name = name };
        }

        public static NvdaEvent Braille(string text)
        {
            if (text.Length > MaxText) throw new ArgumentException("more than " + MaxText + " characters");
            return new NvdaEvent { Kind = NvdaEventKind.Braille, Text = text };
        }

        /// <summary>A sound's name as NVDA's waves folder has it: 1-40 letters, digits, underscores or dashes, nothing that could be a path.</summary>
        public static bool IsWaveName(string? name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxWaveName) return false;
            foreach (char c in name) if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')) return false;
            return true;
        }

        /// <summary>All the words of a Speak, one space between parts.</summary>
        public string SpokenText
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var p in Parts) { if (sb.Length > 0) sb.Append(' '); sb.Append(p.Text); }
                return sb.ToString();
            }
        }

        public byte[] Encode()
        {
            var ms = new MemoryStream(64);
            ms.WriteByte((byte)Kind);
            switch (Kind)
            {
                case NvdaEventKind.Speak:
                    ms.WriteByte((byte)Parts.Count);
                    foreach (var p in Parts)
                    {
                        byte[] utf = Strict.GetBytes(p.Text);
                        ms.WriteByte((byte)((p.Capital ? 1 : 0) | (p.Spelled ? 2 : 0)));
                        ms.WriteByte((byte)p.Pitch);
                        U16(ms, utf.Length);
                        ms.Write(utf);
                    }
                    break;
                case NvdaEventKind.Cancel:
                    break;
                case NvdaEventKind.Beep:
                    U16(ms, Hz); U16(ms, Ms); ms.WriteByte((byte)Left); ms.WriteByte((byte)Right);
                    break;
                case NvdaEventKind.Wave:
                    ms.WriteByte((byte)Name.Length);
                    ms.Write(Encoding.ASCII.GetBytes(Name));
                    break;
                case NvdaEventKind.Braille:
                    byte[] b = Strict.GetBytes(Text);
                    U16(ms, b.Length);
                    ms.Write(b);
                    break;
            }
            return ms.ToArray();
        }

        private static void U16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }

        // Broken UTF-8 (or a lone half of a two-part character) is refused, never patched up with replacement marks.
        private static readonly UTF8Encoding Strict = new(false, true);

        /// <summary>The event in a message body, or null if anything about it is wrong (see the class comment).</summary>
        public static NvdaEvent? Decode(ReadOnlySpan<byte> body)
        {
            try
            {
                if (body.Length < 1 || body.Length > MaxBody) return null;
                byte[] b = body.ToArray(); // small (MaxBody), and the helpers below cannot hold a span
                int at = 1;
                bool Take(int n) { if (n < 0 || b.Length - at < n) return false; return true; }
                int U8() => b[at++];
                int U16r() { int v = b[at] | b[at + 1] << 8; at += 2; return v; }
                NvdaEvent? ev;
                switch ((NvdaEventKind)b[0])
                {
                    case NvdaEventKind.Speak:
                    {
                        if (!Take(1)) return null;
                        int count = U8();
                        if (count < 1 || count > MaxParts) return null;
                        var parts = new NvdaSpeechPart[count];
                        int total = 0;
                        for (int i = 0; i < count; i++)
                        {
                            if (!Take(4)) return null;
                            int flags = U8();
                            sbyte pitch = (sbyte)U8();
                            int len = U16r();
                            if ((flags & ~3) != 0 || pitch < -100 || pitch > 100 || len < 1 || !Take(len)) return null;
                            string text = Strict.GetString(b, at, len);
                            at += len;
                            total += text.Length;
                            if (total > MaxText) return null;
                            parts[i] = new NvdaSpeechPart(text, pitch, (flags & 1) != 0, (flags & 2) != 0);
                        }
                        ev = new NvdaEvent { Kind = NvdaEventKind.Speak, Parts = parts };
                        break;
                    }
                    case NvdaEventKind.Cancel:
                        ev = new NvdaEvent { Kind = NvdaEventKind.Cancel };
                        break;
                    case NvdaEventKind.Beep:
                    {
                        if (!Take(6)) return null;
                        int hz = U16r(), ms = U16r(), left = U8(), right = U8();
                        if (hz < 20 || hz > 20000 || ms < 1 || ms > 5000 || left > 100 || right > 100) return null;
                        ev = new NvdaEvent { Kind = NvdaEventKind.Beep, Hz = hz, Ms = ms, Left = left, Right = right };
                        break;
                    }
                    case NvdaEventKind.Wave:
                    {
                        if (!Take(1)) return null;
                        int len = U8();
                        if (len < 1 || len > MaxWaveName || !Take(len)) return null;
                        var chars = new char[len];
                        for (int i = 0; i < len; i++) chars[i] = (char)b[at + i];
                        at += len;
                        string name = new(chars);
                        if (!IsWaveName(name)) return null;
                        ev = new NvdaEvent { Kind = NvdaEventKind.Wave, Name = name };
                        break;
                    }
                    case NvdaEventKind.Braille:
                    {
                        if (!Take(2)) return null;
                        int len = U16r();
                        if (!Take(len)) return null;
                        string text = Strict.GetString(b, at, len);
                        at += len;
                        if (text.Length > MaxText) return null;
                        ev = new NvdaEvent { Kind = NvdaEventKind.Braille, Text = text };
                        break;
                    }
                    default:
                        return null;
                }
                return at == b.Length ? ev : null; // nothing may follow
            }
            catch { return null; } // broken UTF-8
        }

        public override string ToString() => Kind switch
        {
            NvdaEventKind.Speak => "speak: " + SpokenText,
            NvdaEventKind.Beep => "beep " + Hz + " Hz " + Ms + " ms " + Left + "/" + Right,
            NvdaEventKind.Wave => "sound " + Name,
            NvdaEventKind.Braille => "braille: " + Text,
            _ => "cancel",
        };

        // ================= The controlling PC's side =================

        /// <summary>
        /// Cuts off what this PC's NVDA is saying for the remote PC and drops remote lines still waiting. Speech.cs does not
        /// have such a call yet: the lead sets this to Speech.RemoteCancel once it does (see the report). While it is null
        /// a cut-off from the remote NVDA does nothing, and remote lines simply queue up as before.
        /// </summary>
        internal static Action? RemoteCancel;

        /// <summary>
        /// Says SSML (for pitch changes and spelled letters) in this PC's NVDA, in order with the remote lines; the second
        /// string is the same as plain text, for an NVDA too old for SSML. Null until Speech.cs has it: the words are then
        /// said plainly, without the pitch.
        /// </summary>
        internal static Action<string, string>? RemoteSsml;

        /// <summary>Tests: every event Perform acts on, described.</summary>
        internal static Action<string>? PerformedForTest;

        /// <summary>
        /// Acts on an event from the remote PC's NVDA on this PC: words through this PC's NVDA, a cut-off cuts it off, braille
        /// to this PC's braille display, and beeps and sounds played by Kova itself (sounds: false when the remote PC's own
        /// sound already comes through, so they are not heard twice). Never waits: everything is handed to other threads.
        /// </summary>
        public static void Perform(NvdaEvent ev, bool sounds = true)
        {
            PerformedForTest?.Invoke(ev.ToString());
            switch (ev.Kind)
            {
                case NvdaEventKind.Speak:
                {
                    bool special = false;
                    foreach (var p in ev.Parts) if (p.Pitch != 0 || p.Spelled) special = true;
                    string plain = Speech.Clean(ev.SpokenText, MaxText);
                    if (plain.Length == 0 || string.IsNullOrWhiteSpace(plain)) return;
                    var ssml = RemoteSsml;
                    lock (BatchGate)
                    {
                        if (special && ssml != null)
                        {
                            FlushLocked(); // plain words that came first are said first
                            ssml(Ssml(ev.Parts), plain);
                            return;
                        }
                        AddPlainLocked(plain);
                    }
                    return;
                }
                case NvdaEventKind.Cancel:
                    lock (BatchGate)
                    {
                        // Words still gathering were cut off over there before they were said here: they go.
                        _batch.Clear();
                        RemoteCancel?.Invoke();
                    }
                    return;
                case NvdaEventKind.Braille:
                    Speech.Braille(ev.Text);
                    return;
                case NvdaEventKind.Beep:
                    if (sounds) NvdaSounds.Beep(ev.Hz, ev.Ms, ev.Left, ev.Right);
                    return;
                case NvdaEventKind.Wave:
                    if (sounds) NvdaSounds.Wave(ev.Name);
                    return;
            }
        }

        // ---- Plain words, gathered while they pour in ----
        // A console printing hundreds of lines a second made the remote NVDA hand over hundreds of tiny Speak events,
        // each said here with its own call into this PC's NVDA: past Speech's per-second limits the rest was skipped
        // ("The other PC sent more speech than could be read"), and NVDA fell behind. Now words are said at once when
        // things were quiet, and while they keep coming they are gathered for at most BatchMs and said as one line.

        internal const int BatchMs = 25;
        private static readonly object BatchGate = new();
        private static readonly StringBuilder _batch = new();
        private static long _lastFlush = long.MinValue / 2;
        private static System.Threading.Timer? _flushTimer;
        private static bool _flushArmed;

        /// <summary>Tests: the lines Perform hands on to be said, instead of Speech.Remote.</summary>
        internal static Action<string[]>? RemoteLinesForTest;

        private static void AddPlainLocked(string plain)
        {
            long now = Environment.TickCount64;
            if (_batch.Length > 0) _batch.Append(' ');
            _batch.Append(plain);
            // Quiet until now: said at once, never held back. A full line: said now too.
            if ((!_flushArmed && now - _lastFlush >= BatchMs) || _batch.Length >= MaxText) { FlushLocked(); return; }
            if (_flushArmed) return;
            _flushArmed = true;
            _flushTimer ??= new System.Threading.Timer(_ => { lock (BatchGate) { _flushArmed = false; FlushLocked(); } });
            _flushTimer.Change(Math.Clamp(BatchMs - (now - _lastFlush), 1, BatchMs), Timeout.Infinite);
        }

        private static void FlushLocked()
        {
            if (_batch.Length == 0) return;
            string text = _batch.ToString();
            _batch.Clear();
            _lastFlush = Environment.TickCount64;
            var lines = Chunks(text, Speech.RemoteLineMax);
            if (RemoteLinesForTest is { } test) test(lines);
            else Speech.Remote(lines, interrupt: false);
        }

        /// <summary>Tests: says what is gathering now and forgets when the last words were said.</summary>
        internal static void FlushForTest() { lock (BatchGate) { FlushLocked(); _lastFlush = long.MinValue / 2; } }

        /// <summary>
        /// The parts as SSML that this PC's NVDA understands (NVDA 2024.1 and later): a pitch change becomes prosody pitch
        /// as a percentage (NVDA's only form), spelled text say-as characters. Text is cleaned and put in CDATA sections.
        ///
        /// Never escaped with entities: NVDA's SSML reader (Python's expat) hands text over in pieces split at every entity,
        /// and NVDA speaks each piece as a separate string, symbols processed on their own. "don&amp;apos;t" became "don",
        /// "'", "t": the lone apostrophe is below the usual symbol level, so it was dropped and "don t" was heard (the
        /// same for quotes). Inside CDATA the text arrives whole, as NVDA itself would have said it.
        /// </summary>
        internal static string Ssml(IReadOnlyList<NvdaSpeechPart> parts)
        {
            var sb = new StringBuilder("<speak>");
            foreach (var p in parts)
            {
                string clean = Speech.Clean(p.Text, MaxText);
                if (clean.Length == 0) continue;
                string t = Cdata(clean);
                if (p.Spelled) t = "<say-as interpret-as=\"characters\">" + t + "</say-as>";
                // NVDA's pitch scale runs 0-100 around a usual 50, so +30 is about 60 percent higher.
                if (p.Pitch != 0) t = "<prosody pitch=\"" + Math.Clamp(100 + p.Pitch * 2, 20, 300) + "%\">" + t + "</prosody>";
                sb.Append(t);
            }
            return sb.Append("</speak>").ToString();
        }

        /// <summary>Text as an XML CDATA section; the one sequence CDATA cannot hold ("]]&gt;") is split over two sections.</summary>
        internal static string Cdata(string text) => "<![CDATA[" + text.Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal) + "]]>";

        // ================= The host's side: a burst made into fewer messages =================

        /// <summary>
        /// Messages waiting for one controlling PC (whole messages: type byte, then body), made into fewer: Speak events
        /// that follow each other become one (within MaxParts and MaxText), and of several braille events only the newest
        /// is kept (the display shows one thing at a time). Everything else, cut-offs above all, stays exactly where it
        /// was, so nothing changes order. The host only has a burst to merge when the line is slower than NVDA, so a
        /// single line is never held back.
        /// </summary>
        internal static List<byte[]> Merge(List<byte[]> messages)
        {
            int lastBraille = -1;
            var decoded = new NvdaEvent?[messages.Count];
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m.Length >= 2 && m[0] == Protocol.NvdaEventMsg) decoded[i] = Decode(m.AsSpan(1));
                if (decoded[i]?.Kind == NvdaEventKind.Braille) lastBraille = i;
            }
            var output = new List<byte[]>(messages.Count);
            List<NvdaSpeechPart>? run = null;
            int runText = 0;
            byte[]? runOnly = null; // a run of one is sent as it came
            void EndRun()
            {
                if (run == null) return;
                output.Add(runOnly ?? Message(Speak(run.ToArray())));
                run = null; runOnly = null; runText = 0;
            }
            for (int i = 0; i < messages.Count; i++)
            {
                var ev = decoded[i];
                if (ev?.Kind == NvdaEventKind.Braille && i != lastBraille) continue;
                if (ev?.Kind == NvdaEventKind.Speak)
                {
                    int text = 0;
                    foreach (var p in ev.Parts) text += p.Text.Length;
                    if (run != null && (run.Count + ev.Parts.Count > MaxParts || runText + text > MaxText)) EndRun();
                    if (run == null) { run = new List<NvdaSpeechPart>(ev.Parts); runOnly = messages[i]; }
                    else { run.AddRange(ev.Parts); runOnly = null; }
                    runText += text;
                    continue;
                }
                EndRun();
                output.Add(messages[i]);
            }
            EndRun();
            return output;
        }

        /// <summary>The event as a whole NvdaEventMsg message.</summary>
        internal static byte[] Message(NvdaEvent ev)
        {
            byte[] body = ev.Encode();
            byte[] m = new byte[1 + body.Length];
            m[0] = Protocol.NvdaEventMsg;
            body.CopyTo(m, 1);
            return m;
        }

        /// <summary>Text cut into lines Speech.Remote takes whole (it cuts anything longer), at spaces where it can.</summary>
        internal static string[] Chunks(string text, int max)
        {
            var list = new List<string>();
            int at = 0;
            while (at < text.Length)
            {
                int len = Math.Min(max, text.Length - at);
                if (at + len < text.Length)
                {
                    int space = text.LastIndexOf(' ', at + len - 1, len);
                    if (space > at) len = space - at + 1;
                    else if (char.IsHighSurrogate(text[at + len - 1])) len--;
                }
                list.Add(text.Substring(at, len).Trim());
                at += len;
            }
            list.RemoveAll(s => s.Length == 0);
            return list.ToArray();
        }
    }

    /// <summary>
    /// NVDA's beeps and sounds from the remote PC, played on this PC by Kova itself through Windows' plain wave output
    /// (each sound its own output, so they mix with Kova's other sounds and never cut them off). As in NVDA, a new beep
    /// cuts off the beep before it, and a new sound the sound before it. Sounds come from this PC's own NVDA (its waves
    /// folder), found by a checked name only: nothing from the remote PC ever names a file here.
    /// </summary>
    internal static class NvdaSounds
    {
        private const int Rate = 44100;
        private static readonly Channel Beeps = new("Kova NVDA beeps"), Waves = new("Kova NVDA sounds");
        // A broken or hostile host could send thousands a second: past these, the rest of that second is skipped.
        private const int BeepsPerSecond = 30, WavesPerSecond = 10;
        private static long _beepSecond, _waveSecond;
        private static int _beepCount, _waveCount;

        public static void Beep(int hz, int ms, int left, int right)
        {
            if (!Allow(ref _beepSecond, ref _beepCount, BeepsPerSecond)) return;
            Beeps.Play(() => MakeBeep(hz, ms, left, right));
        }

        public static void Wave(string name)
        {
            if (!NvdaEvent.IsWaveName(name) || !Allow(ref _waveSecond, ref _waveCount, WavesPerSecond)) return;
            Waves.Play(() => LoadWave(name));
        }

        private static readonly object RateGate = new();
        private static bool Allow(ref long second, ref int count, int max)
        {
            lock (RateGate)
            {
                long now = Environment.TickCount64 / 1000;
                if (now != second) { second = now; count = 0; }
                return ++count <= max;
            }
        }

        /// <summary>A beep like NVDA's: a sine tone, each side at its volume, with soft edges so it never clicks.</summary>
        internal static (short[] Pcm, int Rate) MakeBeep(int hz, int ms, int left, int right)
        {
            int n = Rate * ms / 1000;
            var pcm = new short[n * 2];
            int fade = Math.Min(n / 2, Rate / 200); // 5 ms
            double step = 2 * Math.PI * hz / Rate, amp = 0.3 * short.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double edge = fade == 0 ? 1 : Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)fade);
                double v = Math.Sin(i * step) * amp * edge;
                pcm[2 * i] = (short)(v * left / 100);
                pcm[2 * i + 1] = (short)(v * right / 100);
            }
            return (pcm, Rate);
        }

        // ---- NVDA's own sounds on this PC ----

        private static readonly Dictionary<string, (short[] Pcm, int Rate)?> WaveCache = new(StringComparer.OrdinalIgnoreCase);
        private static string? _wavesDir;
        private static long _wavesDirAt = long.MinValue / 2;

        private static (short[] Pcm, int Rate)? LoadWave(string name)
        {
            string? dir = WavesFolder();
            if (dir == null) return null;
            lock (WaveCache)
            {
                if (WaveCache.TryGetValue(name, out var have)) return have;
                (short[], int)? w = null;
                try
                {
                    string path = Path.Combine(dir, name + ".wav");
                    var fi = new FileInfo(path);
                    // Only straight inside the waves folder (the name has no path characters anyway), and never huge.
                    if (fi.Exists && fi.Length <= 8 << 20 && string.Equals(fi.DirectoryName, dir, StringComparison.OrdinalIgnoreCase))
                        w = ParseWav(File.ReadAllBytes(path));
                }
                catch { }
                if (WaveCache.Count >= 64) WaveCache.Clear();
                WaveCache[name] = w;
                return w;
            }
        }

        /// <summary>This PC's NVDA's waves folder: the running NVDA's own, else an installed one; looked for again each minute.</summary>
        internal static string? WavesFolder()
        {
            if (Environment.TickCount64 - _wavesDirAt < 60_000) return _wavesDir;
            string? found = null;
            try
            {
                if (NvdaHelper.FindNvda(NvdaHelper.MySession()) is { } p)
                {
                    string d = Path.Combine(Path.GetDirectoryName(p.Exe) ?? "", "waves");
                    if (Directory.Exists(d)) found = d;
                }
                foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                {
                    if (found != null) break;
                    string pf = Environment.GetFolderPath(root);
                    string d = Path.Combine(pf, "NVDA", "waves");
                    if (pf.Length > 0 && Directory.Exists(d)) found = d;
                }
            }
            catch { }
            lock (WaveCache) { if (!string.Equals(found, _wavesDir, StringComparison.OrdinalIgnoreCase)) WaveCache.Clear(); }
            _wavesDir = found;
            _wavesDirAt = Environment.TickCount64;
            return found;
        }

        /// <summary>A plain PCM wave file (8, 16, 24 or 32 bits, mono or stereo) as 16-bit stereo, or null for anything else.</summary>
        internal static (short[] Pcm, int Rate)? ParseWav(byte[] f)
        {
            if (f.Length < 12 || Encoding.ASCII.GetString(f, 0, 4) != "RIFF" || Encoding.ASCII.GetString(f, 8, 4) != "WAVE") return null;
            int at = 12, channels = 0, rate = 0, bits = 0, format = 0;
            while (at + 8 <= f.Length)
            {
                string id = Encoding.ASCII.GetString(f, at, 4);
                int size = BitConverter.ToInt32(f, at + 4);
                if (size < 0 || size > f.Length - at - 8) size = f.Length - at - 8;
                int body = at + 8;
                if (id == "fmt " && size >= 16)
                {
                    format = BitConverter.ToUInt16(f, body);
                    channels = BitConverter.ToUInt16(f, body + 2);
                    rate = BitConverter.ToInt32(f, body + 4);
                    bits = BitConverter.ToUInt16(f, body + 14);
                    if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(f, body + 24); // extensible: its sub-format
                }
                else if (id == "data")
                {
                    if (format != 1 || channels is < 1 or > 2 || rate is < 8000 or > 192000 || bits is not (8 or 16 or 24 or 32)) return null;
                    int bytes = bits / 8, frame = bytes * channels, frames = size / frame;
                    var pcm = new short[frames * 2];
                    for (int i = 0; i < frames; i++)
                        for (int c = 0; c < 2; c++)
                        {
                            int p = body + i * frame + Math.Min(c, channels - 1) * bytes;
                            short s = bits switch
                            {
                                8 => (short)((f[p] - 128) << 8),
                                16 => BitConverter.ToInt16(f, p),
                                24 => (short)(f[p + 1] | f[p + 2] << 8),
                                _ => (short)(BitConverter.ToInt32(f, p) >> 16),
                            };
                            pcm[i * 2 + c] = s;
                        }
                    return (pcm, rate);
                }
                at = body + size + (size & 1);
            }
            return null;
        }

        /// <summary>One kind of sound: one thread, the newest sound cuts off the one playing.</summary>
        private sealed class Channel
        {
            private readonly string _name;
            private readonly object _gate = new();
            private Func<(short[] Pcm, int Rate)?>? _next;
            private readonly AutoResetEvent _wake = new(false);
            private Thread? _thread;

            public Channel(string name) { _name = name; }

            public void Play(Func<(short[] Pcm, int Rate)?> make)
            {
                lock (_gate)
                {
                    _next = make;
                    if (_thread == null)
                    {
                        _thread = new Thread(Run) { IsBackground = true, Name = _name };
                        _thread.Start();
                    }
                }
                _wake.Set();
            }

            private void Run()
            {
                var done = new AutoResetEvent(false);
                IntPtr dev = IntPtr.Zero, hdr = IntPtr.Zero, data = IntPtr.Zero;
                void Stop()
                {
                    if (dev == IntPtr.Zero) return;
                    waveOutReset(dev);
                    if (hdr != IntPtr.Zero) waveOutUnprepareHeader(dev, hdr, (uint)Marshal.SizeOf<WAVEHDR>());
                    waveOutClose(dev);
                    dev = IntPtr.Zero;
                    if (hdr != IntPtr.Zero) { Marshal.FreeHGlobal(hdr); hdr = IntPtr.Zero; }
                    if (data != IntPtr.Zero) { Marshal.FreeHGlobal(data); data = IntPtr.Zero; }
                }
                var waits = new WaitHandle[] { _wake, done };
                while (true)
                {
                    int which = WaitHandle.WaitAny(waits, dev == IntPtr.Zero ? Timeout.Infinite : 250);
                    // Finished playing (or the device fell silent): let it go.
                    if (dev != IntPtr.Zero && (which != 0 && Finished(hdr))) Stop();
                    Func<(short[] Pcm, int Rate)?>? make;
                    lock (_gate) { make = _next; _next = null; }
                    if (make == null) continue;
                    try
                    {
                        var sound = make();
                        if (sound == null) continue;
                        Stop(); // cuts off the one before
                        var (pcm, rate) = sound.Value;
                        if (pcm.Length == 0) continue;
                        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = (uint)rate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = (uint)rate * 4 };
                        if (waveOutOpen(out dev, WAVE_MAPPER, ref fmt, done.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT) != 0) { dev = IntPtr.Zero; continue; }
                        int bytes = pcm.Length * 2;
                        data = Marshal.AllocHGlobal(bytes);
                        Marshal.Copy(pcm, 0, data, pcm.Length);
                        hdr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
                        Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = (uint)bytes }, hdr, false);
                        if (waveOutPrepareHeader(dev, hdr, (uint)Marshal.SizeOf<WAVEHDR>()) != 0 || waveOutWrite(dev, hdr, (uint)Marshal.SizeOf<WAVEHDR>()) != 0) Stop();
                    }
                    catch { Stop(); }
                }
            }

            private static bool Finished(IntPtr hdr) =>
                hdr == IntPtr.Zero || (Marshal.ReadInt32(hdr, (int)Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwFlags))) & WHDR_DONE) != 0;
        }

        private const uint WAVE_MAPPER = 0xFFFFFFFF, CALLBACK_EVENT = 0x00050000;
        private const int WHDR_DONE = 1;
        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr dev, uint id, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr dev, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr dev, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr dev, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr dev);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr dev);
    }
}
