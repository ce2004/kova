using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// How the screen is sent: its widest (0: as it is), the still pictures' quality (1 to 100 for JPEG;
    /// Lossless for PNG, every pixel exact) and most a second, and H.264's bitrate while things move
    /// (kbit/s; 0: still pictures only).
    /// </summary>
    internal readonly record struct VideoSettings(int MaxWidth, int Quality, int Fps, int VideoKbps = 0)
    {
        public const int Lossless = 101;

        /// <summary>
        /// The steps the picture moves along by itself, best first, like the sound's: as good as the line
        /// carries. There is no setting: it starts at Start (quick to arrive) and climbs while pictures
        /// are confirmed quickly, down a step the moment they are not.
        /// </summary>
        public static readonly VideoSettings[] Ladder =
        {
            new(0, Lossless, 15), // every pixel exactly as on the remote screen
            new(0, 92, 15),
            new(0, 82, 15),
            new(1920, 72, 12),
            new(1600, 62, 10),
            new(1280, 52, 8),
            new(960, 42, 6),
        };
        public const int Start = 2;
    }

    /// <summary>One picture of the screen: 32-bit BGRX, top row first, no padding.</summary>
    internal sealed class Frame
    {
        public required int Width, Height;
        public required byte[] Pixels;
    }

    /// <summary>
    /// The screen, taken with GDI (in Windows, no packages). Run on the thread that asks: the agent's
    /// own, which follows the desktop that has the keyboard, so the lock screen and UAC prompts are
    /// seen from the service as well as the desktop. Null when there is nothing to take (a desktop
    /// this process may not see, such as the lock screen from the window).
    /// </summary>
    internal static class ScreenCapture
    {
        public static Frame? Capture(int maxWidth)
        {
            // Real pixels: a thread not aware of the display's scaling gets a blurred, smaller copy.
            IntPtr dpi = SetThreadDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
            IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, bmp = IntPtr.Zero, mem2 = IntPtr.Zero, bmp2 = IntPtr.Zero;
            try
            {
                if (Native.FollowInputDesktop) Native.FollowInputDesktopNow();
                int w = GetSystemMetrics(0), h = GetSystemMetrics(1); // the main screen
                if (w <= 0 || h <= 0) return null;
                int ow = w, oh = h;
                if (maxWidth > 0 && w > maxWidth) { ow = maxWidth; oh = Math.Max(1, (int)((long)h * maxWidth / w)); }
                screen = GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) return null;
                mem = CreateCompatibleDC(screen);
                bmp = CreateCompatibleBitmap(screen, w, h);
                IntPtr was = SelectObject(mem, bmp);
                bool ok = BitBlt(mem, 0, 0, w, h, screen, 0, 0, SRCCOPY | CAPTUREBLT);
                if (ok) DrawCursor(mem);
                SelectObject(mem, was);
                if (!ok) return null;
                IntPtr take = bmp;
                if (ow != w)
                {
                    mem2 = CreateCompatibleDC(screen);
                    bmp2 = CreateCompatibleBitmap(screen, ow, oh);
                    IntPtr was2 = SelectObject(mem2, bmp2);
                    was = SelectObject(mem, bmp);
                    SetStretchBltMode(mem2, 4); // HALFTONE: smaller, still readable
                    SetBrushOrgEx(mem2, 0, 0, IntPtr.Zero);
                    StretchBlt(mem2, 0, 0, ow, oh, mem, 0, 0, w, h, SRCCOPY);
                    SelectObject(mem, was);
                    SelectObject(mem2, was2);
                    take = bmp2;
                }
                var info = new BITMAPINFOHEADER { biSize = 40, biWidth = ow, biHeight = -oh, biPlanes = 1, biBitCount = 32 };
                byte[] pixels = new byte[ow * oh * 4];
                if (GetDIBits(mem, take, 0, (uint)oh, pixels, ref info, 0) != oh) return null;
                return new Frame { Width = ow, Height = oh, Pixels = pixels };
            }
            catch { return null; }
            finally
            {
                if (bmp2 != IntPtr.Zero) DeleteObject(bmp2);
                if (mem2 != IntPtr.Zero) DeleteDC(mem2);
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
                SetThreadDpiAwarenessContext(dpi);
            }
        }

        /// <summary>The mouse pointer, which a screen copy leaves out: whoever watches sees where it is.</summary>
        private static void DrawCursor(IntPtr dc)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
            int hx = 0, hy = 0;
            if (GetIconInfo(ci.hCursor, out var ii))
            {
                hx = ii.xHotspot; hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            DrawIconEx(dc, ci.x - hx, ci.y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
        }

        private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
        [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    }

    /// <summary>A way to take the screen: the picture now (at most this wide), waiting up to waitMs for a change. Null: nothing to take.</summary>
    internal delegate Frame? ScreenTaker(int maxWidth, int waitMs, out bool changed);

    /// <summary>
    /// The screen as this PC can take it: Desktop Duplication (Windows hands over the screen only when it
    /// changes, as fast as it changes), else GDI's copy. In the service's agent it follows the desktop
    /// that has the keyboard first, so the lock screen, the sign-in screen and UAC prompts are taken too.
    /// One thread at a time.
    /// </summary>
    internal sealed class ScreenSource : IDisposable
    {
        private readonly DesktopDuplication _dd = new();
        // GDI's last copy and pace for each width asked for: viewers on different steps take turns here,
        // and comparing one's copy with another's (a different size) would always say "changed", so the
        // copies would never slow down on a still screen.
        private sealed class GdiCopy { public long At; public int Gap = 33; public Frame? Last; }
        private readonly Dictionary<int, GdiCopy> _gdi = new();
        private Frame? _scaled, _scaledFrom;
        private int _scaledWidth;

        // Taking and letting go take turns (a viewer's thread may be taking when hosting stops).
        private readonly object _gate = new();
        private bool _disposed;

        public Frame? Take(int maxWidth, int waitMs, out bool changed)
        {
            changed = false;
            lock (_gate) return _disposed ? null : TakeNow(maxWidth, waitMs, out changed);
        }

        private Frame? TakeNow(int maxWidth, int waitMs, out bool changed)
        {
            // Real pixels, and the pointer where it really is: the agent is not told the display's scaling otherwise.
            IntPtr dpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                if (Native.FollowInputDesktop) Native.FollowInputDesktopNow();
                var f = _dd.Take(waitMs, out changed);
                if (f == null) return FromGdi(maxWidth, out changed);
                _gdi.Clear();
                if (maxWidth <= 0 || f.Width <= maxWidth) return f;
                if (!changed && ReferenceEquals(_scaledFrom, f) && _scaledWidth == maxWidth && _scaled != null) return _scaled;
                _scaledFrom = f; _scaledWidth = maxWidth;
                _scaled = Smaller(f, maxWidth);
                return _scaled;
            }
            finally { SetThreadDpiAwarenessContext(dpi); }
        }

        /// <summary>
        /// GDI's copy, which never says what changed: about 30 a second while the screen changes, then
        /// less and less often (down to twice a second) while it stays the same, so a still screen costs
        /// little; the same picture is handed back (changed false) when nothing changed.
        /// </summary>
        private Frame? FromGdi(int maxWidth, out bool changed)
        {
            changed = false;
            if (!_gdi.TryGetValue(maxWidth, out var g))
            {
                // A whole screen is kept per width: only the few in use now (a viewer stepping through the
                // ladder would otherwise leave a 4K copy behind at every step it passed).
                while (_gdi.Count >= 3) _gdi.Remove(_gdi.MinBy(x => x.Value.At).Key);
                _gdi[maxWidth] = g = new GdiCopy();
            }
            long wait = g.Gap - (Environment.TickCount64 - g.At);
            if (wait > 0) Thread.Sleep((int)wait);
            g.At = Environment.TickCount64;
            var f = ScreenCapture.Capture(maxWidth);
            if (f == null) return null;
            if (g.Last != null && g.Last.Width == f.Width && g.Last.Height == f.Height && g.Last.Pixels.AsSpan().SequenceEqual(f.Pixels))
            {
                g.Gap = Math.Min(500, g.Gap * 2);
                return g.Last;
            }
            g.Gap = 33;
            g.Last = f;
            changed = true;
            return f;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        /// <summary>A smaller copy (a slow line's step): each pixel the average of the ones it covers, so text stays as readable as it can.</summary>
        private static Frame Smaller(Frame f, int maxWidth)
        {
            int ow = maxWidth, oh = Math.Max(1, (int)((long)f.Height * maxWidth / f.Width));
            var o = new byte[ow * oh * 4];
            System.Threading.Tasks.Parallel.For(0, oh, y =>
            {
                int y0 = y * f.Height / oh, y1 = Math.Max(y0 + 1, (y + 1) * f.Height / oh);
                for (int x = 0; x < ow; x++)
                {
                    int x0 = x * f.Width / ow, x1 = Math.Max(x0 + 1, (x + 1) * f.Width / ow);
                    int b = 0, g = 0, r = 0, n = 0;
                    for (int sy = y0; sy < y1; sy++)
                        for (int sx = x0; sx < x1; sx++)
                        {
                            int i = (sy * f.Width + sx) * 4;
                            b += f.Pixels[i]; g += f.Pixels[i + 1]; r += f.Pixels[i + 2]; n++;
                        }
                    int k = (y * ow + x) * 4;
                    o[k] = (byte)(b / n); o[k + 1] = (byte)(g / n); o[k + 2] = (byte)(r / n); o[k + 3] = 255;
                }
            });
            return new Frame { Width = ow, Height = oh, Pixels = o };
        }

        public void Dispose() { lock (_gate) { _disposed = true; _dd.Dispose(); } }
    }

    /// <summary>
    /// The screen as a stream of updates, one per viewer, the way game streaming and video calls do it,
    /// with exact still pictures on top:
    ///  - While things move (scrolling, a window dragged, a video playing: much of the screen changing
    ///    within half a second), H.264 (H264Encoder: the graphics chip's encoder, or Windows' own in
    ///    software), every change, up to 60 a second.
    ///  - Otherwise (typing, menus, a still screen), still pictures of only what changed: 64-pixel tiles,
    ///    those side by side joined into strips, JPEG or lossless PNG. Once moving stops, the whole
    ///    screen comes again this way, so what is left on the viewer's screen is exact.
    /// A still screen costs nothing: the screen is only looked at again when Windows says it changed.
    /// Where H.264 will not work, still pictures carry everything, and H.264 is tried again later.
    ///
    /// Still pictures: u8 1, u16 width, u16 height, u8 flags (1 = everything), u16 strips, then per strip
    /// u16 x, u16 y, u16 width, u16 height, i32 length, JPEG (or PNG, when lossless).
    /// H.264: u8 2, u16 width, u16 height, u8 flags (1 = key frame), then the frame (Annex B).
    /// </summary>
    internal sealed class ScreenVideo
    {
        public const int Tile = 64;
        public const byte StillPicture = 1, VideoFrame = 2;
        private readonly ScreenTaker _take;
        private readonly object _taking = new();
        private readonly Dictionary<int, Viewer> _viewers = new();
        private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        /// <summary>Test only: no H.264, still pictures only (the tests of still pictures need exact pixels).</summary>
        public bool NoVideo;

        private sealed class Viewer
        {
            public Frame? Has;    // what the viewer has from still pictures: what changes are worked out from
            public Frame? Seen;   // the last picture taken for this viewer
            public H264Encoder? Encoder;
            public bool Moving, NeedsWhole;
            public long VideoOffUntil; // H.264 would not start here: still pictures only until then
            public long ResizedAt = long.MinValue / 2; // when the picture last changed size
            public readonly Queue<(long At, bool[] Tiles)> Changes = new(); // which tiles changed, when
        }

        public ScreenVideo(ScreenTaker take) => _take = take;
        public ScreenVideo(Func<int, Frame?> take) : this((int w, int _, out bool changed) => { changed = true; return take(w); }) { }

        public int Viewers { get { lock (_viewers) return _viewers.Count; } }

        /// <summary>Test only: this viewer is being sent H.264 now.</summary>
        public bool IsMoving(int viewer) { lock (_viewers) return _viewers.TryGetValue(viewer, out var v) && v.Moving; }

        /// <summary>The next update for this viewer: null if there is no picture to take, empty if there is nothing new to send.</summary>
        public byte[]? Update(int viewer, bool everything, VideoSettings settings)
        {
            Viewer v;
            lock (_viewers) if (!_viewers.TryGetValue(viewer, out v!)) _viewers[viewer] = v = new Viewer();
            Frame? f;
            lock (_taking) f = _take(settings.MaxWidth, 16, out _);
            if (f == null) return null;
            long now = Environment.TickCount64;
            double part = 0;
            if (!ReferenceEquals(f, v.Seen))
            {
                bool[]? tiles = v.Seen == null || v.Seen.Width != f.Width || v.Seen.Height != f.Height ? null : ChangedTiles(v.Seen, f);
                if (tiles == null) { v.Changes.Clear(); part = 1; v.ResizedAt = now; } // a new size: no history to judge motion by
                else
                {
                    int n = 0;
                    foreach (bool t in tiles) if (t) n++;
                    part = (double)n / Math.Max(1, tiles.Length);
                    if (n > 0) v.Changes.Enqueue((now, tiles));
                }
                v.Seen = f;
            }
            while (v.Changes.Count > 0 && now - v.Changes.Peek().At > 1000) v.Changes.Dequeue();
            var (halfFrames, halfArea) = Motion(v, now, 500);
            var (secondFrames, secondArea) = Motion(v, now, 1000);
            bool mayMove = !NoVideo && settings.VideoKbps > 0 && now >= v.VideoOffUntil;
            // Moving: change again and again (4 times or more within half a second) over a real part of
            // the screen together (a fifth of it: a scroll, a drag, a video). One big change (Alt Tab, a
            // new page) is one exact picture; typing, menus and the mouse cover too little; all stay exact.
            if (!v.Moving && mayMove && halfFrames >= 4 && halfArea >= 0.2) { v.Moving = true; everything = true; }
            // A new size (the line's step changed the picture's width) wipes the history motion is judged
            // by, which is not things stopping: H.264 carries on for the second the history takes to come
            // back (Encode starts an encoder of the new size with a key frame), rather than a whole still
            // picture in the middle of a scroll.
            bool justResized = now - v.ResizedAt < 1000;
            if (v.Moving && (!mayMove || (!justResized && (secondFrames < 3 || secondArea < 0.05))))
            {
                v.Moving = false;
                v.NeedsWhole = true; // what H.264 left on the viewer's screen is replaced by an exact picture
                v.Encoder?.Dispose(); v.Encoder = null;
            }
            if (v.Moving)
            {
                if (part == 0 && !everything) return Array.Empty<byte>();
                byte[]? frame = Encode(v, f, everything, settings.VideoKbps);
                if (frame != null) return frame;
                // H.264 will not work here (no encoder, or it failed): still pictures carry everything, and
                // it is tried again in a minute.
                v.Moving = false; v.NeedsWhole = true; v.VideoOffUntil = now + 60_000;
            }
            if (v.NeedsWhole) everything = true;
            if (part == 0 && !everything && v.Has != null) return Array.Empty<byte>();
            byte[] update = Stills(v, f, everything, settings);
            if (everything && update.Length > 0) v.NeedsWhole = false;
            return update;
        }

        private static byte[]? Encode(Viewer v, Frame f, bool key, int kbps)
        {
            int w = f.Width & ~1, h = f.Height & ~1;
            if (v.Encoder == null || v.Encoder.Width != w || v.Encoder.Height != h)
            {
                v.Encoder?.Dispose();
                v.Encoder = H264Encoder.Create(w, h, 60, kbps);
                if (v.Encoder == null) return null;
                key = true;
            }
            v.Encoder.SetBitrate(kbps);
            byte[]? data = v.Encoder.Encode(f, key, out bool isKey);
            if (data != null && data.Length > 0 && v.Encoder.Hardware) H264Encoder.HardwareWorked();
            if (data == null)
            {
                // The graphics chip's encoder failed: Windows' own for this viewer, straight away (the
                // chip's is only left alone for a while after it fails three times in a row).
                bool wasHardware = v.Encoder.Hardware;
                v.Encoder.Dispose(); v.Encoder = null;
                if (!wasHardware) return null;
                H264Encoder.HardwareFailed();
                v.Encoder = H264Encoder.CreateSoftware(w, h, 60, kbps);
                data = v.Encoder?.Encode(f, true, out isKey);
                if (data == null) { v.Encoder?.Dispose(); v.Encoder = null; return null; }
            }
            if (data.Length == 0) return Array.Empty<byte>(); // the encoder is still filling up
            byte[] m = new byte[6 + data.Length];
            m[0] = VideoFrame;
            BitConverter.TryWriteBytes(m.AsSpan(1), (ushort)w);
            BitConverter.TryWriteBytes(m.AsSpan(3), (ushort)h);
            m[5] = (byte)(isKey ? 1 : 0);
            data.CopyTo(m, 6);
            return m;
        }

        private static byte[] Stills(Viewer v, Frame f, bool everything, VideoSettings settings)
        {
            Frame? had = v.Has;
            bool all = everything || had == null || had.Width != f.Width || had.Height != f.Height;
            var strips = new List<Rectangle>();
            for (int y = 0; y < f.Height; y += Tile)
            {
                int h = Math.Min(Tile, f.Height - y), runFrom = -1;
                for (int x = 0; ; x += Tile)
                {
                    bool changed = x < f.Width && (all || Changed(had!, f, x, y, Math.Min(Tile, f.Width - x), h));
                    if (changed && runFrom < 0) runFrom = x;
                    if (!changed && runFrom >= 0) { strips.Add(new Rectangle(runFrom, y, Math.Min(x, f.Width) - runFrom, h)); runFrom = -1; }
                    if (x >= f.Width) break;
                }
            }
            if (strips.Count == 0) return Array.Empty<byte>();
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(StillPicture); w.Write((ushort)f.Width); w.Write((ushort)f.Height); w.Write((byte)(all ? 1 : 0)); w.Write((ushort)strips.Count);
                bool lossless = settings.Quality >= VideoSettings.Lossless;
                using var q = new EncoderParameters(1);
                q.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(settings.Quality, 1, 100));
                foreach (var r in strips)
                {
                    w.Write((ushort)r.X); w.Write((ushort)r.Y); w.Write((ushort)r.Width); w.Write((ushort)r.Height);
                    long lengthAt = ms.Position;
                    w.Write(0);
                    w.Flush();
                    long start = ms.Position;
                    using (var b = Piece(f, r)) { if (lossless) b.Save(ms, ImageFormat.Png); else b.Save(ms, Jpeg, q); }
                    long end = ms.Position;
                    ms.Position = lengthAt;
                    w.Write((int)(end - start));
                    w.Flush();
                    ms.Position = end;
                }
            }
            v.Has = f;
            return ms.ToArray();
        }

        public void Forget(int viewer)
        {
            Viewer? v;
            lock (_viewers) { if (!_viewers.Remove(viewer, out v)) return; }
            v.Encoder?.Dispose();
        }

        /// <summary>
        /// Every viewer gone at once: the agent's link to the service started again, and a service that
        /// restarted numbers its viewers from 1 again, so nothing kept for the old ones may be reused.
        /// </summary>
        public void ForgetAll()
        {
            List<Viewer> all;
            lock (_viewers) { all = _viewers.Values.ToList(); _viewers.Clear(); }
            foreach (var v in all) v.Encoder?.Dispose();
        }

        /// <summary>Which tiles changed between two pictures of the same size.</summary>
        private static bool[] ChangedTiles(Frame a, Frame b)
        {
            int across = (b.Width + Tile - 1) / Tile, down = (b.Height + Tile - 1) / Tile;
            var map = new bool[across * down];
            for (int ty = 0; ty < down; ty++)
                for (int tx = 0; tx < across; tx++)
                {
                    int x = tx * Tile, y = ty * Tile;
                    map[ty * across + tx] = Changed(a, b, x, y, Math.Min(Tile, b.Width - x), Math.Min(Tile, b.Height - y));
                }
            return map;
        }

        /// <summary>Within the last ms: how many pictures changed, and how much of the screen changed in any of them (the tiles together, 0 to 1).</summary>
        private static (int Frames, double Area) Motion(Viewer v, long now, int ms)
        {
            bool[]? union = null;
            int frames = 0;
            foreach (var (at, tiles) in v.Changes)
            {
                if (now - at > ms) continue;
                frames++;
                union ??= new bool[tiles.Length];
                if (union.Length != tiles.Length) continue;
                for (int i = 0; i < tiles.Length; i++) union[i] |= tiles[i];
            }
            if (union == null) return (0, 0);
            int n = 0;
            foreach (bool t in union) if (t) n++;
            return (frames, (double)n / union.Length);
        }

        private static bool Changed(Frame a, Frame b, int x, int y, int w, int h)
        {
            for (int row = y; row < y + h; row++)
            {
                int at = (row * a.Width + x) * 4;
                if (!a.Pixels.AsSpan(at, w * 4).SequenceEqual(b.Pixels.AsSpan(at, w * 4))) return true;
            }
            return false;
        }

        private static Bitmap Piece(Frame f, Rectangle r)
        {
            var b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
            var d = b.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int row = 0; row < r.Height; row++)
                    Marshal.Copy(f.Pixels, ((r.Y + row) * f.Width + r.X) * 4, d.Scan0 + row * d.Stride, r.Width * 4);
            }
            finally { b.UnlockBits(d); }
            return b;
        }
    }

    /// <summary>
    /// The viewer's copy of the remote screen, kept up to date from the updates: still pictures drawn
    /// in where they go, H.264 frames decoded (Windows' own decoder) over the whole of it. Lock Gate to
    /// read Picture.
    /// </summary>
    internal sealed class VideoCanvas : IDisposable
    {
        public readonly object Gate = new();
        public Bitmap? Picture { get; private set; }
        private H264Decoder? _decoder;
        private byte[]? _decoded;
        // Decoding (the stream's thread) and closing (the window's) take turns: freeing the decoder in the
        // middle of a frame would crash the process.
        private readonly object _decoding = new();
        private volatile bool _disposed;

        /// <summary>This PC has no H.264 decoder at all (Windows N): the host is asked for still pictures only.</summary>
        public bool CannotDecode { get; private set; }

        /// <summary>Test only: every H.264 frame fails to decode, as on a PC whose decoder is broken.</summary>
        public static volatile bool TestFailVideo;

        /// <summary>
        /// Applies one update; returns how many pieces it held (0: an H.264 frame not out yet), or -1 when
        /// the H.264 stream cannot go on from here (ask for a key frame). Throws on a damaged one. Still
        /// pictures are decoded before the lock is taken, so a big one never holds up the window drawing
        /// the last; only JPEG and PNG of exactly the size given are taken.
        /// </summary>
        public int Apply(byte[] m, int offset)
        {
            if (m.Length - offset < 6) throw new InvalidDataException("a picture cut short");
            if (m[offset] == ScreenVideo.VideoFrame) return ApplyVideo(m, offset);
            using var r = new BinaryReader(new MemoryStream(m, offset, m.Length - offset));
            if (r.ReadByte() != ScreenVideo.StillPicture) throw new InvalidDataException("a picture of a kind this version does not know");
            int width = r.ReadUInt16(), height = r.ReadUInt16();
            bool all = (r.ReadByte() & 1) != 0;
            int count = r.ReadUInt16();
            if (width == 0 || height == 0) throw new InvalidDataException("an empty picture");
            var pieces = new List<(Rectangle At, Image Image)>(count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    int x = r.ReadUInt16(), y = r.ReadUInt16(), w = r.ReadUInt16(), h = r.ReadUInt16(), n = r.ReadInt32();
                    long left = r.BaseStream.Length - r.BaseStream.Position;
                    if (n < 8 || n > left || w == 0 || h == 0 || x + w > width || y + h > height) throw new InvalidDataException("a damaged picture");
                    byte[] data = r.ReadBytes(n);
                    bool jpeg = data[0] == 0xFF && data[1] == 0xD8, png = data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
                    if (!jpeg && !png) throw new InvalidDataException("a picture of a kind this version does not take");
                    var img = Image.FromStream(new MemoryStream(data));
                    pieces.Add((new Rectangle(x, y, w, h), img));
                    if (img.Width != w || img.Height != h) throw new InvalidDataException("a picture of the wrong size");
                }
                lock (Gate)
                {
                    if (_disposed) return 0; // the window closed meanwhile: a new bitmap now would never be freed
                    if (Picture == null || all || Picture.Width != width || Picture.Height != height)
                    {
                        if (!all) throw new InvalidDataException("a change to a picture this PC does not have");
                        Picture?.Dispose();
                        Picture = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                    }
                    using var g = Graphics.FromImage(Picture);
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    foreach (var (at, img) in pieces) g.DrawImage(img, at, new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
                }
            }
            finally { foreach (var (_, img) in pieces) img.Dispose(); }
            return count;
        }

        private int ApplyVideo(byte[] m, int offset)
        {
            int width = BitConverter.ToUInt16(m, offset + 1), height = BitConverter.ToUInt16(m, offset + 3);
            if (width < 16 || height < 16) throw new InvalidDataException("a video frame of no size");
            if ((long)width * height > 8192L * 8192) throw new InvalidDataException("a video frame far too big");
            bool got;
            lock (_decoding)
            {
                if (_disposed) return 0;
                if (TestFailVideo) return -1;
                _decoder ??= H264Decoder.Create();
                if (_decoder == null) { CannotDecode = true; return -1; }
                if (_decoded == null || _decoded.Length != width * height * 4) _decoded = new byte[width * height * 4];
                if (!_decoder.Decode(m, offset + 6, m.Length - offset - 6, width, height, _decoded, out got))
                {
                    _decoder.Dispose(); _decoder = null; // a fresh decoder for the key frame asked for
                    return -1;
                }
            }
            if (!got) return 0;
            lock (Gate)
            {
                if (_disposed) return 0;
                if (Picture == null || Picture.Width != width || Picture.Height != height)
                {
                    Picture?.Dispose();
                    Picture = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                }
                var d = Picture.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    for (int row = 0; row < height; row++)
                        Marshal.Copy(_decoded, row * width * 4, d.Scan0 + row * d.Stride, width * 4);
                }
                finally { Picture.UnlockBits(d); }
            }
            return 1;
        }

        /// <summary>A pixel of the copy (tests), or Empty if there is none yet.</summary>
        public System.Drawing.Color At(int x, int y) { lock (Gate) return Picture == null || x >= Picture.Width || y >= Picture.Height ? System.Drawing.Color.Empty : Picture.GetPixel(x, y); }

        public void Dispose()
        {
            lock (Gate) { _disposed = true; Picture?.Dispose(); Picture = null; }
            lock (_decoding) { _decoder?.Dispose(); _decoder = null; }
        }
    }

    /// <summary>
    /// Watching the remote screen: its own connection (a video lane: encrypted, proven like a file
    /// lane), which comes back by itself if it drops, starting from a whole picture. Each update is
    /// confirmed once it is on the canvas, and the host sends the next only then, so pictures never
    /// queue up on a slow connection: what is shown is always the newest the line can carry.
    /// </summary>
    internal sealed class VideoStream : IDisposable
    {
        public readonly VideoCanvas Canvas = new();
        /// <summary>A new picture is on the canvas. Raised on the stream's own thread.</summary>
        public event Action? Updated;
        /// <summary>What the host says about the picture (none to show, and why). Raised on the stream's own thread.</summary>
        public event Action<string>? Note;
        /// <summary>Raised once, when this stream is disposed.</summary>
        public event Action? Stopped;
        public int Pictures, LastStrips, Reconnects, VideoFrames;
        private readonly Queue<long> _times = new();

        /// <summary>Pictures shown in the last second: the frame rate, for the title bar.</summary>
        public int PerSecond { get { lock (_times) { long t = Environment.TickCount64; while (_times.Count > 0 && t - _times.Peek() > 1000) _times.Dequeue(); return _times.Count; } } }
        public long Bytes;
        private readonly Func<(TcpClient, SecureLink)?> _dial;
        private volatile bool _stop;
        private volatile TcpClient? _tcp;
        private volatile SecureLink? _link;
        private readonly object _sendGate = new();

        public VideoStream(Func<(TcpClient, SecureLink)?> dial)
        {
            _dial = dial;
            new Thread(Loop) { IsBackground = true, Name = "Kova remote screen" }.Start();
        }

        /// <summary>Nothing to look at (the window is minimized): the host sends nothing, and takes no pictures, until resumed.</summary>
        public void Pause(bool paused)
        {
            _paused = paused;
            Send(new[] { Protocol.VideoPause, (byte)(paused ? 1 : 0) });
        }
        private volatile bool _paused;

        // H.264 frames that failed to decode in a row, and whether the host was told to stop sending them.
        private int _videoFailures;
        private volatile bool _noH264;

        /// <summary>Test only: the host was asked for still pictures only (H.264 cannot be shown here).</summary>
        public bool NoH264 => _noH264;

        /// <summary>A whole new picture, rather than only what changes.</summary>
        public void Refresh() => Send(new[] { Protocol.VideoAgain });

        private void Send(byte[] m)
        {
            var tcp = _tcp; var link = _link;
            if (tcp == null || link == null) return;
            try { lock (_sendGate) link.Send(tcp.GetStream(), m); } catch { }
        }

        private void Loop()
        {
            bool first = true;
            while (!_stop)
            {
                var lane = _dial();
                if (lane is not var (tcp, link)) { Sleep(2000); continue; }
                if (!first) Interlocked.Increment(ref Reconnects);
                first = false;
                _tcp = tcp; _link = link;
                bool toldWhy = false, saw = false;
                try
                {
                    var stream = tcp.GetStream();
                    stream.ReadTimeout = 15_000; // the host says something at least every few seconds
                    if (_paused) Send(new[] { Protocol.VideoPause, (byte)1 });
                    if (_noH264) Send(new[] { Protocol.VideoNoH264 }); // a new line: the host has to be told again
                    byte[]? buffer = null;
                    while (!_stop)
                    {
                        int n = link.Receive(stream, ref buffer);
                        if (n < 1) continue;
                        switch (buffer![0])
                        {
                            case Protocol.VideoPicture when n >= 5:
                                {
                                    uint number = BitConverter.ToUInt32(buffer, 1);
                                    byte[] update = buffer.AsSpan(5, n - 5).ToArray();
                                    int pieces = Canvas.Apply(update, 0);
                                    bool isVideo = update.Length > 0 && update[0] == ScreenVideo.VideoFrame;
                                    if (pieces < 0)
                                    {
                                        // The H.264 stream broke here: a key frame, please. But a PC that cannot
                                        // decode H.264 at all (no decoder, or one that fails even key frames)
                                        // would ask for key frames for ever with the picture frozen: after 3
                                        // failures in a row the host is told to send still pictures only.
                                        // Only key frames count: the frames still on their way after a break fail
                                        // too, and used to switch H.264 off for good on a PC that decodes it fine.
                                        bool key = isVideo && update.Length > 5 && update[5] == 1;
                                        if (!_noH264 && (Canvas.CannotDecode || (key && ++_videoFailures >= 3)))
                                        {
                                            _noH264 = true;
                                            Send(new[] { Protocol.VideoNoH264 });
                                        }
                                        Send(new[] { Protocol.VideoAgain });
                                        pieces = 0;
                                    }
                                    else if (isVideo && pieces > 0) _videoFailures = 0;
                                    LastStrips = pieces;
                                    if (isVideo) Interlocked.Increment(ref VideoFrames);
                                    lock (_times) { long t = Environment.TickCount64; _times.Enqueue(t); while (_times.Count > 0 && t - _times.Peek() > 1000) _times.Dequeue(); }
                                    saw = true;
                                    Interlocked.Increment(ref Pictures);
                                    Interlocked.Add(ref Bytes, n);
                                    Updated?.Invoke();
                                    byte[] ack = new byte[5];
                                    ack[0] = Protocol.VideoGot;
                                    BitConverter.TryWriteBytes(ack.AsSpan(1), number);
                                    Send(ack);
                                    break;
                                }
                            case Protocol.VideoNote:
                                toldWhy = true;
                                Note?.Invoke(System.Text.Encoding.UTF8.GetString(buffer, 1, n - 1));
                                break;
                        }
                    }
                }
                catch { }
                finally
                {
                    _tcp = null; _link = null;
                    try { tcp.Dispose(); } catch { }
                    link.Dispose();
                }
                // Turned away with a reason and nothing shown (8 PCs already watching): asking again every
                // second would only cost both PCs a login each time. Every 20 seconds is plenty.
                Sleep(toldWhy && !saw ? 20_000 : 1000);
            }
        }

        private void Sleep(int ms) { for (int i = 0; i < ms / 50 && !_stop; i++) Thread.Sleep(50); }

        /// <summary>Test only: cuts the line, as a dropped connection would.</summary>
        internal void TestCut() { try { _tcp?.Dispose(); } catch { } }

        public void Dispose()
        {
            if (_stop) return;
            _stop = true;
            try { _tcp?.Dispose(); } catch { }
            Canvas.Dispose();
            Stopped?.Invoke();
        }
    }

    /// <summary>Test only: a made-up screen (a gradient with a square on it) the tests can move the square on.</summary>
    internal sealed class TestScreen
    {
        public int Width = 640, Height = 360;
        public volatile int SquareX = 100, SquareY = 100;
        public const int Square = 40;
        public Frame? Take(int maxWidth)
        {
            var p = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int i = (y * Width + x) * 4;
                    bool sq = x >= SquareX && x < SquareX + Square && y >= SquareY && y < SquareY + Square;
                    p[i] = (byte)(sq ? 0 : x * 255 / Width);      // blue
                    p[i + 1] = (byte)(sq ? 0 : y * 255 / Height); // green
                    p[i + 2] = (byte)(sq ? 255 : 128);            // red
                }
            return new Frame { Width = Width, Height = Height, Pixels = p };
        }

        /// <summary>The colour the made-up screen has here.</summary>
        public Color At(int x, int y)
        {
            bool sq = x >= SquareX && x < SquareX + Square && y >= SquareY && y < SquareY + Square;
            return sq ? Color.FromArgb(255, 0, 0) : Color.FromArgb(128, y * 255 / Height, x * 255 / Width);
        }

        /// <summary>Close enough for a JPEG: each colour within the given distance.</summary>
        public static bool Near(Color a, Color b, int by) => Math.Abs(a.R - b.R) <= by && Math.Abs(a.G - b.G) <= by && Math.Abs(a.B - b.B) <= by;
    }
}
