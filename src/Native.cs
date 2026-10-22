using System;
using System.Runtime.InteropServices;

namespace TailRemote
{
    internal static class Native
    {
        // ---- Audio thread priority ----

        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
        [DllImport("avrt.dll")]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

        /// <summary>
        /// Releases the MMCSS handle a thread was given by ProAudioThread() when that thread
        /// actually terminates, instead of leaking it for the rest of the process. Nothing calls
        /// AvRevertMmThreadCharacteristics directly: the handle lives in a [ThreadStatic] field
        /// inside an object with a finalizer, so once the thread that owns it exits (and nothing
        /// else can be holding a live ThreadStatic slot for a dead thread), the sentinel becomes
        /// collectible and its finalizer reverts the handle on the next GC.
        /// </summary>
        private sealed class AvrtSentinel
        {
            public IntPtr Handle;
            ~AvrtSentinel() { if (Handle != IntPtr.Zero) try { AvRevertMmThreadCharacteristics(Handle); } catch { } }
        }

        [ThreadStatic] private static AvrtSentinel? _avrtSentinel;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);

        /// <summary>
        /// Opts this process out of Windows' power throttling (EcoQoS), which would
        /// otherwise put its audio and network threads on slow, power-saving cores
        /// and make the sound run dry. Also asks for 1 ms timer precision.
        /// </summary>
        public static void FullSpeed()
        {
            try
            {
                // Also IGNORE_TIMER_RESOLUTION (4) off: Windows 11 drops a minimized or hidden window's 1 ms timer
                // request (a host in the tray), and every short sleep (the H.264 event poll) then took 15 ms.
                var s = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1 /* EXECUTION_SPEED */ | 4 /* IGNORE_TIMER_RESOLUTION */, StateMask = 0 /* off */ };
                SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref s, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                timeBeginPeriod(1);
            }
            catch { }
        }

        /// <summary>
        /// Puts the calling thread in the "Pro Audio" scheduling class. The handle this hands the
        /// thread is released automatically once the thread ends (see AvrtSentinel above), so
        /// repeatedly promoting new threads (e.g. on every reconnect) does not leak a handle per call.
        /// </summary>
        public static void ProAudioThread()
        {
            uint i = 0;
            try
            {
                IntPtr h = AvSetMmThreadCharacteristicsW("Pro Audio", ref i);
                if (h != IntPtr.Zero) _avrtSentinel = new AvrtSentinel { Handle = h };
            }
            catch { }
        }

        // ---- Keyboard hook ----

        public const int WH_KEYBOARD_LL = 13;
        public const int LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? name);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

        // ---- Key injection ----

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        private const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2;

        /// <summary>Set by the service's agent: send keys to whichever desktop is showing, secure desktop included.</summary>
        public static bool FollowInputDesktop;

        [ThreadStatic] private static IntPtr _desktop, _originalDesktop;
        [ThreadStatic] private static string? _desktopName;

        /// <summary>Moves this thread to the desktop that has the keyboard (Default, Winlogon for the lock screen and UAC).</summary>
        private static void FollowDesktop()
        {
            IntPtr d = NativeService.OpenInputDesktop(0, false, 0x02000000 /* MAXIMUM_ALLOWED */);
            if (d == IntPtr.Zero) return;
            string name = NativeService.DesktopName(d);
            if (_desktop != IntPtr.Zero && name == _desktopName) { NativeService.CloseDesktop(d); return; }
            if (_originalDesktop == IntPtr.Zero) _originalDesktop = NativeService.GetThreadDesktop(GetCurrentThreadId()); // never closed
            if (NativeService.SetThreadDesktop(d))
            {
                if (_desktop != IntPtr.Zero) NativeService.CloseDesktop(_desktop);
                _desktop = d;
                _desktopName = name;
            }
            else NativeService.CloseDesktop(d);
        }

        /// <summary>This thread onto the desktop that has the keyboard now (the agent's screen pictures: the lock screen and UAC prompts too).</summary>
        public static void FollowInputDesktopNow() => FollowDesktop();

        /// <summary>Call before a thread that sent keys ends: puts it back on its own desktop and closes the one it followed.</summary>
        public static void LeaveDesktop()
        {
            if (_desktop == IntPtr.Zero) return;
            if (!NativeService.SetThreadDesktop(_originalDesktop)) return; // still in use: leave it
            NativeService.CloseDesktop(_desktop);
            _desktop = IntPtr.Zero;
            _desktopName = null;
        }

        /// <summary>
        /// A Windows tool's full path in System32 ("netsh" or "netsh.exe" gives C:\Windows\System32\netsh.exe).
        /// A bare name is looked for next to Kova.exe first, so a fake netsh.exe dropped in the
        /// Downloads folder beside it would have run as administrator in the elevated copies.
        /// </summary>
        public static string SystemTool(string name) =>
            System.IO.Path.IsPathRooted(name) ? name
            : System.IO.Path.Combine(Environment.SystemDirectory, System.IO.Path.HasExtension(name) ? name : name + ".exe");

        /// <summary>Runs a console program with no window; its exit code, or -1 if it would not start. Bare names are Windows' own tools, from System32.</summary>
        public static int RunHidden(string file, string args)
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SystemTool(file), args)
                    { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
                var errors = p.StandardError.ReadToEndAsync(); // both pipes are read, so a chatty program never blocks on a full one
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                errors.Wait();
                return p.ExitCode;
            }
            catch { return -1; }
        }

        /// <summary>Set by the service: it cannot reach the screen, so its agent in the signed-in session types the keys.</summary>
        public static Func<ushort, ushort, bool, bool, bool>? KeySink;

        // Mouse buttons travel as the keys Windows gives them (left, right, middle, back, forward): they are held
        // and let go like keys, so everything that releases held keys (disconnecting, a silent line, the
        // secure desktop) lets go of a held mouse button too, here, through the agent and in the tests.
        public const ushort VkLeftButton = 1, VkRightButton = 2, VkMiddleButton = 4, VkBackButton = 5, VkForwardButton = 6;
        public static bool IsMouseButton(ushort vk) => vk is VkLeftButton or VkRightButton or VkMiddleButton or VkBackButton or VkForwardButton;

        private const uint MOUSEEVENTF_MOVE = 1, MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4, MOUSEEVENTF_RIGHTDOWN = 8, MOUSEEVENTF_RIGHTUP = 0x10,
            MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_XDOWN = 0x80, MOUSEEVENTF_XUP = 0x100,
            MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;

        /// <summary>Mouse moves and wheels (Protocol.Mouse*): like KeySink, the service hands them to its agent.</summary>
        public static Func<byte, int, int, bool>? MouseSink;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

        private static bool SendMouseInput(uint flags, int dx = 0, int dy = 0, int data = 0)
        {
            var input = new INPUT { type = 0 };
            input.u.mi.dx = dx;
            input.u.mi.dy = dy;
            input.u.mi.mouseData = unchecked((uint)data);
            input.u.mi.dwFlags = flags;
            return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1;
        }

        /// <summary>
        /// A move (dx, dy in pixels, already shaped by the controlling PC's own mouse settings), a wheel
        /// turn (a = the amount, 120 a notch) or a sideways wheel turn. The move is placed exactly where the
        /// cursor is plus the distance: a relative move would put this PC's pointer speed on top of the
        /// other PC's, and the pointer would race away.
        /// </summary>
        public static bool SendMouse(byte kind, int a, int b)
        {
            if (MouseSink is { } sink) return sink(kind, a, b);
            if (FollowInputDesktop) FollowDesktop();
            switch (kind)
            {
                case Protocol.MouseMove:
                    {
                        // In real pixels: the service's agent is not DPI aware, and at 150% scaling every move
                        // went 1.5 times as far (and jumped between screens of different scaling).
                        IntPtr oldDpi = SetThreadDpiAwarenessContext(new IntPtr(-4)); // per monitor v2
                        try
                        {
                        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
                        int width = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)), height = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
                        if (!GetCursorPos(out var p)) return SendMouseInput(MOUSEEVENTF_MOVE, a, b); // the lock screen can refuse: move relative then
                        int x = Math.Clamp(p.X + a, left, left + width - 1), y = Math.Clamp(p.Y + b, top, top + height - 1);
                        // 0 to 65535 across the whole of every screen. Windows turns the number back into a pixel
                        // rounding down, so it is rounded up here: rounded to nearest, x 1, 2 and 6 landed a pixel
                        // short (measured), and a slow move could never creep away from the left or top edge.
                        int nx = (int)Math.Min(65535, ((long)(x - left) * 65536 + width - 1) / width);
                        int ny = (int)Math.Min(65535, ((long)(y - top) * 65536 + height - 1) / height);
                        return SendMouseInput(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
                        }
                        finally { if (oldDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(oldDpi); }
                    }
                case Protocol.MouseWheel: return SendMouseInput(MOUSEEVENTF_WHEEL, data: a);
                case Protocol.MouseSideWheel: return SendMouseInput(MOUSEEVENTF_HWHEEL, data: a);
                default: return false;
            }
        }

        private static bool SendMouseButton(ushort vk, bool up) => vk switch
        {
            VkLeftButton => SendMouseInput(up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN),
            VkRightButton => SendMouseInput(up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN),
            VkMiddleButton => SendMouseInput(up ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN),
            VkBackButton => SendMouseInput(up ? MOUSEEVENTF_XUP : MOUSEEVENTF_XDOWN, data: 1),
            _ => SendMouseInput(up ? MOUSEEVENTF_XUP : MOUSEEVENTF_XDOWN, data: 2),
        };

        public static bool SendKey(ushort vk, ushort scan, bool up, bool extended)
        {
            if (KeySink is { } sink) return sink(vk, scan, up, extended);
            if (FollowInputDesktop) FollowDesktop();
            if (IsMouseButton(vk)) return SendMouseButton(vk, up);
            var input = new INPUT { type = 1 };
            input.u.ki.wVk = vk;
            input.u.ki.wScan = scan;
            input.u.ki.dwFlags = (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
            return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1;
        }

        // ---- DPAPI ----

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

        /// <summary>DPAPI. machine: any process on this PC can open it (the service's settings, kept in an admin-only folder).</summary>
        public static byte[] Protect(byte[] data, bool protect, bool machine = false)
        {
            int flags = 1 | (machine ? 4 : 0); // UI forbidden, local machine
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var inBlob = new DATA_BLOB { cbData = data.Length, pbData = h.AddrOfPinnedObject() };
                bool ok = protect
                    ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, out var outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, out outBlob);
                if (!ok) throw new InvalidOperationException("DPAPI failed: " + Marshal.GetLastWin32Error());
                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, result.Length);
                LocalFree(outBlob.pbData);
                return result;
            }
            finally { h.Free(); }
        }
    }
}
