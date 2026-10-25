using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KovaRelay
{
    /// <summary>
    /// The tray icon, through Shell_NotifyIcon directly rather than WinForms' NotifyIcon. WinForms registers its
    /// icon with the oldest notification behaviour and only handles mouse messages, so Enter or Space on the icon
    /// with the keyboard (Windows+B, arrows) is not reliably passed on. With NOTIFYICON_VERSION_4 Windows sends
    /// NIN_KEYSELECT for Enter and Space, NIN_SELECT for a click and WM_CONTEXTMENU for a right click, Shift+F10 or
    /// the Applications key, all of which are handled here. The menu is a plain Windows menu, which NVDA reads.
    /// </summary>
    internal sealed class TrayIcon : NativeWindow, IDisposable
    {
        private const int WM_USER = 0x0400, CallbackMessage = WM_USER + 77;
        private const int WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203, WM_NULL = 0;
        private const int NIN_SELECT = WM_USER, NIN_KEYSELECT = WM_USER + 1;
        private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
        private const int NIIF_INFO = 1, NIIF_WARNING = 2;
        private const int NOTIFYICON_VERSION_4 = 4;
        private const uint MF_STRING = 0, TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
        private const int CmdOpen = 1, CmdExit = 2;

        private readonly int _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        private readonly Icon _icon;
        private string _tip = "Kova Relay";
        private bool _added;
        private long _lastOpen;

        public event Action? OpenRequested;
        public event Action? ExitRequested;

        public TrayIcon(Icon icon)
        {
            _icon = icon;
            // A hidden top-level window (not message-only: those miss the TaskbarCreated broadcast after Explorer restarts).
            CreateHandle(new CreateParams { Caption = "Kova Relay tray" });
            Add();
        }

        public string Tip
        {
            get => _tip;
            set
            {
                string v = value.Length > 127 ? value[..127] : value;
                if (v == _tip) return;
                _tip = v;
                var d = Data(NIF_TIP | NIF_SHOWTIP);
                Shell_NotifyIconW(NIM_MODIFY, ref d);
            }
        }

        /// <summary>A Windows notification from the tray icon (a toast on Windows 10 and 11). No title: the text says it all.</summary>
        public void Notify(string text, bool warning = false)
        {
            var d = Data(NIF_INFO);
            d.szInfo = text.Length > 255 ? text[..255] : text;
            d.szInfoTitle = "";
            d.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
            Shell_NotifyIconW(NIM_MODIFY, ref d);
        }

        private void Add()
        {
            var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
            _added = Shell_NotifyIconW(NIM_ADD, ref d);
            d.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIconW(NIM_SETVERSION, ref d);
        }

        private NOTIFYICONDATAW Data(int flags) => new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = Handle,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon.Handle,
            szTip = _tip,
            szInfo = "",
            szInfoTitle = "",
        };

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == CallbackMessage)
            {
                int ev = (int)((long)m.LParam & 0xFFFF);
                switch (ev)
                {
                    case NIN_SELECT:
                    case NIN_KEYSELECT:
                    case WM_LBUTTONDBLCLK:
                        // Enter can arrive as two NIN_KEYSELECTs, and a double click brings a select with it: open once.
                        long now = Environment.TickCount64;
                        if (now - _lastOpen > 500) { _lastOpen = now; OpenRequested?.Invoke(); }
                        break;
                    case WM_CONTEXTMENU:
                        long w = (long)m.WParam;
                        ShowMenu((short)(w & 0xFFFF), (short)((w >> 16) & 0xFFFF));
                        break;
                }
                return;
            }
            if (m.Msg == _taskbarCreated && _taskbarCreated != 0) { _added = false; Add(); }
            base.WndProc(ref m);
        }

        private void ShowMenu(int x, int y)
        {
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                AppendMenuW(menu, MF_STRING, CmdOpen, "&Open");
                AppendMenuW(menu, MF_STRING, CmdExit, "E&xit");
                SetMenuDefaultItem(menu, CmdOpen, 0);
                // Needed so the menu closes when the user clicks or tabs elsewhere.
                SetForegroundWindow(Handle);
                int cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, Handle, IntPtr.Zero);
                PostMessageW(Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                if (cmd == CmdOpen) { _lastOpen = Environment.TickCount64; OpenRequested?.Invoke(); }
                else if (cmd == CmdExit) ExitRequested?.Invoke();
            }
            finally { DestroyMenu(menu); }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                var d = Data(0);
                if (_added) Shell_NotifyIconW(NIM_DELETE, ref d);
                _added = false;
                DestroyHandle();
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessageW(string name);
        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenuW(IntPtr menu, uint flags, int id, string text);
        [DllImport("user32.dll")]
        private static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPosition);
        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    }
}
