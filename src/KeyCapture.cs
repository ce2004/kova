using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Grabs the whole keyboard while controlling the remote PC: Windows key,
    /// Alt+Tab, the NVDA key, everything Windows lets a hook see. Ctrl+Alt+Del
    /// and Windows+L are the two it never sees; Windows keeps those for itself.
    ///
    /// Ctrl+Shift+Enter switches between the remote PC and this one. From this
    /// PC it only works while the TailRemote window has focus.
    ///
    /// The hook runs on its own thread with its own message loop, so a busy UI
    /// can never make Windows time the hook out. It is reinstalled each time
    /// remote control starts, which puts it ahead of NVDA's own hook.
    ///
    /// The mouse goes too while controlling a remote PC that takes it: a mouse hook on the same
    /// thread, only while controlling, swallows this PC's moves, buttons and wheel and sends them.
    /// This PC's pointer is parked in the middle of its screen meanwhile (so it never stops at an
    /// edge) and put back where it was afterwards.
    /// </summary>
    internal sealed class KeyCapture : IDisposable
    {
        private const int VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        private const uint WM_APP_REHOOK = 0x8001, WM_APP_MOUSE_OFF = 0x8002, WM_QUIT = 0x0012, WM_TIMER = 0x0113;
        private const int WH_MOUSE_LL = 14, LLMHF_INJECTED = 1;
        private const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205,
            WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C, WM_MOUSEHWHEEL = 0x20E;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT { public Native.POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

        [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
        [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);

        private readonly Native.HookProc _mouseProc;
        private IntPtr _mouseHook, _flushTimer;
        private Native.POINT _savedCursor, _center;
        private int _moveX, _moveY;
        private long _lastMove;
        private bool _away;

        private readonly Func<IntPtr> _ourWindow;
        private readonly Native.HookProc _proc; // kept alive for the hook's lifetime
        private readonly Thread _thread;
        private uint _threadId;
        private IntPtr _hook;
        private readonly ManualResetEventSlim _ready = new();

        private volatile Client? _client;
        private volatile bool _remote;
        private readonly HashSet<(uint Vk, bool Ext)> _held = new();
        private bool _swallowEnterUp, _enterHeld, _swallowEndUp;
        private const int VK_END = 0x23;

        /// <summary>Raised on the hook thread with true when remote control starts, false when it stops.</summary>
        public event Action<bool>? ModeChanged;
        /// <summary>Raised when Ctrl+Shift+Enter is pressed with no connection.</summary>
        public event Action? NotConnected;

        public bool Remote => _remote;

        public KeyCapture(Func<IntPtr> ourWindow)
        {
            _ourWindow = ourWindow;
            _proc = Callback;
            _mouseProc = MouseCallback;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Kova keyboard", Priority = ThreadPriority.Highest };
            _thread.Start();
            _ready.Wait();
        }

        public void SetClient(Client? c)
        {
            var old = _client;
            _client = c;
            if (c == null && _remote) Leave(announce: true, old);
            // Whether the remote PC takes the mouse is only known once it says what it can do, which can
            // come just after remote control resumed by itself on reconnecting: look again then.
            // Once per connection: switching back and forth to a PC in the background for months added one each time.
            if (c != null && !_watched.TryGetValue(c, out _))
            {
                _watched.Add(c, c);
                c.FeaturesKnown += () => { if (_remote && _client == c && _mouseHook == IntPtr.Zero) Native.PostThreadMessageW(_threadId, WM_APP_REHOOK, IntPtr.Zero, IntPtr.Zero); };
            }
        }

        // Connections already watched (weakly: a closed one is never kept alive by this).
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Client, Client> _watched = new();

        private void Loop()
        {
            _threadId = Native.GetCurrentThreadId();
            Hook();
            SetTimer(IntPtr.Zero, IntPtr.Zero, 250, IntPtr.Zero);
            _ready.Set();
            while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_APP_REHOOK) { Unhook(); Hook(); if (_remote) HookMouse(); else UnhookMouse(); }
                else if (msg.message == WM_APP_MOUSE_OFF) UnhookMouse();
                else if (msg.message == WM_TIMER && _flushTimer != IntPtr.Zero && msg.wParam == _flushTimer) FlushMove();
                else if (msg.message == WM_TIMER && _remote) CheckDesktop();
            }
            UnhookMouse();
            Unhook();
        }

        private void Hook() => _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandleW(null), 0);
        private void Unhook() { if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }

        /// <summary>On the hook thread: takes the mouse, if the remote PC takes it.</summary>
        private void HookMouse()
        {
            if (_mouseHook != IntPtr.Zero || _client is not { CanMouse: true }) return;
            Native.GetCursorPos(out _savedCursor);
            _center = new Native.POINT { X = Native.GetSystemMetrics(0) / 2, Y = Native.GetSystemMetrics(1) / 2 };
            Native.SetCursorPos(_center.X, _center.Y);
            _moveX = _moveY = 0;
            _mouseHook = Native.SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, Native.GetModuleHandleW(null), 0);
            _flushTimer = SetTimer(IntPtr.Zero, IntPtr.Zero, 15, IntPtr.Zero); // sends what is left of a move once the mouse stops
        }

        /// <summary>On the hook thread: gives the mouse back to this PC, the pointer where it was.</summary>
        private void UnhookMouse()
        {
            if (_mouseHook == IntPtr.Zero) return;
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            if (_flushTimer != IntPtr.Zero) KillTimer(IntPtr.Zero, _flushTimer);
            _flushTimer = IntPtr.Zero;
            _moveX = _moveY = 0;
            Native.SetCursorPos(_savedCursor.X, _savedCursor.Y);
        }

        /// <summary>Sends the distance moved so far as one move: a gaming mouse reports 1000 times a second, the line needs no more than about 125.</summary>
        private void FlushMove()
        {
            if (_moveX == 0 && _moveY == 0) return;
            if (_remote) _client?.SendMouse(Protocol.MouseMove, _moveX, _moveY);
            _moveX = _moveY = 0;
            _lastMove = Environment.TickCount64;
        }

        private unsafe IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0) return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
            var m = *(MSLLHOOKSTRUCT*)lParam;
            var c = _client;
            // Software on this PC moving the pointer (NVDA's mouse tracking, for one) stays here.
            if (!_remote || c == null || (m.flags & LLMHF_INJECTED) != 0) return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
            int msg = (int)wParam;
            if (msg == WM_MOUSEMOVE)
            {
                // Where the pointer would go, less where it is: the move, after this PC's own pointer speed.
                Native.GetCursorPos(out var now);
                _moveX += m.pt.X - now.X;
                _moveY += m.pt.Y - now.Y;
                if (now.X != _center.X || now.Y != _center.Y) Native.SetCursorPos(_center.X, _center.Y); // something else moved it: back to the middle
                if (Environment.TickCount64 - _lastMove >= 8) FlushMove();
                return 1;
            }
            FlushMove(); // a click lands where the pointer has got to
            short wheel = (short)(m.mouseData >> 16);
            switch (msg)
            {
                case WM_MOUSEWHEEL: c.SendMouse(Protocol.MouseWheel, wheel, 0); return 1;
                case WM_MOUSEHWHEEL: c.SendMouse(Protocol.MouseSideWheel, wheel, 0); return 1;
            }
            ushort vk = msg switch
            {
                WM_LBUTTONDOWN or WM_LBUTTONUP => Native.VkLeftButton,
                WM_RBUTTONDOWN or WM_RBUTTONUP => Native.VkRightButton,
                WM_MBUTTONDOWN or WM_MBUTTONUP => Native.VkMiddleButton,
                WM_XBUTTONDOWN or WM_XBUTTONUP => wheel == 1 ? Native.VkBackButton : Native.VkForwardButton,
                _ => 0,
            };
            if (vk == 0) return 1;
            bool up = msg is WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP or WM_XBUTTONUP;
            lock (_held)
            {
                // Pressed before remote control started: let go here, where it went down.
                if (up) { if (!_held.Remove((vk, false))) return Native.CallNextHookEx(_mouseHook, code, wParam, lParam); }
                else _held.Add((vk, false));
            }
            c.SendKey(vk, 0, up, false);
            return 1;
        }

        public void Dispose() => Native.PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        /// <summary>Starts or stops remote control, as if Ctrl+Shift+Enter were pressed.</summary>
        public void Toggle()
        {
            if (_remote) Leave(announce: true);
            else Enter();
        }

        private void Enter()
        {
            if (_client == null) { NotConnected?.Invoke(); return; }
            _remote = true;
            Native.PostThreadMessageW(_threadId, WM_APP_REHOOK, IntPtr.Zero, IntPtr.Zero);
            ModeChanged?.Invoke(true);
        }

        private void Leave(bool announce, Client? from = null)
        {
            _remote = false;
            Native.PostThreadMessageW(_threadId, WM_APP_MOUSE_OFF, IntPtr.Zero, IntPtr.Zero);
            var c = from ?? _client;
            lock (_held)
            {
                // Let go of everything the remote PC still thinks is held down.
                if (c != null) foreach (var (vk, ext) in _held) c.SendKey((ushort)vk, 0, true, ext);
                _held.Clear();
            }
            c?.ReleaseAll();
            if (announce) ModeChanged?.Invoke(false);
        }

        /// <summary>
        /// Control Alt Delete or locking this PC switches it to the secure desktop,
        /// where the hook sees nothing, not even the keys being let go. Whatever
        /// was held (Control and Alt, at least) is released on the remote PC, or
        /// they would stay down there.
        /// </summary>
        private void CheckDesktop()
        {
            IntPtr d = NativeService.OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
            bool ours = d != IntPtr.Zero && NativeService.DesktopName(d) == "Default";
            if (d != IntPtr.Zero) NativeService.CloseDesktop(d);
            if (ours) { _away = false; return; }
            if (_away) return;
            _away = true;
            var c = _client;
            lock (_held)
            {
                if (c != null) foreach (var (vk, ext) in _held) c.SendKey((ushort)vk, 0, true, ext);
                _held.Clear();
            }
            c?.ReleaseAll();
        }

        private static bool Down(int vk) => Native.GetAsyncKeyState(vk) < 0;

        private bool HeldRemote(uint a, uint b) => _held.Contains((a, false)) || _held.Contains((b, false)) || _held.Contains((b, true));

        private unsafe IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0) return Native.CallNextHookEx(_hook, code, wParam, lParam);
            var k = *(Native.KBDLLHOOKSTRUCT*)lParam;
            bool up = (k.flags & Native.LLKHF_UP) != 0;
            bool ext = (k.flags & Native.LLKHF_EXTENDED) != 0;

            // Keys typed by software on this PC (NVDA itself, for one) stay here.
            if ((k.flags & Native.LLKHF_INJECTED) != 0) return Native.CallNextHookEx(_hook, code, wParam, lParam);

            bool enterRepeat = false;
            if (k.vkCode == VK_RETURN)
            {
                enterRepeat = !up && _enterHeld;
                _enterHeld = !up;
            }
            if (k.vkCode == VK_RETURN && up && _swallowEnterUp) { _swallowEnterUp = false; return 1; }
            // Holding Ctrl+Shift+Enter must not flip back and forth with every repeat.
            if (enterRepeat && _swallowEnterUp) return 1;

            bool ctrl, shift, alt, win;
            lock (_held)
            {
                ctrl = Down(VK_CONTROL) || HeldRemote(0xA2, 0xA3);
                shift = Down(VK_SHIFT) || HeldRemote(0xA0, 0xA1);
                alt = Down(VK_MENU) || HeldRemote(0xA4, 0xA5);
                win = Down(VK_LWIN) || Down(VK_RWIN) || _held.Contains((VK_LWIN, true)) || _held.Contains((VK_RWIN, true));
            }
            bool toggle = k.vkCode == VK_RETURN && !up && ctrl && shift && !alt && !win;

            if (!_remote)
            {
                if (toggle && IsOurWindowFocused())
                {
                    _swallowEnterUp = true;
                    Enter();
                    return 1;
                }
                return Native.CallNextHookEx(_hook, code, wParam, lParam);
            }

            if (toggle)
            {
                _swallowEnterUp = true;
                Leave(announce: true);
                return 1;
            }

            // Ctrl+Alt+End stands for Ctrl+Alt+Del, which never reaches a hook (as in Remote Desktop).
            if (k.vkCode == VK_END && up && _swallowEndUp)
            {
                _swallowEndUp = false;
                lock (_held) _held.Remove((VK_END, true));
                return 1;
            }
            if (k.vkCode == VK_END && !up && ctrl && alt && !shift && !win && _client is Client sas)
            {
                _swallowEndUp = true;
                // Add first, then only send if this key-down was not already held: an OS auto-repeat
                // tick re-fires the same key-down while it is held, and Add returns false for it, so
                // SendSecureAttention() fires once per press rather than on every repeat.
                bool firstDown;
                lock (_held) firstDown = _held.Add((VK_END, true));
                if (firstDown) sas.SendSecureAttention();
                return 1;
            }

            var c = _client;
            if (c == null) return Native.CallNextHookEx(_hook, code, wParam, lParam);

            lock (_held)
            {
                if (up)
                {
                    // A key that went down before remote control started is
                    // released here, or this PC would think it is still held.
                    if (!_held.Remove((k.vkCode, ext))) return Native.CallNextHookEx(_hook, code, wParam, lParam);
                }
                else _held.Add((k.vkCode, ext));
            }
            c.SendKey((ushort)k.vkCode, (ushort)k.scanCode, up, ext);
            return 1;
        }

        private bool IsOurWindowFocused()
        {
            IntPtr fg = Native.GetForegroundWindow();
            return fg != IntPtr.Zero && Native.GetAncestor(fg, 3) == _ourWindow();
        }
    }
}
