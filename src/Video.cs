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
    /// (kbit/s; 0: still pictures only). Screen: which of the PC's screens the viewer asked for (0: the
    /// first, the main one), passed along with the rest so it reaches the service's agent too.
    /// </summary>
    internal readonly record struct VideoSettings(int MaxWidth, int Quality, int Fps, int VideoKbps = 0, int Screen = 0)
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
    /// One of a PC's screens, in real pixels. Windows always puts the main screen's top left corner at
    /// 0,0, so X and Y are both where it is on the whole desktop and where it is from the main screen.
    /// </summary>
    internal readonly record struct ScreenInfo(int X, int Y, int Width, int Height, bool Main)
    {
        public Rectangle Area => new(X, Y, Width, Height);
    }

    /// <summary>
    /// What the host says about its screens (Protocol.VideoScreen), before the first picture and after
    /// each choice: which one is shown (0 = the first, the main one), how many there are, where the shown
    /// one is, and whether the one just asked for does not exist (so the viewer can say so).
    /// u8 shown, u8 count, u8 flags (1 = refused, 2 = the shown one is the main one, 4 = the screen
    /// shown until now was unplugged, so it is the main one again), u8 asked,
    /// i32 x, y, width, height, i32 the main screen's width, height.
    /// </summary>
    internal sealed record ScreenChoice(int Shown, int Count, bool Refused, int Asked, ScreenInfo Screen, int MainWidth, int MainHeight, bool Gone = false)
    {
        public const int Size = 28;

        public byte[] ToBytes()
        {
            var b = new byte[Size];
            b[0] = (byte)Shown; b[1] = (byte)Math.Min(255, Count); b[2] = (byte)((Refused ? 1 : 0) | (Screen.Main ? 2 : 0) | (Gone ? 4 : 0)); b[3] = (byte)Asked;
            BitConverter.TryWriteBytes(b.AsSpan(4), Screen.X);
            BitConverter.TryWriteBytes(b.AsSpan(8), Screen.Y);
            BitConverter.TryWriteBytes(b.AsSpan(12), Screen.Width);
            BitConverter.TryWriteBytes(b.AsSpan(16), Screen.Height);
            BitConverter.TryWriteBytes(b.AsSpan(20), MainWidth);
            BitConverter.TryWriteBytes(b.AsSpan(24), MainHeight);
            return b;
        }

        /// <summary>Null when it is cut short or makes no sense (nothing is said rather than nonsense).</summary>
        public static ScreenChoice? From(byte[] b, int offset, int n)
        {
            if (n < Size) return null;
            int shown = b[offset], count = b[offset + 1];
            if (count < 1 || shown >= count) return null;
            var s = new ScreenInfo(BitConverter.ToInt32(b, offset + 4), BitConverter.ToInt32(b, offset + 8), BitConverter.ToInt32(b, offset + 12), BitConverter.ToInt32(b, offset + 16), (b[offset + 2] & 2) != 0);
            if (s.Width <= 0 || s.Height <= 0) return null;
            return new ScreenChoice(shown, count, (b[offset + 2] & 1) != 0, b[offset + 3], s, BitConverter.ToInt32(b, offset + 20), BitConverter.ToInt32(b, offset + 24), (b[offset + 2] & 4) != 0);
        }

        /// <summary>Where the shown screen is, the way someone would say it: "right of the main screen".</summary>
        public string Where()
        {
            if (Screen.Main) return "the main screen";
            string across = Screen.X >= MainWidth ? "right of" : Screen.X + Screen.Width <= 0 ? "left of" : "";
            string up = Screen.Y >= MainHeight ? "below" : Screen.Y + Screen.Height <= 0 ? "above" : "";
            if (across == "" && up == "") return "overlapping the main screen";
            return (up != "" && across != "" ? up + " and " + across : up + across) + " the main screen";
        }

        /// <summary>What NVDA says: "Screen 2 of 3, 1920 by 1080, right of the main screen."</summary>
        public string Say()
        {
            if (Refused)
                return Count == 1 ? "There is no screen " + (Asked + 1) + ". The remote PC has only one screen."
                    : "There is no screen " + (Asked + 1) + ". The remote PC has " + Count + " screens; still showing screen " + (Shown + 1) + ".";
            if (Count == 1) return "The remote PC has only one screen.";
            return "Screen " + (Shown + 1) + " of " + Count + ", " + Screen.Width + " by " + Screen.Height + ", " + Where() + ".";
        }
    }

    /// <summary>
    /// The screen, taken with GDI (in Windows, no packages). Run on the thread that asks: the agent's
    /// own, which follows the desktop that has the keyboard, so the lock screen and UAC prompts are
    /// seen from the service as well as the desktop. Null when there is nothing to take (a desktop
    /// this process may not see, such as the lock screen from the window).
    /// </summary>
    internal static class ScreenCapture
    {
        /// <summary>The main screen, or the part of the desktop given (one of the other screens: ScreenInfo.Area).</summary>
        public static Frame? Capture(int maxWidth, Rectangle? area = null)
        {
            // Real pixels: a thread not aware of the display's scaling gets a blurred, smaller copy.
            IntPtr dpi = SetThreadDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
            IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, bmp = IntPtr.Zero, mem2 = IntPtr.Zero, bmp2 = IntPtr.Zero;
            try
            {
                if (Native.FollowInputDesktop) Native.FollowInputDesktopNow();
                int x0 = area?.X ?? 0, y0 = area?.Y ?? 0;
                int w = area?.Width ?? GetSystemMetrics(0), h = area?.Height ?? GetSystemMetrics(1); // the main screen unless told
                if (w <= 0 || h <= 0) return null;
                int ow = w, oh = h;
                if (maxWidth > 0 && w > maxWidth) { ow = maxWidth; oh = Math.Max(1, (int)((long)h * maxWidth / w)); }
                screen = GetDC(IntPtr.Zero); // the whole desktop, every screen, in desktop places
                if (screen == IntPtr.Zero) return null;
                mem = CreateCompatibleDC(screen);
                bmp = CreateCompatibleBitmap(screen, w, h);
                IntPtr was = SelectObject(mem, bmp);
                bool ok = BitBlt(mem, 0, 0, w, h, screen, x0, y0, SRCCOPY | CAPTUREBLT);
                if (ok) DrawCursor(mem, x0, y0);
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
        private static void DrawCursor(IntPtr dc, int x0, int y0)
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
            // The pointer's place is on the whole desktop; the picture starts at this screen's corner.
            DrawIconEx(dc, ci.x - hx - x0, ci.y - hy - y0, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
        }

        /// <summary>
        /// This PC's screens, in real pixels: the main one first, then the others from left to right
        /// (top to bottom where they line up), so screen 1 is always the main one and the numbers follow
        /// the desk. Never empty: if Windows will not say, the main screen alone.
        /// </summary>
        public static List<ScreenInfo> Screens()
        {
            IntPtr dpi = SetThreadDpiAwarenessContext(new IntPtr(-4)); // real pixels, the same as the pictures
            var found = new List<ScreenInfo>();
            try
            {
                MonitorEnumProc each = (IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data) =>
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref mi) && mi.Right > mi.Left && mi.Bottom > mi.Top)
                        found.Add(new ScreenInfo(mi.Left, mi.Top, mi.Right - mi.Left, mi.Bottom - mi.Top, (mi.Flags & 1) != 0));
                    return true;
                };
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, each, IntPtr.Zero);
                GC.KeepAlive(each);
                if (found.Count == 0)
                {
                    int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
                    if (w > 0 && h > 0) found.Add(new ScreenInfo(0, 0, w, h, true));
                }
            }
            catch { }
            finally { SetThreadDpiAwarenessContext(dpi); }
            if (found.Count == 0) found.Add(new ScreenInfo(0, 0, 0, 0, true)); // nothing to go on: the main screen, as GDI finds it when it is taken
            // Exactly one main screen, even if Windows marks none (it is the one at 0,0).
            if (!found.Any(s => s.Main))
            {
                int i = Math.Max(0, found.FindIndex(s => s.X == 0 && s.Y == 0));
                found[i] = found[i] with { Main = true };
            }
            return found.OrderBy(s => s.Main ? 0 : 1).ThenBy(s => s.X).ThenBy(s => s.Y).ToList();
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public int Left, Top, Right, Bottom; public int WorkLeft, WorkTop, WorkRight, WorkBottom; public int Flags; }
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc each, IntPtr data);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

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

    /// <summary>The same, from one of the PC's screens (0: the first, the main one; as ScreenSource.Screens numbers them).</summary>
    internal delegate Frame? ScreensTaker(int maxWidth, int screen, int waitMs, out bool changed);

    /// <summary>
    /// The screen as this PC can take it: Desktop Duplication (Windows hands over the screen only when it
    /// changes, as fast as it changes), else GDI's copy. In the service's agent it follows the desktop
    /// that has the keyboard first, so the lock screen, the sign-in screen and UAC prompts are taken too.
    /// Any of the PC's screens (Screens): each viewer may watch a different one. One thread at a time.
    /// </summary>
    internal sealed class ScreenSource : IDisposable
    {
        // A duplication for each screen being watched: two viewers on different screens would otherwise
        // start it again for every picture, which is slow.
        private readonly Dictionary<Rectangle, DesktopDuplication> _dd = new();
        private readonly Dictionary<Rectangle, long> _ddUsed = new(); // when each screen's duplication was last taken from
        // GDI's last copy and pace for each screen and width asked for: viewers on different steps take
        // turns here, and comparing one's copy with another's (a different size) would always say
        // "changed", so the copies would never slow down on a still screen.
        private sealed class GdiCopy { public long At; public int Gap = 33; public Frame? Last; }
        private readonly Dictionary<(int Width, Rectangle Area), GdiCopy> _gdi = new();
        private Frame? _scaled, _scaledFrom;
        private int _scaledWidth;
        private List<ScreenInfo>? _screens;
        private long _screensAt;

        // Taking and letting go take turns (a viewer's thread may be taking when hosting stops).
        private readonly object _gate = new();
        private bool _disposed;

        /// <summary>This PC's screens, the main one first (looked at again every 2 seconds: one may be plugged in or out).</summary>
        public IReadOnlyList<ScreenInfo> Screens() { lock (_gate) return ScreensNow(); }

        private List<ScreenInfo> ScreensNow()
        {
            long now = Environment.TickCount64;
            if (_screens != null && now - _screensAt < 2000) return _screens;
            _screens = ScreenCapture.Screens();
            _screensAt = now;
            // A screen that is gone (unplugged, or its size changed), or that nobody has looked at for 15 seconds
            // (a viewer stepped through the screens and back): its duplication and its copy of the screen go.
            foreach (var gone in _dd.Keys.Where(a => !_screens.Any(s => s.Area == a) || now - _ddUsed.GetValueOrDefault(a) > 15_000).ToList())
            {
                _dd[gone].Dispose();
                _dd.Remove(gone);
                _ddUsed.Remove(gone);
            }
            return _screens;
        }

        /// <summary>The main screen.</summary>
        public Frame? Take(int maxWidth, int waitMs, out bool changed) => Take(maxWidth, 0, waitMs, out changed);

        /// <summary>One of the screens (Screens' number; one that does not exist: the main one).</summary>
        public Frame? Take(int maxWidth, int screen, int waitMs, out bool changed)
        {
            changed = false;
            lock (_gate)
            {
                if (_disposed) return null;
                var list = ScreensNow();
                var s = screen >= 0 && screen < list.Count ? list[screen] : list[0];
                return TakeNow(maxWidth, s, waitMs, out changed);
            }
        }

        private Frame? TakeNow(int maxWidth, ScreenInfo screen, int waitMs, out bool changed)
        {
            // Real pixels, and the pointer where it really is: the agent is not told the display's scaling otherwise.
            IntPtr dpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                if (Native.FollowInputDesktop) Native.FollowInputDesktopNow();
                var area = screen.Area;
                if (!_dd.TryGetValue(area, out var dd)) _dd[area] = dd = new DesktopDuplication(area);
                _ddUsed[area] = Environment.TickCount64;
                var f = dd.Take(waitMs, out changed);
                // GDI: the main screen as Windows says it is now (also when the list could not be read), else this one's part of the desktop.
                if (f == null) return FromGdi(maxWidth, screen.Main ? null : area, out changed);
                foreach (var key in _gdi.Keys.Where(k => k.Area == (screen.Main ? Rectangle.Empty : area)).ToList()) _gdi.Remove(key);
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
        private Frame? FromGdi(int maxWidth, Rectangle? area, out bool changed)
        {
            changed = false;
            var key = (maxWidth, area ?? Rectangle.Empty);
            if (!_gdi.TryGetValue(key, out var g))
            {
                // A whole screen is kept per width: only the few in use now (a viewer stepping through the
                // ladder would otherwise leave a 4K copy behind at every step it passed).
                while (_gdi.Count >= 3) _gdi.Remove(_gdi.MinBy(x => x.Value.At).Key);
                _gdi[key] = g = new GdiCopy();
            }
            long wait = g.Gap - (Environment.TickCount64 - g.At);
            if (wait > 0) Thread.Sleep((int)wait);
            g.At = Environment.TickCount64;
            var f = ScreenCapture.Capture(maxWidth, area);
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

        /// <summary>
        /// Nobody is watching any more: everything kept for taking the screen goes (the graphics card's
        /// duplication of each screen, the last copies of the screen, the smaller copy), so a host left
        /// running for months holds none of it between viewers. The next picture starts it all again.
        /// </summary>
        public void Idle()
        {
            lock (_gate)
            {
                foreach (var dd in _dd.Values) dd.Dispose();
                _dd.Clear();
                _ddUsed.Clear();
                _gdi.Clear();
                _scaled = _scaledFrom = null;
                _screens = null;
            }
        }

        public void Dispose() { lock (_gate) { _disposed = true; foreach (var dd in _dd.Values) dd.Dispose(); _dd.Clear(); } }
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
    /// Not a picture: u8 3, then a ScreenChoice (which screen is shown, how many there are), handed back
    /// before the first picture and whenever that changes; the host passes it on as Protocol.VideoScreen.
    /// </summary>
    internal sealed class ScreenVideo
    {
        public const int Tile = 64;
        public const byte StillPicture = 1, VideoFrame = 2, ScreenNote = 3;
        private readonly ScreensTaker _take;
        private readonly Func<IReadOnlyList<ScreenInfo>>? _screens; // null: one screen, nothing said about it (tests)
        private readonly object _taking = new();
        private readonly Dictionary<int, Viewer> _viewers = new();
        // Run when the last viewer goes (ScreenSource.Idle): what was kept for taking the screen is let go of.
        private readonly Action? _idle;
        private bool _sourceIdle = true; // nothing taken since the last idle (under _viewers)
        private long _sweptAt;           // when viewers nobody asked about were last looked for (under _viewers)
        /// <summary>A viewer not asked for a picture in this long is dropped: its "gone" was lost on the way (AgentLink), or it is minimized.</summary>
        public const int StaleMs = 10 * 60_000;
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
            public long SentAskedAt;   // when the ask that last sent something came (0: the last ask sent nothing)
            public double Pace;        // usual time from one ask that sent something to the next, ms: how often the screen is looked at
            public readonly Queue<(long At, bool[] Tiles)> Changes = new(); // which tiles changed, when
            public int Shown, Asked;       // the screen shown, and the last one asked for (0: the main one)
            public int Asks;               // how many asks the viewer had made by the last one taken in
            public int ToldCount;          // what the viewer was last told: how many screens, and the one shown
            public ScreenInfo ToldScreen;
            public Rectangle ShownArea;    // where the shown screen is on the desktop: followed when others come and go
            public long UsedAt;            // when a picture was last asked for (under _viewers): the sweep drops ones long unused
            public bool Resting;           // forgotten (gone, or minimized): only its screen choice is kept, a few numbers
        }

        // A resting viewer is a few numbers (which screen it chose and where that is): kept a day, so one that was
        // minimized comes back to the same screen even after others were unplugged meanwhile.
        private const long RestingKeptMs = 24L * 3600_000;

        public ScreenVideo(ScreenTaker take) => _take = (int w, int _, int wait, out bool changed) => take(w, wait, out changed);
        public ScreenVideo(Func<int, Frame?> take) : this((int w, int _, out bool changed) => { changed = true; return take(w); }) { }

        /// <summary>
        /// Any of the PC's screens, as each viewer chooses (VideoSettings.Screen). idle: run when the last
        /// viewer goes; when not given and the pictures come from a ScreenSource (take is its Take), that
        /// source's Idle, so whoever makes one this way never has to remember it.
        /// </summary>
        public ScreenVideo(ScreensTaker take, Func<IReadOnlyList<ScreenInfo>> screens, Action? idle = null)
        {
            _take = take; _screens = screens;
            _idle = idle ?? (take.Target is ScreenSource source ? source.Idle : null);
        }

        public int Viewers { get { lock (_viewers) return _viewers.Values.Count(v => !v.Resting); } }

        /// <summary>Test only: this viewer is being sent H.264 now.</summary>
        public bool IsMoving(int viewer) { lock (_viewers) return _viewers.TryGetValue(viewer, out var v) && v.Moving; }

        /// <summary>The next update for this viewer: null if there is no picture to take, empty if there is nothing new to send.</summary>
        public byte[]? Update(int viewer, bool everything, VideoSettings settings)
        {
            Viewer v;
            long asked = Environment.TickCount64;
            lock (_viewers)
            {
                if (!_viewers.TryGetValue(viewer, out v!)) _viewers[viewer] = v = new Viewer();
                v.UsedAt = asked; // stamped while the list is held, so the sweep never drops one being asked for now
                v.Resting = false;
                _sourceIdle = false;
            }
            Sweep(asked);
            // One viewer's pictures at a time: the sweep (on another viewer's thread) may let this one's go.
            lock (v)
            {
                // How long the line makes each picture wait: a still picture is only followed by the next
                // once it is confirmed, a round trip later. Motion is judged over enough time for the same
                // number of looks at the screen, else a far-away viewer would never get H.264.
                long at = Environment.TickCount64;
                if (v.SentAskedAt != 0)
                {
                    double gap = Math.Min(2000, at - v.SentAskedAt);
                    v.Pace = v.Pace == 0 ? gap : v.Pace * 0.75 + gap * 0.25;
                }
                byte[]? u = UpdateNow(v, everything, settings);
                v.SentAskedAt = u is { Length: > 0 } && u[0] != ScreenNote ? at : 0;
                return u;
            }
        }

        private byte[]? UpdateNow(Viewer v, bool everything, VideoSettings settings)
        {
            byte[]? told = ChooseScreen(v, settings.Screen);
            if (told != null) return told;
            Frame? f;
            lock (_taking) f = _take(settings.MaxWidth, v.Shown, 16, out _);
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
            // Half a second and a second on a quick line; on a slow one (a round trip of 200 ms or more, so
            // the screen is looked at less than 8 times a second) as long as 4 looks take, up to 2 seconds.
            int halfMs = (int)Math.Clamp(v.Pace * 4, 500, 2000), secondMs = 2 * halfMs;
            while (v.Changes.Count > 0 && now - v.Changes.Peek().At > secondMs) v.Changes.Dequeue();
            var (halfFrames, halfArea) = Motion(v, now, halfMs);
            var (secondFrames, secondArea) = Motion(v, now, secondMs);
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

        /// <summary>
        /// Follows the screen the viewer asked for: one that does not exist is refused (and the one shown
        /// stays), and if the one shown goes (unplugged) it is the main one again. Hands back what to tell
        /// the viewer (a ScreenNote) when that changed, else null.
        /// </summary>
        private byte[]? ChooseScreen(Viewer v, int asking)
        {
            int asked = asking & 0xFF, asks = asking >> 8;
            if (_screens == null) return null;
            IReadOnlyList<ScreenInfo> list;
            try { list = _screens(); } catch { return null; }
            if (list.Count == 0) return null;
            bool refused = false, switched = false, gone = false;
            // The screen shown is followed by where it is, not by its place in the list: unplugging another
            // screen moves the rest up the list. Not found with fewer screens than before: it was the one
            // unplugged, so the main one again. Not found with as many: only its size changed, it stays.
            if (v.ShownArea != default)
            {
                int at = -1;
                for (int i = 0; i < list.Count; i++) if (list[i].Area == v.ShownArea) { at = i; break; }
                if (at >= 0) { if (at != v.Shown) { v.Shown = at; v.ToldCount = 0; } }
                else if (list.Count < v.ToldCount) { gone = true; v.Shown = 0; switched = true; }
            }
            if (asked != v.Asked || asks != v.Asks)
            {
                v.Asked = asked;
                v.Asks = asks;
                if (asked >= 0 && asked < list.Count) { switched = asked != v.Shown; v.Shown = asked; }
                else refused = true;
                v.ToldCount = 0; // a choice is always answered, even "that one again"
            }
            if (v.Shown >= list.Count) { v.Shown = 0; switched = true; gone = true; }
            v.ShownArea = list[v.Shown].Area;
            if (switched)
            {
                // Another screen: nothing on the viewer's copy is worth keeping, and its motion is not this one's.
                v.Changes.Clear();
                v.NeedsWhole = true;
            }
            var shown = list[v.Shown];
            if (!refused && v.ToldCount == list.Count && v.ToldScreen == shown) return null;
            v.ToldCount = list.Count; v.ToldScreen = shown;
            var main = list.FirstOrDefault(s => s.Main);
            var choice = new ScreenChoice(v.Shown, list.Count, refused, Math.Clamp(asked, 0, 255), shown, main.Width, main.Height, gone);
            byte[] body = choice.ToBytes();
            byte[] m = new byte[1 + body.Length];
            m[0] = ScreenNote;
            body.CopyTo(m, 1);
            return m;
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

        /// <summary>
        /// The viewer has gone, or has nothing to look at for now (its window is minimized): everything kept
        /// for it goes (its copies of the screen, its H.264 encoder). Safe to carry on from: asked for a
        /// picture again, it starts afresh with a whole one, on the same screen as before (which screen it
        /// chose, and where that one is, are kept: a few numbers, swept after a day).
        /// </summary>
        public void Forget(int viewer)
        {
            Viewer? v;
            lock (_viewers) _viewers.TryGetValue(viewer, out v);
            if (v != null) lock (v) { DropNow(v); lock (_viewers) v.Resting = true; }
            IdleIfNobody();
        }

        /// <summary>
        /// Every viewer gone at once: the agent's link to the service started again, and a service that
        /// restarted numbers its viewers from 1 again, so nothing kept for the old ones may be reused.
        /// </summary>
        public void ForgetAll()
        {
            List<Viewer> all;
            lock (_viewers) { all = _viewers.Values.ToList(); _viewers.Clear(); }
            foreach (var v in all) Drop(v);
            IdleIfNobody();
        }

        /// <summary>
        /// Drops viewers not asked for a picture in StaleMs (looked for at most once a minute). A "gone"
        /// lost on the way would otherwise keep a viewer's copies of the screen and its encoder until the
        /// agent restarts, which could be months. One that was only minimized starts afresh, whole.
        /// </summary>
        public void Sweep() { Sweep(Environment.TickCount64); IdleIfNobody(); }

        private void Sweep(long now)
        {
            List<Viewer> stale;
            lock (_viewers)
            {
                if (now - _sweptAt < 60_000) return;
                _sweptAt = now;
                stale = _viewers.Values.Where(v => now - v.UsedAt > (v.Resting ? RestingKeptMs : StaleMs)).ToList();
            }
            foreach (var v in stale)
            {
                // Busy making a picture: not stale after all.
                if (!Monitor.TryEnter(v)) continue;
                try
                {
                    lock (_viewers)
                    {
                        // Asked for again since it was picked out: it stays.
                        if (now - v.UsedAt <= (v.Resting ? RestingKeptMs : StaleMs)) continue;
                        foreach (var p in _viewers) if (p.Value == v) { _viewers.Remove(p.Key); break; }
                    }
                    DropNow(v);
                }
                finally { Monitor.Exit(v); }
            }
        }

        private static void Drop(Viewer v) { lock (v) DropNow(v); }

        private static void DropNow(Viewer v)
        {
            v.Encoder?.Dispose(); v.Encoder = null;
            v.Has = v.Seen = null;
            v.Changes.Clear();
            v.Moving = false;
            v.SentAskedAt = 0; v.Pace = 0;
        }

        /// <summary>No viewer left: what the screen source keeps for taking pictures goes too (once, until someone watches again).</summary>
        private void IdleIfNobody()
        {
            if (_idle == null) return;
            lock (_viewers)
            {
                if (_sourceIdle || _viewers.Values.Any(v => !v.Resting)) return;
                _sourceIdle = true;
            }
            // Never in the middle of a picture being taken for someone who has just come.
            try { lock (_taking) _idle(); } catch { }
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
        public int Apply(byte[] m, int offset) => Apply(m, offset, m.Length - offset);

        /// <summary>
        /// The same, from length bytes of m at offset: straight from the buffer the line was read into.
        /// Nothing of m is kept once this returns (pieces are copied out, video goes into the decoder), so
        /// the caller may read the next update into it.
        /// </summary>
        public int Apply(byte[] m, int offset, int length)
        {
            if (offset < 0 || length < 6 || offset + length > m.Length) throw new InvalidDataException("a picture cut short");
            if (m[offset] == ScreenVideo.VideoFrame) return ApplyVideo(m, offset, length);
            using var r = new BinaryReader(new MemoryStream(m, offset, length));
            if (r.ReadByte() != ScreenVideo.StillPicture) throw new InvalidDataException("a picture of a kind this version does not know");
            int width = r.ReadUInt16(), height = r.ReadUInt16();
            bool all = (r.ReadByte() & 1) != 0;
            int count = r.ReadUInt16();
            if (width == 0 || height == 0) throw new InvalidDataException("an empty picture");
            // The same limit as H.264 frames: a few bytes claiming a huge screen would otherwise make this PC
            // set aside gigabytes for it.
            if ((long)width * height > MaxArea) throw new InvalidDataException("a picture far too big");
            // Each piece is at least one 64-pixel tile, so there are never more pieces than tiles: a flood of
            // tiny ones would all be held decoded at once.
            if (count > (long)((width + ScreenVideo.Tile - 1) / ScreenVideo.Tile) * ((height + ScreenVideo.Tile - 1) / ScreenVideo.Tile)) throw new InvalidDataException("a damaged picture");
            var pieces = new List<(Rectangle At, Image Image)>(count);
            long area = 0;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    int x = r.ReadUInt16(), y = r.ReadUInt16(), w = r.ReadUInt16(), h = r.ReadUInt16(), n = r.ReadInt32();
                    long left = r.BaseStream.Length - r.BaseStream.Position;
                    if (n < 8 || n > left || w == 0 || h == 0 || x + w > width || y + h > height) throw new InvalidDataException("a damaged picture");
                    // The host's pieces never overlap, so together they cover the picture at most: more is a
                    // flood of pieces that would all be held decoded at once.
                    area += (long)w * h;
                    if (area > (long)width * height) throw new InvalidDataException("a damaged picture");
                    byte[] data = r.ReadBytes(n);
                    bool jpeg = data[0] == 0xFF && data[1] == 0xD8, png = data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
                    if (!jpeg && !png) throw new InvalidDataException("a picture of a kind this version does not take");
                    // Its size as the picture itself says, checked before it is decoded: a small file can
                    // unpack to a huge picture, which decoding would set aside memory for first.
                    if (StoredSize(data, png) != (w, h)) throw new InvalidDataException("a picture of the wrong size");
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

        private int ApplyVideo(byte[] m, int offset, int length)
        {
            int width = BitConverter.ToUInt16(m, offset + 1), height = BitConverter.ToUInt16(m, offset + 3);
            if (width < 16 || height < 16) throw new InvalidDataException("a video frame of no size");
            if ((long)width * height > MaxArea) throw new InvalidDataException("a video frame far too big");
            bool got;
            lock (_decoding)
            {
                if (_disposed) return 0;
                if (TestFailVideo) return -1;
                _decoder ??= H264Decoder.Create();
                if (_decoder == null) { CannotDecode = true; return -1; }
                if (_decoded == null || _decoded.Length != width * height * 4) _decoded = new byte[width * height * 4];
                if (!_decoder.Decode(m, offset + 6, length - 6, width, height, _decoded, out got))
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

        /// <summary>The biggest picture taken, either way (an 8K screen fits): bigger is a damaged or hostile update.</summary>
        private const long MaxArea = 8192L * 8192;

        /// <summary>
        /// The width and height a PNG or JPEG says it has, read from its header without decoding it; (-1, -1)
        /// when it cannot be read (then the piece is refused).
        /// </summary>
        private static (int W, int H) StoredSize(byte[] d, bool png)
        {
            if (png)
            {
                // The signature, then the IHDR chunk, which must come first: width and height big-endian.
                if (d.Length < 24 || d[12] != 'I' || d[13] != 'H' || d[14] != 'D' || d[15] != 'R') return (-1, -1);
                long pw = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(16)), ph = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(20));
                return pw > ushort.MaxValue || ph > ushort.MaxValue ? (-1, -1) : ((int)pw, (int)ph);
            }
            // JPEG: the segments after the start, each a marker and a length, up to the frame header (SOF).
            int at = 2;
            while (at + 4 <= d.Length)
            {
                if (d[at] != 0xFF) return (-1, -1);
                byte m = d[at + 1];
                if (m == 0xFF) { at++; continue; } // padding before a marker
                if (m == 0x01 || (m >= 0xD0 && m <= 0xD8)) { at += 2; continue; } // markers with nothing after them
                if (m == 0xD9 || m == 0xDA) return (-1, -1); // the end, or the picture data, with no frame header first
                int len = (d[at + 2] << 8) | d[at + 3];
                if (len < 2) return (-1, -1);
                if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                {
                    if (len < 7 || at + 9 > d.Length) return (-1, -1);
                    return ((d[at + 7] << 8) | d[at + 8], (d[at + 5] << 8) | d[at + 6]);
                }
                at += 2 + len;
            }
            return (-1, -1);
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
        private int _disposedOnce;
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

        /// <summary>What the host last said about its screens (which one is shown, how many); null until it says.</summary>
        public ScreenChoice? Screens { get; private set; }
        /// <summary>The host said which screen it shows (before the first picture, and after each choice). Raised on the stream's own thread.</summary>
        public event Action<ScreenChoice>? ScreenTold;
        private volatile int _screen = -1; // the screen chosen here (-1: none chosen, the host shows its main one)
        /// <summary>The screen chosen here (-1: none), so a new stream to the same PC can keep it.</summary>
        public int Chosen => _screen;

        /// <summary>Show this one of the host's screens (0: the first, the main one). The host answers with ScreenTold, refusing one it does not have.</summary>
        public void ChooseScreen(int screen)
        {
            _screen = _asked = Math.Clamp(screen, 0, 255);
            Send(new[] { Protocol.VideoChooseScreen, (byte)_screen });
        }
        private volatile int _asked = -1; // the last screen asked for on the line: only the host's answers to it move _screen

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
                    if (_screen is int again and >= 0) { _asked = again; Send(new[] { Protocol.VideoChooseScreen, (byte)again }); } // and which screen
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
                                    // Shown straight from the buffer (Apply keeps nothing of it), not a copy per picture.
                                    int length = n - 5;
                                    int pieces = Canvas.Apply(buffer, 5, length);
                                    bool isVideo = length > 0 && buffer[5] == ScreenVideo.VideoFrame;
                                    if (pieces < 0)
                                    {
                                        // The H.264 stream broke here: a key frame, please. But a PC that cannot
                                        // decode H.264 at all (no decoder, or one that fails even key frames)
                                        // would ask for key frames for ever with the picture frozen: after 3
                                        // failures in a row the host is told to send still pictures only.
                                        // Only key frames count: the frames still on their way after a break fail
                                        // too, and used to switch H.264 off for good on a PC that decodes it fine.
                                        bool key = isVideo && length > 5 && buffer[10] == 1;
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
                            case Protocol.VideoScreen:
                                if (ScreenChoice.From(buffer, 1, n - 1) is { } choice)
                                {
                                    Screens = choice;
                                    // What is asked for again on a new line follows what the host shows: back to
                                    // the main one when ours was unplugged, its new place when another was.
                                    // Only notes answering our latest ask count: an older one (sent before the
                                    // host took that ask in) would undo it.
                                    if (!choice.Refused && _asked >= 0 && choice.Asked == _asked) _screen = choice.Gone ? -1 : choice.Shown;
                                    ScreenTold?.Invoke(choice);
                                }
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
            // Once only, even from two threads at once: Stopped running twice would count the client's
            // open screens below zero.
            if (Interlocked.Exchange(ref _disposedOnce, 1) == 1) return;
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

    /// <summary>Test only: a made-up PC with 3 screens of different sizes (main; one to its right; one to its left), each a TestScreen.</summary>
    internal sealed class TestScreens
    {
        public readonly TestScreen[] All =
        {
            new() { Width = 640, Height = 360 },
            new() { Width = 480, Height = 320, SquareX = 300, SquareY = 200 },
            new() { Width = 320, Height = 480, SquareX = 20, SquareY = 400 },
        };
        private readonly ScreenInfo[] _where =
        {
            new(0, 0, 640, 360, true),
            new(640, 0, 480, 320, false),
            new(-320, -60, 320, 480, false),
        };
        public IReadOnlyList<ScreenInfo> Screens() => _where;
        public Frame? Take(int maxWidth, int screen, int waitMs, out bool changed)
        {
            changed = true;
            return All[screen >= 0 && screen < All.Length ? screen : 0].Take(maxWidth);
        }
    }
}
