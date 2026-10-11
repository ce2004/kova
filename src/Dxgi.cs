using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TailRemote
{
    /// <summary>
    /// The screen through Desktop Duplication (DXGI, in Windows): Windows hands over the screen only
    /// when something on it changed, straight from the graphics card, so a still screen costs nothing
    /// and a moving one is taken as fast as it changes, up to the display's own rate. One screen: the one
    /// whose top left corner is where it was told (the main one at 0,0), on whichever graphics chip shows
    /// it. The mouse pointer is drawn in (duplication leaves it out). When Windows switches desktop
    /// (the lock screen, a UAC prompt) duplication has to start again: done by itself on the next call.
    /// Anything it cannot do (no duplication on this PC, a desktop it may not see) makes Take return
    /// null, and ScreenCapture's GDI copy is used instead.
    /// </summary>
    internal sealed unsafe class DesktopDuplication : IDisposable
    {
        private IntPtr _device, _context, _dup, _staging;
        private int _width, _height, _stagingW, _stagingH;
        private bool _systemMemory;
        private Frame? _last;
        private long _failedAt;
        private readonly int _left, _top; // where this screen's top left corner is on the whole desktop
        private int _cursorSeen = -1;
        private bool _cursorWasHere;

        public DesktopDuplication() : this(System.Drawing.Rectangle.Empty) { }
        public DesktopDuplication(System.Drawing.Rectangle screen) { _left = screen.X; _top = screen.Y; }

        /// <summary>
        /// The pointer moved or changed shape since this screen last looked, on this screen or off it (it
        /// left, so it is taken off the picture). Moving about on another screen changes nothing here.
        /// </summary>
        private bool CursorMoved()
        {
            Cursor.Look();
            if (Cursor.Version == _cursorSeen) return false;
            _cursorSeen = Cursor.Version;
            bool here = Cursor.Current is var (x, y, w, h, _) && _width > 0 && x - _left < _width && x - _left + w > 0 && y - _top < _height && y - _top + h > 0;
            bool moved = here || _cursorWasHere;
            _cursorWasHere = here;
            return moved;
        }

        private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
        private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        /// <summary>
        /// The screen: a new picture if it changed within waitMs (Windows wakes this the moment it
        /// does; a still screen costs nothing while it waits), else the last one again with changed
        /// false. Null if duplication cannot be used here right now.
        /// </summary>
        public Frame? Take(int waitMs, out bool changed)
        {
            changed = false;
            if (_dup == IntPtr.Zero && !Start()) return null;
            DXGI_OUTDUPL_FRAME_INFO info;
            IntPtr resource;
            int hr = Call(_dup, 8, (uint)Math.Max(0, waitMs), (IntPtr)(&info), (IntPtr)(&resource)); // AcquireNextFrame
            if (hr == unchecked((int)0x887A0027)) // DXGI_ERROR_WAIT_TIMEOUT: nothing changed
            {
                if (_last == null) return null;
                if (CursorMoved()) { changed = true; return WithCursor(_last); }
                return _shown ?? _last;
            }
            if (hr < 0)
            {
                // Access lost (a desktop switch: the lock screen, a UAC prompt): straight back on the next
                // call, on the new desktop. Anything else: not again for a few seconds.
                Stop();
                if (hr != unchecked((int)0x887A0026)) _failedAt = Environment.TickCount64;
                return null;
            }
            try
            {
                // Only a frame Windows really drew is read: the first after duplication (re)starts may carry
                // only a pointer move, with nothing in the picture yet (black). Until a real one comes,
                // null, so GDI's copy is shown instead.
                if (info.LastPresentTime != 0)
                {
                    var f = Read(resource);
                    if (f != null) { _last = f; changed = true; }
                }
            }
            finally
            {
                Marshal.Release(resource);
                Call(_dup, 14); // ReleaseFrame
            }
            if (_last == null) return null;
            if (CursorMoved()) changed = true;
            if (!changed) return _shown ?? _last;
            return WithCursor(_last);
        }

        // The last picture as shown, with the pointer drawn in: handed back again while nothing changes.
        private Frame? _shown;

        private Frame WithCursor(Frame f)
        {
            _shown = Cursor.Current == null ? f : Cursor.DrawOn(Copy(f), _left, _top);
            return _shown;
        }

        private static Frame Copy(Frame f) => new() { Width = f.Width, Height = f.Height, Pixels = (byte[])f.Pixels.Clone() };

        private Frame? Read(IntPtr resource)
        {
            var pixels = new byte[_width * _height * 4];
            if (_systemMemory)
            {
                DXGI_MAPPED_RECT rect;
                if (Call(_dup, 12, (IntPtr)(&rect)) < 0) return null; // MapDesktopSurface
                try { CopyRows(rect.pBits, rect.Pitch, pixels); }
                finally { Call(_dup, 13); } // UnMapDesktopSurface
            }
            else
            {
                Guid iid = IID_ID3D11Texture2D;
                if (Marshal.QueryInterface(resource, ref iid, out IntPtr texture) < 0) return null;
                try
                {
                    if (!EnsureStaging()) return null;
                    CallVoid(_context, 47, _staging, texture); // CopyResource
                    D3D11_MAPPED_SUBRESOURCE map;
                    if (Call(_context, 14, _staging, 0, 1, 0, (IntPtr)(&map)) < 0) return null; // Map, READ
                    try { CopyRows(map.pData, (int)map.RowPitch, pixels); }
                    finally { CallVoid(_context, 15, _staging, 0u); } // Unmap (subresource 0)
                }
                finally { Marshal.Release(texture); }
            }
            return new Frame { Width = _width, Height = _height, Pixels = pixels };
        }

        private void CopyRows(IntPtr from, int pitch, byte[] to)
        {
            int row = _width * 4;
            fixed (byte* dst = to)
                for (int y = 0; y < _height; y++)
                    Buffer.MemoryCopy((byte*)from + (long)y * pitch, dst + (long)y * row, row, row);
        }

        private bool EnsureStaging()
        {
            if (_staging != IntPtr.Zero && _stagingW == _width && _stagingH == _height) return true;
            if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)_width, Height = (uint)_height, MipLevels = 1, ArraySize = 1, Format = 87, // B8G8R8A8_UNORM
                SampleCount = 1, Usage = 3 /* STAGING */, CPUAccessFlags = 0x20000 /* READ */,
            };
            IntPtr tex;
            if (Call(_device, 5, (IntPtr)(&desc), IntPtr.Zero, (IntPtr)(&tex)) < 0) return false; // CreateTexture2D
            _staging = tex; _stagingW = _width; _stagingH = _height;
            return true;
        }

        private bool Start()
        {
            // Not again and again when it cannot work here (a PC with no duplication): once every 5 seconds.
            if (_failedAt != 0 && Environment.TickCount64 - _failedAt < 5000) return false;
            try
            {
                if (!FindOutput(_left, _top, out IntPtr adapter, out IntPtr output)) { _failedAt = Environment.TickCount64; return false; }
                try
                {
                    IntPtr device, context;
                    int level;
                    int hr = D3D11CreateDevice(adapter, 0 /* UNKNOWN: the adapter given */, IntPtr.Zero, 0x20 /* BGRA */, IntPtr.Zero, 0, 7, &device, &level, &context);
                    if (hr < 0) { _failedAt = Environment.TickCount64; return false; }
                    _device = device; _context = context;
                    Guid iid = IID_IDXGIOutput1;
                    if (Marshal.QueryInterface(output, ref iid, out IntPtr output1) < 0) { Stop(); _failedAt = Environment.TickCount64; return false; }
                    try
                    {
                        IntPtr dup;
                        hr = Call(output1, 22, _device, (IntPtr)(&dup)); // DuplicateOutput
                        if (hr < 0) { Stop(); _failedAt = Environment.TickCount64; return false; }
                        _dup = dup;
                    }
                    finally { Marshal.Release(output1); }
                    DXGI_OUTDUPL_DESC d;
                    CallVoid(_dup, 7, (IntPtr)(&d)); // GetDesc
                    // A turned display (portrait, or upside down) is handed over unturned, so it would arrive
                    // sideways: GDI's copy, which is the right way round, is used for it (looked at again
                    // every 30 seconds, in case the display is turned back).
                    if (d.Rotation > 1) { Stop(); _failedAt = Environment.TickCount64 + 25_000; return false; }
                    _width = (int)d.Width; _height = (int)d.Height; _systemMemory = d.DesktopImageInSystemMemory != 0;
                    _last = null; _shown = null;
                    _failedAt = 0;
                    return _width > 0 && _height > 0;
                }
                finally { Marshal.Release(adapter); Marshal.Release(output); }
            }
            catch { Stop(); _failedAt = Environment.TickCount64; return false; }
        }

        /// <summary>
        /// The adapter and output showing the screen whose top left corner is here (the main one: 0,0),
        /// looking through every graphics chip: on a laptop with two, or a PC with screens on two cards,
        /// the right one. Two screens never share a corner (copies of one screen are one to Windows).
        /// </summary>
        private static bool FindOutput(int left, int top, out IntPtr adapter, out IntPtr output)
        {
            adapter = output = IntPtr.Zero;
            Guid iid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iid, out IntPtr factory) < 0) return false;
            try
            {
                for (uint a = 0; ; a++)
                {
                    IntPtr ad;
                    if (Call(factory, 7, a, (IntPtr)(&ad)) < 0) return false; // EnumAdapters: none left
                    for (uint o = 0; ; o++)
                    {
                        IntPtr outp;
                        if (Call(ad, 7, o, (IntPtr)(&outp)) < 0) break; // EnumOutputs
                        DXGI_OUTPUT_DESC desc;
                        if (Call(outp, 7, (IntPtr)(&desc)) >= 0 && desc.AttachedToDesktop != 0 && desc.Left == left && desc.Top == top)
                        {
                            adapter = ad; output = outp;
                            return true;
                        }
                        Marshal.Release(outp);
                    }
                    Marshal.Release(ad);
                }
            }
            finally { Marshal.Release(factory); }
        }

        private void Stop()
        {
            if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
            if (_dup != IntPtr.Zero) { Marshal.Release(_dup); _dup = IntPtr.Zero; }
            if (_context != IntPtr.Zero) { Marshal.Release(_context); _context = IntPtr.Zero; }
            if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
        }

        // The last pictures go too: a whole screen each, kept by nothing else once this is let go of.
        public void Dispose() { Stop(); _last = _shown = null; }

        // ---- COM calls by the vtable slot (no interface declarations needed) ----
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

        [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint nLevels, uint sdk, IntPtr* device, int* level, IntPtr* context);

        [StructLayout(LayoutKind.Sequential)] private struct DXGI_OUTDUPL_FRAME_INFO { public long LastPresentTime, LastMouseUpdateTime; public uint AccumulatedFrames; public int RectsCoalesced, ProtectedContentMaskedOut; public int PointerX, PointerY, PointerVisible; public uint TotalMetadataBufferSize, PointerShapeBufferSize; }
        [StructLayout(LayoutKind.Sequential)] private struct DXGI_OUTDUPL_DESC { public uint Width, Height, RefreshNum, RefreshDen, Format, ScanlineOrdering, Scaling, Rotation; public int DesktopImageInSystemMemory; }
        [StructLayout(LayoutKind.Sequential)] private struct DXGI_MAPPED_RECT { public int Pitch; public IntPtr pBits; }
        [StructLayout(LayoutKind.Sequential)] private struct D3D11_MAPPED_SUBRESOURCE { public IntPtr pData; public uint RowPitch, DepthPitch; }
        [StructLayout(LayoutKind.Sequential)] private struct D3D11_TEXTURE2D_DESC { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] private struct DXGI_OUTPUT_DESC { public fixed char DeviceName[32]; public int Left, Top, Right, Bottom; public int AttachedToDesktop, Rotation; public IntPtr Monitor; }

        /// <summary>
        /// The mouse pointer, drawn into a copy of the picture (duplication leaves it out). Each pointer
        /// shape is turned into colours with see-through edges once, by drawing it on black and on white.
        /// </summary>
        private static class Cursor
        {
            public static (int X, int Y, int W, int H, byte[] Bgra)? Current;
            private static IntPtr _shape;
            private static int _x = int.MinValue, _y;
            private static readonly Dictionary<IntPtr, (int Hx, int Hy, int W, int H, byte[] Bgra)> Shapes = new();

            /// <summary>
            /// Counts each change of where the pointer is or what it looks like. Shared by every screen's
            /// duplication, so each keeps the count it last saw rather than being told "moved" once.
            /// </summary>
            public static int Version;

            /// <summary>Reads where the pointer is (on the whole desktop) and what it looks like; Version goes up if that changed.</summary>
            public static void Look() { if (Moved()) Version++; }

            private static bool Moved()
            {
                var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero)
                {
                    bool was = Current != null;
                    Current = null; _shape = IntPtr.Zero;
                    return was;
                }
                if (ci.hCursor == _shape && ci.x == _x && ci.y == _y) return false;
                if (!Shapes.TryGetValue(ci.hCursor, out var s))
                {
                    if (Shapes.Count > 64) Shapes.Clear();
                    s = Render(ci.hCursor);
                    Shapes[ci.hCursor] = s;
                }
                _shape = ci.hCursor; _x = ci.x; _y = ci.y;
                Current = s.W == 0 ? null : (ci.x - s.Hx, ci.y - s.Hy, s.W, s.H, s.Bgra);
                return true;
            }

            /// <summary>Draws the pointer onto a picture of the screen whose top left corner is at left, top on the whole desktop.</summary>
            public static Frame DrawOn(Frame f, int left, int top)
            {
                if (Current is not var (px, py, w, h, src)) return f;
                int cx = px - left, cy = py - top;
                for (int y = 0; y < h; y++)
                {
                    int ty = cy + y;
                    if (ty < 0 || ty >= f.Height) continue;
                    for (int x = 0; x < w; x++)
                    {
                        int tx = cx + x;
                        if (tx < 0 || tx >= f.Width) continue;
                        int si = (y * w + x) * 4, di = (ty * f.Width + tx) * 4;
                        int a = src[si + 3];
                        if (a == 0) continue;
                        for (int c = 0; c < 3; c++) f.Pixels[di + c] = (byte)((src[si + c] * a + f.Pixels[di + c] * (255 - a)) / 255);
                    }
                }
                return f;
            }

            private static (int, int, int, int, byte[]) Render(IntPtr cursor)
            {
                int hx = 0, hy = 0;
                if (GetIconInfo(cursor, out var ii))
                {
                    hx = ii.xHotspot; hy = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                }
                const int size = 128; // big pointers too (accessibility sizes)
                byte[]? black = DrawOn(cursor, 0x000000, size), white = DrawOn(cursor, 0xFFFFFF, size);
                if (black == null || white == null) return (0, 0, 0, 0, Array.Empty<byte>());
                var bgra = new byte[size * size * 4];
                for (int i = 0; i < size * size; i++)
                {
                    int alpha = 255 - Math.Clamp((white[i * 4 + 1] - black[i * 4 + 1]), 0, 255);
                    bgra[i * 4 + 3] = (byte)alpha;
                    for (int c = 0; c < 3; c++) bgra[i * 4 + c] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, black[i * 4 + c] * 255 / alpha);
                }
                return (hx, hy, size, size, bgra);
            }

            private static byte[]? DrawOn(IntPtr cursor, int background, int size)
            {
                IntPtr screen = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, size, size);
                try
                {
                    IntPtr was = SelectObject(dc, bmp);
                    IntPtr brush = CreateSolidBrush(background);
                    var r = new RECT { Right = size, Bottom = size };
                    FillRect(dc, ref r, brush);
                    DeleteObject(brush);
                    DrawIconEx(dc, 0, 0, cursor, 0, 0, 0, IntPtr.Zero, 3);
                    SelectObject(dc, was);
                    var info = new BITMAPINFOHEADER { biSize = 40, biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
                    byte[] px = new byte[size * size * 4];
                    return GetDIBits(dc, bmp, 0, (uint)size, px, ref info, 0) == size ? px : null;
                }
                finally { DeleteObject(bmp); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen); }
            }

            [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }
            [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
            [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
            [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
            [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
            [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
            [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
            [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
            [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
            [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
            [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
            [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
            [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
            [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
            [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
            [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
            [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
        }
    }
}
