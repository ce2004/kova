using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// The service hosts from session 0, from the moment Windows starts it (before the sign-in
    /// screen, so the startup sound is heard). Keys must be typed on the screen that is showing,
    /// which a service cannot reach, so the service hands them to its agent in the signed-in
    /// session over this pipe, together with what goes onto the clipboard when no TailRemote
    /// window is open. Only SYSTEM can open it, and the agent types nothing until it has checked
    /// that the pipe really is SYSTEM's: anyone else could otherwise type as SYSTEM.
    /// </summary>
    internal static class AgentLink
    {
        private static string PipeName => Names.AgentPipe;
        // Service to agent: type a key, set the clipboard (text / files), and ask the agent to read its clipboard.
        private const byte KeyMessage = (byte)'K', MouseMessage = (byte)'M', TextMessage = (byte)'T', FilesMessage = (byte)'F', GetMessage = (byte)'G';
        // Agent to service: the clipboard it read, in answer to a GetMessage (text, or file paths).
        private const byte ClipTextMessage = (byte)'t', ClipFilesMessage = (byte)'f';
        // The remote screen, which only the agent can see: the service asks for a viewer's next picture
        // (i32 request, i32 viewer, u8 whole, u16 widest, u8 quality, u8 most a second, i32 H.264 kbit/s, u8 which screen) and the agent
        // answers (i32 request, u8 1 + the update, or u8 0: no picture to take). 'X' (i32 viewer): that
        // viewer has gone.
        private const byte VideoMessage = (byte)'V', VideoForgetMessage = (byte)'X', VideoReplyMessage = (byte)'v';
        // NVDA as text, which only the agent can read (its viewers are in the signed-in session): 'N' (u8 flags,
        // Protocol.NvdaSpeechFlag...) says what the controlling PCs want; the agent answers with 'n': u8 kind
        // (1 lines spoken, 2 braille, 3 a problem, empty when it is gone) and UTF-8.
        private const byte NvdaWantMessage = (byte)'N', NvdaTextMessage = (byte)'n';
        public const byte NvdaKindLines = 1, NvdaKindBraille = 2, NvdaKindProblem = 3;

        /// <summary>The agent did not have the picture ready in time: too much at this step, not "no picture" (Host steps down).</summary>
        public static readonly byte[] TooSlow = new byte[1];

        /// <summary>Test only (--chaostest): the test plays the service too, so the pipe is not SYSTEM's.</summary>
        internal static bool TestAnyOwner;

        private static void Write(Stream s, byte type, byte[] payload)
        {
            byte[] m = new byte[5 + payload.Length];
            m[0] = type;
            BitConverter.TryWriteBytes(m.AsSpan(1), payload.Length);
            payload.CopyTo(m, 5);
            s.Write(m);
            s.Flush();
        }

        // ================= The service's side =================

        public sealed class Server
        {
            // Keys are never waited on (the host types under its lock): queued, written by a thread of their own.
            private readonly BlockingCollection<(byte, byte[])> _out = new(4096);
            private volatile NamedPipeServerStream? _pipe;

            public Server()
            {
                new Thread(AcceptLoop) { IsBackground = true, Name = "Kova agent link" }.Start();
                new Thread(WriteLoop) { IsBackground = true, Name = "Kova agent link out", Priority = ThreadPriority.AboveNormal }.Start();
            }

            /// <summary>Hands a key to the agent; false if no agent is there (nobody's session yet, or it is starting).</summary>
            public bool Key(ushort vk, ushort scan, bool up, bool extended)
            {
                if (_pipe == null) return false;
                byte[] k = new byte[5];
                BitConverter.TryWriteBytes(k.AsSpan(0), vk);
                BitConverter.TryWriteBytes(k.AsSpan(2), scan);
                k[4] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
                return _out.TryAdd((KeyMessage, k));
            }

            /// <summary>Hands a mouse move or wheel turn to the agent (Native.SendMouse); false if no agent is there.</summary>
            public bool Mouse(byte kind, int a, int b)
            {
                if (_pipe == null) return false;
                // The agent is behind (switching desktops): moves are dropped while the queue is half full, so
                // a key let go after them always has room. The next move puts the pointer right again.
                if (kind == Protocol.MouseMove && _out.Count > 2048) return false;
                byte[] m = new byte[9];
                m[0] = kind;
                BitConverter.TryWriteBytes(m.AsSpan(1), a);
                BitConverter.TryWriteBytes(m.AsSpan(5), b);
                return _out.TryAdd((MouseMessage, m));
            }

            public void Text(string text) { if (_pipe != null) _out.TryAdd((TextMessage, Encoding.UTF8.GetBytes(text))); }
            public void Files(string[] paths) { if (_pipe != null) _out.TryAdd((FilesMessage, Encoding.UTF8.GetBytes(string.Join("\n", paths)))); }

            /// <summary>Asks the agent to read its clipboard and send it back; false if no agent is there.</summary>
            public bool GetClipboard() => _pipe != null && _out.TryAdd((GetMessage, Array.Empty<byte>()));

            // Each with the agent it was sent to: one that went (a switch of user, a sign out) never answers, and
            // its waits end at once as "no picture", not 5 seconds later as "too slow" (which stepped the viewer's
            // quality down whenever the next agent had connected by then).
            private readonly ConcurrentDictionary<int, (System.Threading.Tasks.TaskCompletionSource<byte[]?> Wait, NamedPipeServerStream Pipe)> _videoWaits = new();

            /// <summary>Ends every picture wait sent to an agent other than this one (all of them for null).</summary>
            private void EndWaitsNotOn(NamedPipeServerStream? current)
            {
                foreach (var w in _videoWaits.Values) if (w.Pipe != current) w.Wait.TrySetResult(null);
            }
            private int _nextVideo;

            /// <summary>
            /// A viewer's next picture update, from the agent at the screen (ScreenVideo.Update): null if
            /// there is no agent (nobody's session yet), TooSlow if it did not answer within 5 seconds.
            /// </summary>
            public byte[]? Video(int viewer, bool whole, VideoSettings settings)
            {
                if (_pipe is not NamedPipeServerStream sentOn) return null;
                int id = Interlocked.Increment(ref _nextVideo);
                var wait = new System.Threading.Tasks.TaskCompletionSource<byte[]?>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                _videoWaits[id] = (wait, sentOn);
                try
                {
                    byte[] m = new byte[21];
                    BitConverter.TryWriteBytes(m.AsSpan(0), id);
                    BitConverter.TryWriteBytes(m.AsSpan(4), viewer);
                    m[8] = (byte)(whole ? 1 : 0);
                    BitConverter.TryWriteBytes(m.AsSpan(9), (ushort)settings.MaxWidth);
                    m[11] = (byte)settings.Quality;
                    m[12] = (byte)settings.Fps;
                    BitConverter.TryWriteBytes(m.AsSpan(13), settings.VideoKbps);
                    BitConverter.TryWriteBytes(m.AsSpan(17), settings.Screen); // the screen and how many asks (ScreenVideo.ChooseScreen)
                    if (!_out.TryAdd((VideoMessage, m))) return TooSlow;
                    return wait.Task.Wait(5000) ? wait.Task.Result : _pipe != sentOn ? null : TooSlow;
                }
                finally { _videoWaits.TryRemove(id, out _); }
            }

            // Viewers gone, kept apart from the bounded queue: a full queue (an agent busy switching desktops)
            // would drop one, and the agent would keep that viewer's copies of the screen and its encoder.
            private readonly ConcurrentQueue<int> _forgets = new();
            private const byte Wake = 0; // not a message: only wakes the writer to send the forgets

            public void ForgetVideo(int viewer)
            {
                if (_pipe == null) return; // no agent: the next one to connect forgets every viewer anyway
                _forgets.Enqueue(viewer);
                _out.TryAdd((Wake, Array.Empty<byte>())); // full: the writer is busy and sends them before its next one
            }

            private volatile byte _nvdaFlags;

            /// <summary>What of NVDA the controlling PCs want now; kept, and told again to each new agent (a sign in, a switch of user).</summary>
            public void Nvda(byte flags)
            {
                _nvdaFlags = flags;
                if (_pipe != null) _out.TryAdd((NvdaWantMessage, new[] { flags }));
            }

            /// <summary>NVDA's text from the agent (AgentLink.NvdaKind*, UTF-8). Raised on the link's read thread.</summary>
            public Action<byte, string>? NvdaText;

            /// <summary>What the agent read from its clipboard, in answer to GetClipboard. Raised on the link's read thread.</summary>
            public Action<string>? ClipboardText;
            public Action<string[]>? ClipboardFiles;

            /// <summary>
            /// The clipboard the agent sent back (GetClipboard's answer). One per connected agent: the
            /// pipe is now two-way (it was one-way, service to agent), so the service can pull as well
            /// as push. The old copy's thread ends when its pipe is disposed on the next connection.
            /// </summary>
            private void ReadLoop(NamedPipeServerStream pipe)
            {
                byte[] head = new byte[5], reply = new byte[5];
                try
                {
                    while (true)
                    {
                        Protocol.ReadExactly(pipe, head);
                        int n = BitConverter.ToInt32(head, 1);
                        if (n < 0 || n > 600 << 20) throw new InvalidDataException("a message too big");
                        if (head[0] == VideoReplyMessage && n >= 5)
                        {
                            // A picture: its 5 bytes of answer first, then the update read straight into an
                            // array of its own, rather than read whole and copied out again.
                            Protocol.ReadExactly(pipe, reply);
                            byte[] update = new byte[n - 5];
                            Protocol.ReadExactly(pipe, update);
                            if (_videoWaits.TryGetValue(BitConverter.ToInt32(reply, 0), out var wait))
                                wait.Wait.TrySetResult(reply[4] == 1 ? update : null);
                            continue;
                        }
                        byte[] p = new byte[n];
                        Protocol.ReadExactly(pipe, p);
                        switch (head[0])
                        {
                            case ClipTextMessage: ClipboardText?.Invoke(Encoding.UTF8.GetString(p)); break;
                            case ClipFilesMessage: ClipboardFiles?.Invoke(Encoding.UTF8.GetString(p).Split('\n', StringSplitOptions.RemoveEmptyEntries)); break;
                            case NvdaTextMessage when n >= 1 && n <= 1 << 20: NvdaText?.Invoke(p[0], Encoding.UTF8.GetString(p, 1, n - 1)); break;
                        }
                    }
                }
                catch { } // the agent went: its read thread ends, the accept loop takes the next one
                // Nobody is there any more until the next agent connects. Left set, keys looked typed and
                // every picture waited 5 seconds and came back as "too slow", stepping the viewer's
                // quality down after each sign in or switch of user (only a write noticed the agent had gone).
                if (Interlocked.CompareExchange(ref _pipe, null, pipe) == pipe)
                    try { pipe.Dispose(); } catch { }
                EndWaitsNotOn(_pipe);
            }

            private void AcceptLoop()
            {
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var sec = new PipeSecurity();
                sec.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
                bool asSystem;
                using (var me = WindowsIdentity.GetCurrent())
                {
                    asSystem = me.IsSystem;
                    if (asSystem) sec.SetOwner(system); // the agent checks this before typing anything
                    else sec.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow)); // the chaos test plays both halves
                }
                bool first = true, warned = false, warnedOwner = false;
                while (true)
                {
                    // Claimed before the attempt, win or lose: if Create throws (something else is
                    // squatting on the name at that moment), later retries must stop demanding
                    // FirstPipeInstance too, or they would keep failing forever even after the
                    // squatter is gone.
                    bool useFirst = first;
                    first = false;
                    NamedPipeServerStream? pipe = null;
                    try
                    {
                        // Two-way and asynchronous: the service writes keys and clipboard to the agent while a
                        // thread of its own reads the clipboard the agent sends back. On a plain pipe a waiting
                        // read holds up every write (as the service link found), so nothing would reach the agent.
                        pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous | (useFirst ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, sec);
                        // A later instance takes the rules of the name's first one, whoever made it. Not ours
                        // (a program squatted on the name first): its maker could connect here posing as the
                        // agent and read every key typed. Let it go and only take a pipe that really is the
                        // first of its name again.
                        if (asSystem && !ServiceLink.Server.OwnedBySystem(pipe))
                        {
                            pipe.Dispose();
                            if (!warnedOwner) ServiceHost.Log("Agent link: another program holds the pipe " + PipeName + "; waiting for it to go.");
                            warnedOwner = true;
                            first = true;
                            Thread.Sleep(1000);
                            continue;
                        }
                        pipe.WaitForConnection();
                        // The newest agent (the session now at the screen) takes over; anything queued for the old one goes.
                        var old = Interlocked.Exchange(ref _pipe, pipe);
                        try { old?.Dispose(); } catch { }
                        // A picture asked for and thrown away here would never be answered: its wait ends now.
                        while (_out.TryTake(out var gone))
                            if (gone.Item1 == VideoMessage && gone.Item2.Length >= 4 && _videoWaits.TryGetValue(BitConverter.ToInt32(gone.Item2, 0), out var w))
                                w.Wait.TrySetResult(null);
                        EndWaitsNotOn(pipe);
                        var linked = pipe;
                        new Thread(() => ReadLoop(linked)) { IsBackground = true, Name = "Kova agent link in" }.Start();
                        // A new agent knows nothing yet of what the controlling PCs want of NVDA.
                        if (_nvdaFlags != 0) _out.TryAdd((NvdaWantMessage, new[] { _nvdaFlags }));
                        ServiceHost.Log("The agent in the signed-in session is connected.");
                    }
                    catch (Exception e)
                    {
                        // A pipe that never got its agent (one that connected and went at once makes the wait
                        // throw) is let go: kept, it held one of the name's two places for good, and after
                        // that no agent could ever connect again until the service restarted.
                        if (pipe != null && pipe != _pipe) try { pipe.Dispose(); } catch { }
                        if (!warned) ServiceHost.Log("Agent link: " + e.Message + (useFirst ? " Another program may hold the pipe " + PipeName + "." : ""));
                        warned = true;
                        Thread.Sleep(1000);
                    }
                }
            }

            private void WriteLoop()
            {
                foreach (var (type, payload) in _out.GetConsumingEnumerable())
                {
                    while (_forgets.TryDequeue(out int viewer)) Send(VideoForgetMessage, BitConverter.GetBytes(viewer));
                    if (type != Wake) Send(type, payload);
                }
            }

            private void Send(byte type, byte[] payload)
            {
                var pipe = _pipe;
                if (pipe == null) return;
                try { Write(pipe, type, payload); }
                catch
                {
                    // The agent is gone (signed out, or switching user): until the next one connects, keys go nowhere.
                    Interlocked.CompareExchange(ref _pipe, null, pipe);
                    try { pipe.Dispose(); } catch { }
                }
            }
        }

        // ================= The agent's side =================

        /// <summary>
        /// Keeps connected to the service and does what it is handed. Never returns. getClipboard, when
        /// given, reads this session's clipboard and hands back text or file paths (on its own thread),
        /// which are written straight back to the service: the answer to a GetMessage.
        /// </summary>
        public static void Run(Action<ushort, ushort, bool, bool> key, Action<byte, int, int> mouse, Action<string> text, Action<string[]> files,
            Action<Action<string>, Action<string[]>>? getClipboard = null, ScreenVideo? screen = null, NvdaViewers? nvda = null)
        {
            byte[] head = new byte[5];
            // NVDA's text goes to whichever link is up now (none while the service restarts: dropped, it is live speech).
            Action<byte, byte[]>? nvdaTo = null;
            void NvdaOut(byte kind, string s)
            {
                byte[] utf = Encoding.UTF8.GetBytes(s);
                byte[] m = new byte[1 + utf.Length];
                m[0] = kind;
                utf.CopyTo(m, 1);
                Volatile.Read(ref nvdaTo)?.Invoke(NvdaTextMessage, m);
            }
            if (nvda != null)
            {
                nvda.Spoke += lines => NvdaOut(NvdaKindLines, string.Join("\n", lines));
                nvda.Braille += b => NvdaOut(NvdaKindBraille, b);
                nvda.Problem += why => NvdaOut(NvdaKindProblem, why ?? "");
            }
            // Pictures are taken on a thread of their own (below normal: keys first), one at a time, never
            // holding up the keys read here. It follows the desktop with the keyboard, like the keys.
            var pictures = new BlockingCollection<Action>(64);
            // The newest request for each viewer: an older one still queued (the service gave up waiting
            // on it) is skipped, so the agent never takes pictures nobody waits for.
            var newest = new ConcurrentDictionary<int, int>();
            // Viewers gone, and "every viewer gone" (a new link), kept out of the bounded queue: a full one
            // would drop them, and the viewer's copies of the screen and its encoder would stay until the
            // agent restarts. The picture thread does them before each picture, on its own thread, so they
            // never race a picture.
            var forgets = new ConcurrentQueue<int>();
            int forgetAll = 0, link = 0; // link: counts links, so a picture asked for on an old one is never taken for the new one
            void Forgets()
            {
                if (screen == null) return;
                if (Interlocked.Exchange(ref forgetAll, 0) == 1) screen.ForgetAll();
                while (forgets.TryDequeue(out int gone)) screen.Forget(gone);
            }
            var worker = new Thread(() =>
            {
                while (true)
                {
                    // Woken at least once a minute, so viewers whose "gone" never came are dropped even
                    // when nobody asks for pictures any more.
                    pictures.TryTake(out var job, 60_000);
                    try { Forgets(); } catch { }
                    try { job?.Invoke(); } catch { }
                    try { screen?.Sweep(); } catch { }
                }
            }) { IsBackground = true, Name = "Kova agent screen", Priority = ThreadPriority.BelowNormal };
            worker.Start();
            while (true)
            {
                try
                {
                    // Two-way and asynchronous, to match the service: reads come in while a clipboard answer goes back.
                    // Identification only: the service never needs to act as this agent, and a pipe made by
                    // someone else must not be able to act as SYSTEM through it.
                    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                    pipe.Connect(2000);
                    // Really the service: a pipe of this name made by anyone else (an ordinary user's
                    // program, before the service made its own) would be typing as SYSTEM.
                    var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                    if (owner?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true && !TestAnyOwner)
                    {
                        ServiceHost.Log("Agent: refused a pipe " + PipeName + " not owned by SYSTEM (owner " + owner + ").");
                        Thread.Sleep(5000);
                        continue;
                    }
                    // The clipboard answer is written from the reader's own thread (getClipboard runs later),
                    // never while the read loop below is also writing, so one lock keeps writes whole.
                    var writeGate = new object();
                    void Reply(byte type, byte[] payload) { try { lock (writeGate) Write(pipe, type, payload); } catch { } }
                    // A picture's answer: its header, then the update as it is, rather than copied into one
                    // message and then again into what is written.
                    void ReplyPicture(int id, byte[]? update)
                    {
                        byte[] h = new byte[10];
                        h[0] = VideoReplyMessage;
                        BitConverter.TryWriteBytes(h.AsSpan(1), 5 + (update?.Length ?? 0));
                        BitConverter.TryWriteBytes(h.AsSpan(5), id);
                        h[9] = (byte)(update == null ? 0 : 1);
                        try { lock (writeGate) { pipe.Write(h); if (update != null) pipe.Write(update); pipe.Flush(); } } catch { }
                    }
                    // A new link (the service may have restarted, numbering its viewers from 1 again): what
                    // was kept for the old viewers goes, on the picture thread so it never races a picture.
                    newest.Clear();
                    int thisLink = Interlocked.Increment(ref link);
                    if (screen != null) { Volatile.Write(ref forgetAll, 1); pictures.TryAdd(() => { }); }
                    if (nvda != null) { nvda.ReadSpeech = nvda.ReadBraille = false; } // until this service says what it wants
                    Volatile.Write(ref nvdaTo, Reply);
                    while (true)
                    {
                        Protocol.ReadExactly(pipe, head);
                        int n = BitConverter.ToInt32(head, 1);
                        // As big as the service may send: clipboard text up to 512 MB (FileChannel.MaxText). At
                        // 64 MB, bigger text broke the link (and the keys queued behind it) and never arrived.
                        if (n < 0 || n > 600 << 20) throw new InvalidDataException("a message too big");
                        byte[] p = new byte[n];
                        Protocol.ReadExactly(pipe, p);
                        switch (head[0])
                        {
                            case KeyMessage when n == 5: key(BitConverter.ToUInt16(p, 0), BitConverter.ToUInt16(p, 2), (p[4] & 1) != 0, (p[4] & 2) != 0); break;
                            case MouseMessage when n == 9: mouse(p[0], BitConverter.ToInt32(p, 1), BitConverter.ToInt32(p, 5)); break;
                            case TextMessage: text(Encoding.UTF8.GetString(p)); break;
                            case FilesMessage: files(Encoding.UTF8.GetString(p).Split('\n', StringSplitOptions.RemoveEmptyEntries)); break;
                            case GetMessage:
                                getClipboard?.Invoke(
                                    t => Reply(ClipTextMessage, Encoding.UTF8.GetBytes(t)),
                                    paths => Reply(ClipFilesMessage, Encoding.UTF8.GetBytes(string.Join("\n", paths))));
                                break;
                            case VideoMessage when n == 21 && screen != null:
                                {
                                    int id = BitConverter.ToInt32(p, 0), viewer = BitConverter.ToInt32(p, 4);
                                    bool whole = p[8] == 1;
                                    var settings = new VideoSettings(BitConverter.ToUInt16(p, 9), p[11], p[12], BitConverter.ToInt32(p, 13), BitConverter.ToInt32(p, 17));
                                    newest[viewer] = id;
                                    pictures.TryAdd(() =>
                                    {
                                        // Asked on a link that has since gone: taken now it would bring back a
                                        // viewer just forgotten (and the new link may give its number to another).
                                        if (Volatile.Read(ref link) != thisLink) return;
                                        // Not the newest ask, or the viewer has gone since (its "gone" is done
                                        // before this, so taking it now would bring the viewer back for good).
                                        if (!newest.TryGetValue(viewer, out int latest) || latest != id) return;
                                        ReplyPicture(id, screen.Update(viewer, whole, settings));
                                    });
                                    break;
                                }
                            case NvdaWantMessage when n == 1 && nvda != null:
                                nvda.ReadSpeech = (p[0] & Protocol.NvdaSpeechFlag) != 0;
                                nvda.ReadBraille = (p[0] & Protocol.NvdaBrailleFlag) != 0;
                                break;
                            case VideoForgetMessage when n == 4 && screen != null:
                                {
                                    int viewer = BitConverter.ToInt32(p, 0);
                                    newest.TryRemove(viewer, out _);
                                    forgets.Enqueue(viewer);
                                    pictures.TryAdd(() => { }); // wakes the picture thread; full: it is busy and does them before its next picture
                                    break;
                                }
                        }
                    }
                }
                catch
                {
                    Volatile.Write(ref nvdaTo, null);
                    if (nvda != null) nvda.ReadSpeech = nvda.ReadBraille = false; // nobody to send it to: piling up is never sent later
                    Thread.Sleep(500); // the service is starting or restarting: try again soon
                }
            }
        }
    }
}
