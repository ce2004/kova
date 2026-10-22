using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// NVDA's own Speech Viewer and Braille Viewer (NVDA menu, Tools), used without any NVDA add-on:
    /// the Speech Viewer shows every line NVDA speaks, the Braille Viewer what the braille display
    /// shows. On a host, Kova hides both windows completely and reads them, so a controlling PC can
    /// say the remote NVDA's speech through its own NVDA and show its braille.
    ///
    /// Hidden means: not shown (so not on screen, not in Alt Tab, never focused or read by NVDA) and
    /// moved far off the screen. The move matters: NVDA skips adding speech while the mouse pointer is
    /// over the viewer's area, even when it is hidden, and Kova itself moves the pointer when someone
    /// controls the mouse. NVDA only ever shows a viewer when it creates it (at its start, or from its
    /// menu), so a watcher hides each new one as it appears.
    ///
    /// The Speech Viewer's text is taken and then removed from it, so it never grows (NVDA would keep
    /// it all for as long as it runs). All messages are window messages Windows carries between
    /// processes itself, each with a time limit, so a busy NVDA never holds Kova up.
    ///
    /// Not covered: the lock screen and sign-in screen (NVDA there runs a separate secure copy, and its
    /// Speech Viewer clears and pauses while Windows is locked), NVDA's sounds and beeps, and voice
    /// changes such as pitch: only the words come through.
    /// </summary>
    internal sealed class NvdaViewers : IDisposable
    {
        /// <summary>Where hidden viewers are put: far outside any screen, so the mouse can never be over them.</summary>
        public const int OffX = -20000, OffY = -20000;

        private readonly Action<string>? _log;
        private readonly Thread _thread;
        private volatile bool _stop;
        private readonly AutoResetEvent _wake = new(false);

        /// <summary>New lines NVDA spoke, as they came in one look (raised on the watcher thread, only while ReadSpeech is set).</summary>
        public event Action<string[]>? Spoke;

        /// <summary>
        /// Why the speech cannot come (no Speech Viewer open in NVDA), raised 10 seconds after someone wants it and
        /// there is none; null once one is there again. Raised on the watcher thread, only when it changes.
        /// </summary>
        public event Action<string?>? Problem;
        public const string NoViewer = "The remote PC's NVDA has no Speech Viewer open, so its speech cannot come through. On that PC, open the NVDA menu, Tools, Speech Viewer, and check Show Speech Viewer on Startup in it. Kova keeps it hidden.";
        private string? _problem;
        private long _wantedSince;
        /// <summary>What the braille display shows now, as text (only while ReadBraille is set; raised when it changes).</summary>
        public event Action<string>? Braille;

        /// <summary>Set while some PC wants NVDA's speech: the Speech Viewer is read every 50 ms.</summary>
        public volatile bool ReadSpeech;
        public volatile bool ReadBraille;

        /// <summary>Test and diagnostics: how many times a viewer was hidden, and the windows found.</summary>
        public int Hidden;
        public volatile bool SawSpeechViewer, SawBrailleViewer;

        private IntPtr _speechWnd, _speechEdit, _brailleWnd, _brailleText;
        private string _lastBraille = "";

        /// <summary>
        /// desktop: the desktop to watch (the service's agent follows the input desktop for keys, but
        /// NVDA's viewers live on the normal one, "Default"); null keeps the thread's own.
        /// </summary>
        public NvdaViewers(Action<string>? log = null, string? desktop = null, bool testOnly = false)
        {
            _log = log;
            _testOnly = testOnly;
            _thread = new Thread(() => Run(desktop)) { IsBackground = true, Name = "Kova NVDA viewers", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
        }

        private void Run(string? desktop)
        {
            IntPtr desk = IntPtr.Zero;
            if (desktop != null)
            {
                // GENERIC_ALL is more than needed; these three are what finding and moving windows takes.
                desk = OpenDesktop(desktop, 0, false, 0x0001 | 0x0008 | 0x0040 | 0x0080 | 0x0100 /* read objects, hook control (the show hook), enumerate, write objects, switch */);
                if (desk != IntPtr.Zero && !SetThreadDesktop(desk)) { _log?.Invoke("NVDA viewers: could not join the " + desktop + " desktop"); }
            }
            long nextLook = 0;
            // Windows says the moment any window is shown: a viewer NVDA opens (at its start, or from its menu) is
            // hidden straight away rather than at the next look. Needs this thread to take its messages (below).
            _onShow = (hook, ev, hwnd, obj, child, thread, time) => { if (obj == 0 && child == 0) _lookNow = true; };
            IntPtr hookHandle = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _onShow, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            IntPtr[] handles = { _wake.SafeWaitHandle.DangerousGetHandle() };
            try
            {
                while (!_stop)
                {
                    bool reading = ReadSpeech || ReadBraille;
                    MsgWaitForMultipleObjects(1, handles, false, reading ? 50u : 250u, QS_ALLINPUT);
                    while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE)) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
                    if (_stop) break;
                    long now = Environment.TickCount64;
                    // Looked for (new) viewers four times a second as well: windows that went away (NVDA restarted, or
                    // the viewer was turned off) are let go, and a show Windows did not report is caught.
                    if (now >= nextLook || _lookNow)
                    {
                        _lookNow = false;
                        nextLook = now + 250;
                        if (!_testOnly) try { Look(); } catch (Exception e) { _log?.Invoke("NVDA viewers: " + e.Message); }
                    }
                    if (ReadSpeech && _speechEdit != IntPtr.Zero) try { ReadSpeechNow(); } catch (Exception e) { _log?.Invoke("NVDA speech: " + e.Message); }
                    else if (!ReadSpeech && _speechEdit != IntPtr.Zero) TrimSpeech(); // nobody listens: old speech is never sent later
                    if (ReadBraille && _brailleText != IntPtr.Zero) try { ReadBrailleNow(); } catch (Exception e) { _log?.Invoke("NVDA braille: " + e.Message); }
                    // Wanted for 10 seconds with no Speech Viewer (an NVDA restart takes a few): said once; and said again when one is back.
                    if (!ReadSpeech) _wantedSince = 0;
                    else if (_wantedSince == 0) _wantedSince = now;
                    string? problem = ReadSpeech && _speechWnd == IntPtr.Zero && now - _wantedSince >= 10_000 ? NoViewer
                        : _speechWnd != IntPtr.Zero ? null : _problem;
                    if (problem != _problem) { _problem = problem; try { Problem?.Invoke(problem); } catch { } }
                }
            }
            finally
            {
                if (hookHandle != IntPtr.Zero) UnhookWinEvent(hookHandle);
                if (desk != IntPtr.Zero) CloseDesktop(desk);
            }
        }

        // Tests only (--chaostest): never looks for NVDA's windows; lines come from TestSpeak.
        private readonly bool _testOnly;
        internal void TestSpeak(string[] lines) => Spoke?.Invoke(lines);

        private WinEventProc? _onShow; // kept: Windows calls it for as long as the hook is there
        private volatile bool _lookNow;

        /// <summary>Finds NVDA's viewers and hides any that show.</summary>
        private void Look()
        {
            if (_speechWnd != IntPtr.Zero && !IsWindow(_speechWnd)) { _speechWnd = _speechEdit = IntPtr.Zero; }
            if (_brailleWnd != IntPtr.Zero && !IsWindow(_brailleWnd)) { _brailleWnd = _brailleText = IntPtr.Zero; _lastBraille = ""; }
            foreach (var (wnd, kind, edits) in Find())
            {
                if (kind == 'S')
                {
                    if (wnd != _speechWnd) { _speechWnd = wnd; _speechEdit = edits[0]; SawSpeechViewer = true; _log?.Invoke("NVDA viewers: found the Speech Viewer"); }
                }
                else if (wnd != _brailleWnd)
                {
                    // Two lines: braille cells first, then the same as plain text (what Kova sends).
                    _brailleWnd = wnd; _brailleText = edits[^1]; SawBrailleViewer = true; _lastBraille = "";
                    _log?.Invoke("NVDA viewers: found the Braille Viewer");
                }
                Hide(wnd);
            }
        }

        /// <summary>Hidden and far off screen; done again only when NVDA moved or showed it.</summary>
        private void Hide(IntPtr wnd)
        {
            bool visible = IsWindowVisible(wnd);
            GetWindowRect(wnd, out RECT r);
            if (!visible && r.Left == OffX && r.Top == OffY) return;
            SetWindowPos(wnd, IntPtr.Zero, OffX, OffY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_HIDEWINDOW | SWP_ASYNCWINDOWPOS);
            Interlocked.Increment(ref Hidden);
            if (visible) _log?.Invoke("NVDA viewers: hid a viewer");
        }

        /// <summary>NVDA's top-level viewer windows on this desktop: S = Speech Viewer, B = Braille Viewer, with their text boxes.</summary>
        public static List<(IntPtr Wnd, char Kind, IntPtr[] Edits)> Find()
        {
            var found = new List<(IntPtr, char, IntPtr[])>();
            // Which processes are NVDA, kept between looks (four a second for months): a process is asked once, and the
            // list starts over each minute, so a number Windows gives to a new process is asked again.
            var nvda = NvdaPids;
            lock (nvda)
            {
                if (Environment.TickCount64 - _pidsAt > 60_000) { nvda.Clear(); _pidsAt = Environment.TickCount64; }
            }
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                bool isNvda;
                lock (nvda) { if (!nvda.TryGetValue(pid, out isNvda)) nvda[pid] = isNvda = IsNvda(pid); }
                if (!isNvda || ClassOf(h) != "wxWindowNR") return true;
                var edits = new List<IntPtr>();
                int boxes = 0;
                EnumChildWindows(h, (c, _) =>
                {
                    string cls = ClassOf(c);
                    if (cls.StartsWith("RICHEDIT", StringComparison.OrdinalIgnoreCase) || cls.StartsWith("RichEdit", StringComparison.Ordinal)) edits.Add(c);
                    else if (cls == "Button" && (GetWindowLong(c, GWL_STYLE) & 0x0F) is 2 or 3) boxes++; // BS_CHECKBOX, BS_AUTOCHECKBOX
                    return true;
                }, IntPtr.Zero);
                string title = TitleOf(h);
                // By title in English, and by shape in any language: the Speech Viewer is one text box and
                // one checkbox; the Braille Viewer two text boxes (cells, then text) and two checkboxes.
                if (title == "NVDA Speech Viewer" || (title.Length > 0 && edits.Count == 1 && boxes == 1 && !title.Contains("Log", StringComparison.OrdinalIgnoreCase)))
                { if (edits.Count >= 1) found.Add((h, 'S', edits.ToArray())); }
                else if (title == "NVDA Braille Viewer" || (edits.Count == 2 && boxes == 2))
                { if (edits.Count >= 2) found.Add((h, 'B', edits.ToArray())); }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static readonly Dictionary<uint, bool> NvdaPids = new();
        private static long _pidsAt;

        private static bool IsNvda(uint pid)
        {
            IntPtr p = OpenProcess(0x1000 /* query limited information */, false, pid);
            if (p == IntPtr.Zero) return false;
            try
            {
                var sb = new StringBuilder(1024);
                int n = sb.Capacity;
                if (!QueryFullProcessImageName(p, 0, sb, ref n)) return false;
                string name = System.IO.Path.GetFileName(sb.ToString());
                return name.Equals("nvda.exe", StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseHandle(p); }
        }

        /// <summary>Takes the complete lines NVDA added since the last time, and removes them from the viewer.</summary>
        private void ReadSpeechNow()
        {
            string? text = Take(_speechEdit, out _);
            if (text == null) return;
            var lines = new List<string>();
            foreach (string line in text.Split('\n', '\r'))
            {
                string l = line.Trim('\r', ' ');
                if (l.Length > 0) lines.Add(l);
            }
            if (lines.Count > 0) Spoke?.Invoke(lines.ToArray());
        }

        /// <summary>Nobody wants the speech: what piles up is removed, so it is never sent later.</summary>
        private void TrimSpeech() { try { Take(_speechEdit, out _); } catch { } }

        /// <summary>
        /// The text of the viewer up to its last line break, removed from the viewer, or null if there is
        /// none. Only that part is removed: a line NVDA adds between reading and removing stays for next
        /// time (each of NVDA's additions is a whole line ending in a line break, added in one go).
        /// </summary>
        // Normally every read empties the viewer of what it took, so it only ever holds the newest line or two and each
        // read is tiny, however long Kova runs (NVDA itself would keep every line it ever spoke). Should the removal
        // ever not work (Windows refused it), what was taken is skipped by place instead, and once the text passes
        // MaxKeep characters it is cleared outright: read first, so no line Kova has not sent is lost.
        private int _skip;          // characters (as WM_GETTEXT hands them out) already taken but still there
        public const int MaxKeep = 200_000; // about three thousand lines of speech
        /// <summary>Test and diagnostics: how many times the viewer was cleared outright.</summary>
        public int Cleared;

        private string? Take(IntPtr edit, out int consumed)
        {
            consumed = 0;
            if (SendMessageTimeout(edit, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out IntPtr lenP) == IntPtr.Zero) return null;
            int len = (int)lenP;
            if (len <= 0) { _skip = 0; return null; }
            if (len < _skip) _skip = 0; // cleared meanwhile (NVDA clears it when Windows locks)
            if (len > 4 << 20) len = 4 << 20;
            var sb = new StringBuilder(len + 1);
            if (SendMessageTimeout(edit, WM_GETTEXT, (IntPtr)(len + 1), sb, SMTO_ABORTIFHUNG, 500, out _) == IntPtr.Zero) return null;
            string all = sb.ToString();
            int from = Math.Min(_skip, all.Length);
            int end = all.LastIndexOfAny(new[] { '\r', '\n' });
            if (end < from) return null; // nothing new and whole yet
            if (all[end] == '\r' && end + 1 < all.Length && all[end + 1] == '\n') end++;
            string taken = all.Substring(from, end + 1 - from);
            string upTo = all.Substring(0, end + 1);
            // A rich edit box counts a line break as one character, though it may hand out "\r\n".
            int chars = upTo.Length - CountCrLf(upTo);
            SendMessageTimeout(edit, EM_SETSEL, IntPtr.Zero, (IntPtr)chars, SMTO_ABORTIFHUNG, 500, out _);
            SendMessageTimeout(edit, EM_REPLACESEL, IntPtr.Zero, new StringBuilder(""), SMTO_ABORTIFHUNG, 500, out _);
            // Did the removal work? Judged by what is there now, not by its length alone: NVDA may have added a line in the
            // meantime (it runs its additions between the messages sent here), which made a removal that worked look failed,
            // and the skip then cut the start off new lines. Only text that still begins with all that was taken (and is no
            // shorter) means it was refused.
            SendMessageTimeout(edit, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out IntPtr afterP);
            int after = (int)afterP;
            bool refused = false;
            if (after >= len)
            {
                var again = new StringBuilder(Math.Min(after, 4 << 20) + 1);
                SendMessageTimeout(edit, WM_GETTEXT, (IntPtr)(again.Capacity), again, SMTO_ABORTIFHUNG, 500, out _);
                refused = again.ToString().StartsWith(upTo, StringComparison.Ordinal);
            }
            if (!refused) _skip = 0; // removed, as it should be
            else
            {
                // Still there: skipped from now on; past the limit cleared outright, but only when nothing new has come
                // since it was read, so no line Kova has not sent is ever thrown away.
                _skip = end + 1;
                if (after > MaxKeep && after == len && all.Length == end + 1)
                {
                    SendMessageTimeout(edit, WM_SETTEXT, IntPtr.Zero, new StringBuilder(""), SMTO_ABORTIFHUNG, 500, out _);
                    _skip = 0;
                    Interlocked.Increment(ref Cleared);
                }
            }
            consumed = chars;
            return taken;
        }

        private static int CountCrLf(string s)
        {
            int n = 0;
            for (int i = 0; i + 1 < s.Length; i++) if (s[i] == '\r' && s[i + 1] == '\n') n++;
            return n;
        }

        private void ReadBrailleNow()
        {
            string? now = Text(_brailleText);
            if (now == null) return;
            now = now.TrimEnd(' ', '⠀', '\r', '\n');
            if (now == _lastBraille) return;
            _lastBraille = now;
            Braille?.Invoke(now);
        }

        /// <summary>A text box's whole text, or null if it would not answer.</summary>
        public static string? Text(IntPtr edit)
        {
            if (SendMessageTimeout(edit, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out IntPtr lenP) == IntPtr.Zero) return null;
            int len = Math.Min((int)lenP, 64 << 10);
            var sb = new StringBuilder(len + 1);
            if (SendMessageTimeout(edit, WM_GETTEXT, (IntPtr)(len + 1), sb, SMTO_ABORTIFHUNG, 500, out _) == IntPtr.Zero) return null;
            return sb.ToString();
        }

        private static string ClassOf(IntPtr h) { var sb = new StringBuilder(64); GetClassName(h, sb, 64); return sb.ToString(); }
        private static string TitleOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            SendMessageTimeout(h, WM_GETTEXT, (IntPtr)256, sb, SMTO_ABORTIFHUNG, 300, out _);
            return sb.ToString();
        }

        private const uint WM_SETTEXT = 0x000C, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, EM_SETSEL = 0x00B1, EM_REPLACESEL = 0x00C2;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const int GWL_STYLE = -16;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_HIDEWINDOW = 0x0080, SWP_NOOWNERZORDER = 0x0200, SWP_ASYNCWINDOWPOS = 0x4000;

        private const uint EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2, QS_ALLINPUT = 0x04FF, PM_REMOVE = 1;
        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint ms, uint mask);
        [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr p, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr OpenDesktop(string name, int flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool SetThreadDesktop(IntPtr desk);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desk);
    }
}
