using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// The PC being controlled. One controller at a time (a new controller
    /// replaces the old one, so reconnecting after a dropped network just works),
    /// plus up to 100 listeners who hear everything but cannot type. Which one a
    /// client becomes depends on which password it proved.
    /// </summary>
    internal sealed class Host : IDisposable
    {
        public const int MaxListeners = 100;

        // The password keys, from this host's own salt (sent in every hello). New each time hosting
        // starts: a reconnecting PC works its key out again once, and nothing precomputed for one
        // host, or for an earlier run of this one, is any use.
        private readonly byte[] _salt = RandomNumberGenerator.GetBytes(Protocol.SaltBytes);

        /// <summary>This host's lasting identity (HostIdentity): signs every hello, makes device tickets, registers on a relay.</summary>
        public HostIdentity Identity { get; }
        private readonly bool _ownIdentity;

        // ---- Hosting through a Kova Relay (Relay.cs) ----
        private RelayHost? _relay, _listenRelay; // 4.6: the listen-only password has its own place on the relay

        /// <summary>Also reachable through this relay, under this name (null: not, or no longer).</summary>
        public void UseRelay(Relay.Target? target)
        {
            UseRelay(target, null);
        }

        /// <summary>
        /// Reachable through this relay (null: not, or no longer). listenTarget: the same relay under the code made from the
        /// listen-only password (4.6), so listeners find this PC by that password; it says nothing of its own.
        /// </summary>
        public void UseRelay(Relay.Target? target, Relay.Target? listenTarget)
        {
            Interlocked.Exchange(ref _listenRelay, null)?.Dispose();
            var old = Interlocked.Exchange(ref _relay, null);
            old?.Dispose();
            if (target == null || _stop) return;
            _relay = new RelayHost(target, this, Identity, (b, n, ep) => { try { _udp.Send(b, n, Dual(ep)); } catch { } }, msg => { _status(msg); RelayStatus?.Invoke(msg); });
            if (listenTarget != null && listenTarget.Name != target.Name)
                _listenRelay = new RelayHost(listenTarget, this, Identity, (b, n, ep) => { try { _udp.Send(b, n, Dual(ep)); } catch { } }, msg => DiagLog.Write("host: listen-only relay: " + msg));
        }

        /// <summary>What happens with the relay, in a sentence (registered, refused, lost and trying again), each said once: the window speaks it.</summary>
        public event Action<string>? RelayStatus;

        /// <summary>Reachable only through the relay: the port is taken on this PC itself, never opened to others.</summary>
        public bool RelayOnly { get; }

        /// <summary>The TCP port this host listens on (relay only: the free one it was given).</summary>
        public int ListeningPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>Whether this host is registered on its relay right now.</summary>
        public bool RelayRegistered => _relay?.Registered == true;

        // The UDP socket is IPv6 with IPv4 mapped in: an IPv4 relay is written the mapped way.
        private static IPEndPoint Dual(IPEndPoint ep) => ep.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(ep.Address.MapToIPv6(), ep.Port) : ep;

        /// <summary>Sends to a controlling PC: straight there, or back through the relay it came by.</summary>
        private void UdpSend(byte[] data, int length, IPEndPoint to)
        {
            if (Relay.RouteOf(to.Address) is { } r) r.Via.SendUdp(r.Id, data, length);
            else _udp.Send(data, length, to);
        }
        private readonly byte[] _key;
        private readonly byte[]? _listenKey;
        private readonly Action<string> _status;
        private readonly TcpListener _listener;
        private readonly UdpClient _udp;
        private volatile bool _stop;

        /// <summary>The pause before answering a wrong password. The self-test sets it to 0.</summary>
        public static int WrongPasswordDelayMs = 2000;

        /// <summary>Clipboard text from the controller. Raised on a network thread.</summary>
        public event Action<string>? ClipboardReceived;

        /// <summary>
        /// A controller asked for this PC's clipboard (the pull that mirrors Send the clipboard).
        /// Raised on a network thread; the owner reads the local clipboard and calls SendClipboard /
        /// SendClipboardFiles, which fan it out to the controllers. The Host itself cannot read the
        /// clipboard: it lives in a desktop session (the window, or the service's agent).
        /// </summary>
        public event Action? ClipboardRequested;

        // When the last clipboard pull was let through, for coalescing a flood of them (see ReadLoop).
        private long _lastClipboardRequestAt = long.MinValue / 2;

        /// <summary>
        /// Hosting as the service: the user signed in at the screen (null: nobody is). Get files,
        /// folder listing and sending into a chosen folder use only that user's rights; as SYSTEM, the
        /// password alone opened every user's files and every system folder. Unset in the window,
        /// which already runs as its user.
        /// </summary>
        public Func<System.Security.Principal.WindowsIdentity?>? FileUser { get; init; }

        /// <summary>Sends Ctrl+Alt+Del; set only when hosting as the service. Returns false if it could not.</summary>
        public Func<bool>? SecureAttention { get; init; }

        /// <summary>
        /// A controlling PC asked to update TailRemote here to this version. Raised on a pool
        /// thread; the second argument sends a message back to that PC alone.
        /// </summary>
        public event Action<string, Action<string>>? UpdateRequested;

        private readonly object _gate = new();
        // Any number of controllers up to MaxControllers, all at once: a second one used to replace
        // the first, which reconnected and replaced the second, back and forth for ever.
        private readonly List<Session> _controllers = new();
        public const int MaxControllers = 100; // the same password lets many PCs control at once; this only stops a runaway
        private readonly List<FileChannel> _kept = new(); // file channels of controllers that dropped, for their next connection; under _gate
        private readonly List<Session> _listeners = new();
        private LoopbackCapture? _capture;

        private sealed class Session
        {
            public required TcpClient Tcp;
            public required NetworkStream Stream;
            public required byte[] Token;
            public required IPAddress Address;
            public required SecureLink Link;
            public required byte Role;
            public required byte[] Key, HostNonce, ClientNonce;
            public FileChannel? Files; // under _gate
            public double Pace = 8 << 20; // bytes a second the host may send this controller files at (FilePace)
            // The remote screen's step on the ladder and its climbing back-off, kept for its next video line.
            public int VideoStep = -1, VideoFloor;
            public long VideoFloorUntil, VideoLastDown, VideoBackoffMs = 30_000;
            public bool VideoClimbed;
            public uint PeerFeatures;
            public long LastWakeAt;
            public volatile bool Ready; // its features message came: it has said everything it wants first (a locked bitrate)
            public volatile int Quality; // the bitrate step (0 = the best); the client asks for lower while its connection struggles
            public volatile bool Paused; // it asked for no sound (AudioPause): muted, so nothing is encoded or sent for it
            public volatile IPEndPoint? AudioTo;
            public long HeardAt; // when its last UDP hello came (every second): no hello for 3 s, no audio
            public long HelloStamp = long.MinValue; // the newest UDP hello's stamp (UdpLoop only): an older one is a replay
            public readonly long Since = Environment.TickCount64;
            public long LastMessage = Environment.TickCount64; // anything at all from it (it pings several times a second while holding keys)
            public bool WarnedNoUdp;
            public readonly HashSet<(ushort Vk, bool Ext)> Held = new();
            // Modifiers KeySafety let go while the controller went quiet, whose key-up has not come since
            // (under Held's lock): if the controller presses another key first, it was still holding them.
            public readonly HashSet<(ushort Vk, bool Ext)> LetGo = new();
            public readonly byte[] Audio = new byte[5 + 1 + Protocol.MaxOpusBytes + SecureLink.TagSize];
            public uint SentUntil; // the tick after the last audio sent
            public uint SentFirst; // the first tick ever sent to it: packet numbers are compared from here
            public bool SentAny;
            public TcpClient? Video; // its newest screen connection (one per controller: a newer one closes the older)
            // Remote tools under way for it (one of each at a time, so one PC cannot tie up the host's threads).
            public int InfoBusy, ListBusy, Fetches; // ListBusy: under ListGate
            public bool DrivesBusy; // under ListGate: the drive list, listed beside a slow folder
            public readonly Queue<(byte[] Id, string Path)> ListQueue = new(); // folders asked for while one was being listed; under ListGate
            public readonly object ListGate = new();
            // The host's NVDA as text (NvdaWant): what it asked for, and its own queue, sent in order by one
            // pool thread at a time, so a PC that stops reading holds up nobody else and lines never swap places.
            public volatile byte NvdaWant;
            public readonly List<byte[]> NvdaOut = new(); // under its own lock
            public bool NvdaPumping; // under NvdaOut's lock
            // Its microphone: the newest packet number seen (an older one is a replay or hopelessly late), and
            // whether it was told another PC's microphone is already on.
            public uint MicHigh;
            public bool MicAny, MicBusyTold;
        }

        private string? _captureDevice;

        /// <summary>captureDevice: the output whose sound is sent, or null for Windows' default.</summary>
        /// <summary>
        /// relayOnly (4.5): reachable only through a Kova Relay. Any free port is then taken, only on this PC itself
        /// (loopback), so no other PC can connect to it directly and nothing needs opening in Windows Firewall;
        /// every controlling PC comes in through the relay, which this host dials out to. The UDP socket still
        /// listens everywhere: the relay's sound packets come back to it, as answers to what it sends out.
        /// </summary>
        public Host(int port, string password, string? listenPassword, Action<string> status, string? captureDevice = null, HostIdentity? identity = null, bool relayOnly = false)
        {
            RelayOnly = relayOnly;
            // Lasting identity: the window and the service pass the one they keep; tests get a throwaway one.
            _ownIdentity = identity == null;
            Identity = identity ?? HostIdentity.Create();
            // Checked here, with a message to show: TcpListener throws ArgumentOutOfRange, which nothing translated.
            if (port < 1 || port > 65535) throw new InvalidOperationException("Port " + port + " is not a port number. Choose a port from 1 to 65535.");
            _captureDevice = captureDevice;
            _key = Protocol.DeriveKey(password, _salt);
            _listenKey = string.IsNullOrEmpty(listenPassword) ? null : Protocol.DeriveKey(listenPassword, _salt);
            _status = msg => { DiagLog.Write("host: " + msg); status(msg); };
            _mic = new MicIn(() => _captureDevice, _status);
            _screenSource = new ScreenSource();
            var screen = new ScreenVideo(_screenSource.Take, _screenSource.Screens);
            VideoUpdate = screen.Update;
            VideoForget = screen.Forget;

            // Relay only: any free port, on this PC alone. No other PC ever uses it, so it can never clash with another program.
            int bindPort = relayOnly ? 0 : port;
            _listener = new TcpListener(relayOnly ? IPAddress.IPv6Loopback : IPAddress.IPv6Any, bindPort);
            _listener.Server.DualMode = true;
            _udp = new UdpClient(AddressFamily.InterNetworkV6);
            _udp.Client.DualMode = true;
            // Exclusive: no other program may share the port while TailRemote holds it.
            _listener.ExclusiveAddressUse = true;
            _udp.Client.ExclusiveAddressUse = true;
            try
            {
                _listener.Start();
                _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, bindPort));
            }
            catch (SocketException e)
            {
                _listener.Stop();
                _udp.Dispose();
                throw new InvalidOperationException(e.SocketErrorCode == SocketError.AccessDenied
                    ? "Port " + port + " is reserved by Windows on this PC. Choose another port."
                    : "Port " + port + " is already used by another program on this PC. Choose another port.");
            }
            IgnoreUdpResets(_udp.Client);
            // Only once the port is ours: started before it, a busy port left these two running for good.
            new Thread(LogLoop) { IsBackground = true, Name = "Kova host log" }.Start();
            new Thread(KeySafety) { IsBackground = true, Name = "Kova key safety" }.Start();
            new Thread(HealthLoop) { IsBackground = true, Name = "Kova health", Priority = ThreadPriority.BelowNormal }.Start();

            // No capture yet: the sound is taken only while some PC wants it (AudioDemand).
            new Thread(AudioDemand) { IsBackground = true, Name = "Kova sound on demand" }.Start();
            _wifi = new WlanStreaming("host"); // steady Wi-Fi while hosting
            new Thread(AcceptLoop) { IsBackground = true, Name = "Kova accept" }.Start();
            new Thread(UdpLoop) { IsBackground = true, Name = "Kova host udp" }.Start();
        }

        // ---- The controlling PC's microphone (MicIn) ----

        private readonly MicIn _mic;
        private readonly object _micGate = new();
        private Session? _micOwner; // under _micGate: whose microphone plays (one at a time)
        private long _micOwnerAt;

        /// <summary>The output chosen on this PC for the controlling PC's microphone ("" or null: Kova picks a virtual cable).</summary>
        public string? MicDevice { get => _mic.Chosen; set => _mic.Chosen = value; }

        /// <summary>Test only: whether a microphone is being played.</summary>
        internal bool TestMicPlaying => _mic.Playing;

        private void HandleMic(byte[] d, IPEndPoint from)
        {
            var s = Array.Find(AllSessions(), x => d.AsSpan(1, 8).SequenceEqual(x.Token));
            // Only a PC controlling this one: a listener hears, it never speaks into this PC.
            if (s == null || s.Role != Protocol.RoleControl || !s.Ready) return;
            var addr = from.Address.IsIPv4MappedToIPv6 ? from.Address.MapToIPv4() : from.Address;
            if (!Bucket(addr).Equals(Bucket(s.Address))) return;
            if (!s.Link.OpenMic(d, d.Length)) return; // forged or damaged
            int ticks = d[Protocol.MicHeadBytes];
            if (ticks < 1 || ticks > 24) return;
            uint seq = BitConverter.ToUInt32(d, 9);
            // Only newer than every packet seen from it: a packet played back (even the newest, over and over) is nothing, so a
            // recording can neither be heard again nor keep the microphone (and its device) taken. A packet that arrives
            // after a newer one would be too late to play anyway.
            if (s.MicAny && (int)(seq - s.MicHigh) <= 0) return;
            s.MicHigh = seq;
            s.MicAny = true;
            long now = Environment.TickCount64;
            lock (_micGate)
            {
                if (Array.IndexOf(AllSessions(), s) < 0) return; // it has just ended: never the owner again
                if (_micOwner != s)
                {
                    // Still connected: looked up in the ready-made list, never under _gate (End holds _gate and then takes this lock).
                    if (_micOwner != null && now - _micOwnerAt < 1500 && Array.IndexOf(AllSessions(), _micOwner) >= 0)
                    {
                        if (!s.MicBusyTold) { s.MicBusyTold = true; Tell(s, Protocol.MicNote, "Another PC's microphone is already on there. Yours plays once theirs is off."); }
                        return;
                    }
                    _mic.Stop(); // a new owner starts afresh
                    _micOwner = s;
                    s.MicBusyTold = false;
                    Tell(s, Protocol.MicQuality, new[] { (byte)Protocol.MicBestStep });
                }
                _micOwnerAt = now;
            }
            var owner = s;
            _mic.Push(seq, ticks, d.AsSpan(Protocol.MicHeadBytes + 1, d.Length - Protocol.MicHeadBytes - 1 - SecureLink.TagSize), msg => Tell(owner, Protocol.MicNote, msg));
        }

        /// <summary>The microphone's owner said it is off, or went: the device is let go at once.</summary>
        private void MicOff(Session s)
        {
            lock (_micGate)
            {
                if (_micOwner != s) return;
                _micOwner = null;
            }
            _mic.Stop();
        }

        /// <summary>Once a second: the microphone's step, and letting go of its device when it stops coming.</summary>
        private void MicTick()
        {
            Session? owner;
            lock (_micGate) owner = _micOwner;
            int step = _mic.Tick(owner == null ? null : msg => Tell(owner, Protocol.MicNote, msg));
            if (step >= 0 && owner != null) Tell(owner, Protocol.MicQuality, new[] { (byte)step });
        }

        // ---- This PC's NVDA, through the Kova Helper add-on (NvdaHelper, NvdaEvent; 4.4) ----
        // NVDA hands everything it speaks, its cut-offs, beeps, sounds and braille straight to Kova over a local pipe.
        // (Up to 4.3 Kova read NVDA's Speech Viewer, hidden, which needed administrator rights and was slower: gone.)

        private NvdaHelper.Server? _helper;
        private int _helperInstalling;

        /// <summary>The window's menu: where the add-on stands, connected or not.</summary>
        internal NvdaHelper.Server? NvdaHelperServer => _helper;
        private readonly object _nvdaGate = new();

        /// <summary>Tests only: stands in for whether the add-on is talking to this host (null: ask the pipe).</summary>
        internal bool? TestHelperConnected;

        /// <summary>Whether the Kova Helper add-on is talking to this host right now.</summary>
        public bool NvdaHelperConnected => TestHelperConnected ?? _helper?.Connected == true;

        /// <summary>
        /// Listens for the Kova Helper add-on (no network: a pipe only NVDA in the session at the screen may use). The window
        /// and the service both call it; no administrator rights are needed.
        /// </summary>
        public void StartNvdaHelper()
        {
            lock (_nvdaGate)
            {
                if (_helper != null || _stop) return;
                var h = new NvdaHelper.Server(m => DiagLog.Write("host: " + m));
                h.Event += NvdaEventNow;
                h.ConnectedChanged += _ => TellNvdaAvailable(null);
                _helper = h;
                // The window watches the screen itself. The service (session 0) cannot see the screen: its agent at the
                // screen says when a secure screen shows (Agent wires that to SetSecureScreen).
                if (NvdaHelper.MySession() != 0)
                    new Thread(WatchSecureScreen) { IsBackground = true, Name = "Kova secure screen watch" }.Start();
            }
        }

        // ---- Secure screens: sign-in, the lock screen's password box, UAC prompts ----
        // There Windows runs a separate copy of NVDA that never loads add-ons, so no text comes from the Kova Helper,
        // and a controlling PC in text-only mode (the host's sound paused) heard nothing at all. While one shows, the
        // controllers are told the add-on cannot speak (NvdaSecureScreen), so their sound comes back on by itself until
        // the ordinary screen returns.

        private volatile bool _secureScreen;

        /// <summary>Tests only: stands in for whether a secure screen shows (null: as watched).</summary>
        internal bool? TestSecureScreen;

        /// <summary>Whether this PC shows a secure screen now (tests that stand in for the add-on never see the real screen).</summary>
        public bool SecureScreen => TestSecureScreen ?? (TestHelperConnected == null && _secureScreen);

        /// <summary>A secure screen came or went (the window's watch, or the service's agent at the screen).</summary>
        public void SetSecureScreen(bool on)
        {
            if (_secureScreen == on) return;
            _secureScreen = on;
            DiagLog.Write("host: " + (on ? "a secure screen shows (sign-in, lock or UAC): NVDA comes through the sound" : "the ordinary screen is back"));
            TellNvdaAvailable(null);
        }

        private void WatchSecureScreen()
        {
            var settle = new NativeService.SecureScreenSettle(); // three readings alike, 250 ms apart, before anything changes
            while (!_stop)
            {
                try { if (settle.Next(NativeService.InputDesktopIsSecure()) is bool on) SetSecureScreen(on); } catch { }
                Thread.Sleep(250);
            }
        }

        /// <summary>Tells every controller that wants NVDA (or just this one) whether the add-on is there to send it.</summary>
        private void TellNvdaAvailable(Session? only)
        {
            byte there = (byte)(NvdaHelperConnected ? 1 : 0);
            // A controller older than 4.7 takes NvdaSecureScreen as "no add-on" and would say so at every lock or UAC
            // prompt: it gets what it always got.
            byte[] plain = { Protocol.NvdaAvailable, there }, secure = { Protocol.NvdaAvailable, Protocol.NvdaSecureScreen };
            bool onSecure = SecureScreen;
            foreach (var s in only != null ? new[] { only } : AllSessions())
                if (s.Role == Protocol.RoleControl && s.NvdaWant != 0)
                    Queue(s, onSecure && (s.PeerFeatures & Protocol.FeatureNvdaSecureScreen) != 0 ? secure : plain);
        }

        /// <summary>
        /// One thing NVDA did here, to every controller that wants it, in order, on its own small message (never behind
        /// big writes: files and the screen have lanes of their own). Controllers only: a listen-only password is for
        /// hearing the sound, and the text (braille above all) can say more than the sound does.
        /// </summary>
        public void NvdaEventNow(NvdaEvent ev)
        {
            NvdaEventSeen?.Invoke(ev);
            byte[] m = NvdaEvent.Message(ev);
            byte flag = ev.Kind == NvdaEventKind.Braille ? Protocol.NvdaBrailleFlag : Protocol.NvdaSpeechFlag;
            foreach (var s in AllSessions()) if (s.Role == Protocol.RoleControl && (s.NvdaWant & flag) != 0) Queue(s, m);
        }

        /// <summary>Tests and logs: everything NVDA handed over here, as it came.</summary>
        public event Action<NvdaEvent>? NvdaEventSeen;

        /// <summary>Lines spoken here, as the add-on would hand them over (tests, and anything that only has words).</summary>
        public void NvdaLines(string[] lines)
        {
            foreach (string line in lines) if (line.Length > 0) NvdaEventNow(NvdaEvent.Speak(new NvdaSpeechPart(line.Length > NvdaEvent.MaxText ? line[..NvdaEvent.MaxText] : line)));
        }

        /// <summary>What this PC's braille display shows now.</summary>
        public void NvdaBrailleNow(string text) => NvdaEventNow(NvdaEvent.Braille(text.Length > NvdaEvent.MaxText ? text[..NvdaEvent.MaxText] : text));

        /// <summary>
        /// Into the PC's own NVDA queue, sent by one pool thread at a time. Whatever piles up while a message is on its way
        /// goes next as one batch, merged into as few messages as it can be (NvdaEvent.Merge): a console pouring out lines
        /// no longer means one network message per line. A PC far behind loses its oldest messages, never a cut-off.
        /// </summary>
        private static void Queue(Session s, byte[] m)
        {
            lock (s.NvdaOut)
            {
                s.NvdaOut.Add(m);
                if (s.NvdaOut.Count > NvdaQueueMax)
                {
                    var merged = NvdaEvent.Merge(s.NvdaOut);
                    s.NvdaOut.Clear();
                    s.NvdaOut.AddRange(merged);
                    // Only words, braille, beeps and sounds ever go, oldest first. Cut-offs never go, and neither does
                    // the add-on's state (NvdaAvailable: a lost "secure screen" left the controller without sound); only
                    // a state a newer one replaces. Nothing left to drop: the queue stays a little longer for now.
                    while (s.NvdaOut.Count > NvdaQueueMax)
                    {
                        int old = s.NvdaOut.FindIndex(IsDroppableNvda);
                        if (old < 0)
                        {
                            int lastState = s.NvdaOut.FindLastIndex(x => x.Length == 2 && x[0] == Protocol.NvdaAvailable);
                            old = s.NvdaOut.FindIndex(x => x.Length == 2 && x[0] == Protocol.NvdaAvailable);
                            if (old < 0 || old == lastState) break;
                        }
                        s.NvdaOut.RemoveAt(old);
                    }
                }
                if (s.NvdaPumping) return;
                s.NvdaPumping = true;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                while (true)
                {
                    List<byte[]> batch;
                    lock (s.NvdaOut)
                    {
                        if (s.NvdaOut.Count == 0) { s.NvdaPumping = false; return; }
                        batch = new List<byte[]>(s.NvdaOut);
                        s.NvdaOut.Clear();
                    }
                    foreach (var next in batch.Count == 1 ? batch : NvdaEvent.Merge(batch))
                    {
                        try { s.Link.Send(s.Stream, next); }
                        catch { lock (s.NvdaOut) s.NvdaOut.Clear(); break; } // the connection is going: nothing more to send
                    }
                }
            });
        }

        private const int NvdaQueueMax = 256;
        /// <summary>Speech, braille, a beep or a sound: what a PC far behind may lose. Never a cut-off or anything that is not an NVDA event.</summary>
        internal static bool IsDroppableNvda(byte[] m) => m.Length >= 2 && m[0] == Protocol.NvdaEventMsg
            && m[1] is (byte)NvdaEventKind.Speak or (byte)NvdaEventKind.Braille or (byte)NvdaEventKind.Beep or (byte)NvdaEventKind.Wave;

        /// <summary>A small message to one PC, off the caller's thread (the UDP thread must never wait on a connection).</summary>
        private static void Tell(Session s, byte type, string text) => Tell(s, type, System.Text.Encoding.UTF8.GetBytes(text));
        private static void Tell(Session s, byte type, byte[] body)
        {
            byte[] m = new byte[1 + body.Length];
            m[0] = type;
            body.CopyTo(m, 1);
            ThreadPool.QueueUserWorkItem(_ => { try { s.Link.Send(s.Stream, m); } catch { } });
        }

        public void Dispose()
        {
            _stop = true;
            _wifi?.Dispose();
            _screenSource?.Dispose();
            _mic.Dispose();
            lock (_nvdaGate) { _helper?.Dispose(); _helper = null; }
            try { _listener.Stop(); } catch { }
            try { _udp.Dispose(); } catch { }
            LoopbackCapture? c;
            lock (_gate)
            {
                foreach (var s in AllSessions()) End(s, null);
                foreach (var k in _kept) k.Dispose();
                _kept.Clear();
                _controllers.Clear();
                _listeners.Clear();
                Rebuild();
                c = _capture;
                _capture = null;
            }
            // Stopping the capture waits for its thread (up to 2 seconds): never on the caller's (window's) thread.
            if (c != null) ThreadPool.QueueUserWorkItem(_ => c.Dispose());
            _relay?.Dispose();
            _listenRelay?.Dispose();
            if (_ownIdentity) Identity.Dispose();
        }

        /// <summary>
        /// Windows reports an ICMP "port unreachable" from an earlier send as an
        /// error on the next receive, which would end the receive loop.
        /// </summary>
        public static void IgnoreUdpResets(Socket s)
        {
            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            try { s.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { }
        }

        /// <summary>Every controller's file channel (each controlling PC gets what this PC sends).</summary>
        private FileChannel[] ControllerChannels()
        {
            lock (_gate) return _controllers.Select(c => c.Files).OfType<FileChannel>().ToArray();
        }

        /// <summary>Sends clipboard text to every controller's clipboard. Never waits.</summary>
        public void SendClipboard(string text)
        {
            // Over the file lanes: confirmed on arrival, and sent again if a connection breaks on the way.
            foreach (var files in ControllerChannels())
                Sending(() => files.SendText(text));
        }

        /// <summary>
        /// A send on a thread of its own: it holds that thread until it has ended, waiting its turn behind the sends
        /// before it first (4.7), so sends queued up must not tie up the shared pool keys and folder lists run on.
        /// </summary>
        private static void Sending(Action send) =>
            new Thread(() => { try { send(); } catch { } }) { IsBackground = true, Name = "Kova send files" }.Start();

        /// <summary>Copied files from here to every controller's clipboard. Never waits.</summary>
        public void SendClipboardFiles(IReadOnlyList<string> paths, System.Security.Principal.WindowsIdentity? asUser = null) => SendToAll(paths, true, asUser);

        /// <summary>
        /// To every controller, each on a thread of its own. asUser belongs to this from here: it is let go
        /// once the last of those sends is done with it (the caller returns long before they are).
        /// </summary>
        private void SendToAll(IReadOnlyList<string> paths, bool toClipboard, System.Security.Principal.WindowsIdentity? asUser)
        {
            var all = ControllerChannels();
            if (all.Length == 0) { asUser?.Dispose(); return; }
            int left = all.Length;
            foreach (var files in all)
                Sending(() =>
                {
                    try { files.SendFiles(paths, toClipboard, asUser); }
                    finally { if (Interlocked.Decrement(ref left) == 0) asUser?.Dispose(); }
                });
        }

        /// <summary>Send files: into every controller's Downloads\TailRemote. Never waits. asUser: the service's agent opens the files as the window's user, never as SYSTEM.</summary>
        public void SendFiles(IReadOnlyList<string> paths, System.Security.Principal.WindowsIdentity? asUser = null) => SendToAll(paths, false, asUser);

        /// <summary>How many PCs are controlling and listening right now (for the window's title).</summary>
        public (int Controlling, int Listening) Connected { get { lock (_gate) return (_controllers.Count, _listeners.Count); } }

        /// <summary>True while someone is controlling this PC (the only ones files and the clipboard can go to).</summary>
        public bool HasController => ControllerChannels().Length > 0;

        /// <summary>Stops what is going to or coming from the controllers (also while one is reconnecting).</summary>
        public void CancelTransfer()
        {
            FileChannel[] all;
            lock (_gate) all = ControllerChannels().Concat(_kept).ToArray();
            foreach (var files in all) files.CancelSending();
        }

        /// <summary>Test only: the newest controller's file channel.</summary>
        internal FileChannel? ControllerFiles { get { lock (_gate) return _controllers.LastOrDefault()?.Files; } }

        /// <summary>Files from the controller's clipboard, in the holding folder. Raised on a network thread.</summary>
        public event Action<string[]>? ClipboardFilesReceived;
        /// <summary>How a clipboard batch is going, either way. Raised on a network thread.</summary>
        public event Action<FileChannel.Transfer>? TransferProgress;

        /// <summary>Everyone connected, as a ready-made array: the audio thread reads it without waiting for anything.</summary>
        private volatile Session[] _everyone = Array.Empty<Session>();

        private Session[] AllSessions() => _everyone;

        /// <summary>Test only (--chaostest): how many controllers and listeners are connected.</summary>
        internal int TestSessions => _everyone.Length;

        /// <summary>Call under _gate whenever the controller or listeners change.</summary>
        private void Rebuild()
        {
            var all = new List<Session>(_listeners);
            all.AddRange(_controllers);
            _everyone = all.ToArray();
        }

        // ---- Nobody can tie the host up ----

        // Logins under way: at most 32 from any one address, 256 in all. A flood from one
        // place cannot crowd out anyone else, and from one address the NEWEST login always
        // gets in: the oldest silent one is dropped to make room. (Refusing the new one let
        // 8 silent connections lock a real login out for as long as they kept coming.)
        // 32 from one address: many controllers behind one router each open file lanes, and with 8
        // a lane's login could push a real one out.
        private const int MaxHandshakes = 256, MaxHandshakesPerAddress = 32, ReservedForKnown = 64;
        // A whole login (hello, answer, proof) must be done within this: a read timeout alone restarts with every byte.
        public static int LoginDeadlineMs = 8000;
        private int _handshakes;
        private readonly Dictionary<IPAddress, LinkedList<TcpClient>> _pending = new();
        private readonly HashSet<IPAddress> _known = new(); // address blocks that have logged in before

        // Everyone behind one IPv6 /64 counts as one: a single attacker owns 2^64 addresses, so limits
        // keyed on the exact address did nothing. IPv4 is used whole.
        private static IPAddress Bucket(IPAddress a)
        {
            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !a.IsIPv4MappedToIPv6)
            {
                var b = a.GetAddressBytes();
                for (int i = 8; i < 16; i++) b[i] = 0;
                return new IPAddress(b);
            }
            return a;
        }

        /// <summary>
        /// The logged-in controller a file or screen lane belongs to, by the token its login was given. Not by
        /// address: many home, phone and university networks send each new connection out from a different
        /// public address, and every lane from such a PC was refused without a word, so clipboard text, files
        /// and the screen never arrived. Who it is is proven by the lane proof made with the session's key,
        /// checked next. One from the same address block wins, should two tokens ever match.
        /// </summary>
        private Session? LaneOwner(ReadOnlySpan<byte> token, IPAddress remote)
        {
            Session? any = null;
            lock (_gate)
                foreach (var c in _controllers)
                {
                    if (!c.Token.AsSpan().SequenceEqual(token)) continue;
                    if (Bucket(c.Address).Equals(Bucket(remote))) return c;
                    any ??= c;
                }
            return any;
        }

        /// <summary>
        /// The block a device ticket's wrong passwords are counted against: one of its own, made from the ticket's
        /// random half (fd6b: a private range no real controlling PC comes from), so a ticket shared by nobody else
        /// gets its own five guesses a minute, and a stolen one buys no more than that.
        /// </summary>
        private static IPAddress TicketBlock(ReadOnlySpan<byte> id)
        {
            byte[] b = new byte[16];
            b[0] = 0xfd; b[1] = 0x6b;
            id[..6].CopyTo(b.AsSpan(2));
            return new IPAddress(b);
        }

        // Wrong passwords that came with a valid ticket, in the last day (a stolen ticket gets five a minute of its own,
        // but all tickets together get no more than this a day before they stop counting). Under _guesses.
        private readonly Queue<long> _ticketFails = new();
        private const int TicketFailsPerDay = 200;

        private void TicketFailed()
        {
            long now = Environment.TickCount64;
            lock (_guesses) { _ticketFails.Enqueue(now); while (_ticketFails.Count > TicketFailsPerDay + 1) _ticketFails.Dequeue(); }
        }

        private bool TicketsTrusted()
        {
            long now = Environment.TickCount64;
            lock (_guesses)
            {
                while (_ticketFails.Count > 0 && now - _ticketFails.Peek() >= 86_400_000) _ticketFails.Dequeue();
                return _ticketFails.Count < TicketFailsPerDay;
            }
        }

        private byte[] TicketMessage()
        {
            byte[] t = Identity.NewTicket();
            byte[] m = new byte[1 + t.Length];
            m[0] = Protocol.DeviceTicket;
            t.CopyTo(m, 1);
            return m;
        }

        /// <summary>Who an address is, in words for the status line: a ticket's block or a relayed PC is not shown as a made-up address.</summary>
        internal string Who(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            if (b.Length == 16 && b[0] == 0xfd && b[1] == 0x6b) return "a PC that connected here before";
            if (RelayHost.RealAddress(a) is string real) return real + " (through the relay)";
            return a.ToString();
        }

        /// <summary>This address block has logged in before: it keeps reserved handshake slots during a flood.</summary>
        private void MarkKnown(IPAddress a)
        {
            a = Bucket(a);
            lock (_guesses) { if (_known.Count > 10_000) _known.Clear(); _known.Add(a); }
        }

        private bool BeginHandshake(IPAddress a, TcpClient tcp)
        {
            a = Bucket(a);
            TcpClient? drop = null;
            lock (_guesses)
            {
                if (!_pending.TryGetValue(a, out var list)) _pending[a] = list = new LinkedList<TcpClient>();
                // A flood of new addresses cannot use the last slots: those are kept for blocks that have logged in before.
                int cap = _known.Contains(a) ? MaxHandshakes + ReservedForKnown : MaxHandshakes;
                bool full = list.Count >= MaxHandshakesPerAddress;
                // Decided before anything is dropped: dropping the oldest and then refusing this one anyway
                // left that oldest login running on, no longer counted and never hung up on.
                if (_handshakes - (full ? 1 : 0) >= cap) { if (list.Count == 0) _pending.Remove(a); return false; }
                if (full)
                {
                    drop = list.First!.Value; // the oldest from this address makes room
                    list.RemoveFirst();
                    _handshakes--;
                }
                list.AddLast(tcp);
                _handshakes++;
            }
            try { drop?.Dispose(); } catch { } // its login thread ends on its own
            return true;
        }

        private void EndHandshake(IPAddress a, TcpClient tcp)
        {
            a = Bucket(a);
            lock (_guesses)
            {
                if (!_pending.TryGetValue(a, out var list) || !list.Remove(tcp)) return; // already dropped to make room
                _handshakes--;
                if (list.Count == 0) _pending.Remove(a);
            }
        }
        private readonly Dictionary<IPAddress, (int Fails, long Since, long BlockedUntil)> _guesses = new();

        // Five guesses a minute per address block is no limit for someone with thousands of addresses
        // (a botnet, or many IPv6 blocks): past this many wrong passwords in a minute from blocks
        // that have never logged in, every such block is turned away at once until the minute is up.
        // PCs that have logged in before carry on as normal.
        // 4.4: and past 200 in an hour, or 1,000 in a day, until that hour or day is over. 30 a minute alone still let
        // a patient guesser try about 43,000 passwords a day. PCs that logged in before (address block or device
        // ticket) are never held up by this, so turning strangers away for a day costs a real user nothing.
        private const int StrangerFailsPerMinute = 30, StrangerFailsPerHour = 200, StrangerFailsPerDay = 1000;
        private readonly Queue<long> _strangerFails = new(); // when each recent one came (a day's worth at most); under _guesses

        // Each refusal waits out WrongPasswordDelayMs on its own thread, after giving its login slot
        // back, so nothing else bounds how many are waiting. Past this many, a new login from a block
        // that has never logged in is hung up on before its proof is even checked: answering it at
        // once would tell a guesser "wrong" without the pause.
        private const int MaxRefusing = 64;
        private int _refusing;

        /// <summary>Call under _guesses.</summary>
        private bool StrangersLocked(long now)
        {
            while (_strangerFails.Count > 0 && now - _strangerFails.Peek() >= 86_400_000) _strangerFails.Dequeue();
            if (_strangerFails.Count >= StrangerFailsPerDay) return true;
            int hour = 0, minute = 0;
            foreach (long at in _strangerFails)
            {
                if (now - at < 3_600_000) hour++;
                if (now - at < 60_000) minute++;
            }
            return minute >= StrangerFailsPerMinute || hour >= StrangerFailsPerHour;
        }

        private bool Known(IPAddress a)
        {
            a = Bucket(a);
            lock (_guesses) return _known.Contains(a);
        }

        /// <summary>After 5 wrong passwords in a minute, an address is refused at once for a minute.</summary>
        private bool Blocked(IPAddress a)
        {
            a = Bucket(a);
            long now = Environment.TickCount64;
            lock (_guesses)
                return (_guesses.TryGetValue(a, out var g) && g.BlockedUntil > now)
                    || (!_known.Contains(a) && StrangersLocked(now));
        }

        // Passwords being checked right now, per address block: 32 logins sent at the same moment used to be
        // checked side by side before the first wrong one was counted, 32 guesses where 5 are allowed.
        private readonly Dictionary<IPAddress, int> _checking = new();

        /// <summary>False (hang up) if this block is blocked, or already has as many checks under way as guesses left.</summary>
        private bool StartGuess(IPAddress a)
        {
            a = Bucket(a);
            long now = Environment.TickCount64;
            lock (_guesses)
            {
                int fails = _guesses.TryGetValue(a, out var g) && now - g.Since < 60_000 ? g.Fails : 0;
                if ((g.BlockedUntil > now) || (!_known.Contains(a) && StrangersLocked(now))) return false;
                _checking.TryGetValue(a, out int busy);
                if (fails + busy >= 5) return false;
                _checking[a] = busy + 1;
                return true;
            }
        }

        private void EndGuess(IPAddress a)
        {
            a = Bucket(a);
            lock (_guesses)
            {
                if (!_checking.TryGetValue(a, out int busy)) return;
                if (busy <= 1) _checking.Remove(a); else _checking[a] = busy - 1;
            }
        }

        /// <summary>Counts a wrong password; returns what to log, or null. A flood never floods the log.</summary>
        private string? WrongPassword(IPAddress a)
        {
            IPAddress bucket = Bucket(a);
            long now = Environment.TickCount64;
            lock (_guesses)
            {
                if (_guesses.Count > 10_000)
                {
                    // Never grows without bound, but only what no longer matters goes: wiping the lot
                    // (as this used to) lifted every block, so rotating through 10,000 addresses was
                    // never blocked at all.
                    foreach (var k in _guesses.Where(e => now - e.Value.Since >= 60_000 && e.Value.BlockedUntil <= now).Select(e => e.Key).ToList())
                        _guesses.Remove(k);
                    if (_guesses.Count > 10_000)
                        foreach (var k in _guesses.Where(e => e.Value.BlockedUntil <= now).OrderBy(e => e.Value.Since).Take(_guesses.Count - 10_000).Select(e => e.Key).ToList())
                            _guesses.Remove(k);
                }
                (int Fails, long Since, long BlockedUntil) g = _guesses.TryGetValue(bucket, out var old) && now - old.Since < 60_000 ? old : (0, now, 0L);
                g.Fails++;
                if (g.Fails >= 5 && g.BlockedUntil <= now) g.BlockedUntil = now + 60_000;
                _guesses[bucket] = g;
                if (!_known.Contains(bucket))
                {
                    bool wasLocked = StrangersLocked(now);
                    _strangerFails.Enqueue(now);
                    if (!wasLocked && StrangersLocked(now))
                        return "Too many wrong passwords from many places: PCs that have not connected here before are turned away for a while. PCs that have connected before still get in.";
                }
                return g.Fails == 1 ? "Refused " + Who(a) + ": wrong password."
                     : g.Fails == 5 ? "Refused " + Who(a) + " for a minute: too many wrong passwords."
                     : null;
            }
        }

        /// <summary>Switches to recording another output (null: Windows' default) without stopping hosting.</summary>
        public void SetCaptureDevice(string? id)
        {
            lock (_switching)
            {
                LoopbackCapture? old;
                lock (_gate)
                {
                    if (_captureDevice == id || _stop) return;
                    _captureDevice = id;
                    old = _capture;
                    _capture = null;
                }
                // The old capture must stop before the new one starts: the packet number is
                // the audio nonce, and two captures counting at once would repeat one. Outside
                // _gate, because stopping waits for the capture thread.
                StopCapture(old);
                // Taken again only if some PC is listening now; otherwise AudioDemand starts it,
                // on the new device, when one does.
                if (old != null) lock (_gate) StartCapture();
            }
        }

        private readonly object _switching = new();
        private uint _nextSeq;
        private readonly AutoResetEvent _audioNudge = new(false);

        /// <summary>Some PC wants sound now: ready, not muted (AudioPause), and its UDP hello came in the last 3 seconds.</summary>
        private bool SoundWanted()
        {
            long now = Environment.TickCount64;
            foreach (var s in AllSessions())
                if (s.Ready && !s.Paused && s.AudioTo != null && now - Volatile.Read(ref s.HeardAt) < 3000) return true;
            return false;
        }

        /// <summary>
        /// The sound is taken and encoded only while some PC wants it: nobody connected, everyone muted
        /// or gone quiet, and the capture stops, so hosting idle costs nothing. It starts the moment a PC
        /// wants it (nudged by its hello, its features or its unmute; else within a quarter second), and
        /// stops only after 10 seconds with nobody, so a reconnect or a brief mute never reopens the
        /// device. Packet numbers carry on from where the last capture stopped (StopCapture), so no
        /// session ever sees one repeated.
        /// </summary>
        private void AudioDemand()
        {
            long unwantedSince = 0;
            while (!_stop)
            {
                _audioNudge.WaitOne(250);
                if (_stop) return;
                bool wanted = SoundWanted();
                bool running;
                lock (_gate) running = _capture != null;
                if (wanted)
                {
                    unwantedSince = 0;
                    if (!running) lock (_switching) lock (_gate) StartCapture();
                }
                else if (running)
                {
                    long now = Environment.TickCount64;
                    if (unwantedSince == 0) unwantedSince = now;
                    else if (now - unwantedSince >= 10_000)
                    {
                        lock (_switching)
                        {
                            LoopbackCapture? old;
                            lock (_gate)
                            {
                                if (SoundWanted()) { unwantedSince = 0; continue; }
                                old = _capture;
                                _capture = null;
                            }
                            StopCapture(old); // outside _gate: it waits for the capture thread
                            DiagLog.Write("host: nobody wants the sound; stopped taking it");
                        }
                        unwantedSince = 0;
                    }
                }
                else unwantedSince = 0;
            }
        }

        /// <summary>Starts taking the sound (AudioDemand: only while some PC wants it). Call under _gate.</summary>
        private void StartCapture()
        {
            if (_stop || _capture != null) return;
            // Only the current capture sends: one that was late to stop (SetCaptureDevice waits 2
            // seconds, then carries on) and drains its buffer afterwards would otherwise share each
            // session's packet buffer and the encoder with the new one, and could seal two packets
            // under the same number.
            LoopbackCapture? mine = null;
            mine = _capture = new LoopbackCapture((seq, pcm) => { if (ReferenceEquals(Volatile.Read(ref _capture), mine)) SendAudio(seq, pcm); },
                msg => { _status(msg); Broadcast(msg); }, _captureDevice, _nextSeq);
        }

        private void StopCapture(LoopbackCapture? c)
        {
            if (c == null) return;
            bool stopped = c.Stop();
            // A second's gap (the player starts fresh); far more if the thread would not stop.
            _nextSeq = c.NextSeq + (stopped ? 172u : 1u << 24);
        }

        private readonly WlanStreaming _wifi;
        private int _txPackets, _txBytes;

        /// <summary>Once a second while logging is on: what the host is capturing and sending.</summary>
        private void LogLoop()
        {
            while (!_stop)
            {
                Thread.Sleep(1000);
                // Connected, but its UDP hello never came: this PC cannot send it any sound. Said once.
                foreach (var s in AllSessions())
                    if (s.AudioTo == null && !s.WarnedNoUdp && Environment.TickCount64 - s.Since > 5000)
                    {
                        s.WarnedNoUdp = true;
                        _status("No sound can reach " + s.Address + ": nothing from it arrives here over UDP port " + ((IPEndPoint)_listener.LocalEndpoint).Port +
                            ", though its keys do. Open the port with Port editor on this PC, or check the firewall and the network.");
                    }
                // Taken every second, logging or not: left adding up for months they wrapped round, and the
                // first line after logging was turned on said nonsense.
                int packets = Interlocked.Exchange(ref _txPackets, 0), bytes = Interlocked.Exchange(ref _txBytes, 0);
                try { MicTick(); } catch { }
                if (!DiagLog.Enabled) continue;
                int fills = Interlocked.Exchange(ref LoopbackCapture.TestGapFills, 0);
                int fillMs = Interlocked.Exchange(ref LoopbackCapture.TestGapFillMs, 0);
                int skips = Interlocked.Exchange(ref LoopbackCapture.TestSeqSkips, 0);
                int chunk = Interlocked.Exchange(ref LoopbackCapture.TestChunkMax, 0);
                var sb = new System.Text.StringBuilder();
                foreach (var s in AllSessions())
                    sb.Append(" [").Append(s.Address).Append(s.Role == Protocol.RoleControl ? " control" : " listen")
                      .Append(", quality step ").Append(s.Quality).Append(s.AudioTo == null ? ", no audio address yet" : "").Append(']');
                DiagLog.Write("host: capturing " + LoopbackCapture.DeviceInfo + ", biggest chunk " + chunk +
                    " ms, silence added " + fills + "x (" + fillMs + " ms), count skips " + skips + ", sent " + packets + " packets, " +
                    (bytes * 8 / 1000) + " kbit/s, sessions" + (sb.Length == 0 ? " none" : sb.ToString()));
            }
        }

        /// <summary>
        /// Tells every connected PC why this host is about to go away (updating, restarting,
        /// stopped), so they say that instead of "the connection was forcibly closed", and an
        /// update or restart is waited out quietly. Waits up to a second for the sends; the
        /// connections stay open, so a restart that fails changes nothing.
        /// </summary>
        public void Leave(byte why, string detail = "")
        {
            byte[] utf = System.Text.Encoding.UTF8.GetBytes(detail);
            byte[] m = new byte[2 + utf.Length];
            m[0] = Protocol.Leaving;
            m[1] = why;
            utf.CopyTo(m, 2);
            var sends = AllSessions().Select(s => System.Threading.Tasks.Task.Run(() => { try { s.Link.Send(s.Stream, m); } catch { } })).ToArray();
            try { System.Threading.Tasks.Task.WaitAll(sends, 1000); } catch { }
            _status(why switch
            {
                Protocol.LeavingUpdating => "Told the connected PCs this PC is updating Kova.",
                Protocol.LeavingRestarting => "Told the connected PCs this PC is restarting.",
                _ => "Told the connected PCs hosting is stopping.",
            });
        }

        private void Broadcast(string msg)
        {
            foreach (var s in AllSessions())
                ThreadPool.QueueUserWorkItem(_ => { try { Protocol.SendMessage(s.Link, s.Stream, msg); } catch { } });
        }

        private void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch { if (_stop) return; Thread.Sleep(20); continue; }
                var ra = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
                if (ra.IsIPv4MappedToIPv6) ra = ra.MapToIPv4();
                // A guessing address is no longer turned away here, before a word is read: its file and screen lanes
                // (proven with the session key) and PCs holding a device ticket must still get in. Serve refuses its
                // logins once it has read the answer; the handshake caps (BeginHandshake) still bound what it can hold.
                // Out of threads or memory under a flood: drop this one, never the whole host.
                try { new Thread(() => Serve(tcp)) { IsBackground = true, Name = "Kova session" }.Start(); }
                catch { try { tcp.Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// One connection, from its hello to the end of its session. relayedAs: a connection that came through
        /// a Kova Relay (RelayHost), with the address it stands for (RelayHost.Stand): every limit and
        /// match by address then works on the controlling PC's own address, not the relay's.
        /// </summary>
        internal void Serve(TcpClient tcp, IPAddress? relayedAs = null)
        {
            var remote = relayedAs ?? ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            Session? s = null;
            bool handedOff = false, counted = false;
            try
            {
                // Refused at once, holding nothing: too many logins under way from here, or in all.
                if (!BeginHandshake(remote, tcp)) { tcp.Dispose(); return; }
                counted = true;
                // The deadline is kept by the reads themselves (ReadBefore): a timer on the thread pool fired
                // late whenever the pool was busy, and a login trickled in a byte at a time stayed open 26 s.
                long deadline = Environment.TickCount64 + LoginDeadlineMs;
                tcp.NoDelay = true;
                tcp.SendTimeout = 5000; // a PC that stops reading is dropped, never waited on forever
                var stream = tcp.GetStream();

                byte[] nonce = RandomNumberGenerator.GetBytes(16);
                using var exchange = Protocol.NewExchange();
                // Signed with this host's lasting identity: the controlling PC checks it is the PC it knows before it proves anything.
                byte[] hello = Protocol.MakeHello(nonce, _salt, exchange, Identity);
                stream.Write(hello);

                byte[] answer = new byte[Protocol.AnswerBytes];
                // The whole login time for the answer, not 3 seconds: a line with a 3 second round trip could
                // never log in, and the controller was told the login was damaged.
                ReadBefore(stream, answer, deadline);
                // Damaged on the way (or junk): hang up. Never counted as a wrong password, so
                // a bad link cannot get a PC blocked, and the client simply tries again.
                if (!Protocol.CheckOk(answer)) { tcp.Dispose(); return; }
                if (answer.AsSpan(0, 4).SequenceEqual(Protocol.VideoMagic))
                {
                    // A controller watching the screen: proven like a file lane, then it has a thread of its own.
                    Session? owner = null;
                    for (int i = 0; i < 40 && owner == null; i++)
                    {
                        owner = LaneOwner(answer.AsSpan(4, 8), remote);
                        if (owner == null) Thread.Sleep(50);
                    }
                    if (owner == null) { tcp.Dispose(); return; }
                    if (!CryptographicOperations.FixedTimeEquals(answer.AsSpan(Protocol.LaneProofAt, 32),
                            Protocol.LaneProof(owner.Key, nonce, answer.AsSpan(12, 16), answer.AsSpan(28, 16))))
                    { tcp.Dispose(); return; }
                    EndHandshake(remote, tcp); counted = false; // a viewer must not hold a login's place
                    using var link = new SecureLink(owner.Key, owner.HostNonce, owner.ClientNonce, isHost: true,
                        Protocol.VideoPurpose(nonce, answer.AsSpan(28, 16)), maxReceive: 64 << 10, maxSend: SecureLink.MaxSend);
                    VideoLane(owner, tcp, link);
                    return;
                }
                if (answer.AsSpan(0, 4).SequenceEqual(Protocol.FileMagic))
                {
                    // The controller's second connection, for files.
                    // It can arrive a moment before the main connection is registered.
                    Session? owner = null;
                    for (int i = 0; i < 40 && owner == null; i++)
                    {
                        owner = LaneOwner(answer.AsSpan(4, 8), remote);
                        if (owner == null) Thread.Sleep(50);
                    }
                    if (owner == null) { tcp.Dispose(); return; }
                    // Only the PC that logged in can open a lane: checked before its transfers are touched.
                    if (!CryptographicOperations.FixedTimeEquals(answer.AsSpan(Protocol.LaneProofAt, 32),
                            Protocol.LaneProof(owner.Key, nonce, answer.AsSpan(12, 16), answer.AsSpan(28, 16))))
                    { tcp.Dispose(); return; }
                    // One more lane for the controller's channel. A channel this PC already has
                    // (the controller's main connection dropped and came back: kept, or still on
                    // its old session that has not timed out yet) carries on with what it was doing.
                    byte[] channel = answer[12..28];
                    FileChannel files;
                    lock (_gate)
                    {
                        // The main connection may have ended a moment ago: a channel attached to it now would
                        // never be closed, and could replace the one kept for its transfers to carry on.
                        if (!_controllers.Contains(owner)) { tcp.Dispose(); return; }
                        if (owner.Files is FileChannel mine && !mine.Gone && mine.Id.AsSpan().SequenceEqual(channel)) files = mine;
                        else
                        {
                            bool Same(FileChannel f) => !f.Gone && f.Id.AsSpan().SequenceEqual(channel);
                            var stale = _controllers.Find(c => c != owner && c.Files is FileChannel f && Same(f));
                            var kept = _kept.Find(Same);
                            if (stale != null) { files = stale.Files!; stale.Files = null; }
                            else if (kept != null) { files = kept; _kept.Remove(kept); files.Reattached(); }
                            else
                            {
                                files = new FileChannel(channel)
                                {
                                    TextReceived = text => ClipboardReceived?.Invoke(text),
                                    FilesReceived = paths => ClipboardFilesReceived?.Invoke(paths),
                                    Progress = t => TransferProgress?.Invoke(t),
                                };
                            }
                            if (owner.Files != files) owner.Files?.Dispose();
                            owner.Files = files;
                            var o = owner;
                            files.Rate = () => Volatile.Read(ref o.Pace);
                            // Sending into a folder of its choosing is for a PC still controlling this one,
                            // like every other remote tool. (When this is the service, the files are written
                            // as the signed-in user, like everything else received there: FolderUser.)
                            files.MayChooseFolder = () => IsController(o);
                            files.FolderUser = FileUser;
                            files.PeerName = owner.Address.ToString();
                        }
                    }
                    files.AddLane(tcp, new SecureLink(owner.Key, owner.HostNonce, owner.ClientNonce, isHost: true, FileChannel.LanePurpose(nonce, answer.AsSpan(28, 16))));
                    handedOff = true;
                    return;
                }
                byte[] clientNonce = answer[4..20];
                byte role = 0;
                byte[]? key = null;
                // An answer that passed the check but is not a valid key exchange is junk or a forgery:
                // treated exactly like a wrong password (slowed, counted), never as damage.
                byte[]? shared = answer.AsSpan(0, 4).SequenceEqual(Protocol.Magic)
                    ? Protocol.SharedSecret(exchange, answer.AsSpan(Protocol.AnswerPublicAt, Protocol.PublicKeyBytes))
                    : null;
                exchange.Dispose(); // this login's private key is done with: nothing can recreate the secret now
                // A PC that logged in before shows the ticket it was given: it gets in while strangers are being turned
                // away, and its wrong passwords are counted against its ticket, not its (perhaps shared) address. The
                // ticket travels masked with this login's own secret (Protocol.TicketPad), so nobody watching the line,
                // and no relay, can read it and use it. Past a day's budget of wrong passwords that came with tickets,
                // tickets stop counting for anything until the day is over.
                byte[] ticket = answer[Protocol.AnswerTicketAt..(Protocol.AnswerTicketAt + HostIdentity.TicketBytes)];
                bool ticketed = false;
                if (shared != null)
                {
                    byte[] pad = Protocol.TicketPad(shared, nonce, clientNonce);
                    for (int i = 0; i < ticket.Length; i++) ticket[i] ^= pad[i];
                    ticketed = Identity.TicketOk(ticket) && TicketsTrusted();
                }
                IPAddress guessFrom = ticketed ? TicketBlock(ticket.AsSpan(0, 16)) : remote;
                if (ticketed) MarkKnown(guessFrom);
                // This address (or ticket) keeps guessing, or strangers are being turned away: said so (no password is
                // checked, so this tells a guesser nothing), so the controlling PC stops trying instead of trying again.
                if (Blocked(guessFrom) || (Volatile.Read(ref _refusing) >= MaxRefusing && !Known(guessFrom)))
                {
                    if (shared != null) CryptographicOperations.ZeroMemory(shared);
                    EndHandshake(remote, tcp); counted = false;
                    byte[] away = new byte[Protocol.ReplyBytes];
                    away[0] = Protocol.ReplyTurnedAway;
                    Protocol.AddCheck(away);
                    try { stream.Write(away); } catch { }
                    tcp.Dispose();
                    return;
                }
                if (shared != null && !StartGuess(guessFrom)) { CryptographicOperations.ZeroMemory(shared); tcp.Dispose(); return; }
                bool guessing = shared != null;
                try
                {
                if (shared != null)
                {
                    var head = answer.AsSpan(0, Protocol.AnswerProofAt);
                    var proof = answer.AsSpan(Protocol.AnswerProofAt, 32);
                    byte[] control = Protocol.SessionKey(_key, shared, hello, head);
                    byte[]? listen = _listenKey == null ? null : Protocol.SessionKey(_listenKey, shared, hello, head);
                    CryptographicOperations.ZeroMemory(shared);
                    if (CryptographicOperations.FixedTimeEquals(proof, Protocol.Proof(control, 'C', nonce, clientNonce)))
                    { role = Protocol.RoleControl; key = control; }
                    else if (listen != null && CryptographicOperations.FixedTimeEquals(proof, Protocol.Proof(listen, 'C', nonce, clientNonce)))
                    { role = Protocol.RoleListen; key = listen; }
                }
                }
                finally { if (guessing) EndGuess(guessFrom); } // never left counted: five leaks would lock that address out until hosting restarts
                if (key == null)
                {
                    EndHandshake(remote, tcp); counted = false; // give the slot back before the slow part, so guesses cannot hold slots
                    if (ticketed) TicketFailed();
                    string? note = WrongPassword(guessFrom);
                    if (note != null) _status(note);
                    Interlocked.Increment(ref _refusing);
                    try
                    {
                        Thread.Sleep(WrongPasswordDelayMs); // slows down anyone guessing passwords
                        byte[] refuse = new byte[Protocol.ReplyBytes]; // the same size as an acceptance
                        Protocol.AddCheck(refuse);
                        try { stream.Write(refuse); } catch { }
                    }
                    finally { Interlocked.Decrement(ref _refusing); }
                    tcp.Dispose();
                    return;
                }

                byte[] token = RandomNumberGenerator.GetBytes(8);
                byte[] accept = new byte[Protocol.ReplyBytes];
                accept[0] = 1;
                accept[1] = role;
                token.CopyTo(accept, 2);
                Protocol.Proof(key, 'H', clientNonce, nonce).CopyTo(accept, 10);
                Protocol.AddCheck(accept);
                stream.Write(accept);
                // The client pings every 2 seconds; 10 silent seconds means it is gone,
                // and ending the session lets go of any keys it was holding.
                stream.ReadTimeout = 10_000;

                s = new Session
                {
                    Tcp = tcp, Stream = stream, Token = token, Address = remote, Role = role,
                    Key = key, HostNonce = nonce, ClientNonce = clientNonce,
                    // A listener only ever sends pings and tiny control messages, so it cannot make the host hold 8 MB.
                    Link = new SecureLink(key, nonce, clientNonce, isHost: true, maxReceive: role == Protocol.RoleListen ? 64 << 10 : SecureLink.MaxMessage),
                };
                s.Link.Send(s.Stream, Protocol.FeaturesMessage(SecureAttention != null ? Protocol.FeatureSecureAttention : 0));
                // How to wake this PC later (its network cards), and anything about it already worth knowing.
                if (role == Protocol.RoleControl && Wake.Describe() is { Length: > 0 } cards) Send(s, Protocol.WakeInfo, cards);
                // A fresh ticket for next time (kept by the controlling PC with this host's identity).
                if (role == Protocol.RoleControl) s.Link.Send(s.Stream, TicketMessage());
                if (role == Protocol.RoleControl && Volatile.Read(ref _lastHealth) is HealthSample health)
                    foreach (string line in HealthWatch.Standing(health)) Send(s, Protocol.HealthAlert, line);
                EndHandshake(remote, tcp);
                MarkKnown(remote); // the proof verified: this block keeps reserved slots in future floods
                counted = false;
                lock (_gate)
                {
                    if (_stop) { End(s, null); return; }
                    if (role == Protocol.RoleControl)
                    {
                        // The same PC coming back after its connection broke: its old session, silent for
                        // a while now, is let go at once (keys released), not after its 10 seconds.
                        long now = Environment.TickCount64;
                        foreach (var old in _controllers.Where(c => c.Address.Equals(remote) && now - Volatile.Read(ref c.LastMessage) > 5000).ToList()) // pings come every 2 s: more than two missed
                        {
                            _controllers.Remove(old);
                            End(old, null);
                        }
                        if (_controllers.Count >= MaxControllers)
                        {
                            End(s, "There are already " + MaxControllers + " PCs controlling this one.");
                            s = null;
                            handedOff = true; // End says why and closes it; closing it here first lost the message
                            return;
                        }
                        _controllers.Add(s);
                    }
                    else
                    {
                        if (_listeners.Count >= MaxListeners)
                        {
                            End(s, "There are already " + MaxListeners + " people listening.");
                            s = null;
                            handedOff = true;
                            return;
                        }
                        _listeners.Add(s);
                    }
                    Rebuild();
                }
                _status((role == Protocol.RoleControl ? "Connected: " : "Listening: ") + remote + ".");

                ReadLoop(s);
            }
            catch
            {
                // A login that was abandoned or was not TailRemote: dropped without a word,
                // so a flood of them cannot flood the log.
            }
            finally
            {
                if (counted) EndHandshake(remote, tcp);
                if (s != null)
                {
                    bool was = false;
                    lock (_gate)
                    {
                        if (_controllers.Remove(s) || _listeners.Remove(s)) was = true;
                        if (was) { End(s, null); Rebuild(); }
                    }
                    if (was) _status((s.Role == Protocol.RoleControl ? "Disconnected: " : "Stopped listening: ") + remote + ".");
                    else
                    {
                        // Never registered (the host was stopping, or the line broke before it was): its keys
                        // (Windows key handles) go now too, as End lets them go, not whenever the collector runs.
                        try { s.Tcp.Dispose(); } catch { }
                        try { s.Link.Dispose(); } catch { }
                    }
                }
                else if (!handedOff) tcp.Dispose();
                Native.LeaveDesktop();
            }
        }

        /// <summary>Fills buffer, or throws once the login's deadline has passed, however the bytes trickle in.</summary>
        private static void ReadBefore(NetworkStream stream, byte[] buffer, long deadline)
        {
            int got = 0;
            while (got < buffer.Length)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0) throw new TimeoutException();
                stream.ReadTimeout = (int)Math.Max(1, left);
                int n = stream.Read(buffer, got, buffer.Length - got);
                if (n <= 0) throw new System.IO.EndOfStreamException();
                got += n;
            }
        }

        private bool IsController(Session s) { lock (_gate) return _controllers.Contains(s); }

        // ---- The remote screen ----

        /// <summary>
        /// The screen as updates for each viewer (ScreenVideo.Update: null, no picture to take; empty,
        /// nothing changed). This PC's own screen unless set: the service hands it to its agent, which
        /// is at the screen (Agent.StartHosting). Nothing is taken unless someone watches.
        /// </summary>
        public Func<int, bool, VideoSettings, byte[]?> VideoUpdate { get; set; }
        public Action<int> VideoForget { get; set; }
        private int _nextViewer, _viewers;
        private ScreenSource? _screenSource; // this PC's own screen (not the service's: its agent has its own); let go of with the host
        public int TestViewers => Volatile.Read(ref _viewers);

        /// <summary>Test only: the line to every controller is no faster than this (bytes a second), as on a slow connection.</summary>
        public static double TestPaceCap = double.MaxValue;

        /// <summary>Test only: the remote screen's step for the controllers (the highest: the worst picture), or -1.</summary>
        public int TestVideoStep { get { lock (_gate) return _controllers.Count == 0 ? -1 : _controllers.Max(c => c.VideoStep); } }

        /// <summary>
        /// One controller watching: a picture, then wait until it is on their screen, then the next, no
        /// faster than the line to them carries (the pace the controller works out from the sound's ping
        /// while it watches, the same as for files). How good the pictures are follows the line by itself,
        /// like the sound: down a step (VideoSettings.Ladder) the moment pictures are slow to arrive or
        /// would take more than that pace, up a step after a while of quick ones, up to every pixel exact.
        /// After a step down it does not climb back above it for a while (30 s, doubling, up to 5 minutes),
        /// and the step is kept for the controller's next video line. A still screen is only looked at
        /// again when Windows says it changed (and climbs, sent again whole at the better quality).
        /// While things move, H.264 frames go up to 60 a second, up to 4 on their way at once, at most of
        /// the pace (ScreenVideo decides which).
        /// </summary>
        private void VideoLane(Session owner, TcpClient tcp, SecureLink link)
        {
            var stream = tcp.GetStream();
            tcp.NoDelay = true;
            tcp.SendTimeout = 60_000; // a whole picture on a slow line takes a while: judged by the steps, not cut off
            stream.ReadTimeout = Timeout.Infinite; // the reader below ends when the line does
            var sendGate = new object();
            void Send(byte type, ReadOnlySpan<byte> body)
            {
                byte[] m = new byte[1 + body.Length];
                m[0] = type;
                body.CopyTo(m.AsSpan(1));
                lock (sendGate) link.Send(stream, m);
            }
            // One screen connection per controller: a viewer that reconnected after its line stalled would
            // otherwise be counted against its own dead connections, which never end on their own (still
            // pictures are small enough to keep buffering), until it was told 8 PCs were already watching.
            var older = Interlocked.Exchange(ref owner.Video, tcp);
            if (older != null)
            {
                try { older.Dispose(); } catch { }
                // Its place is this one's: a moment for it to give it back (its thread ends once its line is closed).
                for (int i = 0; i < 100 && Volatile.Read(ref _viewers) >= Protocol.MaxViewers; i++) Thread.Sleep(50);
            }
            if (Interlocked.Increment(ref _viewers) > Protocol.MaxViewers)
            {
                try { Send(Protocol.VideoNote, System.Text.Encoding.UTF8.GetBytes("No picture: " + Protocol.MaxViewers + " PCs are already watching this screen.")); } catch { }
                Interlocked.Decrement(ref _viewers);
                Interlocked.CompareExchange(ref owner.Video, null, tcp);
                return;
            }
            int viewer = Interlocked.Increment(ref _nextViewer);
            var priority = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; // keys and sound always come first
            // Where this controller's pictures stood last time (a dropped line comes back where it was);
            // the first time, from the pace: a slow line does not start with big pictures.
            double startPace = Math.Min(TestPaceCap, Volatile.Read(ref owner.Pace));
            int step = owner.VideoStep >= 0 ? owner.VideoStep
                : startPace < (64 << 10) ? VideoSettings.Ladder.Length - 1
                : startPace < (256 << 10) ? VideoSettings.Ladder.Length - 2
                : startPace < (1 << 20) ? VideoSettings.Ladder.Length - 3
                : VideoSettings.Start;
            int wholeStep = int.MaxValue; // the step the last whole picture went at
            long goodSince = Environment.TickCount64;
            int slow = 0;
            int whole = 1, paused = 0, closed = 0; // whole: set by either thread, taken by the loop (Interlocked)
            int noH264 = 0; // the viewer cannot show H.264: still pictures only, for this line
            int screen = 0; // which of this PC's screens the viewer asked for (0: the main one); ScreenVideo checks it
            uint acked = 0;
            long[] ackTimes = new long[8]; // when each of the last frames' confirmations came (by number & 7)
            var got = new AutoResetEvent(false);
            bool MayClimb(long t) => step > 0 && (step - 1 >= owner.VideoFloor || t > owner.VideoFloorUntil);
            void StepDown(long t, int by)
            {
                int was = step;
                step = Math.Min(VideoSettings.Ladder.Length - 1, step + by);
                slow = 0; goodSince = t;
                // Longer only when it falls again soon after climbing back (a line that cannot hold the
                // better step, so it would go up and down for ever); a burst of steps down is one fall.
                if (t - owner.VideoLastDown > 120_000) owner.VideoBackoffMs = 30_000;
                else if (owner.VideoClimbed) owner.VideoBackoffMs = Math.Min(owner.VideoBackoffMs * 2, 300_000);
                owner.VideoClimbed = false;
                owner.VideoLastDown = t; owner.VideoFloor = step; owner.VideoFloorUntil = t + owner.VideoBackoffMs;
                if (VideoSettings.Ladder[step].MaxWidth != VideoSettings.Ladder[was].MaxWidth) Volatile.Write(ref whole, 1);
            }
            var reader = new Thread(() =>
            {
                byte[]? b = null;
                try
                {
                    while (true)
                    {
                        int n = link.Receive(stream, ref b);
                        if (n < 1) continue;
                        switch (b![0])
                        {
                            case Protocol.VideoGot when n == 5:
                                {
                                    uint g = BitConverter.ToUInt32(b, 1);
                                    Volatile.Write(ref ackTimes[g & 7], Environment.TickCount64); // stamped first: read only once acked says g
                                    Volatile.Write(ref acked, g);
                                    got.Set();
                                    break;
                                }
                            case Protocol.VideoAgain: Volatile.Write(ref whole, 1); got.Set(); break;
                            // The viewer cannot show H.264 (no decoder, or it keeps failing): asking for key
                            // frames for ever would leave its picture frozen, so still pictures only.
                            case Protocol.VideoNoH264: Volatile.Write(ref noH264, 1); Volatile.Write(ref whole, 1); got.Set(); break;
                            // Counted as well as kept (Screen = index, plus how many asks so far above it): asking for the
                            // same screen again, after it was unplugged and is back, must count as a new ask.
                            case Protocol.VideoChooseScreen when n == 2: Volatile.Write(ref screen, b[1] | ((((Volatile.Read(ref screen) >> 8) + 1) & 0xFFFFFF) << 8)); Volatile.Write(ref whole, 1); got.Set(); break;
                            case Protocol.VideoPause when n == 2:
                                Volatile.Write(ref paused, b[1] != 0 ? 1 : 0);
                                if (b[1] == 0) Volatile.Write(ref whole, 1);
                                got.Set();
                                break;
                        }
                    }
                }
                catch { }
                Volatile.Write(ref closed, 1);
                try { got.Set(); } catch { } // the lane may be gone (and its event with it) by now
            }) { IsBackground = true, Name = "Kova remote screen in" };
            reader.Start();
            try
            {
                uint number = 0;
                bool lastWasVideo = false;
                bool resting = false; // the viewer is minimized and its copies of the screen and encoder were let go of
                long lastSent = Environment.TickCount64;
                uint ackSeen = 0;
                long ackSeenAt = lastSent; // when a confirmation last came (or nothing was waiting for one)
                string? said = null;
                // The screen choice whose answer (ScreenNote) was last passed on. The agent can take a choice after
                // the wait for it ran out (TooSlow); its answer then went nowhere, and the agent, having answered,
                // never said it again: the picture switched, but "Screen 2 of 3" was never heard.
                int noted = -1; // nothing passed on yet: the very first answer, lost the same way, is asked again too
                int reasked = -1; // the ask made here last: once, so a busy agent does not say the screen line over and over
                // How long frames take to be confirmed: the least lately (the line's own delay) against the newest.
                // A queue building up on the way shows as the newest growing well past the least.
                long[] sentTimes = new long[8];
                uint latencyOf = 0;
                long latency = 0, leastNow = long.MaxValue, leastBefore = long.MaxValue, leastSince = 0;
                while (!_stop && Volatile.Read(ref closed) == 0 && IsController(owner))
                {
                    long now = Environment.TickCount64;
                    owner.VideoStep = step;
                    if (Volatile.Read(ref paused) == 1)
                    {
                        // Minimized: what is kept for this viewer (its copies of the screen, its H.264 encoder) goes
                        // until it looks again; it then starts with a whole picture (the unpause asks for one).
                        if (!resting) { resting = true; try { VideoForget(viewer); } catch { } }
                        ackSeenAt = now; // minimized: nothing is waited for meanwhile
                        if (now - lastSent > 5000) { Send(Protocol.VideoStill, default); lastSent = now; }
                        got.WaitOne(1000);
                        continue;
                    }
                    resting = false;
                    // Pictures on their way and not one confirmed for a minute: the line is dead (a stalled
                    // connection still takes small pictures into its buffers), so it ends and gives its place back.
                    uint ackNow = Volatile.Read(ref acked);
                    if (ackNow != ackSeen || number == ackNow) { ackSeen = ackNow; ackSeenAt = now; }
                    else if (now - ackSeenAt > 60_000) break;
                    // H.264's share of the line while things move: most of the pace, less on the lower steps.
                    double linePace = Math.Max(16 << 10, Math.Min(TestPaceCap, Volatile.Read(ref owner.Pace)));
                    int kbps = (int)Math.Clamp(linePace * 8 / 1000 * 0.7 * Math.Pow(0.7, Math.Max(0, step - VideoSettings.Start)), 250, 40_000);
                    var settings = VideoSettings.Ladder[step] with { VideoKbps = Volatile.Read(ref noH264) == 1 ? 0 : kbps, Screen = Volatile.Read(ref screen) };
                    int fps = settings.Fps;
                    bool all = Interlocked.Exchange(ref whole, 0) == 1;
                    byte[]? update;
                    try { update = VideoUpdate(viewer, all, settings); }
                    catch { update = null; }
                    if (ReferenceEquals(update, AgentLink.TooSlow))
                    {
                        // The agent did not have the picture ready in time (a big screen, a busy PC): that
                        // is the screen being too much at this step, not "no picture". A step down, quietly.
                        if (step < VideoSettings.Ladder.Length - 1) StepDown(now, 1);
                        Volatile.Write(ref whole, 1);
                        // A choice not answered yet is asked again (as one more ask: a choice is always answered).
                        int sc = Volatile.Read(ref screen);
                        if (settings.Screen != noted && settings.Screen != reasked && settings.Screen == sc)
                        {
                            int again = (sc & 0xFF) | ((((sc >> 8) + 1) & 0xFFFFFF) << 8);
                            if (Interlocked.CompareExchange(ref screen, again, sc) == sc) reasked = again;
                        }
                        // Something every 5 seconds, as everywhere else here: each of these waits 5 seconds for the
                        // agent, and three in a row ran past the viewer's 15 seconds, so it hung up and dialled again.
                        if (now - lastSent > 5000) { Send(Protocol.VideoStill, default); lastSent = now; }
                        // A moment before asking again: when the agent's queue is full the answer comes at
                        // once, and asking straight back would spin a core for every viewer.
                        long retry = Environment.TickCount64 + 250;
                        while (Volatile.Read(ref closed) == 0 && Environment.TickCount64 < retry) got.WaitOne((int)Math.Max(1, retry - Environment.TickCount64));
                        continue;
                    }
                    if (update == null)
                    {
                        string why = FileUser != null
                            ? "No picture: there is no screen to show at the moment. It comes as soon as there is."
                            : "No picture right now. The lock screen and administrator prompts can only be seen when Kova runs as a Windows service there.";
                        if (said != why) { Send(Protocol.VideoNote, System.Text.Encoding.UTF8.GetBytes(why)); said = why; lastSent = now; }
                        else if (now - lastSent > 5000) { Send(Protocol.VideoStill, default); lastSent = now; }
                        Volatile.Write(ref whole, 1);
                        got.WaitOne(1000);
                        continue;
                    }
                    said = null;
                    if (update.Length == 0)
                    {
                        // Still: nothing to measure the line by, so the last good measures stand, and the
                        // picture climbs as it would have. Better now than the last whole picture: all of
                        // it again, at this quality (a still screen is when there is room for it).
                        if (MayClimb(now) && now - goodSince > (step >= VideoSettings.Start ? 2000 : 5000)) { step--; goodSince = now; owner.VideoClimbed = true; }
                        // (Only after still pictures: while things move, a whole picture would be a key frame.)
                        if (!lastWasVideo && step < wholeStep) { Volatile.Write(ref whole, 1); continue; }
                        if (now - lastSent > 5000) { Send(Protocol.VideoStill, default); lastSent = now; }
                        // Taking the screen already waited for a change (Windows says the moment there is
                        // one), so only a moment more here: anything new shows straight away.
                        got.WaitOne(5);
                        continue;
                    }
                    if (update[0] == ScreenVideo.ScreenNote)
                    {
                        // Not a picture: which screen is shown and how many there are (first, and after a
                        // choice). Passed on as it is; the picture itself comes next time round, whole.
                        Send(Protocol.VideoScreen, update.AsSpan(1));
                        noted = settings.Screen;
                        lastSent = now;
                        Volatile.Write(ref whole, 1);
                        continue;
                    }
                    if (update.Length + 5 > SecureLink.MaxSend - 1024)
                    {
                        // Too big to send at all (lossless on an enormous screen): the next step down.
                        StepDown(now, 1);
                        Volatile.Write(ref whole, 1);
                        continue;
                    }
                    bool video = update[0] == ScreenVideo.VideoFrame;
                    lastWasVideo = video;
                    // Whole by its own flag, not only when asked for here: the whole exact picture sent by itself
                    // once motion stops was judged as a small change, so ordinary scroll-and-stop stepped it down.
                    if (!video && update.Length > 5 && (update[5] & 1) != 0) all = true;
                    if (all && !video) wholeStep = step;
                    number++;
                    byte[] m = new byte[5 + update.Length];
                    m[0] = Protocol.VideoPicture;
                    BitConverter.TryWriteBytes(m.AsSpan(1), number);
                    update.CopyTo(m, 5);
                    long sentAt = Environment.TickCount64; // before sending: the time the line takes to carry it counts
                    sentTimes[number & 7] = sentAt;
                    lock (sendGate) link.Send(stream, m);
                    lastSent = Environment.TickCount64;
                    if (video)
                    {
                        // Moving: up to 4 frames on their way at once, so 60 a second gets through a line
                        // with a delay; never more, so nothing queues up. Not getting through in 2 seconds:
                        // a step down and a key frame.
                        while (Volatile.Read(ref closed) == 0 && number - Volatile.Read(ref acked) >= 4 && Environment.TickCount64 - sentAt < 2000) got.WaitOne(10);
                        long tv = Environment.TickCount64;
                        long roomWait = tv - lastSent;
                        uint seen = Volatile.Read(ref acked);
                        if (seen != latencyOf && number - seen < 8)
                        {
                            latencyOf = seen;
                            // From its sending to its confirmation coming, not to now: measured to now, a pause in
                            // the motion (or a lower frame rate) looked like a queue, and climbing stopped.
                            latency = Volatile.Read(ref ackTimes[seen & 7]) - sentTimes[seen & 7];
                            if (tv - leastSince > 10_000) { leastBefore = leastNow; leastNow = long.MaxValue; leastSince = tv; }
                            leastNow = Math.Min(leastNow, latency);
                        }
                        long least = Math.Min(leastNow, leastBefore);
                        if (number - Volatile.Read(ref acked) >= 4)
                        {
                            StepDown(tv, 1);
                            Volatile.Write(ref whole, 1);
                            // The key frame waits until the line has room for it again (fewer than 4 on
                            // their way): sent now, frames in flight would only grow on a line that is
                            // already behind. Not in 15 seconds either: the loop goes round and tries again.
                            while (Volatile.Read(ref closed) == 0 && number - Volatile.Read(ref acked) >= 4 && Environment.TickCount64 - tv < 15_000) got.WaitOne(50);
                            continue;
                        }
                        // The line is full (no climbing) only when room for the next frame was long in coming. Frames
                        // on their way alone are only the line's delay: at 60 a second, 3 of them is any ping over
                        // 50 ms, and moving pictures never climbed back after a fall on an ordinary internet line.
                        if (roomWait > 250 || (least != long.MaxValue && latency > least * 2 + 40)) goodSince = tv;
                        else if (MayClimb(tv) && tv - goodSince > (step >= VideoSettings.Start ? 2000 : 5000)) { step--; goodSince = tv; owner.VideoClimbed = true; }
                        // At most 60 a second, and no faster than the line carries: until then, whatever wakes
                        // this (each confirmation does) it goes back to waiting.
                        long frameDue = now + (long)Math.Max(1000.0 / 60, m.Length * 1000.0 / linePace);
                        while (Volatile.Read(ref closed) == 0 && Environment.TickCount64 < frameDue) got.WaitOne((int)Math.Max(1, frameDue - Environment.TickCount64));
                        continue;
                    }
                    // A still picture: on its way alone; the next waits until this one is on their screen.
                    while (Volatile.Read(ref closed) == 0 && Volatile.Read(ref acked) != number && Environment.TickCount64 - sentAt < 15_000) got.WaitOne(500);
                    if (Volatile.Read(ref closed) == 1) break;
                    long t = Environment.TickCount64;
                    if (Volatile.Read(ref acked) != number)
                    {
                        // Not on their screen in 15 seconds: far too much for this line. Two steps down,
                        // and a whole picture at that size, rather than giving up.
                        StepDown(t, 2);
                        Volatile.Write(ref whole, 1);
                        continue;
                    }
                    long tookMs = t - sentAt;
                    double pace = Math.Max(16 << 10, Math.Min(TestPaceCap, Volatile.Read(ref owner.Pace)));
                    double lineMs = m.Length * 1000.0 / pace;
                    // Following the line: slow to arrive (twice running), or more than the pace allows at
                    // this many pictures a second, is a step down; a while of quick ones, a step up. A
                    // whole picture is expected to take longer, so it only counts when very slow.
                    bool tooSlow = all ? tookMs > 3000 : tookMs > 600 || lineMs > 1000.0 / fps * 1.5;
                    slow = tooSlow ? slow + 1 : 0;
                    if (slow >= 2 && step < VideoSettings.Ladder.Length - 1) StepDown(t, 1);
                    else if (tooSlow || tookMs > 250 || lineMs > 1000.0 / fps * 0.5) { if (!all) goodSince = t; }
                    else if (MayClimb(t) && t - goodSince > (step >= VideoSettings.Start ? 2000 : 5000))
                    {
                        int was = step;
                        step--; goodSince = t; owner.VideoClimbed = true;
                        if (VideoSettings.Ladder[step].MaxWidth != VideoSettings.Ladder[was].MaxWidth) Volatile.Write(ref whole, 1);
                    }
                    long due = now + (long)Math.Max(1000.0 / fps, lineMs);
                    while (Volatile.Read(ref closed) == 0 && Volatile.Read(ref whole) == 0 && Environment.TickCount64 < due) got.WaitOne((int)Math.Max(1, due - Environment.TickCount64));
                }
            }
            catch { }
            finally
            {
                owner.VideoStep = step;
                Volatile.Write(ref closed, 1);
                Interlocked.CompareExchange(ref owner.Video, null, tcp);
                try { tcp.Dispose(); } catch { }
                // Its place is given back at once, before the slower tidying below (the agent, the reader): a
                // replacing connection from the same PC waits for it and must not be told the screen is full.
                Interlocked.Decrement(ref _viewers);
                try { VideoForget(viewer); } catch { }
                // The reader ends with the line closed above; only then are the event and keys let go
                // (a handle each per screen opened, otherwise left to the collector).
                if (reader.Join(2000)) { got.Dispose(); try { link.Dispose(); } catch { } }
                Thread.CurrentThread.Priority = priority;
            }
        }

        private void ReadLoop(Session s)
        {
            while (!_stop)
            {
                byte[] m = s.Link.Receive(s.Stream);
                Volatile.Write(ref s.LastMessage, Environment.TickCount64);
                if (m.Length == 0) continue;
                switch (m[0])
                {
                    case Protocol.Key when m.Length == 6:
                        ushort vk = BitConverter.ToUInt16(m, 1);
                        ushort scan = BitConverter.ToUInt16(m, 3);
                        bool up = (m[5] & 1) != 0, ext = (m[5] & 2) != 0;
                        lock (_gate)
                        {
                            // Only connected controllers type. A closed session must not press
                            // anything after its keys were released.
                            if (!_controllers.Contains(s)) break;
                            lock (s.Held)
                            {
                                if (s.LetGo.Count > 0)
                                {
                                    // KeySafety let go of modifiers while this PC was quiet. Its key-up for one means the
                                    // user really let go (TCP keeps the order, so it comes before any later key); any other
                                    // key pressed first means they were held all along: pressed again before it, so
                                    // Ctrl+A stays Ctrl+A and never becomes a plain A.
                                    if (up) s.LetGo.Remove((vk, ext));
                                    else
                                    {
                                        foreach (var k in s.LetGo)
                                            if (k != (vk, ext) && s.Held.Add(k)) Native.SendKey(k.Vk, 0, false, k.Ext);
                                        s.LetGo.Clear();
                                    }
                                }
                                if (up) s.Held.Remove((vk, ext)); else s.Held.Add((vk, ext));
                                Native.SendKey(vk, scan, up, ext);
                            }
                        }
                        break;
                    case Protocol.WakeRequest when m.Length >= 7 && (m.Length - 1) % 6 == 0 && m.Length <= 1 + 6 * 8 && IsController(s):
                        {
                            // At most once a second per PC (each card is a few dozen packets onto this network); every
                            // card of the PC to wake comes in the one request, so none is lost to that limit.
                            long at = Environment.TickCount64;
                            if (at - s.LastWakeAt < 1000) break;
                            s.LastWakeAt = at;
                            var macs = Enumerable.Range(0, (m.Length - 1) / 6).Select(i => m[(1 + 6 * i)..(7 + 6 * i)]).ToArray();
                            ThreadPool.QueueUserWorkItem(_ => { foreach (var mac in macs) try { Wake.Send(mac, Array.Empty<IPAddress?>()); } catch { } });
                            DiagLog.Write("host: " + s.Address + " asked to wake " + string.Join(", ", macs.Select(Convert.ToHexString)));
                            break;
                        }
                    case Protocol.Mouse when m.Length == 6 && m[1] <= Protocol.MouseSideWheel: // an unknown kind never reaches the agent
                        lock (_gate)
                        {
                            if (!_controllers.Contains(s)) break; // only controllers move the pointer, like keys
                            // A wheel turn after a stall: modifiers KeySafety let go are pressed again first, so
                            // Ctrl+wheel stays a zoom (as for keys in the Key case above).
                            if (m[1] is Protocol.MouseWheel or Protocol.MouseSideWheel)
                                lock (s.Held)
                                {
                                    foreach (var k in s.LetGo) if (s.Held.Add(k)) Native.SendKey(k.Vk, 0, false, k.Ext);
                                    s.LetGo.Clear();
                                }
                            Native.SendMouse(m[1], BitConverter.ToInt16(m, 2), BitConverter.ToInt16(m, 4));
                        }
                        break;
                    case Protocol.Ping when m.Length == 9:
                        m[0] = Protocol.Pong;
                        s.Link.Send(s.Stream, m);
                        break;
                    case Protocol.ReleaseAll:
                        ReleaseHeld(s);
                        break;
                    case Protocol.SecureAttention when IsController(s):
                        if (SecureAttention == null) Protocol.SendMessage(s.Link, s.Stream, "Control Alt Delete needs the Kova service on the remote PC.");
                        else if (!SecureAttention()) Protocol.SendMessage(s.Link, s.Stream, "The remote PC could not send Control Alt Delete.");
                        break;
                    case Protocol.UpdateTo when IsController(s):
                        {
                            string version = System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1);
                            void Reply(string msg) { try { Protocol.SendMessage(s.Link, s.Stream, msg); } catch { } }
                            if (UpdateRequested == null) Reply("Kova on the remote PC cannot update itself from here.");
                            else ThreadPool.QueueUserWorkItem(_ => UpdateRequested(version, Reply));
                        }
                        break;
                    case Protocol.ListFolder when m.Length >= 5 && IsController(s):
                        {
                            byte[] id = m[1..5];
                            string path = System.Text.Encoding.UTF8.GetString(m, 5, m.Length - 5);
                            if (FilesRefusal(path) is string no) { SendListing(s, id, "E\t" + no + "\n"); break; }
                            // One listing at a time per PC: a flood of them (each waits up to a second and a
                            // half on the drives) tied up the host's pool threads, and every other PC's
                            // clipboard and Get files waited minutes behind it. Asked again while one is being
                            // listed: a few wait their turn, in order; past that, the PC is told to wait.
                            bool start = false, full = false, drives = false;
                            lock (s.ListGate)
                            {
                                if (s.ListBusy == 0) { s.ListBusy = 1; start = true; }
                                // The list of drives never waits behind a folder on a dead network share or a
                                // sleeping disk (which can take a minute): going back to This PC always works. One at a time.
                                else if (path.Length == 0 && !s.DrivesBusy) { s.DrivesBusy = true; drives = true; }
                                else if (s.ListQueue.Count < 4) s.ListQueue.Enqueue((id, path));
                                else full = true;
                            }
                            if (full) SendListing(s, id, "E\tThe remote PC is still busy with the folders asked for before. Try again in a moment.\n");
                            if (start) ThreadPool.QueueUserWorkItem(_ => ListFolders(s, id, path)); // off this thread: a slow drive must not hold up the keys behind it
                            if (drives) ThreadPool.QueueUserWorkItem(_ => { try { ListOne(s, id, ""); } finally { lock (s.ListGate) s.DrivesBusy = false; } });
                        }
                        break;
                    case Protocol.Fetch when IsController(s):
                        {
                            string[] paths = System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                            FileChannel? files;
                            lock (_gate) files = s.Files;
                            string? refused = null;
                            foreach (string one in paths) if ((refused = FilesRefusal(one)) != null) break;
                            if (files != null && refused != null) { var f2 = files; string why = refused; ThreadPool.QueueUserWorkItem(_ => { try { f2.Refuse(paths, why); } catch { } }); }
                            else if (files == null) Protocol.SendMessage(s.Link, s.Stream, "The file connection is not open yet. Try again in a moment.");
                            else if (paths.Length > 0)
                            {
                                // A thread of its own, not the pool's (a batch holds it until it has been sent), and a
                                // few at a time per PC: a flood of them starved every other PC's clipboard and folder
                                // lists. Since 4.7 a newer Get waits its turn behind the ones still going (SendFiles) instead
                                // of replacing them, each holding its thread meanwhile: a handful waiting is plenty.
                                if (Interlocked.Increment(ref s.Fetches) > 8)
                                {
                                    Interlocked.Decrement(ref s.Fetches);
                                    // Off this thread: Refuse can wait up to 10 s for file lanes, and keys are read here.
                                    ThreadPool.QueueUserWorkItem(_ => { try { files.Refuse(paths, "The remote PC already has 8 lots of files waiting to be sent. Get more once some have arrived."); } catch { } });
                                    break;
                                }
                                try
                                {
                                    new Thread(() =>
                                    {
                                        // Read as the signed-in user when this is the service (FileUser); the batch keeps the
                                        // identity to open each file, so it is let go only once the batch has ended (SendFiles
                                        // returns then).
                                        System.Security.Principal.WindowsIdentity? user = null;
                                        try
                                        {
                                            // Every way this ends reaches the controller as the end of a transfer: a message
                                            // alone left its Get files saying "getting" for good.
                                            if (FileUser != null && (user = FileUser()) == null) { files.Refuse(paths, FileChannel.NobodySignedIn); return; }
                                            files.SendFiles(paths, toClipboard: false, asUser: user);
                                        }
                                        catch (Exception e) { try { files.Refuse(paths, "The remote PC could not send them: " + e.Message); } catch { } }
                                        finally { user?.Dispose(); Interlocked.Decrement(ref s.Fetches); }
                                    }) { IsBackground = true, Name = "Kova get files" }.Start();
                                }
                                catch (Exception e)
                                {
                                    Interlocked.Decrement(ref s.Fetches);
                                    try { files.Refuse(paths, "The remote PC could not send them: " + e.Message); } catch { }
                                }
                            }
                        }
                        break;
                    case Protocol.InfoRequest when IsController(s):
                        // One at a time per PC; asked again meanwhile, the answer on its way does for both
                        // (the controlling PC waits for one answer, whichever request it was).
                        if (Interlocked.CompareExchange(ref s.InfoBusy, 1, 0) != 0) break;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            string text;
                            try
                            {
                                var (controlling, listening) = Connected;
                                // The processor reading of the health watch (the last 10 seconds), if it has one yet.
                                int cpu = Volatile.Read(ref _healthReads) > 1 && Volatile.Read(ref _lastHealth) is HealthSample h ? h.CpuPercent : -1;
                                text = RemoteTools.Info(Updater.Current + (SecureAttention != null ? ", running as the Windows service" : ""), cpu) +
                                    "\nConnected: " + controlling + " controlling" + (listening > 0 ? ", " + listening + " listening" : "");
                            }
                            catch (Exception e) { text = "Problem: could not gather this PC's information: " + e.Message; } // never ends the host
                            finally { Volatile.Write(ref s.InfoBusy, 0); }
                            try { s.Link.Send(s.Stream, Protocol.TextMessage(Protocol.Info, text)); } catch { }
                        });
                        break;
                    case Protocol.SpeedTestRequest when IsController(s):
                        // A minute apart at most (4.4): each test moves up to 1.5 GB, and back-to-back tests could run
                        // up a metered line's bill.
                        {
                            long nowTest = Environment.TickCount64, lastTest = Interlocked.Read(ref _lastSpeedTest);
                            if (nowTest - lastTest < 60_000 || Interlocked.CompareExchange(ref _lastSpeedTest, nowTest, lastTest) != lastTest)
                            {
                                try { s.Link.Send(s.Stream, Protocol.TextMessage(Protocol.SpeedResult, "The remote PC ran a speed test less than a minute ago. Try again in a minute.")); } catch { }
                                break;
                            }
                        }
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            _status("Running an internet speed test, as the controlling PC asked.");
                            string text = await SpeedTest.RunAsync();
                            try { s.Link.Send(s.Stream, Protocol.TextMessage(Protocol.SpeedResult, text)); } catch { }
                        });
                        break;
                    case Protocol.RestartPc when IsController(s):
                        _status("Restarting this PC, as the controlling PC asked.");
                        Leave(Protocol.LeavingRestarting);
                        // A normal restart: programs can still ask to save their work.
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Native.SystemTool("shutdown.exe"), "/r /t 0") { CreateNoWindow = true, UseShellExecute = false }); }
                        catch (Exception e) { Broadcast("The remote PC could not restart: " + e.Message); }
                        break;
                    case Protocol.ClipboardRequest when IsController(s) && ScreenLocked():
                        // Said, not silently dropped: the controlling PC heard nothing at all and took it for broken.
                        ThreadPool.QueueUserWorkItem(_ => { try { Protocol.SendMessage(s.Link, s.Stream, "The remote PC is locked, so its clipboard cannot be read. Sign in there first, then try again."); } catch { } });
                        break;
                    case Protocol.ClipboardRequest when IsController(s):
                        // Not while this PC is locked (4.4): the Kova password must not be a way round the Windows one.
                        // Only a controller may pull the clipboard; a listener never reaches here.
                        // Coalesce a flood of rapid pulls (a mashed or held Ctrl+Shift+B, or an old
                        // controller with no throttle): each pull reads the clipboard and fans a
                        // transfer out to every controller, and the host window's message pump drowns
                        // under the progress updates. At most one goes through this often; the rest
                        // drop, so many rapid requests become at most one clipboard send.
                        long nowReq = Environment.TickCount64;
                        long prevReq = Interlocked.Read(ref _lastClipboardRequestAt);
                        if (nowReq - prevReq >= Protocol.ClipboardPullThrottleMs &&
                            Interlocked.CompareExchange(ref _lastClipboardRequestAt, nowReq, prevReq) == prevReq)
                            ClipboardRequested?.Invoke();
                        break;
                    case Protocol.AudioPause when m.Length == 2:
                        s.Paused = m[1] != 0;
                        if (!s.Paused) _audioNudge.Set(); // unmuted: the sound starts at once, not on the next check
                        break;
                    case Protocol.AudioQuality when m.Length == 2:
                        {
                            int q = Math.Min((int)m[1], Protocol.OpusSteps.Length - 1);
                            // Logged only when it changes: anyone logged in, even a listener, could otherwise send
                            // thousands a second and push everything useful out of the size-capped log.
                            if (q != s.Quality) DiagLog.Write("host: " + s.Address + " asked for quality step " + q);
                            s.Quality = q;
                        }
                        break;
                    case Protocol.FilePace when m.Length == 5:
                        Volatile.Write(ref s.Pace, Math.Max(16 << 10, BitConverter.ToUInt32(m, 1) * 1024.0));
                        break;
                    case Protocol.NvdaWant when m.Length == 2 && s.Role == Protocol.RoleControl: // never a listener (4.4)
                        {
                            byte want = (byte)(m[1] & (Protocol.NvdaSpeechFlag | Protocol.NvdaBrailleFlag));
                            byte was = s.NvdaWant;
                            s.NvdaWant = want;
                            // Asked for it just now: told at once whether the Kova Helper is here to send it.
                            if (was == 0 && want != 0) TellNvdaAvailable(s);
                            break;
                        }
                    case Protocol.NvdaHelperInstall when m.Length == 1 && IsController(s):
                        // The controlling PC chose File, Install the Kova Helper on the remote PC: installed (or updated) into the
                        // NVDA of whoever is at this PC's screen, then that NVDA restarts. One at a time; the answer is said there.
                        if (Interlocked.CompareExchange(ref _helperInstalling, 1, 0) == 0)
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                string said;
                                try
                                {
                                    var st = NvdaHelper.GetState(_helper);
                                    said = st.ConfigDir == null ? "The remote PC could not install the Kova Helper: " + st.Problem
                                        : st.IsInstalled && !st.UpdateAvailable ? "The remote PC already has the Kova Helper NVDA add-on" + (NvdaHelperConnected ? ", and it is working." : ". It starts working when NVDA there restarts.")
                                        : "The remote PC: " + NvdaHelper.Apply(st.IsInstalled ? NvdaHelperAction.Update : NvdaHelperAction.Install, remote: true, log: m2 => DiagLog.Write("host: " + m2));
                                }
                                catch (Exception e) { said = "The remote PC could not install the Kova Helper: " + e.Message; }
                                finally { Volatile.Write(ref _helperInstalling, 0); }
                                Queue(s, Protocol.TextMessage(Protocol.NvdaNote, said));
                            });
                        else Queue(s, Protocol.TextMessage(Protocol.NvdaNote, "The remote PC is already installing the Kova Helper."));
                        break;
                    case Protocol.MicState when m.Length == 2:
                        if (m[1] == 0) MicOff(s);
                        break;
                    case Protocol.Features when m.Length >= 5:
                        s.PeerFeatures = BitConverter.ToUInt32(m, 1);
                        s.Ready = true;
                        _audioNudge.Set();
                        break;
                    // Anything else is from a newer version: ignore it.
                }
            }
        }

        /// <summary>Lists this folder, then each one asked for meanwhile (ListFolder: one at a time per PC).</summary>
        private void ListFolders(Session s, byte[] id, string path)
        {
            while (true)
            {
                ListOne(s, id, path);
                lock (s.ListGate)
                {
                    if (s.ListQueue.Count == 0) { s.ListBusy = 0; return; }
                    (id, path) = s.ListQueue.Dequeue();
                }
            }
        }

        private long _lastSpeedTest = long.MinValue / 2;

        /// <summary>
        /// Whether the screen of the session whose files and clipboard a controlling PC would reach is locked:
        /// the console's for the service (which acts as whoever is signed in there), this process's own for a
        /// window. While it is, Get files and the clipboard pull are refused (4.4): before, someone with only the
        /// Kova password could read the signed-in user's files at the lock screen.
        /// </summary>
        /// <summary>Tests only: true or false stands in for whether the screen is locked (this VPS's console usually is); null asks Windows.</summary>
        internal static bool? TestLocked;

        private bool ScreenLocked()
        {
            if (TestLocked is bool test) return test;
            try
            {
                uint session = FileUser != null ? NativeService.WTSGetActiveConsoleSessionId() : (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
                return NativeService.SessionLocked(session);
            }
            catch { return false; }
        }

        /// <summary>Why Get files may not read this path now, or null if it may.</summary>
        private string? FilesRefusal(string path)
        {
            if (ScreenLocked()) return "The remote PC is locked. Sign in there first, then try again.";
            // Only this PC's own drives (C:\ and so on). A network path (\\server\share) made Windows sign in to
            // that server as the user, handing a stranger's server the user's password hash; device paths (\\?\, \\.\)
            // reached past what Get files is for.
            if (path.Length > 0 && !FileChannel.RemotePathOk(path)) return "Get files only reads this PC's own drives, not network or device paths.";
            return null;
        }

        private void ListOne(Session s, byte[] id, string path)
        {
            // Never let a problem here end the host (or the service): it is said instead.
            string listing;
            System.Security.Principal.WindowsIdentity? user = null;
            try
            {
                if (FileUser == null) listing = RemoteTools.ListFolder(path);
                else if ((user = FileUser()) == null) listing = "E\t" + FileChannel.NobodySignedIn;
                else listing = System.Security.Principal.WindowsIdentity.RunImpersonated(user.AccessToken, () => RemoteTools.ListFolder(path));
            }
            catch (Exception e) { listing = "E\t" + e.Message; }
            finally { user?.Dispose(); }
            SendListing(s, id, listing);
        }

        private static void SendListing(Session s, byte[] id, string listing)
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(listing);
            byte[] r = new byte[5 + body.Length];
            r[0] = Protocol.FolderList;
            id.CopyTo(r, 1);
            body.CopyTo(r, 5);
            try { s.Link.Send(s.Stream, r); }
            catch (Exception e)
            {
                // Said, never dropped in silence: Get files on the other PC would wait for it and then say
                // "did not answer". A connection that has closed simply fails this too.
                DiagLog.Write("host: could not send " + s.Address + " a folder list of " + r.Length + " bytes: " + e.Message);
                if (r.Length > 4096) SendListing(s, id, "E\tThe list of that folder was too big to send.\n");
            }
        }

        // ---- How this PC is doing (HealthWatch): said to every connected PC when something changes ----

        /// <summary>Test only: readings instead of this PC's, and how often they are taken.</summary>
        public static Func<HealthSample>? TestHealth;
        public static int HealthEveryMs = HealthWatch.EveryMs;
        private object? _lastHealth; // the last HealthSample (boxed, so it can be read whole from another thread)
        private int _healthReads;
        private readonly HealthWatch _health = new();

        private void HealthLoop()
        {
            while (!_stop)
            {
                try
                {
                    var sample = TestHealth?.Invoke() ?? HealthSample.Read();
                    Volatile.Write(ref _lastHealth, sample);
                    if (_healthReads < 2) Volatile.Write(ref _healthReads, _healthReads + 1); // the first reading's processor figure is 0: nothing to compare with yet
                    var lines = _health.Next(sample, Environment.TickCount64);
                    foreach (string line in lines) DiagLog.Write("host health: " + line);
                    // In order, one thread per PC: a PC that has stopped reading holds up nobody else.
                    if (lines.Count > 0)
                        foreach (var s in AllSessions().Where(x => x.Role == Protocol.RoleControl)) // a listener hears the sound, not how the PC is doing
                            ThreadPool.QueueUserWorkItem(_ => { foreach (string line in lines) Send(s, Protocol.HealthAlert, line); });
                }
                catch { }
                for (int waited = 0; waited < HealthEveryMs && !_stop; waited += 250) Thread.Sleep(Math.Min(250, HealthEveryMs));
            }
        }

        /// <summary>A small message of one type with UTF-8 text; a PC that has gone is simply skipped.</summary>
        private static void Send(Session s, byte type, string text)
        {
            byte[] t = System.Text.Encoding.UTF8.GetBytes(text);
            byte[] m = new byte[1 + t.Length];
            m[0] = type;
            t.CopyTo(m, 1);
            try { s.Link.Send(s.Stream, m); } catch { }
        }

        /// <summary>
        /// Never a stuck key. A controller holding keys tells this PC it is still there several times a
        /// second; if it goes silent for 1.5 seconds (the connection broke, or is badly delayed) its keys
        /// are let go at once, without waiting the 10 seconds it takes to call the connection dead.
        /// </summary>
        private void KeySafety()
        {
            while (!_stop)
            {
                Thread.Sleep(250);
                long now = Environment.TickCount64;
                foreach (var s in AllSessions())
                {
                    lock (s.Held)
                    {
                        // Looked at again under the lock: a key arriving at this very moment (the line came
                        // back) has already marked the session as heard from, and is not let go behind its back.
                        long quiet = Environment.TickCount64 - Volatile.Read(ref s.LastMessage);
                        if (s.Held.Count == 0 || quiet <= 1500) continue;
                        // Its messages are here, only not read yet (this PC busy, the reader a moment behind):
                        // not quiet at all, so nothing is let go, unless that goes on for far too long.
                        bool waiting;
                        try { waiting = s.Tcp.Available > 0; } catch { waiting = false; }
                        if (waiting && quiet <= 5000) continue;
                        ReleaseHeld(s, remember: true);
                    }
                    DiagLog.Write("host: " + s.Address + " went quiet while holding keys; let them go");
                }
            }
        }

        /// <summary>
        /// Lets go of every key the session holds. remember (KeySafety only): the modifiers among them are
        /// kept in LetGo, to press again if the controller turns out to be still holding them (ReadLoop).
        /// Anything else (ReleaseAll, the session ending) forgets them for good.
        /// </summary>
        private static void ReleaseHeld(Session s, bool remember = false)
        {
            lock (s.Held)
            {
                foreach (var (vk, ext) in s.Held) Native.SendKey(vk, 0, true, ext);
                if (remember)
                {
                    // Only modifiers: pressing a held letter again would type it once more, and a mouse
                    // button again would start a new drag wherever the pointer is now.
                    foreach (var k in s.Held) if (IsModifier(k.Vk)) s.LetGo.Add(k);
                }
                else s.LetGo.Clear();
                s.Held.Clear();
            }
        }

        private static bool IsModifier(ushort vk) => vk is 0x10 or 0x11 or 0x12 // Shift, Ctrl, Alt
            or >= 0xA0 and <= 0xA5 // left and right Shift, Ctrl, Alt
            or 0x5B or 0x5C; // Windows keys

        /// <summary>
        /// Lets go of the session's keys at once, then says goodbye and closes it
        /// in the background: a PC that has stopped reading must never hold up
        /// the host, and End is called while holding _gate.
        /// </summary>
        private void End(Session s, string? why)
        {
            ReleaseHeld(s);
            MicOff(s);
            // Worked out again once it has left the list of sessions (End is called under _gate, before Rebuild).
            s.NvdaWant = 0;
            if (s.Files is FileChannel f)
            {
                // Kept for the controller's next connection (a dropped one comes straight back),
                // so its transfers carry on; stopping hosting ends them. With nothing going there
                // is nothing to carry on: closed at once (it would otherwise hold two threads for a minute).
                if (_stop || !f.HasWork) f.Dispose();
                else
                {
                    f.Detach();
                    _kept.RemoveAll(k => k.Gone); // ones that closed themselves since
                    if (!_kept.Contains(f)) _kept.Add(f);
                    while (_kept.Count > MaxControllers) { _kept[0].Dispose(); _kept.RemoveAt(0); }
                }
                s.Files = null; // kept or closed: no longer this session's
            }
            // The session's keys (Windows key handles) go with it, not whenever the collector gets round to it.
            // Late senders (audio, health, broadcasts) all catch what a disposed link throws.
            if (why == null) { try { s.Tcp.Dispose(); } catch { } try { s.Link.Dispose(); } catch { } return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Protocol.SendMessage(s.Link, s.Stream, why); } catch { }
                // Closed gently: closing with the other PC's messages still unread makes Windows reset the
                // connection, and the other PC then threw away the goodbye unread ("closed", never why).
                try
                {
                    s.Tcp.Client.Shutdown(SocketShutdown.Send);
                    s.Tcp.Client.ReceiveTimeout = 1000;
                    byte[] junk = new byte[4096];
                    long until = Environment.TickCount64 + 2000;
                    while (Environment.TickCount64 < until && s.Tcp.Client.Receive(junk) > 0) { }
                }
                catch { }
                try { s.Tcp.Dispose(); } catch { }
                try { s.Link.Dispose(); } catch { }
            });
        }

        private readonly OpusBank _opus = new();
        private readonly bool[] _wanted = new bool[Protocol.OpusSteps.Length];

        /// <summary>
        /// Called on the capture thread with every 5 ms tick (null: silence). Each
        /// step someone uses is encoded once; every session gets its own step's
        /// packets, sealed with its own keys.
        /// </summary>
        private void SendAudio(uint seq, short[]? pcm)
        {
            // Still one at a time, should a capture be swapped between the check above and here.
            lock (_opus) SendAudioLocked(seq, pcm);
        }

        private void SendAudioLocked(uint seq, short[]? pcm)
        {
            var all = AllSessions();
            if (all.Length == 0) return;
            // Only to PCs that are really there: one that has gone quiet (no hello for 3
            // seconds) gets nothing, rather than a stream nobody hears until it times out.
            long now = Environment.TickCount64;
            Array.Clear(_wanted);
            foreach (var s in all) if (s.Ready && !s.Paused && s.AudioTo != null && now - Volatile.Read(ref s.HeardAt) < 3000) _wanted[s.Quality] = true;
            _opus.Feed(seq, pcm, _wanted);
            foreach (var s in all)
            {
                var to = s.AudioTo;
                if (to == null || !s.Ready || s.Paused || now - Volatile.Read(ref s.HeardAt) >= 3000 || !_opus.Ready(s.Quality, out uint first, out int ticks, out var packet)) continue;
                // The packet number is also the audio nonce: never send one at or before what
                // this session already has (after a change of step, the new step's packet can
                // start earlier). The player fills the gap.
                // A little behind (well under 2^20 ticks) is a change of step: skipped here, before the check
                // below could take it for a connection worn out.
                // Measured from the first number sent (which never moves), so a PC muted for months is never
                // taken for "a little behind" and kept silent.
                if (s.SentAny && first - s.SentFirst >= 0xFFF00000u) continue;
                // Months into one connection: closing it ends its session as any drop does, and the
                // PC reconnects with new keys, rather than the sound stopping for good. Checked before the
                // comparison below, so every number compared is within the connection's span.
                if (s.Link.AudioWornOut(first)) { try { s.Tcp.Dispose(); } catch { } continue; }
                // Compared from the first number sent, not as a signed difference: after 124 days of
                // capture with nothing sent to this PC (muted while someone else listened), the next
                // number looked like it was behind, and its sound never came back.
                if (s.SentAny && first - s.SentFirst < s.SentUntil - s.SentFirst) continue;
                if (!s.SentAny) s.SentFirst = first;
                s.SentUntil = first + (uint)ticks;
                s.SentAny = true;
                byte[] a = s.Audio;
                a[0] = Protocol.UdpOpus;
                BitConverter.TryWriteBytes(a.AsSpan(1), first);
                a[5] = (byte)ticks;
                packet.CopyTo(a.AsSpan(6));
                int payload = 1 + packet.Length;
                try
                {
                    s.Link.SealAudio(a, payload);
                    UdpSend(a, 5 + payload + SecureLink.TagSize, to);
                    Interlocked.Increment(ref _txPackets);
                    Interlocked.Add(ref _txBytes, 5 + payload + SecureLink.TagSize);
                }
                catch { }
            }
        }

        private void UdpLoop()
        {
            var any = new IPEndPoint(IPAddress.IPv6Any, 0);
            while (!_stop)
            {
                byte[] d;
                try { d = _udp.Receive(ref any); }
                catch { if (_stop) return; Thread.Sleep(20); continue; }
                // From the relay: its header off, and the sender becomes the relayed PC's stand-in address.
                if (_relay is RelayHost via && !via.Unwrap(ref d, ref any)) continue;
                if (d.Length >= Protocol.MinMicPacketBytes && d[0] == Protocol.UdpMic)
                {
                    if (d.Length <= Protocol.MicHeadBytes + 1 + Protocol.MaxOpusBytes + SecureLink.TagSize)
                        try { HandleMic(d, any); } catch (Exception e) { DiagLog.Write("host: microphone packet failed: " + e.Message); }
                    continue;
                }
                if (d.Length != Protocol.UdpHelloBytes || d[0] != Protocol.UdpHello) continue;
                Session? s;
                s = Array.Find(AllSessions(), x => d.AsSpan(1, 8).SequenceEqual(x.Token));
                if (s == null) continue;
                var from = any.Address.IsIPv4MappedToIPv6 ? any.Address.MapToIPv4() : any.Address;
                if (!Bucket(from).Equals(Bucket(s.Address))) continue; // the block: a new IPv6 privacy address each day must not silence the sound (the signed hello proves who)
                // Only the PC that logged in can make a hello, and only a newer one counts (the stamp
                // is its own clock, which only goes forward), so a recorded hello played back from
                // another port cannot send the sound there.
                if (!s.Link.HelloOk(d)) continue;
                long stamp = BitConverter.ToInt64(d, 9);
                if (stamp <= s.HelloStamp) continue;
                s.HelloStamp = stamp;
                s.AudioTo = new IPEndPoint(any.Address, any.Port);
                long heard = Environment.TickCount64;
                // Its first hello (or the first after a quiet spell): the sound may need starting.
                if (heard - Interlocked.Exchange(ref s.HeardAt, heard) >= 3000 && Volatile.Read(ref _capture) == null) _audioNudge.Set();
                // Straight back, on the same path the sound takes: the client's audio ping.
                // Signed like the hello, so nobody else can fake the ping the sound and the file pacing go by.
                byte[] pong = new byte[Protocol.UdpPongBytes];
                pong[0] = Protocol.UdpPong;
                d.AsSpan(9, 8).CopyTo(pong.AsSpan(1));
                s.Link.SignHello(pong);
                try { UdpSend(pong, pong.Length, s.AudioTo); } catch { }
            }
        }
    }
}
