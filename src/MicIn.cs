using System;
using System.Collections.Generic;
using Concentus;

namespace TailRemote
{
    /// <summary>
    /// The controlling PC's microphone, on the host: decoded from Opus and played into a sound device whose
    /// recording side programs on the host (TeamTalk, calls) can use as their microphone.
    ///
    /// Which device: never the output Kova sends to the controlling PCs (the one it captures), or the
    /// controller would hear itself, and every program using it as a microphone would hear the whole PC
    /// too. VB-Cable (the "Kova" device) is one cable: whatever plays into it comes out of its recording
    /// side, "CABLE Output". So on a PC whose sound card is what Kova sends, the Kova cable is free and
    /// becomes the microphone. On a cloud PC the Kova cable is the PC's only output, which Kova sends, so the
    /// microphone needs a second virtual cable (VB-Audio's Hi-Fi Cable, or CABLE-A/B): found by itself.
    /// The person can also choose the device on the host (Settings there); the same rule applies to that.
    ///
    /// The buffer is the same Player as the controller's own sound: always heading back to live, never
    /// more than 40 ms, so speech is not delayed. One controller's microphone at a time.
    /// </summary>
    internal sealed class MicIn : IDisposable
    {
        private readonly Action<string> _log;
        private readonly Func<string?> _capturedId; // the output Kova sends right now (its chosen one, or Windows' default)
        private readonly object _gate = new();
        private Player? _player;
        private string? _playingOn, _playingName;
        private IOpusDecoder? _decoder;
        private readonly short[] _pcm = new short[5760 * 2];
        private uint _next;
        private bool _haveNext;
        private long _lastPacket;
        private int _cleanSeconds, _badSeconds;

        /// <summary>The device chosen on this PC for the microphone ("" or null: Kova picks one).</summary>
        public volatile string? Chosen;

        /// <summary>The step the microphone should be sent at (Protocol.OpusSteps); Tick changes it as it arrives.</summary>
        public int Step { get; private set; } = Protocol.MicBestStep;

        /// <summary>Test only: every decoded packet's first tick and length, instead of (as well as) playing it.</summary>
        public static Action<uint, int>? TestHeard;

        public MicIn(Func<string?> capturedId, Action<string> log)
        {
            _capturedId = capturedId;
            _log = log;
        }

        /// <summary>
        /// Where the microphone would play now: (device id, its name, null) or (null, null, why not, in words for the
        /// controlling PC's person). Looked at every second while it plays, so a change of default output is followed.
        /// </summary>
        public (string? Id, string? Name, string? Problem) Resolve()
        {
            var outputs = Wasapi.OutputDevices();
            // What Kova really captures: its chosen output, or Windows' default when none is chosen or the chosen one is
            // unplugged (the capture falls back to the default then, so that is what must be kept clear).
            string? captured = _capturedId() is string cap && outputs.Exists(d => d.Id == cap) ? cap : Wasapi.DefaultOutputId();
            string? chosen = Chosen;
            string capturedName = outputs.Find(d => d.Id == captured).Name ?? "";
            // One cable's inputs all come out of the same recording side (VB-Cable's 16-channel input and its
            // ordinary one both feed CABLE Output): any of them would carry what Kova sends, so each counts as it.
            bool Sends(string id, string name) => id == captured || SameCable(name, capturedName);
            if (!string.IsNullOrEmpty(chosen))
            {
                var c = outputs.Find(d => d.Id == chosen);
                if (c.Id == null) return (null, null, "Your microphone has nowhere to go: the sound device chosen for it on the remote PC is not connected.");
                if (Sends(c.Id, c.Name)) return (null, null, "Your microphone is not played on the remote PC: the device chosen for it there, " + c.Name + ", is also what it sends you, so you would hear yourself. Choose another there, in Settings, Microphone from the controlling PC plays into.");
                return (c.Id, c.Name, null);
            }
            // Kova's pick: a virtual cable that is not what Kova sends. The second cables first (made for exactly this),
            // then the Kova cable itself (free on a PC with its own sound card).
            string[] order = { "Hi-Fi Cable", "CABLE-A", "CABLE-B", "CABLE-C", "CABLE-D", "Kova", "CABLE", "VB-Audio", "Voicemeeter", "Virtual" };
            foreach (string want in order)
                foreach (var d in outputs)
                    if (!Sends(d.Id, d.Name) && d.Name.Contains(want, StringComparison.OrdinalIgnoreCase)) return (d.Id, d.Name, null);
            return (null, null, outputs.Exists(d => d.Id == captured) && outputs.Count == 1
                ? "Your microphone has nowhere to go on the remote PC: its only sound device is the one it sends you, so you would hear yourself. Install a second virtual cable there, such as VB-Audio's Hi-Fi Cable, and Kova uses it by itself."
                : "Your microphone has nowhere to go on the remote PC: it has no virtual cable apart from the one it sends you. Install one there, such as VB-Audio's Hi-Fi Cable, or choose a device there in Settings, Microphone from the controlling PC plays into.");
        }

        /// <summary>Two inputs of one virtual cable (the same driver, named in brackets): whatever plays into either comes out of the same side.</summary>
        private static bool SameCable(string a, string b)
        {
            static string Driver(string n) { int i = n.LastIndexOf('('); return i < 0 ? "" : n[i..]; }
            string da = Driver(a), db = Driver(b);
            return da.Length > 0 && da == db && da.Contains("Cable", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// One packet from the microphone's owner, already opened. tell: says something to that PC (where its
        /// microphone plays, or why it cannot). Called on the host's UDP thread only.
        /// </summary>
        public void Push(uint seq, int ticks, ReadOnlySpan<byte> opus, Action<string> tell)
        {
            lock (_gate)
            {
                _lastPacket = Environment.TickCount64;
                if (_player == null && !Open(tell)) return;
                int frames = 0;
                try
                {
                    if (_haveNext)
                    {
                        int d = (int)(seq - _next);
                        if (d < 0) { _player?.CountLate(ticks); return; } // late: never played out of turn
                        if (d > 400) { _decoder!.ResetState(); _haveNext = false; } // after a pause: start afresh from here
                        else if (d > 0 && d <= 24)
                        {
                            // Lost on the way: Opus fills the gap smoothly, so the voice does not click.
                            for (int left = d; left > 0;)
                            {
                                int t = Math.Min(left, 12);
                                int n = _decoder!.Decode(ReadOnlySpan<byte>.Empty, _pcm, t * Protocol.TickFrames, false);
                                if (n > 0) _player!.Push(seq - (uint)left, _pcm.AsSpan(0, n * 2), t, concealed: true);
                                left -= t;
                            }
                        }
                    }
                    frames = _decoder!.Decode(opus, _pcm, _pcm.Length / 2, false);
                }
                catch { frames = 0; } // damaged in a way the check could not see: skipped like a lost one, and the count moves on
                _next = seq + (uint)ticks;
                _haveNext = true;
                if (frames <= 0) return;
                TestHeard?.Invoke(seq, ticks);
                _player!.Push(seq, _pcm.AsSpan(0, frames * 2), ticks, arrivedAt: Player.Now);
            }
        }

        private bool Open(Action<string> tell)
        {
            if (Environment.TickCount64 - _refusedAt < 5000) return false; // said already; looked at again every few seconds
            // The once-a-second answer (Tick) when there is a fresh one: asking Windows here holds up the UDP thread.
            var (id, name, problem) = TestHeard != null ? (Player.NoDevice, "the test", null)
                : Environment.TickCount64 - _resolvedAt < 2000 ? _resolved : Resolve();
            if (id == null)
            {
                _refusedAt = Environment.TickCount64;
                if (problem != _refusedWhy) { _refusedWhy = problem; tell(problem!); _log("Microphone not played: " + problem); }
                return false;
            }
            _refusedWhy = null;
            _decoder = OpusCodecFactory.CreateDecoder(Protocol.AudioRate, 2);
            _haveNext = false;
            _player = new Player(id, msg => _log("Microphone: " + msg)) { RideOut = false };
            _playingOn = id;
            _playingName = name;
            _cleanSeconds = _badSeconds = 0;
            _log("Playing the controlling PC's microphone into " + name + ".");
            // Said once each time it is turned on: after a quiet spell (a microphone that sends nothing while silent)
            // the device is opened again without a word.
            if (_toldOn != id) { _toldOn = id; tell("Your microphone is on the remote PC now" + RecordingSide(name!)); }
            return true;
        }

        private string? _toldOn; // the device the owner was told its microphone plays on, until it is turned off
        private (string? Id, string? Name, string? Problem) _resolved; // Tick's last answer, for Open
        private long _resolvedAt = long.MinValue / 2;
        private long _refusedAt = long.MinValue / 2;
        private string? _refusedWhy;

        /// <summary>What programs there pick as their microphone: a cable's input comes out of its output.</summary>
        private static string RecordingSide(string playName)
        {
            if (playName == "the test") return ".";
            if (playName.Contains("Input", StringComparison.OrdinalIgnoreCase))
                return ": programs there can use " + playName.Replace("Input", "Output", StringComparison.OrdinalIgnoreCase) + " as their microphone.";
            if (playName.StartsWith("Kova", StringComparison.OrdinalIgnoreCase))
                return ": programs there can use CABLE Output as their microphone.";
            return ", playing into " + playName + ".";
        }

        /// <summary>
        /// Once a second (the host's log loop): lets go of the device 10 seconds after the last packet, follows a change
        /// of device, and works out the step from how the microphone is arriving. Returns the new step when it changed, else -1.
        /// tell: to the owner, or null when there is none.
        /// </summary>
        public int Tick(Action<string>? tell)
        {
            // Asked of Windows outside the lock: the UDP thread waits on the lock for every packet.
            (string? Id, string? Name, string? Problem) now = (null, null, null);
            bool playing;
            lock (_gate) playing = _player != null || Environment.TickCount64 - _lastPacket < 10_000;
            if (playing && TestHeard == null)
            {
                now = Resolve();
                lock (_gate) { _resolved = now; _resolvedAt = Environment.TickCount64; }
            }
            lock (_gate)
            {
                if (_player == null) return -1;
                if (Environment.TickCount64 - _lastPacket > 10_000) { Close(); return -1; } // 10 quiet seconds: the device is let go
                if (TestHeard == null)
                {
                    var (id, _, problem) = now;
                    if (id != _playingOn)
                    {
                        // The default output changed to the microphone's device (or it went): never played back to the controller.
                        _log("Microphone: its device changed; " + (problem ?? "reopening."));
                        Close();
                        if (problem != null) { tell?.Invoke(problem); _refusedWhy = problem; _refusedAt = Environment.TickCount64; }
                        return -1;
                    }
                }
                var (packets, lost, late) = _player.TakeStats();
                if (packets == 0) return -1;
                double loss = (double)(lost) / Math.Max(1, packets);
                int was = Step;
                // Down a step after two bad seconds running (a tenth of it missing), up one after ten clean ones:
                // the same idea as the sound the other way, kept simple for a voice.
                if (loss > 0.10) { _cleanSeconds = 0; if (++_badSeconds >= 2 && Step < Protocol.OpusSteps.Length - 1) { Step++; _badSeconds = 0; } }
                else if (loss < 0.02) { _badSeconds = 0; if (++_cleanSeconds >= 10 && Step > Protocol.MicBestStep) { Step--; _cleanSeconds = 0; } }
                else { _badSeconds = 0; _cleanSeconds = 0; }
                return Step != was ? Step : -1;
            }
        }

        /// <summary>The owner turned it off (or went): the device is let go at once.</summary>
        public void Stop()
        {
            lock (_gate)
            {
                Close();
                _toldOn = null;
                _refusedWhy = null;
                _refusedAt = long.MinValue / 2;
                Step = Protocol.MicBestStep; // the next owner starts at the best (Host tells it so)
            }
        }

        public bool Playing { get { lock (_gate) return _player != null; } }
        public string? PlayingName { get { lock (_gate) return _player != null ? _playingName : null; } }

        private void Close()
        {
            var p = _player;
            _player = null;
            _playingOn = null;
            _haveNext = false;
            if (p != null)
            {
                _log("Stopped playing the controlling PC's microphone.");
                // Stopping waits for the playback thread: never on the UDP thread.
                System.Threading.ThreadPool.QueueUserWorkItem(_ => p.Dispose());
            }
        }

        public void Dispose() => Stop();
    }
}
