using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using Concentus;

namespace TailRemote
{
    /// <summary>
    /// Short event sounds, chosen per event in Sounds (SoundsForm).
    ///
    /// Patterns: 12 little figures (rising, two up, sigh...) played on real
    /// instruments in any major key (Settings.SoundKey): a Steinway (University of
    /// Iowa Electronic Music Studios, free for any use), harp, glockenspiel,
    /// marimba, xylophone and violin pizzicato (VSCO 2 Community Edition, public
    /// domain), and a classic phone buzzer made here. Each note uses the nearest
    /// real recording of it, then a little stereo room.
    ///
    /// Fixed sounds: Android's notification and effect sounds (Android Open Source
    /// Project, Apache License 2.0), Nepalese bells (VSCO 2), and classic phone
    /// tunes written here in the style of the old monophonic phones.
    ///
    /// Recordings are built into the exe as Opus (native\sounds, snd.*.opus) and
    /// decoded the first time they are needed. Played on the controlling PC only,
    /// never on a host, where they would be sent along with its sound.
    /// </summary>
    internal static class Sounds
    {
        public enum Tone { Connected, Disconnected, ClipboardSent, ClipboardReceived, FileSent, FileReceived, ControlRemote, ControlLocal, Error }

        public static string Label(Tone t) => t switch
        {
            Tone.Connected => "&Connected",
            Tone.Disconnected => "&Disconnected",
            Tone.ClipboardSent => "Clipboard &sent",
            Tone.ClipboardReceived => "Clipboard &received",
            Tone.FileSent => "&File sent",
            Tone.FileReceived => "File recei&ved",
            Tone.ControlRemote => "Control re&mote PC",
            Tone.ControlLocal => "Control &this PC",
            _ => "&Error",
        };

        private const int Rate = 48000;
        public const string NoSound = "None";

        /// <summary>The 12 major keys, by pitch class (C = 0).</summary>
        public static readonly string[] Keys =
            { "C major", "D flat major", "D major", "E flat major", "E major", "F major", "F sharp major", "G major", "A flat major", "A major", "B flat major", "B major" };

        /// <summary>The key the patterns play in (pitch class, C = 0). B flat until changed.</summary>
        public static volatile int Key = 10;

        // Instruments: display name, the recordings they use (snd.NAME.MIDI.opus) or null for the made one,
        // and how many semitones above the piano they play: each where its real recordings are, as the
        // instrument itself sounds (a glockenspiel two octaves up), so no note is retuned more than a little.
        private static readonly (string Name, string? Bank, bool Soft, int Up)[] Instruments =
        {
            ("Piano", "piano", false, 0), ("Soft piano", "piano", true, 0), ("Harp", "harp", false, 0), ("Glockenspiel", "glockenspiel", false, 24),
            ("Marimba", "marimba", false, 0), ("Xylophone", "xylophone", false, 12), ("Pizzicato strings", "pizzicato", false, 12), ("Classic phone", null, false, 24),
            // Made here, in code:
            ("Chime", "made", false, 0), ("Music box", "made", false, 0), ("Electric piano", "made", false, 0), ("Glass", "made", false, 0),
            ("Kalimba", "made", false, 0), ("Bell", "made", false, 0), ("Text tone", "made", false, 0), ("Vibraphone", "made", false, 0),
        };

        /// <summary>The choice that plays the event's own pattern on a different instrument each time.</summary>
        public const string DefaultName = "Default";
        /// <summary>The choice that plays any sound at all, a different one each time.</summary>
        public const string RandomName = "Random sound";

        // Patterns: semitones above the key's home note, and when (seconds). Every one is in the major key.
        private static readonly (string Name, (int Step, double At)[] Notes)[] Patterns =
        {
            ("rising", new[] { (0, 0.0), (4, 0.055), (7, 0.11), (12, 0.165) }),
            ("falling", new[] { (7, 0.0), (4, 0.075), (0, 0.15) }),
            ("two up", new[] { (7, 0.0), (12, 0.05) }),
            ("two down", new[] { (12, 0.0), (7, 0.05) }),
            ("four up", new[] { (4, 0.0), (7, 0.05), (12, 0.10), (16, 0.15) }),
            ("three down", new[] { (16, 0.0), (14, 0.07), (12, 0.14) }),
            ("chord high", new[] { (4, 0.0), (7, 0.0), (12, 0.0) }),
            ("chord low", new[] { (0, 0.0), (4, 0.0), (7, 0.0) }),
            ("sigh", new[] { (5, 0.0), (4, 0.13) }),
            ("single", new[] { (12, 0.0) }),
            ("bounce", new[] { (7, 0.0), (12, 0.06), (7, 0.12) }),
            ("sparkle", new[] { (9, 0.0), (11, 0.035), (12, 0.07), (14, 0.105), (16, 0.14) }),
        };

        // Classic phone tunes, written here: (MIDI note or 0 for a rest, length in seconds).
        private static readonly (string Name, (int Note, double Len)[] Notes)[] PhoneTunes =
        {
            // S M S in Morse code.
            ("SMS", new[] { (93, .06), (0, .06), (93, .06), (0, .06), (93, .06), (0, .18), (93, .18), (0, .06), (93, .18), (0, .18), (93, .06), (0, .06), (93, .06), (0, .06), (93, .06) }),
            // Gran Vals (Tárrega, 1902): the phrase old phones made famous.
            ("Gran Vals", new[] { (88, .11), (86, .11), (78, .22), (80, .22), (85, .11), (83, .11), (74, .22), (76, .22), (83, .11), (81, .11), (73, .22), (76, .22), (81, .44) }),
            ("Ascending", new[] { (84, .05), (88, .05), (91, .05), (96, .1) }),
            ("Standard", new[] { (93, .09), (0, .07), (93, .09) }),
            ("Ripple", new[] { (88, .04), (91, .04), (88, .04), (91, .04), (88, .04), (91, .04), (88, .04), (91, .08) }),
            ("Low", new[] { (69, .12), (0, .03), (64, .16) }),
            ("Espionage", new[] { (91, .07), (0, .03), (91, .07), (0, .03), (98, .14) }),
            ("Bee", new[] { (86, .03), (87, .03), (86, .03), (87, .03), (86, .03), (87, .03), (86, .03), (87, .06) }),
        };

        private static readonly Lazy<string[]> Resources = new(() => typeof(Sounds).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("snd.")).ToArray());

        /// <summary>Every sound by name, None first.</summary>
        public static IReadOnlyList<string> All => _all.Value;
        private static readonly Lazy<List<string>> _all = new(() =>
        {
            var names = new List<string> { NoSound };
            foreach (var i in Instruments) foreach (var p in Patterns) names.Add(i.Name + ", " + p.Name);
            foreach (var t in PhoneTunes) names.Add("Classic phone: " + t.Name);
            // Recorded fixed sounds: snd.fixed.GROUP.NAME.opus, grouped as they sort.
            foreach (string r in Resources.Value.Where(r => r.StartsWith("snd.fixed.")).OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
            {
                string rest = r["snd.fixed.".Length..^".opus".Length];
                int dot = rest.IndexOf('.');
                string group = rest[..dot], name = rest[(dot + 1)..];
                names.Add(group switch { "android" => "Android: " + name, "android effect" => "Android effect: " + name, _ => name });
            }
            return names;
        });

        public static string Default(Tone t) => "Piano, " + t switch
        {
            Tone.Connected => "rising",
            Tone.Disconnected => "falling",
            Tone.ClipboardSent => "two up",
            Tone.ClipboardReceived => "two down",
            Tone.FileSent => "four up",
            Tone.FileReceived => "three down",
            Tone.ControlRemote => "chord high",
            Tone.ControlLocal => "chord low",
            _ => "sigh",
        };

        /// <summary>The chosen sound for each event, or DefaultName or RandomName.</summary>
        public static Func<Tone, string> Choice = _ => DefaultName;

        private static readonly Random Rng = new();
        private static readonly Dictionary<Tone, string> Last = new();

        /// <summary>
        /// What an event plays this time. Default: always the same, its own tune on the piano
        /// (rising for connecting, a sigh for an error...), so each event sounds like itself.
        /// (It used to pick a new instrument every time, which just sounded wrong.) Random
        /// sound: anything at all, never the same twice in a row; only if you choose it.
        /// </summary>
        public static string Resolve(Tone t, string choice)
        {
            if (choice == DefaultName) return Default(t);
            if (choice != RandomName) return choice;
            lock (Last)
            {
                Last.TryGetValue(t, out var before);
                string pick;
                do pick = All[Rng.Next(1, All.Count)];
                while (pick == before && All.Count > 2);
                Last[t] = pick;
                return pick;
            }
        }

        // Made sounds, newest used first, at most CacheMax of them. Kept for ever, they grew past 100 MB over
        // weeks of Random sound. Dropping one is always safe: a SoundPlayer playing it holds its own reference.
        private const int CacheMax = 32;
        private static readonly Dictionary<string, LinkedListNode<(string Key, byte[] Wav)>> Cache = new();
        private static readonly LinkedList<(string Key, byte[] Wav)> CacheOrder = new();

        // One thread makes and starts every sound. Windows plays one SoundPlayer sound at a time (a
        // new one stops the one before), so only the newest request matters: moving quickly
        // through the Sounds lists used to queue a pool thread per sound, each asleep for the
        // sound's whole length, and old previews kept starting after newer ones.
        private static readonly object SlotGate = new();
        private static (string Name, int Key)? _next;
        private static System.Threading.Thread? _soundThread;
        // The sound playing now, held so it is never finalised mid-playback (that cut it off).
        private static SoundPlayer? _current;

        /// <summary>Plays an event's sound. Built and started in the background: the window never waits on a sound.</summary>
        public static void Play(Tone t)
        {
            string name = Resolve(t, Choice(t));
            PlayNamed(name);
        }

        /// <summary>Plays a sound by name, in the background (the Sounds window previews with it).</summary>
        public static void PlayNamed(string name)
        {
            lock (SlotGate)
            {
                _next = (name, Key); // the key now: the Sounds window may put it back before this is made
                if (_soundThread == null)
                {
                    _soundThread = new System.Threading.Thread(SoundLoop) { IsBackground = true, Name = "Kova sounds" };
                    _soundThread.Start();
                }
                System.Threading.Monitor.Pulse(SlotGate);
            }
        }

        private static void SoundLoop()
        {
            while (true)
            {
                (string Name, int Key) want;
                lock (SlotGate)
                {
                    while (_next == null) System.Threading.Monitor.Wait(SlotGate);
                    want = _next.Value;
                    _next = null;
                }
                try
                {
                    if (Get(want.Name, want.Key) is not byte[] wav) continue;
                    // A newer one came in while this was being made: play that one instead.
                    lock (SlotGate) if (_next != null) continue;
                    var player = new SoundPlayer(new MemoryStream(wav));
                    player.Play(); // returns at once; stops whatever was playing
                    var before = _current;
                    _current = player;
                    before?.Dispose();
                }
                catch { }
            }
        }

        public static void PlayNamedAndWait(string name)
        {
            try
            {
                if (Get(name, Key) is not byte[] wav) return;
                using var player = new SoundPlayer(new MemoryStream(wav));
                player.PlaySync();
            }
            catch { }
        }

        public static void PlayAndWait(Tone t) => PlayNamedAndWait(Resolve(t, Choice(t)));

        /// <summary>Decodes the piano in the background, so the first sounds play at once.</summary>
        public static void Warm() => System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (Tone t in Enum.GetValues<Tone>()) try { Get(Default(t), Key); } catch { }
        });

        private static byte[]? Get(string name, int key)
        {
            if (name == NoSound) return null;
            // Only the patterns play in a key: the same fixed sound made once per key was twelve copies.
            string cacheKey = UsesKey(name) ? name + "|" + key : name;
            lock (Cache)
            {
                if (Cache.TryGetValue(cacheKey, out var have))
                {
                    CacheOrder.Remove(have);
                    CacheOrder.AddFirst(have);
                    return have.Value.Wav;
                }
                var (l, r) = Make(name, key);
                if (l == null) return null;
                byte[] wav = Wav(l, r!);
                Cache[cacheKey] = CacheOrder.AddFirst((cacheKey, wav));
                while (Cache.Count > CacheMax)
                {
                    var oldest = CacheOrder.Last!;
                    CacheOrder.RemoveLast();
                    Cache.Remove(oldest.Value.Key);
                }
                return wav;
            }
        }

        /// <summary>Whether a sound depends on the key: the patterns do; phone tunes and recordings don't.</summary>
        private static bool UsesKey(string name) =>
            !name.StartsWith("Classic phone: ") && FixedResource(name) == null;

        /// <summary>The built-in recording a fixed sound plays, or null if it is not one.</summary>
        private static string? FixedResource(string name) =>
            name.StartsWith("Android effect: ") ? "snd.fixed.android effect." + name["Android effect: ".Length..] + ".opus"
            : name.StartsWith("Android: ") ? "snd.fixed.android." + name["Android: ".Length..] + ".opus"
            : name.StartsWith("Nepalese bell") ? "snd.fixed.bell." + name + ".opus" : null;

        private static (double[]? L, double[]? R) Make(string name, int key)
        {
            if (name.StartsWith("Classic phone: "))
            {
                var tune = PhoneTunes.FirstOrDefault(t => t.Name == name["Classic phone: ".Length..]);
                return tune.Notes == null ? (null, null) : PhoneTune(tune.Notes);
            }
            // A fixed sound is decoded, made and let go: only its finished WAV is cached. (Keeping the
            // decoded recording as well was 768 KB a second of sound, for every one ever played.)
            if (FixedResource(name) is string res) return Recording(res, keep: false);
            int comma = name.IndexOf(", ", StringComparison.Ordinal);
            if (comma < 0) return (null, null);
            int instrument = Array.FindIndex(Instruments, i => i.Name == name[..comma]);
            int pattern = Array.FindIndex(Patterns, p => p.Name == name[(comma + 2)..]);
            if (instrument < 0 || pattern < 0) return (null, null);
            return PatternSound(Instruments[instrument], Patterns[pattern].Notes, key);
        }

        // ---- Patterns on real instruments ----

        private static (double[] L, double[] R) PatternSound((string Name, string? Bank, bool Soft, int Up) inst, (int Step, double At)[] notes, int key)
        {
            // The key's home note: the one from G flat 3 (54) up, so every key fits the recordings.
            int home = 54 + ((key - 54 % 12) % 12 + 12) % 12 + inst.Up;
            if (inst.Bank == "made") return MadePattern(inst.Name, notes, home);
            if (inst.Bank == null)
            {
                // The classic phone plays one note at a time: chords become a quick run, an octave up.
                var run = new List<(int, double)>();
                foreach (var (step, _) in notes) run.Add((home + step, 0.07));
                return PhoneTune(run.SelectMany(n => new[] { n, (0, 0.015) }).ToArray());
            }
            double last = notes.Max(n => n.At);
            int length = (int)((last + 1.0) * Rate);
            var left = new double[length];
            var right = new double[length];
            for (int k = 0; k < notes.Length; k++)
            {
                var (step, at) = notes[k];
                var (nl, nr) = Note(inst.Bank, home + step);
                double pan = notes.Length == 1 ? 0 : -0.2 + 0.4 * k / (notes.Length - 1); // a slight spread, low to high
                double gl = Math.Sqrt(0.5 * (1 - pan)) * 1.414, gr = Math.Sqrt(0.5 * (1 + pan)) * 1.414;
                int start = (int)(at * Rate), n = Math.Min(nl.Length, length - start);
                double lpL = 0, lpR = 0, a = inst.Soft ? 0.2 : 1; // the soft piano: rounder and quieter
                for (int i = 0; i < n; i++)
                {
                    lpL += a * (nl[i] - lpL); lpR += a * (nr[i] - lpR);
                    left[start + i] += lpL * gl * (inst.Soft ? 0.8 : 1);
                    right[start + i] += lpR * gr * (inst.Soft ? 0.8 : 1);
                }
            }
            Room(left, right, inst.Soft ? 0.26 : 0.18);
            return (left, right);
        }

        /// <summary>One note from an instrument's recordings: the nearest one, retuned the rest of the way. Quick: it rings 0.42 s, then fades over 0.22 s.</summary>
        private static (double[] L, double[] R) Note(string bank, int midi)
        {
            var have = Resources.Value.Where(r => r.StartsWith("snd." + bank + "."))
                .Select(r => int.Parse(r["snd.".Length..^".opus".Length][(bank.Length + 1)..])).ToArray();
            int nearest = have.OrderBy(m => Math.Abs(m - midi)).First();
            var (sl, sr) = Recording("snd." + bank + "." + nearest + ".opus");
            double ratio = Math.Pow(2, (midi - nearest) / 12.0);
            int ring = (int)(0.42 * Rate), release = (int)(0.22 * Rate), length = Math.Min(ring + release, (int)((sl.Length - 2) / ratio));
            var l = new double[Math.Max(0, length)];
            var r = new double[l.Length];
            for (int i = 0; i < l.Length; i++)
            {
                double pos = i * ratio;
                int p = (int)pos;
                double f = pos - p;
                double g = i > ring ? Math.Pow(Math.Cos((i - ring) / (double)release * Math.PI / 2), 2) : 1;
                l[i] = (sl[p] + (sl[p + 1] - sl[p]) * f) * g;
                r[i] = (sr[p] + (sr[p + 1] - sr[p]) * f) * g;
            }
            return (l, r);
        }

        // ---- The instruments made in code ----

        private static (double[] L, double[] R) MadePattern(string instrument, (int Step, double At)[] notes, int home)
        {
            double last = notes.Max(n => n.At);
            int length = (int)((last + 1.0) * Rate);
            var left = new double[length];
            var right = new double[length];
            for (int k = 0; k < notes.Length; k++)
            {
                var (step, at) = notes[k];
                int start = (int)(at * Rate);
                double pan = notes.Length == 1 ? 0 : -0.25 + 0.5 * k / (notes.Length - 1);
                double gl = Math.Sqrt(0.5 * (1 - pan)) * 1.414, gr = Math.Sqrt(0.5 * (1 + pan)) * 1.414;
                double[] v = Voice(instrument, 440 * Math.Pow(2, (home + step - 69) / 12.0), length - start);
                for (int i = 0; i < v.Length; i++) { left[start + i] += v[i] * gl; right[start + i] += v[i] * gr; }
            }
            Room(left, right, 0.18);
            return (left, right);
        }

        /// <summary>One note of a made instrument, mono, at most 0.75 s.</summary>
        private static double[] Voice(string instrument, double f, int room)
        {
            int length = Math.Min(room, (int)(0.75 * Rate));
            var v = new double[length];
            static double Tau(double seconds, double t) => Math.Exp(-t / seconds);
            for (int i = 0; i < length; i++)
            {
                double t = i / (double)Rate, w = 2 * Math.PI * f * t;
                double attack = Math.Min(1, i / (Rate * 0.003));
                v[i] = attack * instrument switch
                {
                    "Chime" => Math.Sin(w + 2.2 * Tau(0.25, t) * Math.Sin(3.5 * w)) * Tau(0.45, t),
                    "Music box" => (Math.Sin(2 * w) + 0.3 * Math.Sin(6 * w) * Tau(0.08, t)) * Tau(0.4, t),
                    "Electric piano" => (Math.Sin(w + 1.2 * Tau(0.3, t) * Math.Sin(w)) + 0.25 * Math.Sin(14 * w) * Tau(0.015, t)) * Tau(0.5, t),
                    "Glass" => (Math.Sin(w) + 0.4 * Math.Sin(2.76 * w) * Tau(0.3, t)) * Math.Min(1, t / 0.02) * Tau(0.5, t),
                    "Kalimba" => (Math.Sin(w) + 0.5 * Math.Sin(5.4 * w) * Tau(0.012, t)) * Tau(0.25, t),
                    "Bell" => (Math.Sin(2 * w) + 0.6 * Math.Sin(2 * 2.4 * w) * Tau(0.2, t) + 0.35 * Math.Sin(2 * 3.9 * w) * Tau(0.12, t)) * Tau(0.5, t),
                    "Text tone" => Math.Sin(2 * Math.PI * f * 2 * (t - 0.02 * t * t)) * Tau(0.09, t),
                    "Vibraphone" => (Math.Sin(w) + 0.25 * Math.Sin(4 * w) * Tau(0.05, t)) * (1 - 0.3 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 5.5 * t))) * Tau(0.55, t),
                    _ => Math.Sin(w) * Tau(0.3, t),
                };
            }
            int release = Rate / 20; // a soft stop at the end, never a click
            for (int i = 0; i < Math.Min(release, length); i++) v[length - 1 - i] *= i / (double)release;
            return v;
        }

        // ---- The classic phone buzzer ----

        private static (double[] L, double[] R) PhoneTune((int Note, double Len)[] notes)
        {
            int length = (int)((notes.Sum(n => n.Len) + 0.25) * Rate);
            var v = new double[length];
            int at = 0;
            foreach (var (note, len) in notes)
            {
                int n = (int)(len * Rate);
                if (note > 0)
                {
                    double f = 440 * Math.Pow(2, (note - 69) / 12.0), phase = 0;
                    for (int i = 0; i < n && at + i < length; i++)
                    {
                        // A soft-edged square wave (its first few odd overtones), like a phone's buzzer.
                        phase += 2 * Math.PI * f / Rate;
                        double s = 0;
                        for (int h = 1; h <= 9 && h * f < Rate / 2.5; h += 2) s += Math.Sin(h * phase) / h;
                        double edge = Math.Min(1, Math.Min(i, n - i) / (Rate * 0.003)); // no clicks between notes
                        v[at + i] = s * 0.8 * edge;
                    }
                }
                at += n;
            }
            var right = (double[])v.Clone();
            Room(v, right, 0.08);
            return (v, right);
        }

        // ---- Recordings ----

        private static readonly Dictionary<string, (double[] L, double[] R)> Recordings = new();

        /// <summary>
        /// A built-in recording (Ogg Opus), decoded. keep: decoded once and kept, for the instrument notes
        /// the patterns are built from (a fixed set); fixed sounds are not kept, their WAV is cached instead.
        /// A caller that is given a kept one must not change it.
        /// </summary>
        private static (double[] L, double[] R) Recording(string resource, bool keep = true)
        {
            if (Recordings.TryGetValue(resource, out var have)) return have;
            using var s = typeof(Sounds).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("No " + resource);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var decoded = OggOpus(ms.ToArray());
            if (keep) Recordings[resource] = decoded;
            return decoded;
        }

        /// <summary>
        /// Decodes an Ogg Opus file (stereo, 48 kHz, as the asset build makes them):
        /// gathers the packets from the Ogg pages, skips the two header packets and
        /// Opus's pre-skip, and stops at the length the last page gives.
        /// </summary>
        private static (double[] L, double[] R) OggOpus(byte[] file)
        {
            var packets = new List<byte[]>();
            var current = new MemoryStream();
            long lastGranule = 0;
            int pos = 0;
            while (pos + 27 <= file.Length && file[pos] == 'O' && file[pos + 1] == 'g' && file[pos + 2] == 'g' && file[pos + 3] == 'S')
            {
                long granule = BitConverter.ToInt64(file, pos + 6);
                int segments = file[pos + 26];
                int data = pos + 27 + segments;
                for (int i = 0; i < segments; i++)
                {
                    int len = file[pos + 27 + i];
                    current.Write(file, data, len);
                    data += len;
                    if (len < 255) { packets.Add(current.ToArray()); current.SetLength(0); }
                }
                if (granule > 0) lastGranule = granule;
                pos = data;
            }
            if (packets.Count < 3) return (Array.Empty<double>(), Array.Empty<double>());
            int preSkip = BitConverter.ToUInt16(packets[0], 10);
            var decoder = OpusCodecFactory.CreateDecoder(Rate, 2);
            var pcm = new short[5760 * 2];
            var all = new List<short>();
            for (int i = 2; i < packets.Count; i++)
            {
                int frames = decoder.Decode(packets[i], pcm, 5760, false);
                for (int k = 0; k < frames * 2; k++) all.Add(pcm[k]);
            }
            int total = (int)Math.Min(all.Count / 2 - preSkip, lastGranule > preSkip ? lastGranule - preSkip : all.Count / 2);
            var l = new double[Math.Max(0, total)];
            var r = new double[l.Length];
            for (int i = 0; i < l.Length; i++) { l[i] = all[(preSkip + i) * 2] / 32768.0; r[i] = all[(preSkip + i) * 2 + 1] / 32768.0; }
            return (l, r);
        }

        // ---- Output ----

        private static byte[] Wav(double[] left, double[] right)
        {
            int length = left.Length;
            double peak = 1e-9;
            for (int i = 0; i < length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
            double gain = 0.40 / peak;
            int fade = Math.Min(length / 4, Rate / 12);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8); w.Write(36 + length * 4); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(length * 4);
            for (int i = 0; i < length; i++)
            {
                double f = i > length - fade ? (length - i) / (double)fade : 1;
                w.Write((short)Math.Clamp(Math.Round(left[i] * gain * f * 32767), -32768, 32767));
                w.Write((short)Math.Clamp(Math.Round(right[i] * gain * f * 32767), -32768, 32767));
            }
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>A little room in stereo (Freeverb: eight damped echoes and four smears each side).</summary>
        private static void Room(double[] left, double[] right, double wet)
        {
            int[] combs = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
            int[] passes = { 556, 441, 341, 225 };
            double scale = Rate / 44100.0;
            double[] Side(double[] input, int spread)
            {
                var output = new double[input.Length];
                foreach (int c in combs)
                {
                    var buf = new double[(int)((c + spread) * scale)];
                    double store = 0;
                    for (int i = 0, at = 0; i < input.Length; i++)
                    {
                        double y = buf[at];
                        store = y * 0.6 + store * 0.4;
                        buf[at] = input[i] * 0.015 + store * 0.8;
                        at = (at + 1) % buf.Length;
                        output[i] += y;
                    }
                }
                foreach (int p in passes)
                {
                    var buf = new double[(int)((p + spread) * scale)];
                    for (int i = 0, at = 0; i < output.Length; i++)
                    {
                        double b = buf[at];
                        double y = b - output[i];
                        buf[at] = output[i] + b * 0.5;
                        at = (at + 1) % buf.Length;
                        output[i] = y;
                    }
                }
                return output;
            }
            var wl = Side(left, 0);
            var wr = Side(right, 23);
            for (int i = 0; i < left.Length; i++)
            {
                left[i] = left[i] * (1 - wet * 0.5) + wl[i] * wet * 3;
                right[i] = right[i] * (1 - wet * 0.5) + wr[i] * wet * 3;
            }
        }
    }
}
