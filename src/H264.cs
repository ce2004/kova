using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TailRemote
{
    /// <summary>
    /// H.264 through Media Foundation (in Windows, no packages): the graphics chip's encoder when the
    /// PC has one, Windows' own software encoder when it does not, or when the chip's will not work
    /// (three failures in a row, then left alone for 10 minutes). Low latency: every picture in gives a picture out, no reordering.
    /// The bitrate can change while it runs, and a whole picture (a key frame) can be asked for.
    /// Pictures go in as BGRA (Frame) and come out as an Annex B byte stream.
    /// </summary>
    internal sealed unsafe class H264Encoder : IDisposable
    {
        private IntPtr _mft, _codec, _events;
        private readonly int _w, _h, _fps;
        private int _kbps;
        private bool _async, _started;
        private readonly SemaphoreSlim _needInput = new(0), _haveOutput = new(0);
        private Thread? _pump;
        private volatile bool _closing;
        private readonly long _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        private byte[]? _nv12;
        private static int _hardwareFailures;
        private static long _hardwareOffUntil;
        private static bool HardwareOff => Environment.TickCount64 < Interlocked.Read(ref _hardwareOffUntil);

        public bool Hardware { get; private set; }
        public int Width => _w;
        public int Height => _h;
        public int Kbps => _kbps;

        private H264Encoder(int w, int h, int fps, int kbps) { _w = w; _h = h; _fps = fps; _kbps = kbps; }

        /// <summary>An encoder for pictures of this size (rounded down to even), or null if this PC has none that works.</summary>
        public static H264Encoder? Create(int width, int height, int fps, int kbps) => Create(width, height, fps, kbps, hardware: true);

        /// <summary>Windows' own software encoder only: the graphics chip's has just failed.</summary>
        public static H264Encoder? CreateSoftware(int width, int height, int fps, int kbps) => Create(width, height, fps, kbps, hardware: false);

        private static H264Encoder? Create(int width, int height, int fps, int kbps, bool hardware)
        {
            // Windows N and KN come without Media Foundation: no encoder, so still pictures carry everything.
            if (!Mf.Start()) return null;
            int w = width & ~1, h = height & ~1;
            if (w < 16 || h < 16) return null;
            if (hardware && !HardwareOff)
            {
                var hw = new H264Encoder(w, h, fps, kbps);
                if (hw.Open(hardware: true)) return hw;
                hw.Dispose();
            }
            var sw = new H264Encoder(w, h, fps, kbps);
            if (sw.Open(hardware: false)) return sw;
            sw.Dispose();
            return null;
        }

        private bool Open(bool hardware)
        {
            try
            {
                _mft = Mf.FindTransform(Mf.VideoEncoder, hardware ? Mf.EnumHardware : Mf.EnumSync, Mf.NV12, Mf.H264);
                if (_mft == IntPtr.Zero) return false;
                Hardware = hardware;
                IntPtr attrs;
                if (Mf.Call(_mft, 8, (IntPtr)(&attrs)) >= 0 && attrs != IntPtr.Zero) // GetAttributes
                {
                    uint isAsync = 0;
                    Guid g = Mf.MF_TRANSFORM_ASYNC;
                    if (Mf.Call(attrs, 7, (IntPtr)(&g), (IntPtr)(&isAsync)) >= 0 && isAsync != 0)
                    {
                        Guid unlock = Mf.MF_TRANSFORM_ASYNC_UNLOCK;
                        Mf.Call(attrs, 21, (IntPtr)(&unlock), 1u);
                        _async = true;
                    }
                    Guid low = Mf.MF_LOW_LATENCY;
                    Mf.Call(attrs, 21, (IntPtr)(&low), 1u);
                    Marshal.Release(attrs);
                }
                Guid iidCodec = Mf.IID_ICodecAPI;
                if (Marshal.QueryInterface(_mft, ref iidCodec, out IntPtr codec) >= 0) _codec = codec;

                // Output first (H.264), then input (NV12), as encoders require.
                IntPtr outType = Mf.NewType(Mf.H264, _w, _h, _fps);
                Mf.SetUInt32(outType, Mf.MF_MT_AVG_BITRATE, (uint)(_kbps * 1000));
                Mf.SetUInt32(outType, Mf.MF_MT_MPEG2_PROFILE, 77); // Main
                int hr = Mf.Call(_mft, 16, 0, outType, 0); // SetOutputType
                Marshal.Release(outType);
                if (hr < 0) return false;
                IntPtr inType = Mf.NewType(Mf.NV12, _w, _h, _fps);
                hr = Mf.Call(_mft, 15, 0, inType, 0); // SetInputType
                Marshal.Release(inType);
                if (hr < 0) return false;
                // After the formats: encoders take these only once they know what they encode.
                SetCodec(Mf.CODECAPI_AVLowLatencyMode, 11 /* VT_BOOL */, -1);
                SetCodec(Mf.CODECAPI_AVEncCommonRateControlMode, 19 /* VT_UI4 */, 0); // constant bitrate: steady on the line
                SetCodec(Mf.CODECAPI_AVEncCommonMeanBitRate, 19, _kbps * 1000);
                SetCodec(Mf.CODECAPI_AVEncMPVGOPSize, 19, _fps * 4); // a key frame every 4 seconds at most, besides those asked for
                if (_async)
                {
                    Guid iidEvents = Mf.IID_IMFMediaEventGenerator;
                    if (Marshal.QueryInterface(_mft, ref iidEvents, out IntPtr ev) < 0) return false;
                    _events = ev;
                    // The chip's encoder says when it wants a picture and when it has one: a thread of its
                    // own waits on that and counts both, so no word from it is ever missed.
                    _pump = new Thread(Pump) { IsBackground = true, Name = "Kova H.264 events" };
                    _pump.Start();
                }
                Mf.Call(_mft, 23, 0x10000000u, IntPtr.Zero); // BEGIN_STREAMING
                Mf.Call(_mft, 23, 0x10000003u, IntPtr.Zero); // START_OF_STREAM
                _started = true;
                return true;
            }
            catch { return false; }
        }

        private void SetCodec(Guid what, ushort vt, long value)
        {
            if (_codec == IntPtr.Zero) return;
            byte* v = stackalloc byte[24];
            new Span<byte>(v, 24).Clear();
            *(ushort*)v = vt;
            if (vt == 11) *(short*)(v + 8) = (short)value; else *(uint*)(v + 8) = (uint)value;
            Guid g = what;
            Mf.Call(_codec, 9, (IntPtr)(&g), (IntPtr)v); // SetValue
        }

        /// <summary>A new bitrate, at once (no new key frame).</summary>
        public void SetBitrate(int kbps)
        {
            if (kbps == _kbps) return;
            _kbps = kbps;
            SetCodec(Mf.CODECAPI_AVEncCommonMeanBitRate, 19, kbps * 1000L);
        }

        /// <summary>
        /// Encodes one picture (of this encoder's size; a bigger one is cut to it). Returns the bytes for
        /// it (an empty array while the encoder is still filling up), or null if the encoder failed:
        /// throw it away. key: make this one a key frame (a viewer starting, or one that lost pictures).
        /// </summary>
        public byte[]? Encode(Frame f, bool key, out bool isKey)
        {
            isKey = false;
            if (!_started) return null;
            try
            {
                int ySize = _w * _h;
                _nv12 ??= new byte[ySize * 3 / 2];
                Yuv.BgraToNv12(f, _w, _h, _nv12);
                if (key) SetCodec(Mf.CODECAPI_AVEncVideoForceKeyFrame, 19, 1);
                IntPtr sample = Mf.SampleFrom(_nv12, _nv12.Length);
                // Its real time: frames come only when the screen changes, and the bitrate is kept per second.
                // In 100 ns units, as Media Foundation counts; worked out by .NET, because multiplying the raw
                // count by ten million first ran past what a long holds after about 25 hours of motion.
                long t = System.Diagnostics.Stopwatch.GetElapsedTime(_t0).Ticks;
                Mf.Call(sample, 36, t); // SetSampleTime
                Mf.Call(sample, 38, 10_000_000L / _fps); // SetSampleDuration
                try
                {
                    if (_async)
                    {
                        // Pictures that came out after the last call stopped waiting for them are taken first:
                        // left there, a chip's encoder asks for no more input until they are, which looked
                        // like a broken encoder.
                        byte[]? got = null;
                        bool any = false;
                        while (_haveOutput.Wait(0))
                        {
                            byte[]? o = Output(out bool k);
                            if (o == null) return null;
                            if (k) isKey = true;
                            got = got == null ? o : Concat(got, o);
                            any = true;
                        }
                        if (!_needInput.Wait(500)) return null; // it never asked for a picture: not working
                        if (Mf.Call(_mft, 24, 0, sample, 0) < 0) return null; // ProcessInput
                        // Whatever it has ready: the first a moment after the picture went in, the rest at once.
                        for (int wait = 30; _haveOutput.Wait(wait); wait = 0)
                        {
                            byte[]? o = Output(out bool k);
                            if (o == null) return null;
                            if (k) isKey = true;
                            got = got == null ? o : Concat(got, o);
                            any = true;
                        }
                        return any ? got! : Array.Empty<byte>();
                    }
                    int hr = Mf.Call(_mft, 24, 0, sample, 0); // ProcessInput
                    if (hr == unchecked((int)0xC00D36B5)) { Output(out _); hr = Mf.Call(_mft, 24, 0, sample, 0); } // not accepting: take what it has first
                    if (hr < 0) return null;
                    return Output(out isKey); // null: it failed (still pictures from here), not "still filling up"
                }
                finally { Marshal.Release(sample); }
            }
            catch { return null; }
        }

        /// <summary>
        /// The asynchronous encoder's events, looked for here and counted, until it is closed. Never waits
        /// inside Media Foundation: a wait there only ends when the encoder shuts down, and one that would
        /// not (seen on some drivers) left this thread and the whole encoder behind every time things
        /// stopped moving. Instead it asks without waiting and, with nothing there, looks again a
        /// millisecond later (the process asks Windows for 1 ms timers), so events are still taken at once.
        /// </summary>
        private void Pump()
        {
            bool com = Mf.StartThread();
            try
            {
                while (!_closing)
                {
                    IntPtr ev;
                    int hr = Mf.Call(_events, 3, 1u, (IntPtr)(&ev)); // GetEvent, MF_EVENT_FLAG_NO_WAIT
                    if (hr == unchecked((int)0xC00D3E80)) { Thread.Sleep(1); continue; } // MF_E_NO_EVENTS_AVAILABLE
                    if (hr < 0 || ev == IntPtr.Zero) break; // shut down, or broken
                    uint type = 0;
                    Mf.Call(ev, 33, (IntPtr)(&type)); // GetType
                    Marshal.Release(ev);
                    if (type == 601) _needInput.Release(); // METransformNeedInput
                    else if (type == 602) _haveOutput.Release(); // METransformHaveOutput
                }
            }
            catch { }
            finally { if (com) Mf.EndThread(); }
        }

        private static int _toldStuck;

        private byte[]? Output(out bool isKey)
        {
            isKey = false;
            var info = Info();
            bool provides = (info.dwFlags & 0x300) != 0;
            byte[]? result = null;
            for (int guard = 0; guard < 16; guard++)
            {
                IntPtr own = IntPtr.Zero;
                if (!provides) own = Mf.EmptySample(Math.Max((int)info.cbSize, _w * _h * 3 / 2));
                var ob = new MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0, pSample = own };
                uint status;
                int hr = Mf.Call(_mft, 25, 0, 1, (IntPtr)(&ob), (IntPtr)(&status)); // ProcessOutput
                if (ob.pEvents != IntPtr.Zero) Marshal.Release(ob.pEvents);
                try
                {
                    // Only "wants more input" means still filling up (empty, not null); any other failure is a
                    // broken encoder (null), so the caller goes to still pictures instead of waiting for ever.
                    if (hr == unchecked((int)0xC00D6D72)) return result ?? Array.Empty<byte>(); // MF_E_TRANSFORM_NEED_MORE_INPUT
                    if (hr == unchecked((int)0xC00D6D61)) // MF_E_TRANSFORM_STREAM_CHANGE: chip encoders often say so at first
                    {
                        IntPtr t;
                        if (Mf.Call(_mft, 14, 0, 0u, (IntPtr)(&t)) < 0) return null; // GetOutputAvailableType
                        try { if (Mf.Call(_mft, 16, 0, t, 0) < 0) return null; } finally { Marshal.Release(t); } // SetOutputType
                        provides = (Info().dwFlags & 0x300) != 0;
                        continue;
                    }
                    if (hr < 0) return null;
                    if (ob.pSample == IntPtr.Zero) { if (_async) return result ?? Array.Empty<byte>(); continue; }
                    byte[] bytes = Mf.SampleBytes(ob.pSample);
                    uint clean = 0;
                    Guid cp = Mf.MFSampleExtension_CleanPoint;
                    if (Mf.Call(ob.pSample, 7, (IntPtr)(&cp), (IntPtr)(&clean)) >= 0 && clean != 0) isKey = true;
                    result = result == null ? bytes : Concat(result, bytes);
                    if (_async) return result; // one picture per event
                }
                finally { if (ob.pSample != IntPtr.Zero) Marshal.Release(ob.pSample); }
            }
            return result ?? Array.Empty<byte>();
        }

        private MFT_OUTPUT_STREAM_INFO Info()
        {
            MFT_OUTPUT_STREAM_INFO info;
            Mf.Call(_mft, 7, 0, (IntPtr)(&info)); // GetOutputStreamInfo
            return info;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var c = new byte[a.Length + b.Length];
            a.CopyTo(c, 0); b.CopyTo(c, a.Length);
            return c;
        }

        /// <summary>
        /// The chip's encoder misbehaved. One hiccup (a driver busy for a moment) is forgiven; three in a
        /// row and the software one is used for the next 10 minutes, then the chip's is tried again.
        /// </summary>
        public static void HardwareFailed()
        {
            if (Interlocked.Increment(ref _hardwareFailures) < 3) return;
            Interlocked.Exchange(ref _hardwareFailures, 0);
            Interlocked.Exchange(ref _hardwareOffUntil, Environment.TickCount64 + 10 * 60_000);
        }

        /// <summary>The chip's encoder gave a picture: earlier hiccups no longer count against it.</summary>
        public static void HardwareWorked() { if (Volatile.Read(ref _hardwareFailures) != 0) Interlocked.Exchange(ref _hardwareFailures, 0); }

        public void Dispose()
        {
            // The pump ends first: it never waits inside Media Foundation, so it sees _closing within a
            // millisecond or two, and nothing below happens under it.
            _closing = true;
            bool pumpEnded = _pump == null || _pump.Join(2000);
            if (!pumpEnded && Interlocked.Exchange(ref _toldStuck, 1) == 0)
                DiagLog.Write("video: an H.264 encoder's event thread did not end; that encoder is left as it is");
            if (_mft != IntPtr.Zero)
            {
                try { Mf.Call(_mft, 23, 0x10000001u, IntPtr.Zero); } catch { } // END_STREAMING
                // Shut down for real: an asynchronous encoder otherwise keeps its session on the graphics
                // chip alive (its event queue holds it), and chips allow only a few at a time.
                Guid iid = Mf.IID_IMFShutdown;
                if (Marshal.QueryInterface(_mft, ref iid, out IntPtr shut) >= 0) { try { Mf.Call(shut, 3); } catch { } Marshal.Release(shut); } // Shutdown
            }
            // A pump still running (it should never be) may be using the queue: freeing it under it would
            // crash the process, so it is left (a small leak) rather than released.
            if (_events != IntPtr.Zero && pumpEnded) { Marshal.Release(_events); _events = IntPtr.Zero; }
            if (_codec != IntPtr.Zero) { Marshal.Release(_codec); _codec = IntPtr.Zero; }
            if (_mft != IntPtr.Zero) { Marshal.Release(_mft); _mft = IntPtr.Zero; }
            _started = false;
        }

        [StructLayout(LayoutKind.Sequential)] internal struct MFT_OUTPUT_STREAM_INFO { public uint dwFlags, cbSize, cbAlignment; }
        [StructLayout(LayoutKind.Sequential)] internal struct MFT_OUTPUT_DATA_BUFFER { public uint dwStreamID; public IntPtr pSample; public uint dwStatus; public IntPtr pEvents; }
    }

    /// <summary>
    /// Turns the H.264 stream back into pictures: Windows' own decoder (software, everywhere), low
    /// latency so each picture comes out as soon as it is in.
    /// </summary>
    internal sealed unsafe class H264Decoder : IDisposable
    {
        private IntPtr _mft;
        private int _w, _h, _stride, _rows;
        private bool _typed;

        public static H264Decoder? Create()
        {
            if (!Mf.Start()) return null; // no Media Foundation (Windows N): the host is told to send still pictures
            var d = new H264Decoder();
            try
            {
                d._mft = Mf.FindTransform(Mf.VideoDecoder, Mf.EnumSync, Mf.H264, Mf.NV12);
                if (d._mft == IntPtr.Zero) { d.Dispose(); return null; }
                IntPtr attrs;
                if (Mf.Call(d._mft, 8, (IntPtr)(&attrs)) >= 0 && attrs != IntPtr.Zero)
                {
                    Guid low = Mf.MF_LOW_LATENCY;
                    Mf.Call(attrs, 21, (IntPtr)(&low), 1u);
                    Marshal.Release(attrs);
                }
                IntPtr inType = Mf.NewType(Mf.H264, 0, 0, 0);
                int hr = Mf.Call(d._mft, 15, 0, inType, 0); // SetInputType
                Marshal.Release(inType);
                if (hr < 0 || !d.TypeOutput()) { d.Dispose(); return null; }
                Mf.Call(d._mft, 23, 0x10000000u, IntPtr.Zero); // BEGIN_STREAMING
                Mf.Call(d._mft, 23, 0x10000003u, IntPtr.Zero); // START_OF_STREAM
                return d;
            }
            catch { d.Dispose(); return null; }
        }

        /// <summary>Picks NV12 out of what the decoder offers, and learns the picture's size and row length.</summary>
        private bool TypeOutput()
        {
            for (uint i = 0; ; i++)
            {
                IntPtr t;
                if (Mf.Call(_mft, 14, 0, i, (IntPtr)(&t)) < 0) return false; // GetOutputAvailableType
                try
                {
                    Guid sub;
                    Guid key = Mf.MF_MT_SUBTYPE;
                    if (Mf.Call(t, 10, (IntPtr)(&key), (IntPtr)(&sub)) < 0 || sub != Mf.NV12) continue;
                    if (Mf.Call(_mft, 16, 0, t, 0) < 0) return false; // SetOutputType
                    ulong size = 0;
                    Guid fs = Mf.MF_MT_FRAME_SIZE;
                    Mf.Call(t, 8, (IntPtr)(&fs), (IntPtr)(&size));
                    _w = (int)(size >> 32); _h = (int)(size & 0xFFFFFFFF);
                    uint stride = 0;
                    Guid ds = Mf.MF_MT_DEFAULT_STRIDE;
                    _stride = Mf.Call(t, 7, (IntPtr)(&ds), (IntPtr)(&stride)) >= 0 && (int)stride > 0 ? (int)stride : _w;
                    _rows = _h;
                    _typed = true;
                    return true;
                }
                finally { Marshal.Release(t); }
            }
        }

        /// <summary>
        /// Decodes one picture's bytes into the BGRA canvas (its size: width by height, the size the
        /// sender gave). False if the decoder could not: ask for a key frame.
        /// </summary>
        public bool Decode(byte[] data, int offset, int length, int width, int height, byte[] bgra, out bool got)
        {
            got = false;
            if (_mft == IntPtr.Zero) return false;
            IntPtr sample = Mf.SampleFrom(data, length, offset);
            try
            {
                int hr = Mf.Call(_mft, 24, 0, sample, 0); // ProcessInput
                if (hr == unchecked((int)0xC00D36B5)) // MF_E_NOTACCEPTING: take what it has, then this one goes in
                {
                    if (!Drain(width, height, bgra, ref got)) return false;
                    hr = Mf.Call(_mft, 24, 0, sample, 0);
                }
                if (hr < 0) return false;
            }
            finally { Marshal.Release(sample); }
            return Drain(width, height, bgra, ref got);
        }

        private bool Drain(int width, int height, byte[] bgra, ref bool got)
        {
            for (int guard = 0; guard < 8; guard++)
            {
                H264Encoder.MFT_OUTPUT_STREAM_INFO info;
                Mf.Call(_mft, 7, 0, (IntPtr)(&info)); // GetOutputStreamInfo
                bool provides = (info.dwFlags & 0x300) != 0;
                IntPtr own = provides ? IntPtr.Zero : Mf.EmptySample(Math.Max((int)info.cbSize, Math.Max(_stride, 16) * Math.Max(_rows, 16) * 3 / 2));
                var ob = new H264Encoder.MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0, pSample = own };
                uint status;
                int hr = Mf.Call(_mft, 25, 0, 1, (IntPtr)(&ob), (IntPtr)(&status)); // ProcessOutput
                if (ob.pEvents != IntPtr.Zero) Marshal.Release(ob.pEvents);
                try
                {
                    if (hr == unchecked((int)0xC00D6D72)) return true; // need more input: nothing more for now
                    if (hr == unchecked((int)0xC00D6D61)) { if (!TypeOutput()) return false; continue; } // the stream's format changed
                    if (hr < 0) return false;
                    if (!_typed || ob.pSample == IntPtr.Zero) continue;
                    Mf.ReadNv12(ob.pSample, _stride, _rows, (nv12, stride, rows) => Yuv.Nv12ToBgra(nv12, stride, rows, Math.Min(width, _w), Math.Min(height, _h), width, bgra));
                    got = true;
                }
                finally { if (ob.pSample != IntPtr.Zero) Marshal.Release(ob.pSample); }
            }
            return true;
        }

        public void Dispose()
        {
            if (_mft != IntPtr.Zero) { Marshal.Release(_mft); _mft = IntPtr.Zero; }
        }
    }

    /// <summary>
    /// Colours between the screen's BGRA and the H.264 encoder's NV12 (full range, BT.601), the same
    /// sums both ways so what goes in comes out. Rows in parallel: a big screen sixty times a second.
    /// </summary>
    internal static unsafe class Yuv
    {
        public static void BgraToNv12(Frame f, int w, int h, byte[] nv12)
        {
            int srcW = f.Width;
            byte[] px = f.Pixels;
            Parallel.For(0, h / 2, pair =>
            {
                fixed (byte* src = px, dst = nv12)
                {
                    int y0 = pair * 2;
                    byte* uv = dst + w * h + pair * w;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        int y = y0 + dy;
                        byte* s = src + (long)Math.Min(y, f.Height - 1) * srcW * 4;
                        byte* yRow = dst + (long)y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int b = s[x * 4], g = s[x * 4 + 1], r = s[x * 4 + 2];
                            yRow[x] = (byte)((77 * r + 150 * g + 29 * b + 128) >> 8);
                        }
                    }
                    byte* s0 = src + (long)Math.Min(y0, f.Height - 1) * srcW * 4, s1 = src + (long)Math.Min(y0 + 1, f.Height - 1) * srcW * 4;
                    for (int x = 0; x < w; x += 2)
                    {
                        int b = s0[x * 4] + s0[x * 4 + 4] + s1[x * 4] + s1[x * 4 + 4];
                        int g = s0[x * 4 + 1] + s0[x * 4 + 5] + s1[x * 4 + 1] + s1[x * 4 + 5];
                        int r = s0[x * 4 + 2] + s0[x * 4 + 6] + s1[x * 4 + 2] + s1[x * 4 + 6];
                        uv[x] = (byte)Math.Clamp(((-43 * r - 85 * g + 128 * b) >> 10) + 128, 0, 255);
                        uv[x + 1] = (byte)Math.Clamp(((128 * r - 107 * g - 21 * b) >> 10) + 128, 0, 255);
                    }
                }
            });
        }

        public static void Nv12ToBgra(IntPtr nv12, int stride, int rows, int w, int h, int outW, byte[] bgra)
        {
            Parallel.For(0, h, y =>
            {
                byte* yRow = (byte*)nv12 + (long)y * stride;
                byte* uv = (byte*)nv12 + (long)stride * rows + (long)(y / 2) * stride;
                fixed (byte* dst0 = bgra)
                {
                    byte* d = dst0 + (long)y * outW * 4;
                    for (int x = 0; x < w; x++)
                    {
                        int Y = yRow[x], U = uv[x & ~1] - 128, V = uv[(x & ~1) + 1] - 128;
                        int r = Y + ((359 * V) >> 8), g = Y - ((88 * U + 183 * V) >> 8), b = Y + ((454 * U) >> 8);
                        d[x * 4] = (byte)Math.Clamp(b, 0, 255);
                        d[x * 4 + 1] = (byte)Math.Clamp(g, 0, 255);
                        d[x * 4 + 2] = (byte)Math.Clamp(r, 0, 255);
                        d[x * 4 + 3] = 255;
                    }
                }
            });
        }
    }

    /// <summary>Media Foundation calls, by vtable slot (no interface declarations needed).</summary>
    internal static unsafe class Mf
    {
        public static readonly Guid VideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
        public static readonly Guid VideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
        public static readonly Guid MajorVideo = new("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
        public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
        public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        public static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
        public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
        public static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
        public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
        public static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
        public static readonly Guid IID_IMFMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");
        public static readonly Guid IID_IMF2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
        public static readonly Guid IID_IMFShutdown = new("97ec2ea4-0e42-4937-97ac-9d6d328824e1");
        public static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        public static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
        public static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
        public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
        public static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
        public const uint EnumSync = 0x1 | 0x40, EnumHardware = 0x4 | 0x40; // SYNCMFT / HARDWARE, each sorted and filtered

        private static int _started;
        private static volatile bool _unavailable;

        /// <summary>Test only: behave as on Windows N, where Media Foundation is missing.</summary>
        public static volatile bool TestUnavailable;

        /// <summary>
        /// Media Foundation ready on this thread; false where Windows has none (the N editions have no
        /// mfplat.dll), remembered so it is not looked for again and again.
        /// </summary>
        public static bool Start()
        {
            if (TestUnavailable || _unavailable) return false;
            try
            {
                if (Interlocked.Exchange(ref _started, 1) == 0 && MFStartup(0x20070, 0) < 0) { _unavailable = true; return false; }
                CoInitializeEx(IntPtr.Zero, 0); // this thread, for COM (harmless if done already)
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _unavailable = true;
                return false;
            }
        }

        /// <summary>
        /// COM on a thread of Kova's own that ends (an encoder's event pump), with no Media Foundation
        /// start of its own: that is once for the whole process, done before any encoder exists. True:
        /// call EndThread as it ends, so a pump started for every burst of motion leaves nothing behind.
        /// </summary>
        public static bool StartThread() { try { return CoInitializeEx(IntPtr.Zero, 0) >= 0; } catch { return false; } }
        public static void EndThread() { try { CoUninitialize(); } catch { } }

        /// <summary>The first transform of this kind Windows lists (best first), made ready; or zero.</summary>
        public static IntPtr FindTransform(Guid category, uint flags, Guid input, Guid output)
        {
            var i = new TypeInfo { Major = MajorVideo, Sub = input };
            var o = new TypeInfo { Major = MajorVideo, Sub = output };
            if (MFTEnumEx(category, flags, (IntPtr)(&i), (IntPtr)(&o), out IntPtr list, out uint n) < 0 || n == 0) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            try
            {
                for (int k = 0; k < n && found == IntPtr.Zero; k++)
                {
                    IntPtr activate = Marshal.ReadIntPtr(list, k * IntPtr.Size);
                    Guid iid = IID_IMFTransform;
                    IntPtr mft;
                    if (Call(activate, 33, (IntPtr)(&iid), (IntPtr)(&mft)) >= 0) found = mft; // ActivateObject
                }
            }
            finally
            {
                for (int k = 0; k < n; k++) Marshal.Release(Marshal.ReadIntPtr(list, k * IntPtr.Size));
                Marshal.FreeCoTaskMem(list);
            }
            return found;
        }

        public static IntPtr NewType(Guid subtype, int w, int h, int fps)
        {
            MFCreateMediaType(out IntPtr t);
            SetGuid(t, MF_MT_MAJOR_TYPE, MajorVideo);
            SetGuid(t, MF_MT_SUBTYPE, subtype);
            if (w > 0) SetUInt64(t, MF_MT_FRAME_SIZE, ((ulong)w << 32) | (uint)h);
            if (fps > 0) SetUInt64(t, MF_MT_FRAME_RATE, ((ulong)fps << 32) | 1);
            SetUInt64(t, MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
            SetUInt32(t, MF_MT_INTERLACE_MODE, 2); // progressive
            return t;
        }

        public static void CreateType(out IntPtr type) => MFCreateMediaType(out type);

        public static void SetGuid(IntPtr a, Guid key, Guid value) => Call(a, 24, (IntPtr)(&key), (IntPtr)(&value));
        public static void SetUInt32(IntPtr a, Guid key, uint value) => Call(a, 21, (IntPtr)(&key), value);
        public static void SetUInt64(IntPtr a, Guid key, ulong value) => Call(a, 22, (IntPtr)(&key), value);

        /// <summary>A sample holding a copy of these bytes.</summary>
        public static IntPtr SampleFrom(byte[] data, int length, int offset = 0)
        {
            IntPtr sample = EmptySample(length);
            IntPtr buffer;
            Call(sample, 40, 0, (IntPtr)(&buffer)); // GetBufferByIndex
            try
            {
                byte* p; uint max, cur;
                Call(buffer, 3, (IntPtr)(&p), (IntPtr)(&max), (IntPtr)(&cur)); // Lock
                Marshal.Copy(data, offset, (IntPtr)p, length);
                Call(buffer, 4); // Unlock
                Call(buffer, 6, (uint)length); // SetCurrentLength
            }
            finally { Marshal.Release(buffer); }
            return sample;
        }

        public static IntPtr EmptySample(int capacity)
        {
            MFCreateSample(out IntPtr sample);
            MFCreateMemoryBuffer((uint)Math.Max(1, capacity), out IntPtr buffer);
            Call(sample, 42, buffer); // AddBuffer
            Marshal.Release(buffer);
            return sample;
        }

        public static byte[] SampleBytes(IntPtr sample)
        {
            IntPtr buffer;
            if (Call(sample, 41, (IntPtr)(&buffer)) < 0) return Array.Empty<byte>(); // ConvertToContiguousBuffer
            try
            {
                byte* p; uint max, cur;
                if (Call(buffer, 3, (IntPtr)(&p), (IntPtr)(&max), (IntPtr)(&cur)) < 0) return Array.Empty<byte>();
                try { var b = new byte[cur]; Marshal.Copy((IntPtr)p, b, 0, (int)cur); return b; }
                finally { Call(buffer, 4); }
            }
            finally { Marshal.Release(buffer); }
        }

        /// <summary>The picture in a decoded sample, NV12, with its real row length (from IMF2DBuffer when there is one).</summary>
        public static void ReadNv12(IntPtr sample, int stride, int rows, Action<IntPtr, int, int> use)
        {
            IntPtr buffer;
            if (Call(sample, 40, 0, (IntPtr)(&buffer)) < 0) return; // GetBufferByIndex
            try
            {
                Guid iid = IID_IMF2DBuffer;
                if (Marshal.QueryInterface(buffer, ref iid, out IntPtr b2) >= 0)
                {
                    try
                    {
                        byte* line; int pitch;
                        if (Call(b2, 3, (IntPtr)(&line), (IntPtr)(&pitch)) >= 0) // Lock2D
                        {
                            try { use((IntPtr)line, pitch, rows); return; }
                            finally { Call(b2, 4); } // Unlock2D
                        }
                    }
                    finally { Marshal.Release(b2); }
                }
                byte* p; uint max, cur;
                if (Call(buffer, 3, (IntPtr)(&p), (IntPtr)(&max), (IntPtr)(&cur)) < 0) return;
                try { use((IntPtr)p, stride, rows); }
                finally { Call(buffer, 4); }
            }
            finally { Marshal.Release(buffer); }
        }

        private static IntPtr Slot(IntPtr o, int i) => (*(IntPtr**)o)[i];
        // Plain types only: .NET does not allow generic types in an unmanaged function pointer's signature.
        public static int Call(IntPtr o, int i) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(o, i))(o);
        public static int Call(IntPtr o, int i, IntPtr a) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(o, i))(o, a);
        public static int Call(IntPtr o, int i, uint a) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(o, i))(o, a);
        public static int Call(IntPtr o, int i, long a) => ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(o, i))(o, a);
        public static int Call(IntPtr o, int i, IntPtr a, IntPtr b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Slot(o, i))(o, a, b);
        public static int Call(IntPtr o, int i, IntPtr a, uint b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)Slot(o, i))(o, a, b);
        public static int Call(IntPtr o, int i, IntPtr a, ulong b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)Slot(o, i))(o, a, b);
        public static int Call(IntPtr o, int i, uint a, IntPtr b) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Slot(o, i))(o, a, b);
        public static int Call(IntPtr o, int i, IntPtr a, IntPtr b, IntPtr c) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, int>)Slot(o, i))(o, a, b, c);
        public static int Call(IntPtr o, int i, uint a, IntPtr b, uint c) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Slot(o, i))(o, a, b, c);
        public static int Call(IntPtr o, int i, uint a, uint b, IntPtr c) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, int>)Slot(o, i))(o, a, b, c);
        public static int Call(IntPtr o, int i, uint a, IntPtr b, IntPtr c) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(o, i))(o, a, b, c);
        public static int Call(IntPtr o, int i, uint a, uint b, IntPtr c, IntPtr d) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, IntPtr, int>)Slot(o, i))(o, a, b, c, d);
        public static int Call(IntPtr o, int i, IntPtr a, uint b, uint c, uint d, IntPtr e) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, IntPtr, int>)Slot(o, i))(o, a, b, c, d, e);
        public static void CallVoid(IntPtr o, int i, IntPtr a) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Slot(o, i))(o, a);
        public static void CallVoid(IntPtr o, int i, IntPtr a, IntPtr b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(o, i))(o, a, b);
        public static void CallVoid(IntPtr o, int i, IntPtr a, uint b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(o, i))(o, a, b);

        [StructLayout(LayoutKind.Sequential)] private struct TypeInfo { public Guid Major, Sub; }
        [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] private static extern int MFTEnumEx(Guid category, uint flags, IntPtr input, IntPtr output, out IntPtr activates, out uint count);
        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IntPtr sample);
        [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint length, out IntPtr buffer);
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, int flags);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
    }
}
