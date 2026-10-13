using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Records what the host plays (WASAPI loopback on the default output),
    /// converts it to 16-bit 48 kHz stereo and hands out 5 ms ticks (240 frames).
    /// All-silent ticks are passed on as silence for a second, then stop, so a quiet PC costs nothing.
    /// </summary>
    internal sealed class LoopbackCapture : IDisposable
    {
        private readonly Action<uint, short[]?> _onPacket;
        private readonly Action<string> _status;
        private volatile bool _stop;
        private readonly Thread _thread;

        private readonly short[] _packet = new short[Protocol.TickFrames * 2];
        private int _fill;
        private readonly Action<float, float> _emit; // made once: a new one per packet was garbage every 5.8 ms
        private uint _seq;

        /// <summary>The next packet number. The host carries it to the next capture, because it is also the audio nonce and must never repeat.</summary>
        public uint NextSeq => _seq;

        private readonly string? _deviceId;

        /// <summary>Test only (--audiotest): silence inserted for timestamp gaps, and packet-count skips.</summary>
        public static int TestGapFills, TestGapFillMs, TestSeqSkips, TestChunkMax;

        /// <summary>The device being recorded, for the log.</summary>
        public static string DeviceInfo = "none yet";

        /// <summary>deviceId: the output to record, or null for Windows' default (followed when it changes). firstSeq: the first packet number.</summary>
        public LoopbackCapture(Action<uint, short[]?> onPacket, Action<string> status, string? deviceId, uint firstSeq)
        {
            _seq = firstSeq;
            _deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
            _onPacket = onPacket;
            _emit = Emit;
            _status = status;
            _thread = new Thread(Run) { IsBackground = true, Name = "Kova capture", Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        public void Dispose() => Stop();

        /// <summary>Stops and waits for the capture thread. False if it did not stop within 2 seconds.</summary>
        public bool Stop()
        {
            _stop = true;
            return _thread.Join(2000);
        }

        private int _failures;

        private int _resets;
        private long _resetAt;

        /// <summary>
        /// A device reset is reopened quietly, but not for ever: ten in a row within seconds of each other (a
        /// Bluetooth device half there) go the normal way, with a message and the usual wait.
        /// </summary>
        private bool QuickReset()
        {
            long now = Environment.TickCount64;
            if (now - _resetAt > 5000) _resets = 0;
            _resetAt = now;
            return ++_resets <= 10;
        }

        private void Run()
        {
            Native.ProAudioThread();
            string? lastError = null;
            while (!_stop)
            {
                try
                {
                    CaptureUntilDeviceChanges();
                    lastError = null;
                }
                catch (Exception e) when (Wasapi.DeviceReset(e) && !_stop && QuickReset())
                {
                    // Unplugged or changed: open whatever is the output now, quietly (the controlling PCs
                    // used to hear "Host audio is not available" for a pair of headphones).
                    DiagLog.Write("host capture: the device changed under us; reopening");
                    Thread.Sleep(100);
                }
                catch (Exception e)
                {
                    string msg = "Host audio is not available: " + e.Message +
                        " A cloud PC usually has no sound card; install a virtual audio device such as VB-Cable and make it the default output.";
                    DiagLog.Write("host capture error: " + e);
                    if (msg != lastError) _status(msg);
                    lastError = msg;
                    // Quickly at first (at boot, Windows' audio may be a moment from ready: every quarter
                    // second counts for hearing the startup sound), then every 2 seconds.
                    for (int i = 0; i < (++_failures < 40 ? 3 : 20) && !_stop; i++) Thread.Sleep(100);
                }
            }
        }

        private void CaptureUntilDeviceChanges()
        {
            // Opened again every few seconds while a device refuses (another program has it alone), for as
            // long as the host runs: whatever step fails, nothing may be left behind, so everything is let
            // go in the one finally below. The enumerator is made per open rather than kept for the
            // thread's life: after Windows' audio service restarts, an old one may no longer answer.
            Wasapi.IMMDeviceEnumerator? en = null;
            Wasapi.IMMDevice? dev = null;
            Wasapi.IAudioClient3? client = null;
            Wasapi.IAudioCaptureClient? cap = null;
            AutoResetEvent? ev = null;
            IntPtr fmtPtr = IntPtr.Zero;
            try
            {
                // The chosen output, or Windows' default when none is chosen or it has gone.
                en = Wasapi.Enumerator();
                dev = Wasapi.OutputDevice(en, _deviceId);
                dev.GetId(out string devId);
                bool followDefault = _deviceId == null || devId != _deviceId;
                client = Wasapi.Activate(dev);
                client.GetMixFormat(out fmtPtr);
                var fmt = Wasapi.ReadFormat(fmtPtr);
                var mix = Downmix.For(fmt.Channels, ChannelMask(fmtPtr));
                DeviceInfo = (Wasapi.FriendlyName(dev) ?? devId) + ", " + fmt.Rate + " Hz, " + fmt.Channels + " channels, " + fmt.Bits + "-bit" + (fmt.IsFloat ? " float" : "");
                DiagLog.Write("host capture: opened " + DeviceInfo + (followDefault ? " (Windows default)" : " (chosen device)"));
                client.Initialize(0, Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK | Wasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                    200_000, 0, fmtPtr, IntPtr.Zero);
                Marshal.FreeCoTaskMem(fmtPtr);
                fmtPtr = IntPtr.Zero; // freed: the finally must not free it again

                ev = new AutoResetEvent(false);
                client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                var iid = Wasapi.IID_IAudioCaptureClient;
                client.GetService(ref iid, out object o);
                cap = (Wasapi.IAudioCaptureClient)o;

                // Most devices already run at 48 kHz, Opus's own rate: then nothing is converted.
                var rs = fmt.Rate == Protocol.AudioRate ? null : new Resampler(fmt.Rate, Protocol.AudioRate);
                float[] stereo = new float[4096];
                client.Start();
                long lastDataAt = Environment.TickCount64, lastDeviceCheck = lastDataAt;
                ulong expectedPos = 0;  // the device's own sample counter where the next audio should start
                bool havePos = false;
                float[] silence = new float[2 * (fmt.Rate / 5)]; // as long as the biggest gap that is filled (200 ms)
                while (!_stop)
                {
                    ev.WaitOne(5); // loopback events are not guaranteed on every build; poll too
                    int drained = 0;
                    while (true)
                    {
                        int nhr = cap.GetNextPacketSize(out uint next);
                        if (nhr < 0) throw Marshal.GetExceptionForHR(nhr)!; // device reset: reopen
                        if (next == 0) break;
                        int hr = cap.GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out _);
                        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;

                        // Never trust timestamps for gaps: on some virtual devices (Virtual
                        // Audio Cable's Line 2 on Brock's PC) they step unevenly, and filling
                        // what looked like gaps added 30% of fake silence, which the other PC
                        // had to cut away again: choppy sound.
                        long since = Environment.TickCount64 - lastDataAt;
                        if (havePos && since > 200)
                        {
                            // A real pause (nothing played for 200 ms): finish the part-filled
                            // packet and move the count on, so the player starts fresh.
                            while (_fill > 0) Emit(0, 0);
                            _seq += (uint)(since / Protocol.TickMs);
                            Interlocked.Increment(ref TestSeqSkips);
                        }
                        else if (havePos && (flags & 1) != 0 && devPos > expectedPos && devPos - expectedPos < (ulong)(fmt.Rate / 5))
                        {
                            // Windows says sound was lost (DATA_DISCONTINUITY), and the device's own
                            // sample counter says how much: put exactly that much silence back.
                            int n = Math.Min((int)(devPos - expectedPos), silence.Length / 2);
                            Interlocked.Increment(ref TestGapFills);
                            Interlocked.Add(ref TestGapFillMs, n * 1000 / fmt.Rate);
                            Convert(rs, silence.AsSpan(0, n * 2));
                        }
                        expectedPos = devPos + frames;
                        havePos = true;
                        if (stereo.Length < frames * 2) stereo = new float[frames * 4];
                        ToStereo(data, (int)frames, fmt, (flags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) != 0, stereo, mix);
                        cap.ReleaseBuffer(frames);
                        Convert(rs, stereo.AsSpan(0, (int)frames * 2));
                        lastDataAt = Environment.TickCount64;
                        drained += (int)frames;
                    }

                    long now = Environment.TickCount64;
                    if (drained > 0)
                    {
                        int ms = drained * 1000 / fmt.Rate;
                        if (ms > TestChunkMax) TestChunkMax = ms;
                    }
                    // The tail of a sound: after a real stop, send the part-filled packet
                    // rather than hold it. Not sooner: late audio is not the end of a sound.
                    if (_fill > 0 && now - lastDataAt > 100)
                    {
                        while (_fill > 0) Emit(0, 0);
                    }
                    if (now - lastDeviceCheck > 1000)
                    {
                        lastDeviceCheck = now;
                        // Reopen when the default output changes (if following it), or when
                        // the chosen output that was missing comes back.
                        var want = Wasapi.OutputDevice(en, _deviceId);
                        string curId;
                        try { want.GetId(out curId); }
                        finally { Wasapi.Release(want); }
                        if (curId != devId && (followDefault || curId == _deviceId)) return;
                    }
                }
            }
            finally
            {
                if (client != null) try { client.Stop(); } catch { }
                // Let go now, before the event it signals is closed: a closed handle's number can be reused.
                Wasapi.Release(cap);
                Wasapi.Release(client);
                Wasapi.Release(dev);
                Wasapi.Release(en);
                if (fmtPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(fmtPtr);
                ev?.Dispose();
            }
        }

        private void Convert(Resampler? rs, Span<float> stereo)
        {
            if (rs != null) { rs.Process(stereo, _emit); return; }
            for (int i = 0; i + 1 < stereo.Length; i += 2) Emit(stereo[i], stereo[i + 1]);
        }

        private void Emit(float l, float r)
        {
            _packet[_fill++] = ToShort(l);
            _packet[_fill++] = ToShort(r);
            if (_fill < _packet.Length) return;
            _fill = 0;
            uint seq = _seq++;
            foreach (short s in _packet)
            {
                if (s != 0) { _silentRun = 0; _onPacket(seq, _packet); return; }
            }
            // Silence: passed on for the first second, so the sound fades out
            // properly; after that, nothing at all.
            // Counted only up to the limit: a program holding a silent stream open for months ran the
            // count past what an int holds, and it went negative and sent silence again for months.
            if (_silentRun < 200) { _silentRun++; _onPacket(seq, null); }
        }

        private int _silentRun;

        private static short ToShort(float f)
        {
            float v = f * 32767f;
            if (v > 32767f) return 32767;
            if (v < -32768f) return -32768;
            return (short)MathF.Round(v);
        }

        /// <summary>Which speakers the device's channels are for (WAVEFORMATEXTENSIBLE's dwChannelMask), or 0 when the format does not say.</summary>
        private static uint ChannelMask(IntPtr wfx)
        {
            if ((ushort)Marshal.ReadInt16(wfx, 0) != 0xFFFE || Marshal.ReadInt16(wfx, 16) < 22) return 0;
            return (uint)Marshal.ReadInt32(wfx, 20);
        }

        /// <summary>
        /// How a device with more than two channels (5.1, 7.1, four speakers) is heard in stereo: every
        /// channel goes to the left, the right or both, as a stereo downmix does (centre and surround a
        /// little quieter), so voices in the centre and sound from behind are not lost. The low-frequency
        /// channel is left out, as usual. Null for mono and stereo, which are taken as they are.
        /// </summary>
        internal sealed class Downmix
        {
            public readonly float[] Left, Right;
            private Downmix(float[] l, float[] r) { Left = l; Right = r; }

            private const float Half = 0.7071f; // -3 dB

            public static Downmix? For(int channels, uint mask)
            {
                if (channels <= 2) return null;
                // The mask names each channel's speaker, lowest bit first; one that does not (or has too few
                // speakers) gets the usual layout for that many channels.
                if (mask == 0 || System.Numerics.BitOperations.PopCount(mask) < channels)
                    mask = channels switch
                    {
                        3 => 0x7,     // front left, right, centre
                        4 => 0x33,    // front left, right, back left, right
                        5 => 0x37,    // and centre
                        6 => 0x3F,    // 5.1
                        7 => 0x70F,   // 6.1: back centre, side left, right
                        _ => 0x63F,   // 7.1, and the first eight of anything bigger
                    };
                var l = new float[channels];
                var r = new float[channels];
                int c = 0;
                for (int bit = 0; bit < 32 && c < channels; bit++)
                {
                    uint speaker = 1u << bit;
                    if ((mask & speaker) == 0) continue;
                    switch (speaker)
                    {
                        case 0x1: case 0x40: l[c] = 1; break;                       // front left, front left of centre
                        case 0x2: case 0x80: r[c] = 1; break;                       // front right, front right of centre
                        case 0x4: l[c] = r[c] = Half; break;                        // centre
                        case 0x8: break;                                            // low frequencies: left out
                        case 0x10: case 0x200: case 0x1000: case 0x8000: l[c] = Half; break; // back, side, top front, top back left
                        case 0x20: case 0x400: case 0x4000: case 0x20000: r[c] = Half; break; // and right
                        default: l[c] = r[c] = 0.5f; break;                         // back centre, the tops' centres
                    }
                    c++;
                }
                return new Downmix(l, r);
            }

            /// <summary>
            /// All the channels added up can go past full scale where plain stereo would not: the loudest
            /// part is bent down smoothly rather than clipped, and anything below 0.9 is left exactly as it is.
            /// </summary>
            public static float Limit(float x)
            {
                float a = MathF.Abs(x);
                if (a <= 0.9f) return x;
                return MathF.CopySign(0.9f + 0.1f * MathF.Tanh((a - 0.9f) / 0.1f), x);
            }
        }

        private static unsafe void ToStereo(IntPtr data, int frames, Wasapi.Format fmt, bool silent, float[] dst, Downmix? mix)
        {
            if (silent) { Array.Clear(dst, 0, frames * 2); return; }
            int ch = fmt.Channels;
            if (mix != null) { DownmixToStereo(data, frames, fmt, dst, mix); return; }
            for (int i = 0; i < frames; i++)
            {
                float l, r;
                if (fmt.IsFloat && fmt.Bits == 32)
                {
                    float* p = (float*)data + i * ch;
                    l = p[0]; r = ch > 1 ? p[1] : l;
                }
                else if (fmt.Bits == 16)
                {
                    short* p = (short*)data + i * ch;
                    l = p[0] / 32768f; r = ch > 1 ? p[1] / 32768f : l;
                }
                else if (fmt.Bits == 32)
                {
                    int* p = (int*)data + i * ch;
                    l = p[0] / 2147483648f; r = ch > 1 ? p[1] / 2147483648f : l;
                }
                else if (fmt.Bits == 24)
                {
                    byte* p = (byte*)data + i * ch * 3;
                    l = Read24(p); r = ch > 1 ? Read24(p + 3) : l;
                }
                else throw new NotSupportedException("Unsupported audio format: " + fmt.Bits + " bit.");
                dst[i * 2] = l;
                dst[i * 2 + 1] = r;
            }
        }

        // A packed 24-bit sample (three bytes, lowest first), -1 to 1.
        private static unsafe float Read24(byte* p) => (p[0] | (p[1] << 8) | ((sbyte)p[2] << 16)) / 8388608f;

        private static unsafe void DownmixToStereo(IntPtr data, int frames, Wasapi.Format fmt, float[] dst, Downmix mix)
        {
            int ch = fmt.Channels;
            float[] gl = mix.Left, gr = mix.Right;
            bool isFloat = fmt.IsFloat && fmt.Bits == 32;
            if (!isFloat && fmt.Bits != 16 && fmt.Bits != 24 && fmt.Bits != 32) throw new NotSupportedException("Unsupported audio format: " + fmt.Bits + " bit.");
            for (int i = 0; i < frames; i++)
            {
                float l = 0, r = 0;
                for (int c = 0; c < ch; c++)
                {
                    int k = i * ch + c;
                    float s = isFloat ? ((float*)data)[k]
                        : fmt.Bits == 16 ? ((short*)data)[k] / 32768f
                        : fmt.Bits == 32 ? ((int*)data)[k] / 2147483648f
                        : Read24((byte*)data + k * 3);
                    l += s * gl[c];
                    r += s * gr[c];
                }
                dst[i * 2] = Downmix.Limit(l);
                dst[i * 2 + 1] = Downmix.Limit(r);
            }
        }
    }
}
