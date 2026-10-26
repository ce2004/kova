using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace TailRemote
{
    /// <summary>
    /// Send the clipboard and Send files between the two PCs, on up to 16 TCP connections
    /// ("lanes") of their own, so they never hold up a keystroke.
    ///
    /// Every lane has its own encryption keys (fresh random values from both PCs go into
    /// them) and its own two threads: one reads, encrypts and sends, one receives, decrypts
    /// and writes to disk, so a transfer uses every core. Files are cut into pieces that
    /// all lanes share, so several files, or several parts of one big file, travel at once.
    ///
    /// Nothing is lost when a connection breaks. The receiver confirms every piece once it
    /// is on disk; a piece not confirmed goes again on another lane. A lane that dies, or
    /// stalls for 10 seconds, is closed and reopened (the controlling PC dials; the
    /// host only answers), and the transfer carries on where it was. Even the main
    /// connection dropping does not stop it: the channel outlives it and its lanes join
    /// the next connection. Only a whole minute with nothing getting through gives up.
    /// The sender says "sent" only when the receiver has confirmed the whole batch, so the
    /// two PCs always agree on how it ended.
    ///
    /// Sending is paced (Rate): the controlling PC watches the audio path's ping and
    /// slows file data the moment it starts hurting the sound, then speeds back up. At low
    /// speeds fewer lanes and smaller pieces are used, so nothing queues up anywhere.
    /// A new batch replaces the one still going.
    /// </summary>
    internal sealed class FileChannel : IDisposable
    {
        // Wire messages, each one encrypted frame, on any lane:
        private const byte Ping = 0x60;     // nothing: this quiet lane still works
        private const byte Offer = 0x61;    // u32 id, u8 kind, u32 entries, u64 total, u32 first entry, [KindTo: u16 folder bytes, UTF-8 folder], then per entry: i64 length (-1: a folder), u16 path bytes, UTF-8 path
        private const byte Ready = 0x62;    // u32 id: the receiver has made the files; send the pieces
        private const byte Piece = 0x63;    // u32 id, u32 entry, i64 offset, then the bytes
        private const byte Got = 0x64;      // u32 id, u32 entry, i64 offset, u32 length: that piece is on disk
        private const byte Finished = 0x65; // u32 id, u8 outcome, UTF-8 why (for the sender to show)
        private const byte Stop = 0x66;     // u32 id, u8 outcome, UTF-8 why (for the receiver to show; empty: just stopped): the sender stopped it, or could not go on
        private const byte Ask = 0x67;      // u32 id: how did it end? (the answer was lost with a lane)
        private const byte NotSent = 0x68;  // u32 id, u8 kind, u8 outcome, u16 what bytes, UTF-8 what, UTF-8 why: the sender could not even start (nothing there any more, too much), so the receiver is not left waiting
        // u32 id: the receiver, hearing nothing for a while, asks whether the sender still has this transfer. A sender
        // that does stays quiet; one that does not (Kova there restarted, or crashed) answers with a Stop, so the
        // receiver does not wait a week holding its files open. (Added in 4.1.4; older copies never answer.)
        private const byte Have = 0x69;
        private const byte Note = 0x6A;     // u32 id, UTF-8 text: what the sender had to leave out (folders it could not open, links), for the receiver's result
        internal const byte KindFiles = 1, KindText = 2, KindDownloads = 3; // clipboard files, clipboard text, Send files
        internal const byte KindTo = 4; // Send files into a folder the controller chose in Get files (the offer carries it)
        private const byte EndedWell = 0, EndedBadly = 1, EndedStopped = 2;
        // Only ever on a Stop: stopped because something newer was sent. The receiver ends it as stopped, and
        // puts none of what arrived on the clipboard (the newer copy goes there).
        private const byte EndedReplaced = 3;

        /// <summary>The most lanes at once.</summary>
        public const int MaxLanes = 16;
        /// <summary>
        /// The most text a clipboard may carry: 64 MB (of UTF-8). Far more than anyone copies, and small enough
        /// that a hostile PC offering it cannot make this one hold gigabytes of text and copies of it.
        /// </summary>
        public const long MaxText = 64L << 20;
        /// <summary>The most files and folders in one batch (the receiver keeps a slot for each).</summary>
        public const int MaxEntries = 1_000_000;
        private const int GiveUpMs = 60_000, StallMs = 10_000, QuietMs = 12_000, PingMs = 3_000, OfferPartBytes = 1 << 20, HaveMs = 5_000;
        private long _haveAt; // when this side last asked its sender whether it still has the transfer (WatchLoop only)

        private static long Now => Environment.TickCount64;

        /// <summary>Text that arrived for the clipboard.</summary>
        public Action<string>? TextReceived;
        /// <summary>Files that arrived for the clipboard: the top-level files and folders, in the holding folder.</summary>
        public Action<string[]>? FilesReceived;
        /// <summary>How a transfer is going, either way; at most four times a second, and once at the end.</summary>
        public Action<Transfer>? Progress;
        /// <summary>Bytes a second this side may send right now (set from the audio ping).</summary>
        public Func<double>? Rate;
        /// <summary>
        /// May the other PC send files into a folder of its choosing here? Only the host sets it, true
        /// while that PC is controlling (never a listener); unset, such offers are refused.
        /// </summary>
        public Func<bool>? MayChooseFolder;

        /// <summary>
        /// Hosting as the service: the signed-in user, whose rights files sent into a chosen folder are
        /// made with (null from it: nobody is signed in, so refused). Unset in the window, which already
        /// runs as its user. Made as SYSTEM, they could go anywhere, System32 included.
        /// </summary>
        public Func<System.Security.Principal.WindowsIdentity?>? FolderUser;

        /// <summary>
        /// May clipboard text, clipboard files and Send files arrive here without this side asking for them
        /// (ExpectClipboard, ExpectFetch)? True: the other PC's user may send at any time (people at both PCs).
        /// False: a batch nobody here asked for is turned away before anything is written, and its sender
        /// told why. The host leaves it true (its controllers send to it); the controlling PC sets it from a setting.
        /// </summary>
        public bool AcceptUnasked { get; set; } = true;

        /// <summary>
        /// The controlling PC: clipboard files arrive on this PC's clipboard only when this side asked
        /// (ExpectClipboard) in the last 30 seconds; files sent unasked go to Downloads\Kova like Send files.
        /// Clipboard text always goes onto the clipboard (4.5.1, the owner's choice; 4.4.0 kept it aside).
        /// False (the host): everything for the clipboard goes there, as before.
        /// </summary>
        public bool ClipboardOnlyWhenAsked { get; set; }

        /// <summary>
        /// Text the other PC sent for the clipboard without being asked (ClipboardOnlyWhenAsked): the text, and
        /// the file in Downloads\Kova it was saved to (null if it could not be saved). Not put on the clipboard.
        /// </summary>
        public Action<string, string?>? UnaskedTextReceived;

        private readonly object _askGate = new();
        private long _clipboardAskedUntil; // under _askGate: until when one clipboard batch is expected
        private readonly List<(HashSet<string> Names, long Until)> _fetches = new(); // under _askGate: Get files asked for
        private long _toldUnaskedAt = long.MinValue / 2; // the last "turned away" said here (at most one every 10 s)
        private const int ClipboardAskMs = 30_000, FetchAskMs = 120_000;

        /// <summary>The user here pressed Get the remote PC's clipboard: one clipboard batch may come in the next 30 seconds.</summary>
        public void ExpectClipboard()
        {
            lock (_askGate) _clipboardAskedUntil = Now + ClipboardAskMs;
        }

        /// <summary>
        /// The user here asked Get files for these paths on the other PC: a Send files batch whose top-level names
        /// are theirs (as the other PC names them: "D drive" for a whole drive, "name (2)" for a second of the same
        /// name) may come in the next 2 minutes. Once it has begun it carries on however long it takes.
        /// </summary>
        public void ExpectFetch(IEnumerable<string> remotePaths)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var given = remotePaths.Select(TopName).ToList();
            foreach (string n in given)
            {
                names.Add(n);
                // The sender names a second item of the same name "name (2)" and so on.
                for (int i = 2; i <= given.Count; i++) names.Add(Marked(n, " (" + i + ")"));
            }
            if (names.Count == 0) return;
            lock (_askGate)
            {
                _fetches.RemoveAll(f => f.Until < Now);
                if (_fetches.Count >= 16) _fetches.RemoveAt(0);
                _fetches.Add((names, Now + FetchAskMs));
            }
        }

        /// <summary>Was a clipboard batch asked for? Takes the request (one batch each).</summary>
        private bool TakeClipboardAsk()
        {
            lock (_askGate)
            {
                if (_clipboardAskedUntil == 0 || Now > _clipboardAskedUntil) return false;
                _clipboardAskedUntil = 0;
                return true;
            }
        }

        /// <summary>Was a Send files batch with these top-level names asked for (Get files)? Takes the request.</summary>
        private bool TakeFetchAsk(ICollection<string> firsts)
        {
            lock (_askGate)
            {
                _fetches.RemoveAll(f => f.Until < Now);
                int i = _fetches.FindIndex(f => firsts.All(f.Names.Contains));
                if (i < 0) return false;
                _fetches.RemoveAt(i);
                return true;
            }
        }

        /// <summary>Said to the sender of a batch turned away because nobody here asked for it.</summary>
        internal const string NotAsked = "The other PC did not ask for this, so it was not taken.";

        /// <summary>Said when the service cannot act for anyone: nobody is signed in on the host PC.</summary>
        public const string NobodySignedIn = "Nobody is signed in on the remote PC, so its files cannot be reached. Sign in there first.";

        /// <summary>The other PC's address, shown with each transfer.</summary>
        public volatile string PeerName = "";

        /// <summary>Identifies this channel to the host, so a new main connection gets the same transfers back.</summary>
        public readonly byte[] Id;
        /// <summary>Closed for good.</summary>
        public bool Gone => _closed;

        /// <summary>True while a batch is going either way (the client then measures its ping more often).</summary>
        public bool Busy => _out != null || (_in is In b && Now - b.LastActivity < 60_000); // a receiver waiting for its sender to come back is not busy
        /// <summary>
        /// Anything not yet ended, however long it has been waiting. Whether to keep a channel for a
        /// reconnecting PC goes by this, not Busy: a send paused over a minute ago is still not to
        /// be thrown away when the connection drops again before its next piece.
        /// </summary>
        public bool HasWork => _out != null || _in != null;

        public sealed class Transfer
        {
            private static int _serials;
            /// <summary>Tells transfers apart when they are passed on (the service's agent to the window).</summary>
            public readonly int Serial = Interlocked.Increment(ref _serials);
            /// <summary>The newest serial so far: a transfer with a higher one began after this was read.</summary>
            public static int Latest => Volatile.Read(ref _serials);
            public bool Outgoing, Finished, Failed;
            /// <summary>For the clipboard (Send clipboard), not Send files.</summary>
            public bool Clipboard;
            /// <summary>Stopped on purpose (Stop, or something newer sent), not a real failure.</summary>
            public bool Cancelled;
            /// <summary>No connection to the other PC right now: waiting for it to come back.</summary>
            public bool Waiting;
            /// <summary>
            /// Arriving: this side asked for it (ExpectClipboard, or ExpectFetch for Send files). False for anything
            /// the other PC sent of its own accord, and for everything going out.
            /// </summary>
            public bool Asked;
            /// <summary>The other PC's address (several transfers can go at once, one per controlling PC).</summary>
            public string Peer = "";
            public string What = "";
            public long Done, Total;
            /// <summary>Each file moving right now, with how far it has got (a fresh list each report).</summary>
            public IReadOnlyList<(string Name, long Done, long Total)> Files = Array.Empty<(string, long, long)>();
            public double BytesPerSecond;
            public string? Result;
            // Read once (another thread changes it), and at most a year: a stalled line's speed falls towards
            // nothing, and huge numbers of seconds made TimeSpan throw on the window's thread.
            public TimeSpan? Left { get { double bps = BytesPerSecond; long rest = Total - Done; return bps > 1 && rest > 0 ? TimeSpan.FromSeconds(Math.Min(rest / bps, 365.0 * 86400)) : null; } }
        }

        /// <summary>Where arriving clipboard files are held until pasted: %LOCALAPPDATA%\TailRemote\Clipboard.</summary>
        public static string Staging => StagingOverride ?? Path.Combine(Names.LocalDir, "Clipboard");
        /// <summary>The self-test receives into a temporary folder instead.</summary>
        public static string? StagingOverride;
        /// <summary>Where Send files puts what arrives: Downloads\TailRemote.</summary>
        public static string Downloads => DownloadsOverride ?? Path.Combine(MyDownloads.Value, "Kova");
        // Where Downloads really is (moved to another drive or OneDrive included), not just the profile's.
        private static readonly Lazy<string> MyDownloads = new(() =>
            NativeService.MyFolder(NativeService.FolderDownloads) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        public static string? DownloadsOverride;

        /// <summary>The keys' purpose for one lane: both PCs' fresh values for it, so no two lanes ever share keys.</summary>
        public static string LanePurpose(ReadOnlySpan<byte> hostValue, ReadOnlySpan<byte> clientValue) =>
            "files " + Convert.ToHexString(hostValue) + Convert.ToHexString(clientValue) + " ";

        private sealed class Lane
        {
            public required TcpClient Tcp;
            public required NetworkStream Stream;
            public required SecureLink Link;
            public int Slot;
            public readonly ConcurrentQueue<byte[]> Control = new();
            public readonly AutoResetEvent Wake = new(false);
            public long LastIn = Now, LastOut = Now;
            public volatile bool Dead;
            // Reused for every frame (each used by only one thread): never a new megabyte per piece.
            public byte[]? InBuf, OutFrame, PieceBuf;
        }

        private readonly object _lanesGate = new();
        private volatile Lane[] _lanes = Array.Empty<Lane>(); // by slot; replaced, never changed
        private volatile Func<(TcpClient Tcp, SecureLink Link)?>? _dial;
        private readonly AutoResetEvent _dialWake = new(false);
        private volatile bool _closed;
        private long _detachedAt;

        public FileChannel(byte[]? id = null)
        {
            Id = id ?? RandomNumberGenerator.GetBytes(16);
            _nextId = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
            new Thread(WatchLoop) { IsBackground = true, Name = "Kova files watch" }.Start();
            new Thread(DialLoop) { IsBackground = true, Name = "Kova files dial" }.Start();
        }

        /// <summary>The controlling PC only: opens one more lane to the host, or returns null.</summary>
        public Func<(TcpClient Tcp, SecureLink Link)?>? Dial
        {
            set { _dial = value; Interlocked.Exchange(ref _detachedAt, 0); if (value != null) _out?.Unpark(); _dialWake.Set(); }
        }

        /// <summary>
        /// The main connection is gone. The lanes stay (they may still work) and so do the
        /// transfers: a new connection picks them up, however long that takes. With nothing
        /// going, closed after a minute without one.
        /// </summary>
        public void Detach()
        {
            _dial = null;
            Interlocked.Exchange(ref _detachedAt, Now);
        }

        /// <summary>A kept channel taken back by its PC's new connection (host side, under the host's lock).</summary>
        internal void Reattached() => Interlocked.Exchange(ref _detachedAt, 0);

        /// <summary>
        /// Disconnect pressed: the transfers wait (their lanes close, so nothing moves) and carry
        /// on where they were on the next connection to the same PC. Only Stop ends them.
        /// </summary>
        public void Pause()
        {
            Detach();
            foreach (var l in _lanes) Kill(l);
            // The files being sent are let go while paused: an editor saving one (by writing a new copy and
            // moving it over the old) was refused while Kova held it. Reopened, and checked, on the way back.
            _out?.Park();
        }

        /// <summary>
        /// The PC that was sending here is gone for good (it came back as a new Kova, with a new channel):
        /// whatever was arriving from it ends now, kept as it is, instead of waiting a week with its files open.
        /// For the host to call on a kept channel; the controlling PC finds this out by itself (Have).
        /// </summary>
        public void SenderGone()
        {
            if (_in is In b)
                ThreadPool.QueueUserWorkItem(_ => EndIn(b, EndedBadly, NoLongerHas, NoLongerHas, null, tell: false));
        }

        private const string NoLongerHas = "The other PC no longer has this transfer (Kova there was probably restarted), so it cannot carry on. Send it again.";

        /// <summary>Closed on purpose: whatever is going is stopped, and the other PC told so.</summary>
        public void Dispose()
        {
            if (_closed) return;
            if (_out is Out o) EndOut(o, EndedStopped, "Stopped sending " + o.What + ": the connection was closed.", tell: true);
            var incoming = _in;
            _closed = true;
            _dial = null;
            _dialWake.Set();
            Lane[] lanes;
            lock (_lanesGate) lanes = _lanes; // after _closed is set: AddLane checks it under this lock, so no lane is missed
            // Off this thread: ending what comes in waits up to 2 seconds for writes still landing, and this is
            // called with the host's lock held (or from the window), where that would hold up keys or freeze
            // the window. Then a moment for the goodbyes to go out, then the lanes close.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (incoming is In b) try { EndIn(b, EndedStopped, "Stopped receiving " + b.T.What + ": the connection was closed.", "The other PC closed the connection.", null, tell: true); } catch { }
                Thread.Sleep(300);
                foreach (var l in lanes) Kill(l);
            });
        }

        // ---- Lanes ----

        /// <summary>How many lanes this speed can use: one per 2 MB/s, from 1 to 16.</summary>
        private int Wanted() => Rate?.Invoke() is double r && r > 0 ? (int)Math.Clamp(r / (2 << 20), 1, MaxLanes) : MaxLanes;

        public void AddLane(TcpClient tcp, SecureLink link)
        {
            if (_closed) { tcp.Dispose(); link.Dispose(); return; }
            tcp.NoDelay = true;
            // No timeouts: a lane that stops is found by the watch (nothing heard for 12 s, or
            // pieces not confirmed for 10 s) and closed, which ends any wait on it.
            tcp.SendTimeout = 0;
            tcp.ReceiveTimeout = 0;
            var stream = tcp.GetStream();
            stream.ReadTimeout = Timeout.Infinite;
            var lane = new Lane { Tcp = tcp, Stream = stream, Link = link };
            Lane? extra = null;
            lock (_lanesGate)
            {
                // Checked again under the lock Dispose takes its list of lanes under: a lane added just as the
                // channel closed was never closed, and kept its threads and connection for good.
                if (_closed) { tcp.Dispose(); link.Dispose(); return; }
                var list = _lanes.ToList();
                if (list.Count >= MaxLanes) { extra = list.OrderBy(l => l.LastIn).First(); list.Remove(extra); }
                for (int s = 0; ; s++) if (list.All(l => l.Slot != s)) { lane.Slot = s; break; }
                list.Add(lane);
                _lanes = list.OrderBy(l => l.Slot).ToArray();
            }
            if (extra != null) Kill(extra);
            _out?.Unpark(); // a lane joined: the send goes on, and may open its files again
            // _detachedAt is not cleared here: a lane arriving just after Detach would make a kept channel think it
            // was attached again, and it never closed. Reattaching is the Dial setter (controller) or Reattached (host).
            // Stops that may not have reached the other PC yet (sent while it was unreachable).
            lock (_stops) foreach (var (at, stop) in _stops) if (Now - at < 2 * GiveUpMs) lane.Control.Enqueue(stop);
            // Below normal: keys and sound run on their own threads at higher priority, so even a
            // transfer using every core can never hold up a keystroke or the audio.
            new Thread(() => ReadLoop(lane), 256 << 10) { IsBackground = true, Name = "Kova files in", Priority = ThreadPriority.BelowNormal }.Start();
            new Thread(() => WriteLoop(lane), 256 << 10) { IsBackground = true, Name = "Kova files out", Priority = ThreadPriority.BelowNormal }.Start();
            _out?.Changed.Set();
        }

        private void Kill(Lane lane)
        {
            lock (_lanesGate)
            {
                if (lane.Dead) return;
                lane.Dead = true;
                _lanes = _lanes.Where(l => l != lane).ToArray();
            }
            try { lane.Tcp.Dispose(); } catch { }
            // Its keys too, at once rather than whenever memory is next tidied: lanes come and go for years. Safe
            // while its own threads may still be in a send or receive: they fail, and end, as with the connection.
            try { lane.Link.Dispose(); } catch { }
            lane.Wake.Set();
            // Its pieces not confirmed yet go again, on the other lanes.
            if (_out is Out o)
            {
                lock (o.Gate)
                {
                    foreach (var p in o.InFlight.Values.Where(p => p.On == lane).ToList())
                    {
                        o.InFlight.Remove((p.Entry, p.Offset));
                        p.On = null;
                        o.Retry.Enqueue(p);
                    }
                    o.LaneUnacked.Remove(lane);
                    o.LaneHeard.Remove(lane);
                }
                foreach (var l in _lanes) l.Wake.Set();
                o.Changed.Set();
            }
            _dialWake.Set();
        }

        private void DialLoop()
        {
            int failures = 0;
            while (!_closed)
            {
                var dial = _dial;
                if (dial == null || _lanes.Length >= Wanted()) { _dialWake.WaitOne(500); continue; }
                (TcpClient Tcp, SecureLink Link)? lane = null;
                try { lane = dial(); } catch { }
                if (lane is { } l)
                {
                    failures = 0;
                    if (_dial == null || _closed) { l.Tcp.Dispose(); l.Link.Dispose(); }
                    else AddLane(l.Tcp, l.Link);
                }
                else _dialWake.WaitOne(Math.Min(250 << Math.Min(++failures, 3), 2000));
            }
        }

        /// <summary>Four times a second: closes lanes that went quiet or stalled, and gives up on what cannot finish.</summary>
        private void WatchLoop()
        {
            while (!_closed)
            {
                Thread.Sleep(250);
                if (!WatchOnce()) return;
            }
        }

        /// <summary>
        /// One look (a method of its own, so this thread never keeps a finished transfer, with its
        /// list of a million files, alive in a loop variable). False once the channel is closed.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private bool WatchOnce()
        {
            {
                long now = Now;
                foreach (var l in _lanes)
                    if (now - l.LastIn > QuietMs) { DiagLog.Write("files: a lane went quiet; closing it"); Kill(l); }
                if (_out is Out o)
                {
                    List<Lane> stalled;
                    lock (o.Gate)
                    {
                        stalled = o.LaneUnacked.Where(kv => kv.Value > 0 && now - o.LaneHeard[kv.Key] > StallMs).Select(kv => kv.Key).ToList();
                        // A safety net: a piece left on a lane that is gone is sent again, never waited for.
                        foreach (var p in o.InFlight.Values.Where(p => p.On == null || p.On.Dead).ToList())
                        {
                            o.InFlight.Remove((p.Entry, p.Offset));
                            if (p.On != null) { o.LaneUnacked.Remove(p.On); o.LaneHeard.Remove(p.On); }
                            p.On = null;
                            o.Retry.Enqueue(p);
                        }
                    }
                    foreach (var l in stalled) { DiagLog.Write("files: a lane stalled; closing it, its pieces go again"); Kill(l); }
                }
                // Nothing arriving: never given up on (it carries on when the other PC is back), but
                // the Files line says it is waiting rather than look stuck.
                if (_in is In b && b.Prepared && !b.Ended)
                {
                    bool waiting = now - b.LastActivity > 10_000;
                    if (waiting != b.T.Waiting) { b.T.Waiting = waiting; try { Progress?.Invoke(b.T); } catch { } }
                }
                // Lanes are up but nothing arrives: the sending PC may have restarted (lost power, crashed) and
                // come back without this transfer, and then only the receiver knows it exists. Asked every few
                // seconds; a sender that still has it says nothing, one that does not answers with a Stop.
                if (_in is In w && !w.Ended && now - w.LastActivity > HaveMs && now - _haveAt > HaveMs && _lanes is { Length: > 0 } up)
                {
                    _haveAt = now;
                    Tell(up[0], IdMessage(Have, w.Id));
                }
                // Detached with nothing going: nothing to carry on with, so closed after a minute.
                long d = Interlocked.Read(ref _detachedAt);
                if (d != 0 && now - d > GiveUpMs + 10_000 && _out == null && _in == null) { Dispose(); return false; }
                // Detached for a week: that PC is not coming back for it (a restarted Kova there has a new
                // channel). Closed so it holds no files, threads or rights for years; what arrived is kept
                // as "(incomplete)", as with any transfer that ends early.
                if (d != 0 && now - d > 7L * 24 * 3600_000) { Dispose(); return false; }
            }
            return true;
        }

        /// <summary>
        /// After a big transfer: memory it used goes back to Windows at once. .NET otherwise keeps
        /// it for itself, and the app looked as if it never let go until it was closed.
        /// </summary>
        private static void GiveBackMemory(int entries, long bytes)
        {
            if (entries < 1000 && bytes < (32L << 20)) return; // a small one used next to nothing
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(1000); // the window has shown the result; nothing is waiting on this
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            });
        }

        // The loops' steps are methods of their own: a variable in a long-lived loop inside a try
        // stays alive for the whole thread, and an idle lane then kept the last batch it sent
        // (clipboard text and all) in memory for good.
        private void WriteLoop(Lane lane)
        {
            try { while (!lane.Dead) if (!WriteOne(lane)) lane.Wake.WaitOne(200); }
            catch (Exception e) { DiagLog.Write("files: a lane could not send: " + e.Message); }
            Kill(lane);
        }

        /// <summary>Sends one thing on the lane; false if there was nothing to send.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private bool WriteOne(Lane lane)
        {
            if (lane.Control.TryDequeue(out var c)) { Send(lane, c); return true; }
            if (_out is Out o && NextPiece(o, lane) is PieceRef p) { SendPiece(o, lane, p); return true; }
            if (Now - lane.LastOut >= PingMs) Send(lane, new[] { Ping });
            // Nothing going: the big buffers go too (16 lanes would otherwise hold tens of MB for nothing).
            if (_out == null && (lane.PieceBuf?.Length > 64 << 10 || lane.OutFrame?.Length > 64 << 10)) { lane.PieceBuf = null; lane.OutFrame = null; }
            return false;
        }

        private static void Send(Lane lane, ReadOnlySpan<byte> m)
        {
            lane.Link.Send(lane.Stream, m, ref lane.OutFrame);
            lane.LastOut = Now;
        }

        private static void Tell(Lane lane, byte[] m)
        {
            lane.Control.Enqueue(m);
            lane.Wake.Set();
        }

        private long _pauseUntil;

        /// <summary>Test only (--chaostest): stops reading the network for a while, like a stalled PC.</summary>
        internal void TestPause(int ms) => Interlocked.Exchange(ref _pauseUntil, Now + ms);

        /// <summary>Test only (--chaostest): cuts every lane at once, as a broken connection would.</summary>
        internal void TestCutLanes() { foreach (var l in _lanes) Kill(l); }

        /// <summary>Test only: the most files being sent side by side at once.</summary>
        internal int TestMostActive;

        /// <summary>Test only: how many lanes are open.</summary>
        internal int TestLanes => _lanes.Length;

        private void ReadLoop(Lane lane)
        {
            try
            {
                while (!lane.Dead)
                {
                    while (Now < Interlocked.Read(ref _pauseUntil) && !lane.Dead) Thread.Sleep(20); // test only
                    ReceiveOne(lane);
                }
            }
            catch (Exception e) { DiagLog.Write("files: a lane closed: " + e.Message); }
            Kill(lane);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void ReceiveOne(Lane lane)
        {
            int n = lane.Link.Receive(lane.Stream, ref lane.InBuf);
            lane.LastIn = Now;
            if (n == 0) return;
            byte[] buf = lane.InBuf!;
            // A piece is written straight from the lane's own buffer; anything else is small, and copied.
            if (buf[0] == Piece)
            {
                if (n > 17) OnPiece(lane, buf, n);
                if (_in == null && buf.Length > 64 << 10) lane.InBuf = null; // that was the last of it: no big buffer kept
                return;
            }
            byte[] m = buf.AsSpan(0, n).ToArray();
            switch (m[0])
            {
                case Offer when m.Length >= 22: OnOffer(lane, m); break;
                case Ready when m.Length == 5: OnReady(BitConverter.ToUInt32(m, 1)); break;
                case Got when m.Length == 21: OnGot(lane, m); break;
                case Finished when m.Length >= 6:
                    if (_out is Out o && o.Id == BitConverter.ToUInt32(m, 1)) EndOut(o, m[5], Theirs(Said(m, 6, m.Length - 6)), tell: false);
                    break;
                case Stop when m.Length >= 6: OnStop(BitConverter.ToUInt32(m, 1), m[5], Said(m, 6, m.Length - 6)); break;
                case Ask when m.Length == 5: OnAsk(lane, BitConverter.ToUInt32(m, 1)); break;
                case NotSent when m.Length >= 9: OnNotSent(m); break;
                case Have when m.Length == 5: OnHave(lane, BitConverter.ToUInt32(m, 1)); break;
                case Note when m.Length >= 5:
                    lock (_inGate)
                    {
                        uint noteId = BitConverter.ToUInt32(m, 1);
                        // Room for Kova's own three sentences about what was left out; always said as "On the other PC, ...".
                        string said = Clean(Encoding.UTF8.GetString(m, 5, m.Length - 5), 2 * MaxSaid);
                        if (_in is In b && b.Id == noteId) b.Note = said;
                        else _note = (noteId, said); // comes just before its list: kept for the batch it belongs to
                    }
                    break;
            }
        }

        /// <summary>The receiver asks whether this side still sends that transfer: quiet if so, otherwise a Stop.</summary>
        private void OnHave(Lane lane, uint id)
        {
            if (_out is Out o && o.Id == id) return; // still going (or ending right now, and saying so itself)
            lock (_stops)
                foreach (var (_, stop) in _stops)
                    if (stop[0] == Stop && BitConverter.ToUInt32(stop, 1) == id) { Tell(lane, stop); return; } // how it really ended
            Tell(lane, OutcomeMessage(Stop, id, EndedBadly, NoLongerHas));
        }

        private static byte[] IdMessage(byte type, uint id)
        {
            byte[] m = new byte[5];
            m[0] = type;
            BitConverter.TryWriteBytes(m.AsSpan(1), id);
            return m;
        }

        private static byte[] FinishedMessage(uint id, byte outcome, string why) => OutcomeMessage(Finished, id, outcome, why);

        private static byte[] OutcomeMessage(byte type, uint id, byte outcome, string why)
        {
            byte[] w = Encoding.UTF8.GetBytes(why);
            byte[] m = new byte[6 + Math.Min(w.Length, 2000)];
            m[0] = type;
            BitConverter.TryWriteBytes(m.AsSpan(1), id);
            m[5] = outcome;
            w.AsSpan(0, m.Length - 6).CopyTo(m.AsSpan(6));
            return m;
        }

        // Words from the other PC, to be shown (and spoken) here: never more than a sentence or two, whatever it
        // sent, and nothing in them that could hide or reorder what NVDA reads out.
        private static string Said(byte[] m, int at, int bytes) => Clean(Encoding.UTF8.GetString(m, at, Math.Max(0, bytes)), MaxSaid);

        /// <summary>How much of the other PC's own words is shown: a hostile PC must not get to say a speech's worth.</summary>
        internal const int MaxSaid = 200;

        /// <summary>
        /// Text from the other PC made fit to show and speak: control characters, line breaks and invisible
        /// formatting characters (direction marks that make "fdp.exe" show as "exe.pdf") become spaces, and it is
        /// cut to max characters with "..." at the end.
        /// </summary>
        internal static string Clean(string s, int max)
        {
            var sb = new StringBuilder(Math.Min(s.Length, max + 8));
            bool space = false;
            foreach (char c in s)
            {
                bool odd = char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;
                char ch = odd || char.IsWhiteSpace(c) ? ' ' : c;
                if (ch == ' ' && (space || sb.Length == 0)) continue; // runs of spaces said once
                space = ch == ' ';
                sb.Append(ch);
                if (sb.Length > max) break;
            }
            string t = sb.ToString().TrimEnd();
            return t.Length > max ? Cut(t, max - 3).TrimEnd() + "..." : t;
        }

        /// <summary>
        /// The other PC's reason, as this side shows it: cleaned and cut short (Clean), and plainly its words. Kova's
        /// own reasons from there already begin by saying where they come from; anything else gets "The other PC
        /// said:" in front, so a hostile PC cannot make Kova itself seem to say something ("Kova: type your password").
        /// </summary>
        internal static string Theirs(string said)
        {
            string s = Clean(said, MaxSaid);
            if (s.Length == 0) return s;
            if (s is "Stopped." or "Something newer was sent.") return s;
            foreach (string from in new[] { "The other PC ", "The remote PC ", "Nobody is signed in on the remote PC", "Nothing to get: " })
                if (s.StartsWith(from, StringComparison.Ordinal)) return s;
            return "The other PC said: " + s;
        }

        // ---- Sending ----

        private sealed record Entry(string Rel, long Length, string? Full, byte[]? Data);

        private sealed class PieceRef
        {
            public readonly int Entry, Length;
            public readonly long Offset;
            public Lane? On;
            public PieceRef(int entry, long offset, int length) { Entry = entry; Offset = offset; Length = length; }
        }

        private sealed class Out
        {
            public required uint Id;
            public required byte Kind;
            public required string What;
            public required volatile List<Entry> Entries;
            public long Total;
            public string? Into; // KindTo: the folder on the other PC
            public System.Security.Principal.WindowsIdentity? AsUser; // opens files as this user (the service's agent)
            public readonly object Gate = new();
            public readonly ManualResetEventSlim Changed = new();
            public volatile bool Ready, Ended;
            public byte Outcome;
            public string Why = "";
            // Under Gate:
            public readonly Queue<PieceRef> Retry = new();
            public long[] Next = Array.Empty<long>();         // per file: the next offset to hand out
            public int Fresh;                                 // the first file not started yet
            public readonly HashSet<int> Open = new();         // started, with pieces still to hand out
            public readonly SortedSet<int> Active = new();     // started, not all confirmed (for the per-file progress)
            public readonly Dictionary<Lane, int> LaneFile = new(); // each lane keeps to its own file
            public readonly Dictionary<(int, long), PieceRef> InFlight = new();
            public readonly Dictionary<Lane, long> LaneUnacked = new(), LaneHeard = new();
            public long Acked;
            public long[] EntryAcked = Array.Empty<long>();
            public string? Note; // what the list had to leave out, said with the result on both PCs
            // Under Handles:
            public SafeFileHandle?[] Handles = Array.Empty<SafeFileHandle?>();
            public FileMeta?[] Seen = Array.Empty<FileMeta?>(); // per file: which file it was, and when last written, as first opened here
            private int _reading; // reads under way right now (a paused send closes its files only once none is)
            private volatile bool _parked;

            public void Read(int i, long offset, Span<byte> into)
            {
                var e = Entries[i];
                if (e.Data != null) { e.Data.AsSpan((int)offset, into.Length).CopyTo(into); return; }
                SafeFileHandle? h;
                lock (Handles)
                {
                    if (Ended) throw new OperationCanceledException();
                    // A lane that took its piece just before the pause cut it: the piece already went back to be
                    // sent again, and the file is not opened again until the send really goes on.
                    if (_parked) throw new ParkedException();
                    h = Handles[i];
                    if (h != null) _reading++;
                }
                if (h == null)
                {
                    // Opened outside the lock: a file on a share that stopped answering must hold up only this lane.
                    var open = AsUser == null ? OpenRead(e.Full!)
                        : System.Security.Principal.WindowsIdentity.RunImpersonated(AsUser.AccessToken, () => OpenRead(e.Full!));
                    // Opened again after a pause: what an editor saved meanwhile (often a whole new file put in its
                    // place) must not be sent on from the middle, after the start of the old one.
                    if (Edited(i, open) is string why) { open.Dispose(); throw new IOException(Path.GetFileName(e.Full) + why); }
                    lock (Handles)
                    {
                        if (Ended || _parked) { open.Dispose(); throw Ended ? new OperationCanceledException() : new ParkedException(); }
                        if (Handles[i] is SafeFileHandle other) { open.Dispose(); h = other; } // another lane opened it meanwhile
                        else Handles[i] = h = open;
                        _reading++;
                    }
                }
                try
                {
                    for (int got = 0; got < into.Length;)
                    {
                        int n = RandomAccess.Read(h, into[got..], offset + got);
                        if (n <= 0) throw new IOException(Path.GetFileName(e.Full) + " got shorter while it was being sent.");
                        got += n;
                    }
                    // Checked after every read: a file edited part-way (while the send was paused, say) arrived as
                    // a mix of the old and the new, and both PCs called it sent. A file that was not changed until
                    // all of it had been read went whole, as it was, and is fine.
                    if (Edited(i, h) is string changed) throw new IOException(Path.GetFileName(e.Full) + changed);
                }
                finally
                {
                    lock (Handles) { _reading--; if (_parked && _reading == 0) CloseHandles(); }
                }
            }

            /// <summary>
            /// Why the file can no longer be sent as listed (the words after its name), or null. Shorter than listed: cut.
            /// Another file than the one first opened (an editor saves by putting a new file in its place): changed,
            /// whatever its length. The same file at the listed length, written to since: saved over in place, and
            /// what was already sent is the old file. The same file, longer: only added to (a log still being
            /// written), so the listed length is still the same start of it, sent as it was. A file Windows will
            /// not describe is taken as it is (the reads say if it is gone).
            /// </summary>
            private string? Edited(int i, SafeFileHandle h)
            {
                if (Describe(h) is not FileMeta now) return null; // asked outside the lock: only the record below needs it
                FileMeta first;
                lock (Handles)
                {
                    if (i >= Seen.Length) return null; // the send ended and let go of its list
                    Seen[i] ??= now;
                    first = Seen[i]!.Value;
                }
                long listed;
                try { listed = Entries[i].Length; } catch { return null; }
                const string Changed = " was changed while it was being sent, so it was not sent. Send it again.";
                if (now.Length < listed) return " got shorter while it was being sent, so it was not sent. Send it again.";
                if (!now.SameFile(first)) return Changed;
                if (now.Length > listed) return null;
                return now.Written != first.Written ? Changed : null;
            }

            /// <summary>
            /// Paused: no file is opened again until it goes on (Unpark), and the open ones are closed, once no read is
            /// under way, off the caller's thread: Disconnect is pressed on the window's, and a lane can be holding
            /// the lock while it opens a file on a share that stopped answering.
            /// </summary>
            public void Park()
            {
                _parked = true;
                ThreadPool.QueueUserWorkItem(_ => { lock (Handles) if (_parked && _reading == 0) CloseHandles(); });
            }

            /// <summary>The send goes on: files may be opened again (and are checked when they are).</summary>
            public void Unpark() => _parked = false;

            private void CloseHandles()
            {
                for (int i = 0; i < Handles.Length; i++) { Handles[i]?.Dispose(); Handles[i] = null; }
            }

            private static SafeFileHandle OpenRead(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            public void Close(int i)
            {
                lock (Handles) { Handles[i]?.Dispose(); Handles[i] = null; }
            }

            public void CloseAll()
            {
                lock (Handles) for (int i = 0; i < Handles.Length; i++) { Handles[i]?.Dispose(); Handles[i] = null; }
            }

            public IEnumerable<byte[]> OfferParts()
            {
                int i = 0;
                do
                {
                    var part = new MemoryStream();
                    var w = new BinaryWriter(part);
                    w.Write(Offer); w.Write(Id); w.Write(Kind); w.Write(Entries.Count); w.Write(Total); w.Write(i);
                    if (Kind == KindTo)
                    {
                        // In every part: whichever part arrives first must say where it all goes.
                        byte[] into = Encoding.UTF8.GetBytes(Into!);
                        w.Write((ushort)into.Length);
                        w.Write(into);
                    }
                    for (; i < Entries.Count && part.Length < OfferPartBytes; i++)
                    {
                        byte[] path = Encoding.UTF8.GetBytes(Entries[i].Rel);
                        w.Write(Entries[i].Length);
                        w.Write((ushort)path.Length);
                        w.Write(path);
                    }
                    yield return part.ToArray();
                } while (i < Entries.Count);
            }
        }

        private readonly object _sendGate = new(), _latestGate = new();
        private volatile Out? _out;
        private Out? _latest;
        private uint _nextId;
        private readonly List<(long At, byte[] Message)> _stops = new(); // Stop messages, sent again on lanes that join soon after
        // Counts every request to send (and every Stop): a request still listing a big folder when a newer one,
        // or Stop, comes is dropped, never sent after it.
        private long _requests, _lastStop; // _lastStop: the count when Stop was last pressed

        /// <summary>
        /// Sends files and folders. toClipboard: onto the other PC's clipboard (Send clipboard), ready for
        /// Control V; otherwise into its Downloads\TailRemote (Send files). Blocking: run it off the
        /// window's thread. Replaces any batch still going. into: instead, into this folder on the other
        /// PC (a controller sending into the folder it has open in Get files; the host checks it).
        /// </summary>
        public void SendFiles(IReadOnlyList<string> items, bool toClipboard = true, System.Security.Principal.WindowsIdentity? asUser = null, string? into = null)
        {
            long ticket = Interlocked.Increment(ref _requests);
            byte kind = into != null ? KindTo : toClipboard ? KindFiles : KindDownloads;
            string what = items.Count == 1 ? TopName(items[0]) : items.Count + " items";
            if (into != null && (into.Length == 0 || Encoding.UTF8.GetByteCount(into) > ushort.MaxValue))
            {
                NotSending(kind, what, "Could not send " + what + ": the folder to send it into has a name too long to send.", "The other PC could not send " + what + ": the folder to send it into has a name too long.");
                return;
            }
            // The service's agent runs as SYSTEM: it lists and opens what the window's user asked for
            // as that user, so nothing they could not open themselves can be sent.
            List<Entry>? listed = null;
            string? problem = null, note = null;
            try
            {
                if (asUser != null) System.Security.Principal.WindowsIdentity.RunImpersonated(asUser.AccessToken, () => { (listed, note) = List(items); });
                else (listed, note) = List(items);
            }
            catch (Exception e) { problem = Plain(e.Message); }
            // Something newer was asked for, or Stop pressed, while a big folder was being listed: this one is not wanted.
            if (Interlocked.Read(ref _requests) != ticket) { Dropped(kind, what, ticket); return; }
            if (problem != null) { NotSending(kind, what, "Could not send " + what + ": " + problem, "The other PC could not send " + what + ": " + problem); return; }
            if (listed!.Count == 0)
            {
                // Never silence: a Get files on the other PC is waiting for this, and would say "getting" for good.
                string gone = items.Count == 1 ? what + " is not there any more" : "none of the " + items.Count + " items are there any more";
                NotSending(kind, what, "Nothing to send: " + gone + ".", "Nothing to get: " + gone + " on the other PC.");
                return;
            }
            SendBatch(kind, what, listed, ticket, asUser, into, note: note);
        }

        /// <summary>
        /// Refuses a request to send (the host's Get files, when it cannot act for anyone): said here, and the
        /// other PC told why, so it is not left waiting for files that will never come.
        /// </summary>
        public void Refuse(IReadOnlyList<string> items, string whyThere)
        {
            string what = items.Count == 1 ? TopName(items[0]) : items.Count + " items";
            NotSending(KindDownloads, what, "Did not send " + what + ": " + whyThere, whyThere);
        }

        /// <summary>The name an item goes by on the other PC: its own, or "D drive" for a whole drive (whose own name is blank).</summary>
        internal static string TopName(string item)
        {
            string name = Path.GetFileName(item.TrimEnd('\\', '/'));
            if (name.Length > 0) return name;
            string root = Path.GetPathRoot(item) ?? "";
            return root.Length >= 2 && root[1] == ':' && char.IsAsciiLetter(root[0]) ? char.ToUpperInvariant(root[0]) + " drive" : "drive";
        }

        /// <summary>
        /// The sender could not even start (nothing there any more, too much, a name too long): said here as a
        /// failed transfer, and the other PC told, so it shows the end too (a Get files there is waiting for it).
        /// </summary>
        private void NotSending(byte kind, string what, string why, string whyThere, byte outcome = EndedBadly)
        {
            DiagLog.Write("files: " + why);
            try { Progress?.Invoke(new Transfer { Outgoing = true, What = what, Clipboard = kind is KindFiles or KindText, Peer = PeerName, Finished = true, Failed = true, Cancelled = outcome == EndedStopped, Result = why }); } catch { }
            byte[] w = Encoding.UTF8.GetBytes(what.Length > 500 ? what[..500] : what), y = Encoding.UTF8.GetBytes(whyThere.Length > 2000 ? whyThere[..2000] : whyThere);
            byte[] m = new byte[9 + w.Length + y.Length];
            m[0] = NotSent;
            BitConverter.TryWriteBytes(m.AsSpan(1), Interlocked.Increment(ref _nextId));
            m[5] = kind;
            m[6] = outcome;
            BitConverter.TryWriteBytes(m.AsSpan(7), (ushort)w.Length);
            w.CopyTo(m, 9);
            y.CopyTo(m, 9 + w.Length);
            // The lanes may be coming back after a cut: a few seconds for one (this runs off the window's thread).
            // Kept like a Stop, so a lane that comes back later still carries it (the other PC knows each by its id).
            lock (_stops)
            {
                _stops.Add((Now, m));
                if (_stops.Count > 32) _stops.RemoveAt(0);
            }
            for (int i = 0; i < 40 && _lanes.Length == 0 && !_closed; i++) Thread.Sleep(250);
            foreach (var l in _lanes) Tell(l, m);
        }

        /// <summary>
        /// A request dropped before anything was offered (something newer came, or Stop): said on both PCs as
        /// stopped, so a Get files waiting on the other PC does not keep saying "getting". The clipboard
        /// replaced by a newer copy needs no word: nothing waits for it.
        /// </summary>
        private void Dropped(byte kind, string what, long ticket)
        {
            if (kind is KindText or KindFiles) return;
            if (Interlocked.Read(ref _lastStop) > ticket)
                NotSending(kind, what, "Stopped sending " + what + ".", "The other PC stopped sending " + what + ".", EndedStopped);
            else
                NotSending(kind, what, "Stopped sending " + what + ": something newer was sent.", "The other PC sent something newer, so " + what + " was stopped.", EndedStopped);
        }

        private void OnNotSent(byte[] m)
        {
            uint id = BitConverter.ToUInt32(m, 1);
            byte kind = m[5], outcome = m[6] == EndedStopped ? EndedStopped : EndedBadly;
            int n = BitConverter.ToUInt16(m, 7);
            if (9 + n > m.Length) return;
            string what = Said(m, 9, n), why = Said(m, 9 + n, m.Length - 9 - n);
            lock (_inGate)
            {
                if (_results.ContainsKey(id)) return; // already shown
                Remember(id, outcome, why);
            }
            DiagLog.Write("files: " + why);
            try { Progress?.Invoke(new Transfer { What = what, Clipboard = kind is KindFiles or KindText, Peer = PeerName, Finished = true, Failed = true, Cancelled = outcome == EndedStopped, Result = Theirs(why) }); } catch { }
        }

        /// <summary>
        /// Everything to send, and a sentence for what had to be left out (null if nothing was). Paths are used
        /// in their \\?\ form throughout: names a Mac or WSL made ("IMG.jpg.", "notes ") are otherwise quietly
        /// cut short by Windows to a name that is not there, and were left out, or sent under another name.
        /// </summary>
        private static (List<Entry> Entries, string? Note) List(IReadOnlyList<string> items)
        {
            var entries = new List<Entry>();
            var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var left = new LeftOut();
            foreach (string item in items)
            {
                string full = Long(item);
                bool dir = Directory.Exists(full);
                if (!dir && !File.Exists(full)) continue;
                // Two things with the same name (from different folders): the second becomes "name (2)".
                string name = TopName(item); // a whole drive's own name is blank, which no PC could make
                string root = name;
                for (int n = 2; !tops.Add(root); n++) root = Marked(name, " (" + n + ")");
                if (dir)
                {
                    entries.Add(new Entry(root, -1, full, null));
                    // A folder chosen itself that cannot be opened fails the send, as a locked file does: sent
                    // empty, it looked as if it had arrived.
                    if (Walk(full, root, entries, left) is Exception why)
                        throw new IOException(name + " could not be opened: " + (why is UnauthorizedAccessException ? "Windows did not allow this PC's user to look inside it."
                            : why is DirectoryNotFoundException ? "it is not there any more, or is a link to a place that is not there." : Plain(why.Message)));
                }
                else
                {
                    long length;
                    try
                    {
                        var info = new FileInfo(full);
                        length = (info.Attributes & FileAttributes.ReparsePoint) != 0 ? LinkedLength(full) : info.Length;
                    }
                    catch { tops.Remove(root); continue; } // gone since it was looked at: left out, like one in a folder
                    entries.Add(new Entry(root, length, full, null));
                }
            }
            var said = new List<string>();
            int n1 = left.Unopened, n2 = left.Links, n3 = left.Broken;
            if (n1 > 0) said.Add(n1 + (n1 == 1 ? " folder" : " folders") + " could not be opened (Windows did not allow it), so " + (n1 == 1 ? "it was" : "they were") + " left out.");
            if (n2 > 0) said.Add(n2 + (n2 == 1 ? " folder that is only a link to another place was" : " folders that are only links to other places were") + " left out, not followed.");
            if (n3 > 0) said.Add(n3 + (n3 == 1 ? " link to a file that is not there any more was" : " links to files that are not there any more were") + " left out.");
            return (entries, said.Count > 0 ? string.Join(" ", said) : null);
        }

        /// <summary>
        /// Adds everything inside a folder, folders before what is in them. Never follows a folder that is a link
        /// (a junction or symbolic link: it could lead anywhere, even back up into itself): counted too.
        /// Every file is taken whatever its marks, OneDrive's and other cloud files included (whole on this PC
        /// or not: reading one fetches it). A folder inside that cannot be opened is left out, counted. If the folder itself cannot be opened, why.
        /// </summary>
        private static Exception? Walk(string top, string topRel, List<Entry> entries, LeftOut left)
        {
            // By hand, one folder at a time, not Windows' own recursion: that skipped what it could not open in
            // silence. A stack, not recursion: a folder tree can be thousands deep.
            var todo = new Stack<(string Full, string Rel, int At)>();
            todo.Push((top, topRel, -1));
            var gone = new HashSet<int>(); // folders listed, then found not to open
            var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
            while (todo.Count > 0)
            {
                var (dir, rel, at) = todo.Pop();
                List<(string Name, FileAttributes Attributes, long Length)> inside;
                try
                {
                    inside = new System.IO.Enumeration.FileSystemEnumerable<(string, FileAttributes, long)>(dir,
                        (ref System.IO.Enumeration.FileSystemEntry f) => (f.FileName.ToString(), f.Attributes, f.Length), opts).ToList();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    if (at < 0) return e;
                    left.Unopened++;
                    gone.Add(at);
                    continue;
                }
                foreach (var (name, attributes, length) in inside)
                {
                    string full = Path.Join(dir, name), r = rel + "\\" + name;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            bool? link = IsLink(full, true);
                            if (link == true) { left.Links++; continue; }
                            if (link == null) { left.Unopened++; continue; } // cannot even tell what it is: never followed
                        }
                        todo.Push((full, r, entries.Count));
                        entries.Add(new Entry(r, -1, full, null));
                    }
                    else
                    {
                        long size = length;
                        // A link to a file: its listed size is the link's own (nothing), not the file's.
                        if ((attributes & FileAttributes.ReparsePoint) != 0 && IsLink(full, false) == true)
                            try { size = LinkedLength(full); } catch { left.Broken++; continue; } // leads nowhere: left out, and said
                        entries.Add(new Entry(r, size, full, null));
                    }
                }
            }
            if (gone.Count > 0)
            {
                // A folder that could not be opened goes from the list too: sent, it arrived empty, as if it were.
                var keep = new List<Entry>(entries.Count);
                for (int i = 0; i < entries.Count; i++) if (!gone.Contains(i)) keep.Add(entries[i]);
                entries.Clear();
                entries.AddRange(keep);
            }
            return null;
        }

        private sealed class LeftOut { public int Unopened, Links, Broken; }

        /// <summary>Windows' words about a path without the \\?\ in front that this side reads files by (said aloud, it is noise).</summary>
        internal static string Plain(string said) => said.Replace(@"\\?\UNC\", @"\\").Replace(@"\\?\", "");

        /// <summary>A junction or symbolic link (not a cloud file's or deduplication's own marks, which are real files and folders).</summary>
        private static bool? IsLink(string path, bool folder)
        {
            try { return (folder ? new DirectoryInfo(path) : (FileSystemInfo)new FileInfo(path)).LinkTarget != null; }
            catch { return null; } // cannot tell: a folder is then left out as one that could not be opened, never followed
        }

        /// <summary>The size of what a link to a file leads to.</summary>
        private static long LinkedLength(string path)
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return RandomAccess.GetLength(h);
        }

        /// <summary>
        /// The \\?\ form of a path, which Windows takes exactly as it is: no dot or space at the end of a name
        /// cut off, and no 260-character limit. One that is not a plain full path is made one first.
        /// </summary>
        internal static string Long(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
            string p = path.Replace('/', '\\');
            if (p.Length > 3) p = p.TrimEnd('\\');
            string rest = p.StartsWith(@"\\", StringComparison.Ordinal) ? p[2..] : p;
            // "." or ".." or a doubled separator would be taken literally after \\?\: tidied the usual way first
            // (which a name ending in a dot cannot be in, but such a path comes from Explorer already tidy).
            if (!Path.IsPathFullyQualified(p) || rest.Split('\\').Any(s => s.Length == 0 || s == "." || s == ".."))
                p = Path.GetFullPath(p);
            return p.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;
        }

        /// <summary>
        /// May a controlling PC's Get files (a folder to list, files to fetch) name this path? Only a plain path on one
        /// of this PC's drives: a letter, a colon and a backslash ("C:\", "D:\Folder\file.txt"). Never a share
        /// (\\server\share: the host, the service included, would sign in to that server as the signed-in user and hand
        /// it their password's hash), never a device or \\?\ path (\\?\GLOBALROOT reaches shadow copies and raw disks),
        /// never "C:folder" (relative to wherever the process happens to be), never a second colon (a file's hidden
        /// streams) or a wildcard.
        /// </summary>
        internal static bool RemotePathOk(string? path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 32_000) return false;
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
            if (path.IndexOf(':', 2) >= 0 || path.IndexOf('/') >= 0 || path.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|', '\0' }) >= 0) return false;
            if (path.Any(char.IsControl)) return false;
            // Nothing that could turn back into a share or a device once Windows tidies it ("C:\..\..\" stays on C:,
            // but a doubled backslash inside is refused rather than reasoned about).
            return path.IndexOf(@"\\", 2, StringComparison.Ordinal) < 0;
        }

        /// <summary>Sends clipboard text. Blocking. Replaces any batch still going.</summary>
        public void SendText(string text)
        {
            long ticket = Interlocked.Increment(ref _requests);
            SendBatch(KindText, "clipboard text", new List<Entry> { new("", 0, null, Encoding.UTF8.GetBytes(text)) }, ticket);
        }

        /// <summary>Test only (--chaostest): sends a batch with any paths at all, as a hostile PC could.</summary>
        /// <param name="checkCount">false: even more entries than this side would ever offer, to see the receiver refuse them.</param>
        internal void TestSendEntries(List<(string Rel, byte[]? Data)> entries, string what = "test", bool checkCount = true) =>
            SendBatch(KindFiles, what, entries.Select(e => new Entry(e.Rel, e.Data?.Length ?? -1, null, e.Data)).ToList(), Interlocked.Increment(ref _requests), checkCount: checkCount);

        /// <summary>
        /// Test only (--securitytest): offers one entry of any kind and length and sends nothing more, as a hostile
        /// PC could (a huge clipboard text that never comes, a file bigger than the free space).
        /// </summary>
        internal void TestOfferOnly(byte kind, string rel, long length)
        {
            var part = new MemoryStream();
            var w = new BinaryWriter(part);
            byte[] path = Encoding.UTF8.GetBytes(rel);
            w.Write(Offer); w.Write(Interlocked.Increment(ref _nextId)); w.Write(kind); w.Write(1); w.Write(Math.Max(0, length)); w.Write(0);
            w.Write(length); w.Write((ushort)path.Length); w.Write(path);
            if (_lanes is { Length: > 0 } lanes) Tell(lanes[0], part.ToArray());
        }

        /// <summary>Stops what is being sent and what is being received, if anything.</summary>
        public void CancelSending()
        {
            Interlocked.Exchange(ref _lastStop, Interlocked.Increment(ref _requests)); // a request still listing its folders is dropped too
            lock (_latestGate) _latest = null;
            if (_out is Out o) EndOut(o, EndedStopped, "Stopped sending " + o.What + ".", tell: true);
            // Off the window's thread: ending waits for writes still landing (see Dispose).
            if (_in is In b) ThreadPool.QueueUserWorkItem(_ => EndIn(b, EndedStopped, "Stopped receiving " + b.T.What + ".", "The other PC stopped receiving " + b.T.What + ".", null, tell: true));
        }

        private void SendBatch(byte kind, string what, List<Entry> entries, long ticket, System.Security.Principal.WindowsIdentity? asUser = null, string? into = null, bool checkCount = true, string? note = null)
        {
            // Refused here, before anything is offered: the other PC would refuse it anyway, and a name too
            // long for the list's two-byte length would arrive as a different, damaged list.
            if (checkCount && entries.Count > MaxEntries)
            {
                NotSending(kind, what, "Could not send " + what + ": it holds over a million files and folders, more than Kova can send in one go. Send what is in it a part at a time.",
                    "The other PC could not send " + what + ": it holds over a million files and folders, more than Kova can send in one go.");
                return;
            }
            if (entries.FirstOrDefault(e => Encoding.UTF8.GetByteCount(e.Rel) > ushort.MaxValue) is Entry tooLong)
            {
                string name = tooLong.Rel.Length > 80 ? tooLong.Rel[..80] + "..." : tooLong.Rel;
                NotSending(kind, what, "Could not send " + what + ": " + name + " has a name too long to send.", "The other PC could not send " + what + ": one of its names is too long to send.");
                return;
            }
            // Text's length is its bytes'.
            for (int i = 0; i < entries.Count; i++) if (entries[i].Data is byte[] d) entries[i] = entries[i] with { Length = d.Length };
            var o = new Out
            {
                Id = Interlocked.Increment(ref _nextId), Kind = kind, What = what, Entries = entries,
                Total = entries.Sum(e => Math.Max(0, e.Length)),
                AsUser = asUser, Into = into,
            };
            o.EntryAcked = new long[entries.Count];
            o.Next = new long[entries.Count];
            o.Handles = new SafeFileHandle?[entries.Count];
            o.Seen = new FileMeta?[entries.Count];
            o.Note = note;
            // Sending something new replaces what is still going; unless something newer still (or Stop) came
            // while this one was being got ready.
            bool replaced;
            lock (_latestGate)
            {
                replaced = Interlocked.Read(ref _requests) != ticket;
                if (!replaced) _latest = o;
            }
            if (replaced) { Dropped(kind, what, ticket); return; }
            if (_out is Out old) EndOut(old, EndedStopped, "Stopped sending " + old.What + ": something newer was sent.", tell: true,
                "The other PC sent something newer, so " + old.What + " was stopped.", replaced: true);
            lock (_sendGate)
            {
                lock (_latestGate) replaced = _latest != o; // replaced before it even started (said below, outside this lock)
                if (!replaced && !_closed)
                {
                    _out = o;
                    try { Run(o); }
                    finally
                    {
                        _out = null;
                        o.CloseAll();
                        int count = o.Entries.Count;
                        lock (o.Gate) o.Entries = new List<Entry>(); // lets go of the data (a lane still reading finds nothing, and stops)
                        lock (_latestGate) if (_latest == o) _latest = null; // never keeps what was sent (clipboard text can be 512 MB)
                        GiveBackMemory(count, o.Total);
                    }
                }
            }
            if (replaced) Dropped(kind, what, ticket);
        }

        private void Run(Out o)
        {
            var t = new Transfer { Outgoing = true, What = o.What, Total = o.Total, Clipboard = o.Kind is KindFiles or KindText, Peer = PeerName };
            var clock = Stopwatch.StartNew();
            double lastReport = 0, speed = 0, moving = 0;
            long lastDone = 0;
            Lane? offeredOn = null, askedOn = null;
            long askedAt = 0, allAt = 0;
            DiagLog.Write("files: sending " + o.What + ", " + o.Entries.Count + " entries, " + o.Total + " bytes");
            Progress?.Invoke(t);
            while (true)
            {
                o.Changed.Wait(100);
                o.Changed.Reset();
                if (o.Ended) break;
                long now = Now;
                var lanes = _lanes;
                if (!o.Ready)
                {
                    // The list of what is coming, again on another lane only if the first one died: the
                    // other PC may take a while over a list of a million files, and sending the whole
                    // list again every few seconds only piled up memory and slowed it further.
                    if (lanes.Length > 0 && (offeredOn == null || offeredOn.Dead))
                    {
                        offeredOn = lanes[0];
                        // Just ahead of the list, on the same lane: a batch with nothing in its files ends the moment
                        // its list arrives, and must already know what to say.
                        if (o.Note != null)
                        {
                            byte[] note = Encoding.UTF8.GetBytes(o.Note.Length > 1000 ? o.Note[..1000] : o.Note);
                            byte[] nm = new byte[5 + note.Length];
                            nm[0] = Note;
                            BitConverter.TryWriteBytes(nm.AsSpan(1), o.Id);
                            note.CopyTo(nm, 5);
                            offeredOn.Control.Enqueue(nm);
                        }
                        foreach (var part in o.OfferParts()) offeredOn.Control.Enqueue(part);
                        offeredOn.Wake.Set();
                    }
                }
                else if (Interlocked.Read(ref o.Acked) == o.Total)
                {
                    // Everything confirmed: the receiver's word on how it ended normally follows at
                    // once. If that was lost with a lane, ask.
                    if (allAt == 0) allAt = now;
                    if (lanes.Length > 0 && now - allAt > 3000 && (askedOn == null || askedOn.Dead || now - askedAt > 3000))
                    {
                        askedOn = lanes[0];
                        askedAt = now;
                        Tell(askedOn, IdMessage(Ask, o.Id));
                    }
                }
                // Never given up on: with no connection it waits (the Files line says so) and carries
                // on where it was when the connection comes back. Only Stop ends it.
                double s = clock.Elapsed.TotalSeconds;
                if (s - lastReport >= 0.25)
                {
                    long done = Interlocked.Read(ref o.Acked);
                    double dt = s - lastReport;
                    if (done > lastDone) moving += dt;
                    // The speed lately, not since the start: after a pause the time left would be far off.
                    double rate = (done - lastDone) / dt;
                    speed = speed == 0 ? rate : speed * 0.7 + rate * 0.3;
                    lastDone = done;
                    lastReport = s;
                    t.Done = done;
                    t.BytesPerSecond = speed;
                    t.Waiting = lanes.Length == 0;
                    lock (o.Gate) if (!o.Ended) t.Files = o.Active.Take(32).Select(i => (o.Entries[i].Rel, o.EntryAcked[i], o.Entries[i].Length)).ToArray();
                    Progress?.Invoke(t);
                }
            }
            t.Done = o.Outcome == EndedWell ? o.Total : Interlocked.Read(ref o.Acked); // the receiver has it all, whatever confirmations are still on their way
            t.BytesPerSecond = t.Done / Math.Max(0.001, moving > 0.5 ? moving : clock.Elapsed.TotalSeconds); // over the time it was moving, not any waiting
            t.Waiting = false;
            t.Files = Array.Empty<(string, long, long)>();
            if (o.Outcome == EndedWell)
                t.Result = "Sent " + o.What + (o.Kind == KindText ? "" : Summary(o.Entries.Select(e => (e.Rel, e.Length)))) + At(t) + "." + (o.Note != null ? " " + o.Note : "");
            else { t.Failed = true; t.Cancelled = o.Outcome == EndedStopped; t.Result = o.Why; }
            // Last: the window may be reading this transfer from an earlier report, and must never see it
            // finished without its result.
            Volatile.Write(ref t.Finished, true);
            DiagLog.Write("files: " + o.What + ": " + t.Result);
            Progress?.Invoke(t);
        }

        /// <summary>whyThere: what the receiver shows (null: that this PC stopped sending), so both PCs agree how it ended and why.</summary>
        private void EndOut(Out o, byte outcome, string why, bool tell, string? whyThere = null, bool replaced = false)
        {
            lock (o.Gate)
            {
                if (o.Ended) return;
                o.Ended = true;
                o.Outcome = outcome;
                o.Why = why;
            }
            o.CloseAll();
            if (tell)
            {
                var stop = OutcomeMessage(Stop, o.Id, replaced ? EndedReplaced : outcome, whyThere ?? "");
                lock (_stops)
                {
                    _stops.Add((Now, stop));
                    if (_stops.Count > 32) _stops.RemoveAt(0);
                }
                foreach (var l in _lanes) Tell(l, stop);
            }
            o.Changed.Set();
        }

        private void OnReady(uint id)
        {
            if (_out is not Out o || o.Id != id || o.Ready) return;
            o.Ready = true;
            o.Changed.Set();
            foreach (var l in _lanes) l.Wake.Set();
        }

        /// <summary>The next piece for this lane to send, or null (none left, this lane not needed at this speed, or its share already on the way).</summary>
        private PieceRef? NextPiece(Out o, Lane lane)
        {
            if (!o.Ready || o.Ended) return null;
            double rate = Rate?.Invoke() is double r && r > 0 ? r : double.PositiveInfinity;
            int wanted = Wanted();
            int rank = Array.IndexOf(_lanes, lane);
            if (rank < 0 || rank >= wanted) return null;
            // Pieces of about a sixteenth of a second, so pacing is smooth; never more than half a
            // second of data unconfirmed, so nothing queues up anywhere along the way.
            int size = (int)Math.Clamp(rate / 16, 16 << 10, 1 << 20);
            long window = (long)Math.Clamp(rate * 0.5 / wanted, 256 << 10, 32 << 20);
            lock (o.Gate)
            {
                if (o.Ended) return null; // checked again in here: an ended batch lets go of its list of files
                // A lane being cut sends its unconfirmed pieces back under this lock, after marking itself
                // dead. Checked here too: a piece taken by a lane already cut was never sent again, and
                // the whole transfer waited a minute and gave up.
                if (lane.Dead) return null;
                o.LaneUnacked.TryGetValue(lane, out long unacked);
                if (unacked > 0 && unacked + size > window) return null;
                PieceRef p;
                if (o.Retry.Count > 0) p = o.Retry.Dequeue();
                else
                {
                    // Each lane keeps to a file of its own, so many files move side by side, each on
                    // its own thread. With no new file left, it helps the one with the most to go
                    // (one big file is shared by every lane).
                    var entries = o.Entries;
                    int e = o.LaneFile.TryGetValue(lane, out int mine) && mine < entries.Count && o.Next[mine] < entries[mine].Length ? mine : -1;
                    if (e < 0)
                    {
                        while (o.Fresh < entries.Count && entries[o.Fresh].Length <= 0) o.Fresh++;
                        if (o.Fresh < entries.Count) e = o.Fresh++;
                        else
                        {
                            long most = 0;
                            foreach (int i in o.Open) if (entries[i].Length - o.Next[i] > most) { most = entries[i].Length - o.Next[i]; e = i; }
                        }
                        if (e < 0) return null;
                        o.LaneFile[lane] = e;
                    }
                    int n = (int)Math.Min(size, entries[e].Length - o.Next[e]);
                    p = new PieceRef(e, o.Next[e], n);
                    o.Next[e] += n;
                    if (o.Next[e] < entries[e].Length) o.Open.Add(e); else o.Open.Remove(e);
                    o.Active.Add(e);
                    if (o.Active.Count > TestMostActive) TestMostActive = o.Active.Count;
                }
                p.On = lane;
                if (unacked == 0) o.LaneHeard[lane] = Now;
                o.LaneUnacked[lane] = unacked + p.Length;
                o.InFlight[(p.Entry, p.Offset)] = p;
                return p;
            }
        }

        private void SendPiece(Out o, Lane lane, PieceRef p)
        {
            int size = 17 + p.Length;
            if (lane.PieceBuf == null || lane.PieceBuf.Length < size) lane.PieceBuf = new byte[Math.Max(size, 64 << 10)];
            byte[] m = lane.PieceBuf; // the lane's own, reused for every piece
            m[0] = Piece;
            BitConverter.TryWriteBytes(m.AsSpan(1), o.Id);
            BitConverter.TryWriteBytes(m.AsSpan(5), p.Entry);
            BitConverter.TryWriteBytes(m.AsSpan(9), p.Offset);
            try { o.Read(p.Entry, p.Offset, m.AsSpan(17, p.Length)); }
            catch (ParkedException) { return; } // paused: this lane was cut, and its piece already went back to be sent again
            catch (Exception e)
            {
                // A file locked, deleted or not allowed: the receiver is told it failed and why, not that it was stopped.
                string why = Plain(e.Message);
                if (!o.Ended) EndOut(o, EndedBadly, "Could not send " + o.What + ": " + why, tell: true, "The other PC could not send " + o.What + ": " + why);
                return;
            }
            // Paced: never faster than the controlling PC says the sound can bear. Short waits, so
            // a new rate or a stop is seen at once.
            for (int wait; (wait = PaceWait(size)) > 0;)
            {
                if (o.Ended || lane.Dead) return;
                Thread.Sleep(wait);
            }
            if (o.Ended) return;
            Send(lane, m.AsSpan(0, size));
        }

        private void OnGot(Lane lane, byte[] m)
        {
            if (_out is not Out o || o.Id != BitConverter.ToUInt32(m, 1)) return;
            int e = BitConverter.ToInt32(m, 5);
            long offset = BitConverter.ToInt64(m, 9);
            bool all, entryDone;
            lock (o.Gate)
            {
                // Ended: nothing more to count, and its list of files may already be let go (emptied), where
                // looking up this file threw and closed a working lane for nothing.
                if (o.Ended) return;
                if (!o.InFlight.Remove((e, offset), out var p)) return; // already counted (sent twice)
                // Counted against the lane that sent it, which may not be the one the answer came back on
                // (a piece sent again elsewhere). A lane already cut has had its count cleared: never brought back.
                if (p.On is Lane on && !on.Dead)
                {
                    if (o.LaneUnacked.TryGetValue(on, out long u)) o.LaneUnacked[on] = u - p.Length;
                    o.LaneHeard[on] = Now;
                }
                o.Acked += p.Length;
                entryDone = (o.EntryAcked[e] += p.Length) == o.Entries[e].Length;
                if (entryDone) o.Active.Remove(e);
                all = o.Acked == o.Total;
            }
            if (entryDone) o.Close(e);
            lane.Wake.Set();
            if (all) o.Changed.Set();
        }

        private readonly object _bucketGate = new();
        private double _allowance;
        private long _bucketAt = Stopwatch.GetTimestamp();

        /// <summary>0 when this many bytes may go now (and takes them), otherwise how many ms to wait first.</summary>
        private int PaceWait(int bytes)
        {
            if (Rate?.Invoke() is not double rate || rate <= 0) return 0;
            lock (_bucketGate)
            {
                long now = Stopwatch.GetTimestamp();
                _allowance = Math.Min(_allowance + (now - _bucketAt) / (double)Stopwatch.Frequency * rate, rate * 0.05 + bytes);
                _bucketAt = now;
                if (_allowance >= bytes) { _allowance -= bytes; return 0; }
                return Math.Clamp((int)Math.Ceiling((bytes - _allowance) / rate * 1000), 1, 50);
            }
        }

        // ---- Receiving ----

        private sealed class In
        {
            public required uint Id;
            public required byte Kind;
            // The kind as the sender offered it: clipboard files nobody asked for are received as Send files
            // (Kind), and a list sent again must still be known as this batch's.
            public byte OfferKind;
            public bool Asked; // this side asked for it (ExpectClipboard, ExpectFetch)
            public bool Unasked; // for the clipboard, but not asked for, on a PC that keeps its clipboard (ClipboardOnlyWhenAsked)
            public required (string Rel, long Length)?[] Entries;
            public string? Into; // KindTo: the folder the controller chose, already checked
            public System.Security.Principal.WindowsIdentity? MakeAs; // from the service: made as this user (FolderUser); let go when the batch ends

            /// <summary>
            /// Every file operation in the batch's folder goes through here: as the signed-in user when the
            /// service receives (MakeAs), so nothing there is ever done with SYSTEM's rights, not even
            /// after a folder was swapped for a link to somewhere else while the batch waited.
            /// </summary>
            public T As<T>(Func<T> f) => MakeAs is { } u ? System.Security.Principal.WindowsIdentity.RunImpersonated(u.AccessToken, f) : f();
            public void As(Action f) { if (MakeAs is { } u) System.Security.Principal.WindowsIdentity.RunImpersonated(u.AccessToken, f); else f(); }
            public readonly object Gate = new();
            public readonly Transfer T = new() { What = "files" };
            public readonly Stopwatch Clock = new();
            public long LastActivity = Now;
            public int Have;
            public bool Prepared, Ended, Completing;
            public string? Folder;
            // A file with something in it is written under its "(incomplete)" name (Paths) and renamed to its
            // own (Finals) once all of it is there (Renamed), so a crash never leaves a cut-off file that looks whole.
            public string[] Paths = Array.Empty<string>(), Finals = Array.Empty<string>();
            public string?[] PartialRels = Array.Empty<string?>(); // the "(incomplete)" names, checked with the rest of the list
            public bool[] Renamed = Array.Empty<bool>();
            public readonly HashSet<int> Renaming = new(); // being renamed right now, by one lane only
            public int Busy; // lanes writing (or renaming) right now: the end waits for them before it looks at what is on disk
            public readonly List<string> Tops = new();
            public SafeFileHandle?[] Handles = Array.Empty<SafeFileHandle?>();
            public long[] Got = Array.Empty<long>();
            public HashSet<long>?[] Claimed = Array.Empty<HashSet<long>?>();
            // Clipboard text, in pieces of TextChunk bytes, each made only when data for it lands: an offer of 64 MB
            // that never sends a byte costs next to nothing.
            public byte[]?[]? Text;
            public readonly SortedSet<int> Active = new(); // files partly arrived (for the per-file progress)
            public readonly object[] Making = Enumerable.Range(0, 64).Select(_ => new object()).ToArray(); // a file being made holds one of these, not the whole batch
            public long Total, Done;
            public double LastReport, Speed, Moving; // speed lately, and the time data was really arriving
            public long LastDone;
            public long NameBytes; // the list's names so far, capped (MaxNameBytes) as they arrive
            public volatile string? Note; // from the sender: what it had to leave out
            public int Fixed; // names changed to ones Windows allows (Validate)
            public string? FixedFrom; // the first of them, to say
            public int FixedAt = -1; // and its entry, whose name in the end (Finals) is said: a clash can make it "name (2)"
            public int Defused; // files given ".txt" so Explorer cannot act on them just by showing the folder (Validate)
            public string? DefusedFrom;
            public int DefusedAt = -1;

            /// <summary>What the result adds: names changed here, and what the sender left out. Empty if nothing.</summary>
            public string Notes()
            {
                string s = "";
                if (Fixed > 0)
                {
                    string given = FixedAt >= 0 && FixedAt < Finals.Length && Finals[FixedAt] is string f ? Path.GetFileName(f)
                        : FixedAt >= 0 && Entries[FixedAt]?.Rel is string rel ? rel[(Math.Max(rel.LastIndexOf('\\'), rel.LastIndexOf('/')) + 1)..] : "";
                    s += " Some names had to change to suit Windows: \"" + FixedFrom + "\" is now \"" + given + "\"" + (Fixed > 1 ? ", and " + (Fixed - 1) + " more" : "") + ".";
                }
                if (Defused > 0)
                {
                    string given = DefusedAt >= 0 && DefusedAt < Finals.Length && Finals[DefusedAt] is string f ? Path.GetFileName(f)
                        : DefusedAt >= 0 && Entries[DefusedAt]?.Rel is string rel ? rel[(Math.Max(rel.LastIndexOf('\\'), rel.LastIndexOf('/')) + 1)..] : "";
                    s += " \"" + DefusedFrom + "\" was saved as \"" + given + "\"" + (Defused > 1 ? ", and " + (Defused - 1) + " more like it got .txt added" : "") +
                        ", because Windows can act on files like that just by showing the folder they are in. Remove the .txt only if you trust it.";
                }
                if (Note is { Length: > 0 } n) s +=" On the other PC, " + char.ToLowerInvariant(n[0]) + n[1..];
                return s;
            }

            public long Length(int i) => Entries[i]!.Value.Length;
        }

        private readonly object _inGate = new();
        private volatile In? _in;
        private readonly Dictionary<uint, (byte Outcome, string Why)> _results = new();
        private readonly Queue<uint> _resultOrder = new();
        private (uint Id, string Text)? _note; // a Note whose list has not arrived yet (under _inGate)

        /// <summary>How a batch ended, kept so a sender that missed the answer can ask again. Under _inGate.</summary>
        private void Remember(uint id, byte outcome, string why)
        {
            if (_results.ContainsKey(id)) return;
            _results[id] = (outcome, why);
            _resultOrder.Enqueue(id);
            while (_resultOrder.Count > 64) _results.Remove(_resultOrder.Dequeue());
        }

        private void OnOffer(Lane lane, byte[] m)
        {
            uint id = BitConverter.ToUInt32(m, 1);
            byte kind = m[5];
            int count = BitConverter.ToInt32(m, 6);
            int first = BitConverter.ToInt32(m, 18);
            if (count > MaxEntries)
            {
                // Refused out loud: dropped in silence, its sender waited for good.
                const string tooMany = "The other PC could not take this: too many files and folders in one go (over a million). Send them a part at a time.";
                lock (_inGate) Remember(id, EndedBadly, tooMany);
                Tell(lane, FinishedMessage(id, EndedBadly, tooMany));
                return;
            }
            if (count < 1 || first < 0 || first >= count) return;
            int start = 22;
            string? into = null, refusal = null;
            System.Security.Principal.WindowsIdentity? makeAs = null;
            if (kind is < KindFiles or > KindTo)
                // A kind this version does not know (from a newer PC): refused out loud, so its sender is
                // not left waiting, and nothing is written anywhere.
                refusal = "The other PC could not take this: its Kova is too old for it. Update Kova there.";
            else if (kind == KindTo)
            {
                if (m.Length < 24) return;
                int n = BitConverter.ToUInt16(m, 22);
                if (24 + n > m.Length) return;
                into = Encoding.UTF8.GetString(m, 24, n);
                start = 24 + n;
                // Only a controlling PC (never a listener, never this host's own sends coming back) may
                // choose where files go, and only into a folder that is really there. Asked out here:
                // the host's own lock is never taken inside this channel's (the host closes channels
                // while holding it).
                bool may = MayChooseFolder?.Invoke() == true;
                string? folder = null;
                if (!may) refusal = "The remote PC only takes files into a folder of your choosing from a PC controlling it.";
                else if (FolderUser != null && (makeAs = FolderUser()) == null) refusal = NobodySignedIn;
                else
                {
                    // Checked as that user too: as SYSTEM, the answer told whether another user's folder existed.
                    string? asked = into;
                    folder = makeAs is { } u ? System.Security.Principal.WindowsIdentity.RunImpersonated(u.AccessToken, () => Destination(asked)) : Destination(asked);
                    if (folder == null) refusal = "The remote PC did not take the files: " + into + " is not a folder there.";
                }
                into = folder;
            }
            // Hosting as the service, Send files and clipboard files are made as the signed-in user too, in that
            // user's own folders: made as SYSTEM, they were SYSTEM's, and a link swapped in could send them anywhere.
            // (FolderUser is unset in the window and on the controlling PC, which already run as their user.)
            else if ((kind is KindFiles or KindDownloads) && FolderUser != null && (makeAs = FolderUser()) == null)
                refusal = "Nobody is signed in on the remote PC, so it has nowhere to put the files. Sign in there first.";
            In b;
            In? old = null;
            lock (_inGate)
            {
                if (_results.TryGetValue(id, out var r)) { makeAs?.Dispose(); Tell(lane, FinishedMessage(id, r.Outcome, r.Why)); return; }
                if (_in is In cur && cur.Id == id) b = cur;
                else
                {
                    // Clipboard text and files: asked for (Get the remote PC's clipboard) or not. Not asked for, and
                    // this PC takes nothing unasked: turned away before anything is made. Send files is judged by
                    // its names once the whole list is here (OnOffer, below).
                    bool asked = false;
                    if (refusal == null && kind is KindText or KindFiles)
                    {
                        asked = TakeClipboardAsk();
                        if (!asked && !AcceptUnasked) refusal = NotAsked;
                    }
                    // A part of a batch already taken is never refused (its controller may be reconnecting).
                    if (refusal != null)
                    {
                        DiagLog.Write("files: refused an offer of kind " + kind + ": " + refusal);
                        makeAs?.Dispose();
                        Remember(id, EndedBadly, refusal);
                        Tell(lane, FinishedMessage(id, EndedBadly, refusal));
                        // Said off this lock (and off the lane's thread): the window's handler must never run under it.
                        if (refusal == NotAsked) ThreadPool.QueueUserWorkItem(_ => TellTurnedAway(kind == KindText ? "clipboard text" : "files for the clipboard"));
                        return;
                    }
                    old = _in;
                    // Clipboard files not asked for, on a PC that keeps its clipboard: received as Send files are, into
                    // Downloads\Kova, never onto the clipboard. Text not asked for is kept aside the same way (Complete).
                    // 4.5.1 (the owner's choice): clipboard text always replaces this clipboard, as before 4.4; only
                    // unasked clipboard files are still kept aside.
                    bool unasked = ClipboardOnlyWhenAsked && !asked && kind == KindFiles;
                    byte keptAs = unasked && kind == KindFiles ? KindDownloads : kind;
                    b = new In { Id = id, Kind = keptAs, OfferKind = kind, Asked = asked, Unasked = unasked, Entries = new (string, long)?[count], Into = into, MakeAs = makeAs };
                    b.T.Asked = asked;
                    b.T.Clipboard = keptAs is KindFiles or KindText;
                    b.T.Peer = PeerName;
                    if (kind == KindText) b.T.What = "clipboard text";
                    if (_note is var (noteId, said) && noteId == id) { b.Note = said; _note = null; }
                    _in = b;
                }
            }
            if (makeAs != null && !ReferenceEquals(b.MakeAs, makeAs)) makeAs.Dispose(); // a later part of a batch that already has its user
            if (old != null) EndIn(old, EndedStopped, "The other PC sent something newer, so " + old.T.What + " was stopped.", "Something newer was sent.", null, tell: true, replaced: true);
            bool ready = false;
            try
            {
                lock (b.Gate)
                {
                    if (b.Ended || b.OfferKind != kind) return;
                    b.LastActivity = Now;
                    if (b.Prepared) ready = true;
                    else
                    {
                        int pos = start;
                        for (int i = first; pos < m.Length; i++)
                        {
                            if (i >= count || pos + 10 > m.Length) throw new InvalidDataException("a damaged list of files");
                            long length = BitConverter.ToInt64(m, pos);
                            int bytes = BitConverter.ToUInt16(m, pos + 8);
                            if (pos + 10 + bytes > m.Length) throw new InvalidDataException("a damaged list of files");
                            string rel = Encoding.UTF8.GetString(m, pos + 10, bytes);
                            pos += 10 + bytes;
                            if (b.Entries[i] == null)
                            {
                                b.NameBytes += bytes;
                                if (b.NameBytes > MaxNameBytes) throw new InvalidDataException("a list of files far too long to be real");
                                b.Entries[i] = (rel, length);
                                b.Have++;
                            }
                        }
                        if (b.Have == count)
                        {
                            // Send files: asked for (Get files, by these names) or sent of the other PC's own accord.
                            if (b.OfferKind == KindDownloads)
                            {
                                b.Asked = b.T.Asked = TakeFetchAsk(Firsts(b));
                                if (!b.Asked && !AcceptUnasked) throw new NotAskedException();
                            }
                            Prepare(b);
                            ready = true;
                        }
                    }
                }
            }
            catch (NotAskedException)
            {
                // Nothing was made yet: the list is only judged once all of it is here, before Prepare.
                EndIn(b, EndedBadly, "Turned away " + b.T.What + " from the other PC: you did not ask for it.", NotAsked, lane, tell: true);
                return;
            }
            catch (Exception e)
            {
                EndIn(b, EndedBadly, "Could not receive " + b.T.What + ": " + e.Message, "The other PC could not receive " + b.T.What + ": " + e.Message, lane, tell: true);
                return;
            }
            if (!ready) return;
            Tell(lane, IdMessage(Ready, id));
            Progress?.Invoke(b.T);
            bool empty;
            lock (b.Gate) { empty = b.Total == 0 && !b.Completing && !b.Ended; if (empty) b.Completing = true; }
            if (empty) Complete(b, lane);
        }

        /// <summary>Thrown while a Send files list is read: nobody here asked for it, and this PC takes nothing unasked.</summary>
        private sealed class NotAskedException : Exception { }

        /// <summary>
        /// The batch's top-level names as the sender gave them (under b.Gate, the whole list here), and its name
        /// for the Files line from them: the one name, or "3 items".
        /// </summary>
        private static HashSet<string> Firsts(In b)
        {
            var firsts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in b.Entries) { string rel = e!.Value.Rel; int cut = rel.IndexOfAny(new[] { '\\', '/' }); firsts.Add(cut < 0 ? rel : rel[..cut]); }
            b.T.What = firsts.Count == 1 ? Shown(firsts.First()) : firsts.Count + " items";
            return firsts;
        }

        /// <summary>
        /// A received name as it may be said before it is checked: hidden characters (direction marks and the
        /// like) as "_", as Suited makes them, and no longer than a name can be.
        /// </summary>
        private static string Shown(string name)
        {
            if (name.Length > 255) name = Cut(name, 255) + "...";
            return name.Any(Hidden) ? new string(name.Select(c => Hidden(c) ? '_' : c).ToArray()) : name;
        }

        /// <summary>
        /// A character that hides or reorders what is shown: control characters, line breaks, and invisible
        /// formatting such as right-to-left marks ("invoice", U+202E, "fdp.exe" shows as "invoiceexe.pdf"). The
        /// joiners emoji and some scripts need inside a name (U+200C, U+200D) are kept: they cannot move an extension.
        /// </summary>
        private static bool Hidden(char c) => c is not ('\u200C' or '\u200D') && char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Control
            or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;

        /// <summary>Makes the folders and empty files (under b.Gate). Throws if anything is wrong, such as a path that leaves the folder.</summary>
        private static void Prepare(In b)
        {
            int count = b.Entries.Length;
            b.Handles = new SafeFileHandle?[count];
            b.Got = new long[count];
            b.Claimed = new HashSet<long>?[count];
            b.Paths = new string[count];
            b.Finals = new string[count];
            b.Renamed = new bool[count];
            b.Total = b.Entries.Sum(e => Math.Max(0, e!.Value.Length));
            if (b.Kind == KindText)
            {
                if (count != 1 || b.Length(0) < 0 || b.Length(0) > MaxText) throw new InvalidDataException("the other PC copied more text than Kova can carry (" + (MaxText >> 20) + " MB).");
                // Only the list of pieces now (a few hundred bytes); each piece is made when its data arrives.
                b.Text = new byte[]?[(int)((b.Length(0) + TextChunk - 1) / TextChunk)];
            }
            else
            {
                // Named before anything is checked, so a batch refused here still says what it was (and a
                // Get files waiting for it can tell it is the one it asked for).
                Firsts(b);
                string folder;
                if (b.Kind == KindFiles)
                    // Its own folder, even when several PCs send at the same moment.
                    folder = Path.GetFullPath(Path.Combine(Staging, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + b.Id.ToString("x8")));
                else if (b.Kind == KindTo)
                    // Checked again now the whole list is here; never made if it has gone meanwhile.
                    folder = b.As(() => Destination(b.Into)) ?? throw new InvalidDataException((b.Into ?? "the folder") + " is not a folder here.");
                else folder = Downloads;
                // Everything checked before anything is made: one bad entry used to fail the batch
                // after earlier ones were already made, leaving empty files and folders behind (and
                // the next send of the same thing became "name (2)").
                Validate(b, folder);
                if (b.Kind == KindFiles)
                {
                    lock (ActiveStaging) ActiveStaging.Add(folder);
                    // As the signed-in user on the service: the holding folder is theirs to change, and as
                    // SYSTEM a link put in its place would delete whatever it pointed at.
                    b.As(CleanStaging);
                }
                b.Folder = folder;
                if (b.Kind != KindTo) b.As(() => Directory.CreateDirectory(folder)); // as the signed-in user when the service receives
                var reserved = new List<string>();
                try
                {
                    // As the signed-in user when the service receives into a chosen folder: whatever
                    // that user could not make there is refused. The handles made stay usable after.
                    b.As(() => MakeEntries(b, reserved));
                }
                catch
                {
                    // Nothing half-made stays behind: the batch's own holding folder, or the names it
                    // reserved in Downloads or the chosen folder (made just now, so nothing else is in them).
                    try
                    {
                        if (b.Kind == KindFiles) b.As(() => Directory.Delete(folder, true));
                        else b.As(() => { foreach (string r in reserved) { if (Directory.Exists(r)) Directory.Delete(r, true); else File.Delete(r); } });
                    }
                    catch { }
                    b.Tops.Clear();
                    StagingEnded(b);
                    throw;
                }
                b.T.What = b.Tops.Count == 1 ? b.Tops[0] : b.Tops.Count + " items";
            }
            b.T.Total = b.Total;
            b.Prepared = true;
            b.Clock.Start();
        }

        private const int TextChunk = 1 << 20;

        /// <summary>Clipboard text's pieces (b.Text) for the bytes at offset, made if they are not yet. Under b.Gate.</summary>
        private static byte[]?[] TextFor(In b, long offset, int n)
        {
            var chunks = b.Text!;
            long length = b.Length(0);
            if (n > 0)
                for (long c = offset / TextChunk; c <= (offset + n - 1) / TextChunk; c++)
                    chunks[c] ??= new byte[(int)Math.Min(TextChunk, length - c * TextChunk)];
            return chunks;
        }

        /// <summary>Copies a piece of clipboard text into its place (it can fall across two of b.Text's pieces).</summary>
        private static void CopyText(byte[]?[] chunks, long offset, ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                var chunk = chunks[offset / TextChunk]!;
                int at = (int)(offset % TextChunk), n = Math.Min(data.Length, chunk.Length - at);
                data[..n].CopyTo(chunk.AsSpan(at));
                data = data[n..];
                offset += n;
            }
        }

        /// <summary>The text from its pieces, made straight into the string: no second copy of all of it on the way.</summary>
        private static string TextOf(byte[]?[] chunks)
        {
            // Counted by decoding once into a small scratch buffer: a character split across two pieces must be
            // counted as the real decoding below will make it, which only a decoder that keeps its place does.
            var count = Encoding.UTF8.GetDecoder();
            char[] scratch = new char[TextChunk + 2];
            int chars = 0;
            for (int i = 0; i < chunks.Length; i++) chars += count.GetChars(chunks[i] ?? Array.Empty<byte>(), scratch, i == chunks.Length - 1);
            return string.Create(chars, chunks, (into, cs) =>
            {
                var decode = Encoding.UTF8.GetDecoder();
                int at = 0;
                for (int i = 0; i < cs.Length; i++) at += decode.GetChars(cs[i] ?? Array.Empty<byte>(), into[at..], i == cs.Length - 1);
            });
        }

        // A hostile or broken PC must not be able to fill this PC's memory with names: at most this
        // much in one batch (a million files of ordinary depth come nowhere near it).
        private const long MaxNameBytes = 256L << 20;
        private const int MaxPathChars = 32_000; // Windows' own limit for a path

        /// <summary>
        /// Checks the whole list before anything is made: every path stays inside, names are not
        /// absurd, and there is room on the drive. Two files that would land on the same name (on
        /// Windows, names that differ only in capital letters do) get "name (2)" and so on, as
        /// Downloads does, instead of failing the whole batch.
        /// </summary>
        private static void Validate(In b, string folder)
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < b.Entries.Length; i++)
            {
                var (rel, length) = b.Entries[i]!.Value;
                // The list's total size is capped as it arrives (MaxNameBytes); here, each path's own.
                if (rel.Length > MaxPathChars) throw new InvalidDataException("a list of files far too long to be real");
                // A path that names a drive or a share ("C:\Windows\x", "C:x", "\\server\x") is an escape attempt,
                // refused before any renaming below could turn "C:" into a harmless-looking "C_".
                if (Path.IsPathRooted(rel) || rel.StartsWith('\\') || rel.StartsWith('/')) throw new InvalidDataException("a path that leaves the folder");
                // A name a Mac or Linux made that Windows cannot ("IMG.jpg.", "notes ", "con", "a:b") is changed a
                // little and said, rather than refusing the whole batch for one name.
                if (Suited(rel) is string suited)
                {
                    string was = rel[(Math.Max(rel.LastIndexOf('\\'), rel.LastIndexOf('/')) + 1)..], now = suited[(suited.LastIndexOf('\\') + 1)..];
                    if (was != now && b.Fixed++ == 0) { b.FixedFrom = was; b.FixedAt = i; } // counted once each, not again for what is inside a renamed folder
                    b.Entries[i] = (rel = suited, length);
                }
                // A file Explorer acts on merely by showing the folder it is in (a shortcut or .url whose icon is on
                // another server, desktop.ini, a search connector) could make this PC sign in to that server: given
                // ".txt", and said, so it is only ever opened on purpose.
                if (length >= 0 && Defused(rel) is string safe)
                {
                    if (b.Defused++ == 0) { b.DefusedFrom = rel[(Math.Max(rel.LastIndexOf('\\'), rel.LastIndexOf('/')) + 1)..]; b.DefusedAt = i; }
                    b.Entries[i] = (rel = safe, length);
                }
                // Said plainly: a name Windows cannot make (CON, NUL.txt, one ending in a dot) is not an escape attempt.
                foreach (string part in rel.Split('\\', '/')) if (BadName(part) is string bad) throw new InvalidDataException(bad);
                string path = SafePath(folder, rel) ?? throw new InvalidDataException("a path that leaves the folder");
                if (length < 0) folders.Add(path);
                else for (string? d = Path.GetDirectoryName(path); d != null && d.Length > folder.Length; d = Path.GetDirectoryName(d)) folders.Add(d);
            }
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < b.Entries.Length; i++)
            {
                var (rel, length) = b.Entries[i]!.Value;
                if (length < 0) continue;
                string path = SafePath(folder, rel)!;
                if (files.Add(path) && !folders.Contains(path)) continue;
                int cut = Math.Max(rel.LastIndexOf('\\'), rel.LastIndexOf('/')) + 1;
                string parent = rel[..cut], name = rel[cut..];
                for (int n = 2; ; n++)
                {
                    string fresh = parent + Marked(name, " (" + n + ")");
                    string freshPath = SafePath(folder, fresh) ?? throw new InvalidDataException("a path that leaves the folder");
                    if (folders.Contains(freshPath) || !files.Add(freshPath)) continue;
                    b.Entries[i] = (fresh, length);
                    break;
                }
            }
            // The "(incomplete)" name each file with something in it is written under until it is whole. It
            // must not be any other entry's name (a folder sent again can hold "movie (incomplete).mkv" next to
            // "movie.mkv"): two entries writing into one file would ruin both.
            b.PartialRels = new string?[b.Entries.Length];
            for (int i = 0; i < b.Entries.Length; i++)
            {
                var (rel, length) = b.Entries[i]!.Value;
                if (length <= 0) continue;
                int cut = Math.Max(rel.LastIndexOf('\\'), rel.LastIndexOf('/')) + 1;
                for (int n = 1; ; n++)
                {
                    string partial = rel[..cut] + PartialName(rel[cut..], n);
                    string partialPath = SafePath(folder, partial) ?? throw new InvalidDataException("a path that leaves the folder");
                    if (folders.Contains(partialPath) || !files.Add(partialPath)) continue;
                    b.PartialRels[i] = partial;
                    break;
                }
            }
            try
            {
                // Never filled to the last byte: a hostile PC (or a mistake) must not leave Windows and every other
                // program with no room. Kept free: 2 GB, or a twentieth of the drive if that is more.
                if (b.As(() => FreeSpace(folder)) is { } space)
                {
                    var (f, size) = space;
                    long keep = Math.Max(2L << 30, size / 20);
                    if (b.Total > f - keep)
                        throw new InvalidDataException("there is not enough free space on the receiving PC: it needs " + Size(b.Total) + ", and " + Size(f) + " is free, of which Kova always leaves " + Size(keep) + " for Windows and other programs.");
                }
            }
            catch (InvalidDataException) { throw; }
            catch { } // a drive Windows will not describe: the writes themselves say if it is full
        }

        /// <summary>
        /// "name (incomplete).ext" (then "name (incomplete 2).ext"...), shortened if need be so it still fits
        /// Windows' 255 characters for one name: a long name that fitted must not fail only for the mark.
        /// </summary>
        private static string PartialName(string name, int n) => Marked(name, n == 1 ? " (incomplete)" : " (incomplete " + n + ")");

        /// <summary>
        /// The name with mark put before its extension ("photo (2).jpg"), shortened if need be to fit Windows'
        /// 255 characters (an absurdly long "extension", from a long name with an early dot, once left a name too
        /// long to make), never between the two halves of one character. A name that is all extension
        /// (".gitignore") is all name: ".gitignore (2)", not " (2).gitignore".
        /// </summary>
        private static string Marked(string name, string mark)
        {
            string ext = Path.GetExtension(name), stem = name[..^ext.Length];
            // All extension (".gitignore"), or an "extension" too long to keep whole (a long name with an early
            // dot, "Dr. Smith's notes..."): all of it is the name, and the mark goes at its end.
            if (stem.Length == 0 || mark.Length + ext.Length > 254) { stem = name; ext = ""; }
            int room = 255 - mark.Length - ext.Length;
            if (stem.Length > room) stem = Cut(stem, room);
            return stem + mark + ext;
        }

        /// <summary>The first n characters, one fewer if that would split a character made of two (an emoji, say) in half.</summary>
        private static string Cut(string s, int n)
        {
            if (n >= s.Length) return s;
            if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
            return s[..n];
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

        /// <summary>
        /// Free space where the files really go (null if Windows will not say): a folder can be another drive
        /// mounted there, and a user can have a quota, so not simply its drive letter's free space.
        /// </summary>
        internal static (long Free, long Size)? FreeSpace(string folder)
        {
            // The folder itself may not be made yet (Downloads, Kova): the nearest one that is.
            for (string? d = folder; d != null; d = Path.GetDirectoryName(d))
                if (Directory.Exists(d))
                    return GetDiskFreeSpaceExW(d.EndsWith('\\') ? d : d + "\\", out ulong free, out ulong total, out _)
                        ? ((long)Math.Min(free, long.MaxValue), (long)Math.Min(total, long.MaxValue)) : null;
            return null;
        }

        /// <summary>Makes the folders and empty files of a checked list. reserved: the top names made in Downloads or the chosen folder.</summary>
        private static void MakeEntries(In b, List<string> reserved)
        {
            int count = b.Entries.Length;
            {
                var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var chosenTops = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // renamed's new names, quick to look up
                var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var made = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Two different entries (whatever their index) must never resolve to the same file: on
                // case-insensitive NTFS that includes paths that only differ by case. Without this, both
                // would get their own handle and RandomAccess.Write would interleave into one physical
                // file, silently corrupting it.
                var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    var (rel, length) = b.Entries[i]!.Value;
                    if (SafePath(b.Folder!, rel) == null) throw new InvalidDataException("a path that leaves the folder");
                    string top = rel.Split('\\', '/')[0];
                    bool nested = rel.Length > top.Length;
                    string? partialRel = b.PartialRels[i], partialPath = null;
                    if (b.Kind is KindDownloads or KindTo)
                    {
                        // Never over anything already in Downloads (or the chosen folder): "name (2).ext" and so on.
                        if (!renamed.TryGetValue(top, out var fresh))
                        {
                            // Chosen and made in one step, under one lock for every transfer: two PCs
                            // sending "report.bin" at once must never both get the same name and write
                            // into one file (files are otherwise only made when their data arrives).
                            lock (ReserveGate)
                            {
                                while (true)
                                {
                                    fresh = Unique(b.Folder!, top, chosenTops);
                                    string chosen = Path.Combine(b.Folder!, fresh);
                                    if (!nested && length == 0) { using (File.OpenHandle(chosen, FileMode.CreateNew, FileAccess.Write)) { } reserved.Add(chosen); break; }
                                    if (!nested && length > 0)
                                    {
                                        // A file on its own here is written under "(incomplete)", claimed now; its own
                                        // name is taken once all of it is there (Finish), "name (2)" if it went meanwhile.
                                        string p = Path.Combine(b.Folder!, Unique(b.Folder!, PartialName(fresh, 1)));
                                        try { using (File.OpenHandle(p, FileMode.CreateNew, FileAccess.Write)) { } }
                                        catch (IOException) when (File.Exists(p) || Directory.Exists(p)) { continue; } // something else took it that very moment
                                        reserved.Add(p);
                                        partialPath = p;
                                        break;
                                    }
                                    // Directory.CreateDirectory is not exclusive (a no-op if the name is
                                    // already there), so something else racing the exact chosen name
                                    // between Unique()'s check and here would otherwise be silently
                                    // merged into. Detect that and pick another name instead.
                                    bool existed = Directory.Exists(chosen);
                                    Directory.CreateDirectory(chosen);
                                    if (!existed) { reserved.Add(chosen); break; }
                                }
                            }
                            renamed[top] = fresh;
                            chosenTops.Add(fresh);
                        }
                        rel = fresh + rel[top.Length..];
                        if (nested && partialRel != null) partialRel = fresh + partialRel[top.Length..]; // in the same folder as its file
                        top = fresh;
                    }
                    string path = SafePath(b.Folder!, rel)!;
                    // Folders and empty files now; a file with something in it is made when its first
                    // piece arrives, by whichever lane brings it, so a million files are made by 16
                    // threads side by side instead of one by one before anything moves.
                    if (length < 0) { if (made.Add(path)) Directory.CreateDirectory(path); }
                    else
                    {
                        if (!filePaths.Add(path)) throw new InvalidDataException("the same file sent twice in one offer");
                        string dir = Path.GetDirectoryName(path)!;
                        if (made.Add(dir)) Directory.CreateDirectory(dir); // each folder once, not once per file
                        if (length == 0)
                        {
                            using (File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.Write)) { } // may be reserved already (Downloads)
                            MarkFromInternet(path); // an empty file is all there is of it, but it is still from the other PC
                        }
                        else partialPath ??= SafePath(b.Folder!, partialRel!) ?? throw new InvalidDataException("a path that leaves the folder");
                    }
                    if (tops.Add(top)) b.Tops.Add(top); // this batch's own (a fresh name), so it is thrown away if the batch fails
                    b.Paths[i] = partialPath ?? path; // where it is written
                    b.Finals[i] = path; // and the name it gets once it is whole
                    if ((i & 4095) == 0) b.LastActivity = Now; // a long list is still being worked through
                }
            }
        }

        /// <summary>A piece, in the lane's own buffer (m, length bytes): written, then confirmed. Nothing may keep m.</summary>
        private void OnPiece(Lane lane, byte[] m, int length)
        {
            uint id = BitConverter.ToUInt32(m, 1);
            int e = BitConverter.ToInt32(m, 5);
            long offset = BitConverter.ToInt64(m, 9);
            int n = length - 17;
            if (_in is not In b || b.Id != id)
            {
                lock (_inGate)
                    Tell(lane, _results.TryGetValue(id, out var r) ? FinishedMessage(id, r.Outcome, r.Why)
                        : FinishedMessage(id, EndedBadly, "The other PC no longer had this transfer. Send it again."));
                return;
            }
            SafeFileHandle? h = null, closing = null;
            bool text = b.Kind == KindText;
            byte[]?[]? chunks = null;
            bool repeat = false, busy = false, whole = false, report = false, complete = false;
            Exception? failed = null;
            try
            {
                lock (b.Gate)
                {
                    if (b.Ended || !b.Prepared) return;
                    // Written without "offset + n > length": offset is peer-controlled and near
                    // long.MaxValue would overflow that sum, wrapping it negative and passing the
                    // check despite being nonsensical. n and length are both small/non-negative here,
                    // so "length - n" cannot itself go negative in a confusing way once guarded first.
                    if ((uint)e >= (uint)b.Entries.Length || offset < 0 || n < 0 || n > b.Length(e) || offset > b.Length(e) - n) throw new InvalidDataException("a piece that does not fit");
                    // A piece sent again (its confirmation was lost with a lane): confirmed, not written twice.
                    repeat = b.Got[e] >= b.Length(e) || !(b.Claimed[e] ??= new HashSet<long>()).Add(offset);
                    h = repeat || text ? null : b.Handles[e];
                    if (text && !repeat) chunks = TextFor(b, offset, n); // its memory is made only now that its data is here
                    // Counted until this piece is written (and its file renamed, if it was the last piece): an
                    // end waits for it, or what arrived was judged while this write was still landing.
                    b.Busy++;
                    busy = true;
                }
                if (!repeat && !text && h == null)
                {
                    // Its first piece: the file is made now, outside the batch's lock, so the lanes make
                    // files side by side. Only this file's lock is held while it is made.
                    lock (b.Making[e & (b.Making.Length - 1)])
                    {
                        lock (b.Gate) h = b.Handles[e];
                        if (h == null)
                        {
                            // Its folder was made while preparing.
                            // As the user in a chosen folder (b.As): this is where a file with content is really made.
                            h = b.As(() => File.OpenHandle(b.Paths[e], FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read | FileShare.Delete));
                            // Marked as from the internet before any of it is written, so SmartScreen and Office's
                            // Protected View treat it as they would a download. The mark goes with it when it is renamed.
                            b.As(() => MarkFromInternet(b.Paths[e]));
                            lock (b.Gate)
                            {
                                if (b.Ended) { h.Dispose(); return; }
                                b.Handles[e] = h;
                            }
                        }
                    }
                }
                if (!repeat)
                {
                    if (text) CopyText(chunks!, offset, m.AsSpan(17, n));
                    else RandomAccess.Write(h!, m.AsSpan(17, n), offset);
                }
                lock (b.Gate)
                {
                    if (b.Ended) return;
                    b.LastActivity = Now;
                    if (!repeat)
                    {
                        if ((b.Got[e] += n) == b.Length(e))
                        {
                            closing = b.Handles[e]; // looked at, then closed, outside this lock
                            b.Handles[e] = null;
                            b.Claimed[e] = null;
                            b.Active.Remove(e);
                            whole = !text;
                        }
                        else b.Active.Add(e);
                        b.Done += n;
                        b.T.Done = b.Done;
                        double s = b.Clock.Elapsed.TotalSeconds;
                        if (s - b.LastReport >= 0.25)
                        {
                            double dt = s - b.LastReport;
                            // A gap (waiting for the other PC) is not counted as time spent moving.
                            b.Moving += Math.Min(dt, 1);
                            double rate = (b.Done - b.LastDone) / dt;
                            b.Speed = b.Speed == 0 ? rate : b.Speed * 0.7 + rate * 0.3;
                            b.LastDone = b.Done;
                            b.LastReport = s;
                            b.T.BytesPerSecond = b.Speed;
                            b.T.Waiting = false;
                            b.T.Files = b.Active.Take(32).Select(i => (text ? "clipboard text" : Path.GetRelativePath(b.Folder!, b.Finals[i]), b.Got[i], b.Length(i))).ToArray();
                            report = true;
                        }
                        if (b.Done == b.Total && !b.Completing) complete = b.Completing = true;
                    }
                }
                if (closing != null)
                {
                    // Deleted or renamed by someone here while it arrived (it is open so that they can): all of it
                    // went into a file that is gone, or has another name. Said as a failure; it used to be called
                    // received, with a puzzling "could not be given its own name".
                    bool lost = Lost(b, closing, b.Paths[e]);
                    closing.Dispose();
                    if (lost) throw new IOException(Path.GetFileName(b.Finals[e]) + " was deleted (or renamed) on the receiving PC before all of it had arrived.");
                }
                // All of this file is here: from its "(incomplete)" name to its own, before it is confirmed.
                if (whole) try { Finish(b, e); } catch { } // held by something for now: Complete tries again, and says so if it must
            }
            catch (Exception ex) { failed = ex; }
            finally
            {
                closing?.Dispose(); // already, unless something failed first
                if (busy) lock (b.Gate) b.Busy--; // before any end below, which waits for this count to reach nothing
            }
            if (failed != null)
            {
                if (!b.Ended) EndIn(b, EndedBadly, "Could not receive " + b.T.What + ": " + failed.Message, "The other PC could not receive " + b.T.What + ": " + failed.Message, lane, tell: true);
                return;
            }
            byte[] got = new byte[21];
            got[0] = Got;
            Buffer.BlockCopy(m, 1, got, 1, 16);
            BitConverter.TryWriteBytes(got.AsSpan(17), n);
            Tell(lane, got);
            if (report) Progress?.Invoke(b.T);
            if (complete) Complete(b, lane);
        }

        /// <summary>Thrown by Out.Read while a send is paused: not a failure, the piece goes again once it goes on.</summary>
        private sealed class ParkedException : Exception { }

        /// <summary>Which file a handle is (its drive and number there), how long, and when it was last written.</summary>
        private readonly record struct FileMeta(uint Volume, ulong Index, long Length, long Written)
        {
            // Some network drives and NAS shares give no file number (0), or a new one each time the file is
            // opened: unknown counts as the same file, so a good transfer is never failed as "changed" for that.
            public bool SameFile(FileMeta o) => Volume == 0 || o.Volume == 0 || Index == 0 || o.Index == 0 || (Volume == o.Volume && Index == o.Index);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ByHandleInfo
        {
            public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleInfo info);

        /// <summary>One question to Windows for all of it; null if it will not say (or the handle was let go).</summary>
        private static FileMeta? Describe(SafeFileHandle h)
        {
            try
            {
                if (h.IsClosed || !GetFileInformationByHandle(h, out var i)) return null;
                return new FileMeta(i.Volume, ((ulong)i.IndexHigh << 32) | i.IndexLow, (long)(((ulong)i.SizeHigh << 32) | i.SizeLow), (long)(((ulong)i.WrittenHigh << 32) | i.WrittenLow));
            }
            catch { return null; }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FileStandardInfo { public long AllocationSize, EndOfFile; public uint NumberOfLinks; public byte DeletePending, Directory; }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileStandardInfo info, int size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern int GetFinalPathNameByHandleW(SafeFileHandle file, [System.Runtime.InteropServices.Out] char[] path, int size, int flags);

        /// <summary>
        /// True if the file open as h is no longer where it was made: deleted (waiting to go once closed) or given
        /// another name. Only sure answers count; unsure is false, and the rename after says if it cannot be done.
        /// Only "delete pending" is trusted from Windows' own description: drives run by programs (Dokan, some cloud
        /// drives) say a file has no names at all when it is fine. Its own name first: the folders above can be
        /// reached by other paths (a junction, OneDrive). A name that reads differently (a NAS can give back
        /// accented letters in another form) is settled by opening the file where it was made: gone, or another
        /// file there, and it is lost.
        /// </summary>
        private static bool Lost(In b, SafeFileHandle h, string path)
        {
            try
            {
                if (GetFileInformationByHandleEx(h, 1 /* FileStandardInfo */, out var info, System.Runtime.InteropServices.Marshal.SizeOf<FileStandardInfo>())
                    && info.DeletePending != 0) return true;
                char[] buf = new char[1024];
                int n = GetFinalPathNameByHandleW(h, buf, buf.Length, 0);
                if (n >= buf.Length) n = GetFinalPathNameByHandleW(h, buf = new char[n + 1], n + 1, 0); // a long path: it said how long
                if (n > 0 && n < buf.Length)
                {
                    string now = Path.GetFileName(new string(buf, 0, n)), was = Path.GetFileName(path);
                    if (string.Equals(now, was, StringComparison.OrdinalIgnoreCase)) return false;
                    if (string.Equals(now.Normalize(), was.Normalize(), StringComparison.OrdinalIgnoreCase)) return false;
                }
                // Its name could not be read, or reads differently: looked for where it was made.
                if (Describe(h) is not FileMeta mine) return false;
                try
                {
                    using var there = b.As(() => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                    return Describe(there) is FileMeta other && !other.SameFile(mine);
                }
                catch (FileNotFoundException) { return true; }
                catch { return false; }
            }
            catch { return false; }
        }

        /// <summary>Everything arrived: onto the clipboard, or announced in Downloads.</summary>
        private void Complete(In b, Lane lane)
        {
            lock (b.Gate)
            {
                if (b.Ended) return;
                b.Ended = true;
            }
            // Another lane may still be renaming the file it just finished: everything is in its place first.
            WaitForWrites(b);
            lock (b.Gate) for (int i = 0; i < b.Handles.Length; i++) { b.Handles[i]?.Dispose(); b.Handles[i] = null; }
            int unnamed = 0;
            if (b.Kind != KindText)
                for (int i = 0; i < b.Entries.Length; i++)
                    if (b.Length(i) > 0 && !b.Renamed[i])
                        try { Finish(b, i); } catch { unnamed++; } // all of it is there, but it kept its "(incomplete)" name
            var t = b.T;
            t.Files = Array.Empty<(string, long, long)>();
            t.Done = b.Done;
            t.BytesPerSecond = b.Done / Math.Max(0.001, b.Moving > 0.5 ? b.Moving : b.Clock.Elapsed.TotalSeconds);
            if (b.Kind != KindText) lock (b.Gate) t.What = b.Tops.Count == 1 ? b.Tops[0] : b.Tops.Count + " items"; // a name taken meanwhile became "name (2)"
            string? text = null, unaskedText = null;
            string[]? paths = null;
            if (b.Kind == KindFiles)
            {
                // A file on its own that kept its "(incomplete)" name is pasted by that name: its own does not exist.
                lock (b.Gate)
                    paths = b.Tops.Select(x =>
                    {
                        string whole = Path.Combine(b.Folder!, x);
                        for (int i = 0; i < b.Entries.Length; i++)
                            if (b.Length(i) > 0 && !b.Renamed[i] && string.Equals(b.Finals[i], whole, StringComparison.OrdinalIgnoreCase)) return b.Paths[i];
                        return whole;
                    }).ToArray();
                t.Result = "The other PC sent " + t.What + Summary(b.Entries.Select(e => e!.Value)) + " to your clipboard, at " + Speed(t.BytesPerSecond) + ". Press Control V to paste.";
            }
            else if (b.Kind is KindDownloads or KindTo)
            {
                t.Result = "Received " + t.What + Summary(b.Entries.Select(e => e!.Value)) + At(t) + ". It is in " + Where(b.Folder!) + ".";
                // Clipboard files this PC did not ask for: kept in Downloads instead of replacing its clipboard.
                if (b.Unasked) t.Result = "The other PC sent " + t.What + Summary(b.Entries.Select(e => e!.Value)) + " for your clipboard without being asked, so your clipboard was left as it was. It is in " + Where(b.Folder!) + ".";
            }
            else
            {
                text = TextOf(b.Text!);
                b.Text = null;
                t.Result = "Received the clipboard text.";
            }
            string? keptText = null;
            if (text != null && b.Unasked)
            {
                // Text this PC did not ask for never goes onto its clipboard (it could swap a copied command for
                // its own just before it is pasted): saved where received files go, and offered (UnaskedTextReceived).
                unaskedText = text;
                text = null;
                keptText = SaveUnaskedText(b, unaskedText);
                t.Result = "The other PC sent some text without being asked, so your clipboard was left as it was. " +
                    (keptText != null ? "The text is saved in " + Where(Path.GetDirectoryName(keptText)!) + " as " + Path.GetFileName(keptText) + "." : "It could not be saved.");
            }
            t.Result += b.Notes();
            if (unnamed > 0) t.Result +=" " + unnamed + (unnamed == 1 ? " file arrived whole but could not be given its own name, so it is still" : " files arrived whole but could not be given their own names, so they are still") + " called incomplete.";
            Volatile.Write(ref t.Finished, true); // last, as in Run: never seen finished without its result
            lock (_inGate)
            {
                if (_in == b) _in = null;
                Remember(b.Id, EndedWell, "");
            }
            StagingEnded(b);
            Tell(lane, FinishedMessage(b.Id, EndedWell, ""));
            DiagLog.Write("files: " + t.Result);
            Progress?.Invoke(t);
            if (paths != null) FilesReceived?.Invoke(paths);
            if (text != null) TextReceived?.Invoke(text);
            if (unaskedText != null) try { UnaskedTextReceived?.Invoke(unaskedText, keptText); } catch { }
            b.MakeAs?.Dispose(); // the signed-in user's token, held only while the batch was going
            GiveBackMemory(b.Entries.Length, b.Total);
        }

        /// <summary>
        /// Clipboard text nobody here asked for, saved in Downloads\Kova as "clipboard text from (the other PC).txt"
        /// (never over anything: "(2)" and so on), marked as from the internet. Its path, or null if it could not be saved.
        /// </summary>
        private string? SaveUnaskedText(In b, string text)
        {
            try
            {
                string from = Suited(PeerName) ?? PeerName; // an address, but made safe like any received name
                from = from.Replace('\\', '_').Trim();
                if (from.Length == 0 || BadName(from) != null) from = "the other PC";
                if (from.Length > 100) from = Cut(from, 100);
                return b.As(() =>
                {
                    string folder = Downloads;
                    Directory.CreateDirectory(folder);
                    lock (ReserveGate)
                    {
                        string path = Path.Combine(folder, Unique(folder, "clipboard text from " + from + ".txt"));
                        using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                        using (var w = new StreamWriter(f, new UTF8Encoding(true)))
                            w.Write(text);
                        MarkFromInternet(path);
                        return path;
                    }
                });
            }
            catch (Exception e) { DiagLog.Write("files: could not save text the other PC sent: " + e.Message); return null; }
        }

        private static readonly byte[] ZoneInternet = Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n");

        /// <summary>
        /// Marks a received file as from the internet (its Zone.Identifier stream, as a browser does for a
        /// download), so SmartScreen asks before it runs and Office opens it in Protected View. Call as the user
        /// the file is made as. A drive with no such streams (FAT32, exFAT, some network drives) just goes without.
        /// </summary>
        internal static void MarkFromInternet(string path)
        {
            try
            {
                using var h = File.OpenHandle(path + ":Zone.Identifier", FileMode.Create, FileAccess.Write, FileShare.None);
                RandomAccess.Write(h, ZoneInternet, 0);
            }
            catch { }
        }

        /// <summary>
        /// Says here that something the other PC sent unasked was turned away (AcceptUnasked false): at most once every
        /// 10 seconds, so a PC sending over and over cannot keep NVDA talking.
        /// </summary>
        private void TellTurnedAway(string what)
        {
            long now = Now;
            if (now - Interlocked.Read(ref _toldUnaskedAt) < 10_000) return;
            Interlocked.Exchange(ref _toldUnaskedAt, now);
            string why = "Turned away " + what + " from the other PC: you did not ask for it.";
            DiagLog.Write("files: " + why);
            try { Progress?.Invoke(new Transfer { What = what, Clipboard = true, Peer = PeerName, Finished = true, Failed = true, Result = why }); } catch { }
        }

        /// <summary>
        /// After a batch is marked ended: up to 2 seconds for lanes still writing (or renaming) to finish, so
        /// what is on disk is judged only once it stops changing. A write that was under way when the end came
        /// used to land after the files were looked at, and a cut-off file could keep a whole file's name.
        /// </summary>
        private static void WaitForWrites(In b)
        {
            for (int i = 0; i < 200; i++)
            {
                lock (b.Gate) if (b.Busy == 0) return;
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// A file all here: from its "(incomplete)" name to its own. Never over anything: a name taken meanwhile
        /// (a file on its own in Downloads) becomes "name (2)". Tried for a second, as a virus scanner often holds
        /// a new file a moment. Throws if it still cannot be done.
        /// </summary>
        private static void Finish(In b, int e)
        {
            string from, to;
            lock (b.Gate)
            {
                // Another lane is renaming it this moment (a slow end, after the 2 seconds of waiting ran out):
                // a second rename would find it gone and call a whole file incomplete.
                if (b.Renamed[e] || !b.Renaming.Add(e)) return;
                from = b.Paths[e];
                to = b.Finals[e];
            }
            try { Rename(b, e, from, to); }
            finally { lock (b.Gate) b.Renaming.Remove(e); }
        }

        private static void Rename(In b, int e, string from, string to)
        {
            string dir = Path.GetDirectoryName(to)!;
            if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        b.As(() =>
                        {
                            lock (ReserveGate)
                            {
                                if (File.Exists(to) || Directory.Exists(to)) to = Path.Combine(dir, Unique(dir, Path.GetFileName(to)));
                                File.Move(from, to);
                            }
                        });
                        break;
                    }
                    catch (Exception x) when (x is IOException or UnauthorizedAccessException && attempt < 10) { Thread.Sleep(100); }
                }
            lock (b.Gate)
            {
                // A file on its own whose name went meanwhile: the batch is called by the name it really got.
                if (!string.Equals(to, b.Finals[e], StringComparison.OrdinalIgnoreCase)
                    && string.Equals(dir.TrimEnd('\\'), Path.GetFullPath(b.Folder!).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    int top = b.Tops.FindIndex(x => string.Equals(x, Path.GetFileName(b.Finals[e]), StringComparison.OrdinalIgnoreCase));
                    if (top >= 0) b.Tops[top] = Path.GetFileName(to);
                }
                b.Finals[e] = to;
                b.Renamed[e] = true;
            }
        }

        /// <summary>
        /// A batch being received ends without arriving: what did arrive is kept, and both PCs say
        /// so. why: for this PC; whyThere: for the sender. tell: send the sender the outcome.
        /// </summary>
        private void EndIn(In b, byte outcome, string why, string whyThere, Lane? lane, bool tell, bool replaced = false)
        {
            lock (b.Gate)
            {
                if (b.Ended) return;
                b.Ended = true;
            }
            // Writes already under way land first (closing their files under them only failed them).
            WaitForWrites(b);
            lock (b.Gate)
            {
                for (int i = 0; i < b.Handles.Length; i++) { b.Handles[i]?.Dispose(); b.Handles[i] = null; }
                b.Text = null;
            }
            // Nothing that arrived is thrown away (Conner): finished files stay as they are, a file cut
            // off part-way is kept under its "(incomplete)" name, and the Files line says where they are.
            var (whole, files, part, moved) = KeepWhatArrived(b);
            StagingEnded(b);
            if (whole + part > 0)
                why += " What had arrived was kept: " + whole + " of " + files + (files == 1 ? " file" : " files") +
                    (part > 0 ? ", and " + part + " only partly, marked incomplete" : "") +
                    (b.Kind == KindFiles && !replaced ? ", on your clipboard." : ", in " + Where(b.Folder!) + ".");
            if (b.Prepared) why += b.Notes();
            lock (_inGate)
            {
                if (_in == b) _in = null;
                Remember(b.Id, outcome, whyThere);
            }
            if (tell)
            {
                var m = FinishedMessage(b.Id, outcome, whyThere);
                if (lane != null && !lane.Dead) Tell(lane, m);
                else foreach (var l in _lanes) Tell(l, m);
            }
            DiagLog.Write("files: " + why);
            var t = b.T;
            t.Files = Array.Empty<(string, long, long)>();
            t.Failed = true;
            t.Cancelled = outcome == EndedStopped;
            t.Result = why;
            Volatile.Write(ref t.Finished, true); // last, as in Run
            try { Progress?.Invoke(t); } catch { }
            // Clipboard files: whatever arrived goes onto the clipboard, as a finished batch would.
            // Only what is really there: a file cut off part-way now has "(incomplete)" in its name,
            // and one that never got anything is gone, so their old names would paste nothing.
            // Not when a newer copy replaced it: that one goes on the clipboard, and this one's leftovers
            // landing a moment later used to push it off again.
            if (b.Kind == KindFiles && whole + part > 0 && !replaced)
                try
                {
                    var there = new List<string>();
                    foreach (string top in b.Tops)
                    {
                        string full = Path.Combine(b.Folder!, top);
                        if (moved.TryGetValue(full, out var now)) { if (now != null) there.Add(now); }
                        else if (File.Exists(full) || Directory.Exists(full)) there.Add(full);
                    }
                    if (there.Count > 0) FilesReceived?.Invoke(there.ToArray());
                }
                catch { }
            b.MakeAs?.Dispose(); // the signed-in user's token, held only while the batch was going
            GiveBackMemory(b.Entries.Length, b.Total);
        }

        /// <summary>
        /// A batch that did not finish: everything that arrived stays. A file that only partly arrived
        /// keeps the "(incomplete)" name it was written under; one made ahead that never got anything
        /// goes. Returns the files that arrived whole, all the files, and those kept part-received, and
        /// where each file not whole now is (by the name it would have had: its "(incomplete)" name, or
        /// null if it went). Called once no lane is writing any more (WaitForWrites).
        /// </summary>
        private static (int Whole, int Files, int Part, Dictionary<string, string?> Moved) KeepWhatArrived(In b)
        {
            var moved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (b.Folder == null || b.Kind == KindText || !b.Prepared) return (0, 0, 0, moved);
            int whole = 0, files = 0, part = 0;
            for (int i = 0; i < b.Entries.Length; i++)
            {
                long length = b.Length(i), got = b.Got[i];
                if (length < 0) continue; // a folder
                files++;
                if (length == 0 || b.Renamed[i]) { whole++; continue; }
                string partial = b.Paths[i], final = b.Finals[i];
                if (got >= length)
                {
                    // All of it arrived just as the batch ended, before its rename: given its own name now.
                    try { Finish(b, i); whole++; continue; } catch { }
                }
                long onDisk;
                try { onDisk = b.As(() => File.Exists(partial) ? new FileInfo(partial).Length : -1); } catch { onDisk = 0; }
                if (onDisk < 0) { moved[final] = null; continue; } // never made
                // Something written, even a write that landed but was not counted as the batch ended: kept.
                if (got > 0 || onDisk > 0) { part++; moved[final] = partial; continue; }
                // Made (or claimed) ahead, but nothing ever written into it: it goes.
                try { b.As(() => File.Delete(partial)); moved[final] = null; } catch { moved[final] = partial; }
            }
            return (whole, files, part, moved);
        }

        /// <summary>A folder as the Files line says it: "Downloads, Kova" for the usual one.</summary>
        private static string Where(string folder) =>
            string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.GetFullPath(Path.Combine(MyDownloads.Value, "Kova")).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? "Downloads, Kova" : folder;

        /// <summary>The sender ended it: stopped, or it could not go on (a file locked or gone), and then why, so both PCs say the same.</summary>
        private void OnStop(uint id, byte outcome, string why)
        {
            bool replaced = outcome == EndedReplaced;
            if (outcome != EndedBadly) outcome = EndedStopped; // a stop never means it arrived
            if (_in is In b && b.Id == id)
                EndIn(b, outcome, why.Length > 0 ? Theirs(why) : "The other PC stopped sending " + b.T.What + ".", outcome == EndedStopped ? "Stopped." : why, null, tell: false, replaced: replaced);
            else lock (_inGate) Remember(id, outcome, outcome == EndedStopped || why.Length == 0 ? "Stopped." : why);
        }

        private void OnAsk(Lane lane, uint id)
        {
            lock (_inGate)
            {
                if (_results.TryGetValue(id, out var r)) Tell(lane, FinishedMessage(id, r.Outcome, r.Why));
                else if (_in?.Id != id) Tell(lane, FinishedMessage(id, EndedBadly, "The other PC no longer had this transfer. Send it again."));
            }
        }

        private static readonly object ReserveGate = new();

        /// <summary>
        /// A name that is not taken in the folder yet: name, name (2), name (3)... taken: names this batch
        /// chose already but has not made on disk yet (a file waiting under its "(incomplete)" name), so two
        /// of its files never get the same one. Shortened if need be to fit Windows' 255 characters.
        /// </summary>
        private static string Unique(string folder, string name, ICollection<string>? taken = null)
        {
            bool Free(string n) => !File.Exists(Path.Combine(folder, n)) && !Directory.Exists(Path.Combine(folder, n)) && (taken == null || !taken.Contains(n));
            if (Free(name)) return name;
            for (int i = 2; ; i++)
            {
                string n = Marked(name, " (" + i + ")");
                if (Free(n)) return n;
            }
        }

        /// <summary>A path inside the batch folder, or null if it would leave it (or holds a name Windows cannot make).</summary>
        private static string? SafePath(string folder, string rel)
        {
            if (rel.Length == 0 || Path.IsPathRooted(rel)) return null;
            foreach (string part in rel.Split('\\', '/'))
                if (part.Length == 0 || part == "." || part == ".." || BadName(part) != null) return null;
            string full = Path.GetFullPath(Path.Combine(folder, rel));
            // Trimmed first: a drive's root ("D:\") already ends in the separator.
            return full.StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        private static readonly string[] Reserved =
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00b9", "COM\u00b2", "COM\u00b3",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3",
        };

        /// <summary>
        /// Why Windows cannot make a file or folder of this name, or null if it can. CON, NUL.txt or COM1.log
        /// open a device instead of a file, and a name ending in a dot or a space is quietly cut short, so it
        /// would land under another name. ("." and ".." are left to SafePath, which refuses them as escapes.)
        /// </summary>
        private static string? BadName(string part)
        {
            if (part.Length == 0 || part == "." || part == "..") return null;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "the name \"" + part + "\" contains a character Windows does not allow in names";
            if (part.EndsWith('.') || part.EndsWith(' ')) return "the name \"" + part + "\" ends in a dot or a space, which Windows does not allow";
            // Not a name Windows refuses, but one that can show as another: a right-to-left mark makes
            // "invoice(U+202E)fdp.exe" show as "invoiceexe.pdf". Suited makes such characters "_".
            if (part.Any(Hidden)) return "the name \"" + Shown(part) + "\" has hidden characters that can disguise what kind of file it is";
            string stem = part.Split('.')[0].TrimEnd(' ');
            if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) return "the name \"" + part + "\" is one Windows keeps for itself (like CON or NUL), so no file can have it";
            return null;
        }

        /// <summary>
        /// The path with every name Windows cannot make changed a little (null if none needs it): a character it does
        /// not allow becomes "_", a dot or space at the end goes, and a name it keeps for itself gets "_" ("con" is
        /// "con_", "NUL.txt" is "NUL_.txt"). "." and ".." are left alone, for SafePath to refuse as escapes.
        /// </summary>
        internal static string? Suited(string rel)
        {
            string[] parts = rel.Split('\\', '/');
            bool changed = false;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0 || part == "." || part == ".." || BadName(part) == null) continue;
                char[] c = part.ToCharArray();
                for (int j = 0; j < c.Length; j++) if (Array.IndexOf(Path.GetInvalidFileNameChars(), c[j]) >= 0 || Hidden(c[j])) c[j] = '_';
                string s = new string(c).TrimEnd('.', ' ');
                if (s.Length == 0) s = "_";
                int dot = s.IndexOf('.');
                string stem = dot < 0 ? s : s[..dot];
                if (Reserved.Contains(stem.TrimEnd(' '), StringComparer.OrdinalIgnoreCase)) s = stem + "_" + (dot < 0 ? "" : s[dot..]);
                parts[i] = s;
                changed = true;
            }
            return changed ? string.Join('\\', parts) : null;
        }

        // Files Explorer reads by itself when it shows the folder they are in (icons, folder settings, search
        // locations), which can point it at another server: received with ".txt" added.
        private static readonly string[] ActsOnShow = { ".url", ".lnk", ".scf", ".library-ms", ".searchconnector-ms" };

        /// <summary>The path with ".txt" added to its file name if Explorer would act on that file just by showing it; else null.</summary>
        internal static string? Defused(string rel)
        {
            string name = rel[(Math.Max(rel.LastIndexOf('\\'), rel.LastIndexOf('/')) + 1)..];
            bool acts = string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)
                || ActsOnShow.Any(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase));
            return acts ? rel + ".txt" : null;
        }

        /// <summary>
        /// The folder a controller chose to send into, if files may go there: an existing folder on
        /// one of this PC's drives, by its full plain path ("D:\Music\New"). Anything else is null:
        /// relative paths, devices and the like (\\.\, \\?\, \??\), shares, streams (a second colon),
        /// wildcards, ".." or a name Windows would quietly change, and reserved names such as CON or
        /// NUL. What goes inside is then kept inside by SafePath, entry by entry.
        /// </summary>
        internal static string? Destination(string? path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 32_000) return null;
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return null;
            if (path.IndexOf(':', 2) >= 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOfAny(new[] { '/', '*', '?', '"', '<', '>', '|' }) >= 0) return null;
            string rest = path[3..];
            if (rest.EndsWith('\\')) rest = rest[..^1];
            if (rest.Length > 0)
                foreach (string part in rest.Split('\\'))
                {
                    if (part.Length == 0 || part == "." || part == ".." || part.EndsWith('.') || part.EndsWith(' ')) return null;
                    string stem = part.Split('.')[0].TrimEnd(' ');
                    if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) return null;
                }
            try
            {
                string full = Path.GetFullPath(path);
                // Windows read it as something else than it says: not what was chosen.
                if (!string.Equals(full.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
                if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.Directory) == 0) return null;
                return full.Length > 3 ? full.TrimEnd('\\') : full;
            }
            catch { return null; }
        }

        // Holding folders of clipboard batches not yet ended (still arriving, or paused waiting for
        // their sender to come back). Lock on the set itself.
        private static readonly HashSet<string> ActiveStaging = new(StringComparer.OrdinalIgnoreCase);

        private static void StagingEnded(In b)
        {
            if (b.Kind == KindFiles && b.Folder != null) lock (ActiveStaging) ActiveStaging.Remove(b.Folder);
        }

        /// <summary>
        /// Clears old batches out of the holding folder: never the newest (the clipboard points at
        /// it), never one still arriving or paused (however old: a second PC's sends used to delete
        /// a first PC's paused batch from under it), and never one written to in the last 10 minutes.
        /// </summary>
        private static void CleanStaging()
        {
            try
            {
                if (!Directory.Exists(Staging)) return;
                // Never through a junction or link: only real folders of our own are cleared.
                // Only the holding folder and Kova's folder above it are the user's to swap for a link;
                // further up (a profile moved to another drive by a junction) is the PC's own setup.
                var hold = new DirectoryInfo(Staging);
                foreach (var d in new[] { hold, hold.Parent })
                    if (d != null && d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) return;
                var cutoff = DateTime.UtcNow.AddMinutes(-10);
                // The newest by when it was made, not by its name: the name has the local time in it, which goes
                // back an hour when summer time ends (or the PC moves west), and the newest batch was deleted.
                foreach (var old in new DirectoryInfo(Staging).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).ThenByDescending(d => d.Name).Skip(1))
                {
                    if ((old.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (old.CreationTimeUtc >= cutoff) continue;
                    lock (ActiveStaging) if (ActiveStaging.Contains(old.FullName)) continue;
                    try
                    {
                        if (old.EnumerateFiles("*", SearchOption.AllDirectories).Any(f => f.LastWriteTimeUtc >= cutoff)) continue;
                        old.Delete(true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// " (2.3 GB, 152 files and 12 folders)" for what a batch held, or " (2.0 MB)" for one file.
        /// The folders chosen themselves are not counted, only what is in them.
        /// </summary>
        private static string Summary(IEnumerable<(string Rel, long Length)> entries)
        {
            long bytes = 0;
            int files = 0, folders = 0, tops = 0;
            foreach (var (rel, length) in entries)
            {
                if (length >= 0) { files++; bytes += length; }
                else if (rel.IndexOfAny(new[] { '\\', '/' }) < 0) tops++;
                else folders++;
            }
            if (files == 1 && folders == 0 && tops == 0) return " (" + Size(bytes) + ")";
            string what = files + (files == 1 ? " file" : " files") + (folders > 0 ? " and " + folders + (folders == 1 ? " folder" : " folders") : "");
            return " (" + Size(bytes) + ", " + what + ")";
        }


        // The speed, only for something big enough to have one ("at 0 kilobytes a second" said nothing).
        private static string At(Transfer t) => t.Total >= 1 << 20 ? ", at " + Speed(t.BytesPerSecond) : "";
        public static string Speed(double bytesPerSecond) =>
            bytesPerSecond >= 1 << 20 ? (bytesPerSecond / (1 << 20)).ToString(bytesPerSecond < 10 << 20 ? "0.0" : "0") + " megabytes a second"
            : (bytesPerSecond / 1024).ToString("0") + " kilobytes a second";

        public static string Size(long bytes) =>
            bytes >= 1 << 30 ? (bytes / (double)(1 << 30)).ToString("0.0") + " GB" :
            bytes >= 1 << 20 ? (bytes / (double)(1 << 20)).ToString("0.0") + " MB" :
            bytes >= 1 << 10 ? (bytes / 1024) + " KB" : bytes + " bytes";
    }
}
