using System;
using System.Linq;
using Concentus;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TailRemote
{
    /// <summary>The controlling side: one connection to a host.</summary>
    internal sealed class Client : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly UdpClient _udp;
        private readonly byte[] _token;
        private readonly Player _player;
        private readonly SecureLink _link;
        private volatile bool _closed;
        private long _lastPong;
        private int _closing;
        private FileChannel? _files;
        private WlanStreaming? _wifi;
        private uint _peerFeatures;

        /// <summary>Why the host said it is going away (Protocol.Leaving*), or 0; and the detail (the new version when updating).</summary>
        public byte Leaving { get; private set; }
        private long _leavingAt;
        public string LeavingDetail { get; private set; } = "";

        public event Action<string>? Status;
        // The connection can drop between Connect starting its threads and the window subscribing (a host
        // stopping just as the login finished): a handler added after that hears it at once, rather than
        // the window holding a dead connection it thinks is up.
        private Action<string>? _disconnected;
        private string? _closedWhy;
        private readonly object _disconnectGate = new();
        public event Action<string>? Disconnected
        {
            add
            {
                string? already;
                lock (_disconnectGate) { _disconnected += value; already = _closedWhy; }
                if (already != null) value?.Invoke(already);
            }
            remove { lock (_disconnectGate) _disconnected -= value; }
        }

        /// <summary>Raised (on the network thread) when the host says what it can do, which comes just after the login.</summary>
        public event Action? FeaturesKnown;

        /// <summary>The host's network cards (Wake.Describe), told just after the login: how to wake it later. Null until then.</summary>
        public string? WakeInfo { get; private set; }
        public event Action<string>? WakeInfoKnown;
        /// <summary>
        /// Something about the host PC worth saying (battery, drive space, processor, memory). Raised on a network
        /// thread. What is already wrong comes just after the login, before the window has subscribed: it is kept
        /// and handed to the first handler added.
        /// </summary>
        public event Action<string>? HealthAlert
        {
            add
            {
                string[] early;
                lock (_healthGate) { _health += value; early = _healthEarly.ToArray(); _healthEarly.Clear(); }
                foreach (string line in early) value?.Invoke(line);
            }
            remove { lock (_healthGate) _health -= value; }
        }
        private Action<string>? _health;
        private readonly List<string> _healthEarly = new();
        private readonly object _healthGate = new();

        private void HealthSaid(string line)
        {
            Action<string>? h;
            lock (_healthGate) { h = _health; if (h == null && _healthEarly.Count < 8) _healthEarly.Add(line); }
            h?.Invoke(line);
        }

        public bool CanWake => !ListenOnly && (_peerFeatures & Protocol.FeatureWake) != 0;

        /// <summary>Asks the host to send a Wake-on-LAN packet for these cards (up to 8) on its own network.</summary>
        public void SendWake(params byte[][] macs)
        {
            var cards = macs.Where(x => x.Length == 6).Take(8).ToArray();
            if (_closed || cards.Length == 0) return;
            byte[] m = new byte[1 + 6 * cards.Length];
            m[0] = Protocol.WakeRequest;
            for (int i = 0; i < cards.Length; i++) cards[i].CopyTo(m, 1 + 6 * i);
            Write(m);
        }
        /// <summary>Clipboard text from the host. Raised on a network thread.</summary>
        public event Action<string>? ClipboardReceived;
        /// <summary>True when the host let us in with its listen-only password: audio only, no keys.</summary>
        public bool ListenOnly { get; private set; }
        /// <summary>0 = full quality; above that, a lower sample rate while the connection struggles.</summary>
        public int AudioQuality { get; private set; }
        public int LastPingMs { get; private set; } = -1;
        /// <summary>Round trip on the audio path itself (UDP), which the sound's delay is worked out from. -1 until measured.</summary>
        public int AudioPingMs { get; private set; } = -1;
        /// <summary>The ping the audio delay should use: the audio path's own, or the TCP one until that is measured.</summary>
        public int PingForAudio => AudioPingMs >= 0 ? AudioPingMs : Math.Max(0, LastPingMs);
        /// <summary>UDP gets no answer while the connection works (a firewall, usually): no sound can come.</summary>
        public bool UdpBlocked => _udpBlocked;
        public int Port => _hostUdp.Port;
        /// <summary>The host's address and port: whether a new connection is to the same PC.</summary>
        public IPEndPoint HostEndPoint => _hostUdp;
        /// <summary>When this connection was made (Environment.TickCount64).</summary>
        public long ConnectedAt { get; } = Environment.TickCount64;
        private volatile bool _udpBlocked;
        private long _lastUdpPong;
        private long _lastPongStamp = long.MinValue; // the newest audio pong's stamp (UDP receive thread only)
        private IPEndPoint _hostUdp = null!;
        /// <summary>Buffered audio plus the output device, in ms; -1 while nothing plays.</summary>
        public int AudioDelayMs => _player.DelayMs;

        private Client(TcpClient tcp, NetworkStream stream, UdpClient udp, byte[] token, Player player, SecureLink link)
        {
            _tcp = tcp; _stream = stream; _udp = udp; _token = token; _player = player; _link = link;
        }

        /// <summary>
        /// Connects and checks the password; throws with a readable message on failure.
        /// The player is the caller's and outlives the connection, so connecting never
        /// opens or closes an audio device.
        /// </summary>
        /// <summary>lockedStep: the bitrate step to hold from the very first sound, or -1 for Variable.</summary>
        /// <summary>answerMs: how long to wait for the PC to answer at all (shorter while trying again).</summary>
        public static Client Connect(string address, int port, string password, Player player, Action<string> status, int lockedStep = -1, int answerMs = 8000)
        {
            var tcp = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
            tcp.Client.DualMode = true;
            tcp.Client.SendTimeout = 5000; // a host that stops taking data is dropped, never waited on forever
            try
            {
                IPAddress[] addrs;
                try { addrs = Dns.GetHostAddresses(address); }
                catch (SocketException) { addrs = Array.Empty<IPAddress>(); }
                if (addrs.Length == 0)
                    throw new InvalidOperationException(Protocol.LocalTailscaleUp()
                        ? "Could not find " + address + ". Check the name or address."
                        : "Could not find " + address + ". If it is a Tailscale name, open Tailscale on this PC and sign in.");
                bool done;
                try { done = tcp.ConnectAsync(addrs, port).Wait(answerMs); }
                catch (AggregateException e) when (e.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    throw new InvalidOperationException(address + " is on, but Kova is not hosting there. Start hosting on that PC.");
                }
                if (!done)
                    throw new TimeoutException("No answer from " + address + ". It may be off or not hosting, or its port is not open: use Port editor on that PC.");
            }
            catch (AggregateException e) { tcp.Dispose(); throw new InvalidOperationException(e.InnerException?.Message ?? e.Message); }
            catch { tcp.Dispose(); throw; }

            var stream = tcp.GetStream();
            stream.ReadTimeout = 8000;
            UdpClient? udp = null;
            FileChannel? files = null;
            string? filesTo = null;
            try
            {
                byte[] hello = new byte[Protocol.HelloBytes];
                Protocol.ReadExactly(stream, hello.AsSpan(0, 4));
                if (!hello.AsSpan(0, 4).SequenceEqual(Protocol.Magic))
                    throw new InvalidOperationException(hello.AsSpan(0, 3).SequenceEqual("TRM"u8)
                        ? "The other PC has a different Kova version, or the connection damaged its first message. Update both to the latest."
                        : "That is not a Kova host, or the connection damaged its first message.");
                Protocol.ReadExactly(stream, hello.AsSpan(4));
                if (!Protocol.CheckOk(hello)) throw new InvalidOperationException(Protocol.DamagedLogin);
                byte[] hostNonce = hello[4..20];
                byte[] passwordKey = Protocol.DeriveKey(password, hello.AsSpan(Protocol.HelloSaltAt, Protocol.SaltBytes));
                byte[] myNonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
                byte[] answer = new byte[Protocol.AnswerBytes];
                Protocol.Magic.CopyTo(answer, 0);
                myNonce.CopyTo(answer, 4);
                byte[] key;
                using (var exchange = Protocol.NewExchange())
                {
                    byte[] shared = Protocol.SharedSecret(exchange, hello.AsSpan(Protocol.HelloPublicAt, Protocol.PublicKeyBytes))
                        ?? throw new InvalidOperationException("That is not a Kova host, or the connection damaged its first message.");
                    Protocol.WritePublic(exchange, answer.AsSpan(Protocol.AnswerPublicAt, Protocol.PublicKeyBytes));
                    key = Protocol.SessionKey(passwordKey, shared, hello, answer.AsSpan(0, Protocol.AnswerProofAt));
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(shared);
                }
                // Still the client that proves first: the host gives nothing away to a wrong password.
                Protocol.Proof(key, 'C', hostNonce, myNonce).CopyTo(answer, Protocol.AnswerProofAt);
                Protocol.AddCheck(answer);
                stream.Write(answer);

                byte[] reply = new byte[Protocol.ReplyBytes];
                try { Protocol.ReadExactly(stream, reply); }
                catch (System.IO.EndOfStreamException) { throw new InvalidOperationException(Protocol.DamagedLogin); } // the host hung up on a damaged answer
                if (!Protocol.CheckOk(reply)) throw new InvalidOperationException(Protocol.DamagedLogin);
                if (reply[0] != 1) throw new InvalidOperationException("Wrong password.");
                byte[] role = reply[1..2], token = reply[2..10], hostProof = reply[10..42];
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(hostProof, Protocol.Proof(key, 'H', myNonce, hostNonce)))
                    throw new InvalidOperationException("That PC does not know the password, so nothing was sent to it. Check the address.");
                // The host pings back every 2 seconds; 10 silent seconds means it is gone.
                stream.ReadTimeout = 10_000;

                var hostEp = (IPEndPoint)tcp.Client.RemoteEndPoint!;
                udp = new UdpClient(AddressFamily.InterNetworkV6);
                udp.Client.DualMode = true;
                udp.Client.ReceiveBufferSize = 1 << 20;
                Host.IgnoreUdpResets(udp.Client);
                // Not connected to one address: a host with more than one address on a network
                // can answer from another, and a connected socket silently threw that sound away.
                // Every audio packet is encrypted and checked, so nothing false gets in.

                // Done by the next Push on its own thread: the player is shared, and another connection's
                // receive thread may still be pushing into it, while Reset clears Push's state unlocked.
                player.ResetSoon();
                var c = new Client(tcp, stream, udp, token, player, new SecureLink(key, hostNonce, myNonce, isHost: false))
                {
                    ListenOnly = role[0] == Protocol.RoleListen,
                    _hostUdp = new IPEndPoint(hostEp.Address, port),
                    _laneHost = hostEp, _laneKey = key, _hostNonce = hostNonce, _myNonce = myNonce,
                };
                if (lockedStep >= 0)
                {
                    // Before the features message: the host sends no sound until that
                    // arrives, so the very first packet is already at the locked bitrate.
                    c._lockedStep = Math.Min(lockedStep, Protocol.OpusSteps.Length - 1);
                    c.AudioQuality = c._lockedStep;
                    c._link.Send(stream, new[] { Protocol.AudioQuality, (byte)c._lockedStep });
                }
                c._link.Send(stream, Protocol.FeaturesMessage());
                if (!c.ListenOnly)
                {
                    // The same channel as before a dropped connection to this host, so what was
                    // going carries on; anywhere else, a new one.
                    string to = address.Trim().ToLowerInvariant() + ":" + port;
                    lock (KeptGate)
                    {
                        int i = _kept.FindIndex(k => k.For == to);
                        if (i >= 0)
                        {
                            files = _kept[i].Files.Gone ? null : _kept[i].Files;
                            _kept.RemoveAt(i);
                        }
                    }
                    files ??= new FileChannel();
                    filesTo = to;
                    c._files = files;
                    c._filesFor = to;
                    files.Rate = () => c._pace;
                    files.PeerName = address.Trim();
                    files.TextReceived = text => c.ClipboardReceived?.Invoke(text);
                    files.FilesReceived = paths => c.ClipboardFilesReceived?.Invoke(paths);
                    files.Progress = t => c.TransferProgress?.Invoke(t);
                    byte[] channel = files.Id;
                    files.Dial = () => OpenLane(hostEp, token, channel, key, hostNonce, myNonce);
                }
                c.Status += status;
                c.Start();
                return c;
            }
            catch (System.IO.IOException e)
            {
                if (files != null) Keep(filesTo!, files);
                udp?.Dispose(); tcp.Dispose();
                if (e.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
                    throw new InvalidOperationException("No answer from " + address.Trim() + " in time: the line may be very slow, or Kova there is busy.");
                throw new InvalidOperationException("The host closed the connection.");
            }
            catch
            {
                if (files != null) Keep(filesTo!, files);
                udp?.Dispose(); tcp.Dispose(); throw;
            }
        }

        // What a further lane (the remote screen) needs: where the host is, and this login's keys.
        private IPEndPoint _laneHost = null!;
        private byte[] _laneKey = null!, _hostNonce = null!, _myNonce = null!;

        /// <summary>The host can show its screen to this PC: it is new enough, and this PC controls it (not listen-only).</summary>
        public bool CanWatch => !ListenOnly && (_peerFeatures & Protocol.FeatureVideo) != 0;
        private int _watching; // remote screens open now: while there are, the pace is worked out as for files

        /// <summary>
        /// Starts watching the host's screen, or null if it cannot show it. The stream keeps itself
        /// connected (and stops trying once this connection is closed); dispose it to stop watching.
        /// </summary>
        public VideoStream? WatchScreen()
        {
            if (!CanWatch || _closed) return null;
            var host = _laneHost; var token = _token; var key = _laneKey; var hostNonce = _hostNonce; var myNonce = _myNonce;
            Interlocked.Increment(ref _watching);
            var stream = new VideoStream(() => _closed ? null : OpenVideoLane(host, token, key, hostNonce, myNonce));
            stream.Stopped += () => Interlocked.Decrement(ref _watching);
            return stream;
        }

        /// <summary>A video lane: proven with this login's key, on keys of its own (both PCs' fresh values). Null if it could not be opened.</summary>
        private static (TcpClient, SecureLink)? OpenVideoLane(IPEndPoint host, byte[] token, byte[] key, byte[] hostNonce, byte[] myNonce)
        {
            var tcp = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
            tcp.Client.DualMode = true;
            try
            {
                if (!tcp.ConnectAsync(host.Address, host.Port).Wait(5000)) throw new TimeoutException();
                var s = tcp.GetStream();
                s.ReadTimeout = 5000;
                byte[] hello = new byte[Protocol.HelloBytes];
                Protocol.ReadExactly(s, hello);
                if (!hello.AsSpan(0, 4).SequenceEqual(Protocol.Magic) || !Protocol.CheckOk(hello)) throw new InvalidOperationException(Protocol.DamagedLogin);
                byte[] laneValue = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
                byte[] channel = new byte[16];
                byte[] answer = new byte[Protocol.AnswerBytes];
                Protocol.VideoMagic.CopyTo(answer, 0);
                token.CopyTo(answer, 4);
                channel.CopyTo(answer, 12);
                laneValue.CopyTo(answer, 28);
                Protocol.LaneProof(key, hello.AsSpan(4, 16), channel, laneValue).CopyTo(answer, Protocol.LaneProofAt);
                Protocol.AddCheck(answer);
                s.Write(answer);
                return (tcp, new SecureLink(key, hostNonce, myNonce, isHost: false, Protocol.VideoPurpose(hello.AsSpan(4, 16), laneValue), maxReceive: SecureLink.MaxSend));
            }
            catch { tcp.Dispose(); return null; }
        }

        // File channels whose main connection dropped, each kept for the next connection to its own
        // host, oldest first. One per PC: with several PCs connected at once, one slot for all meant
        // connecting to (or letting go of) any other PC ended a waiting transfer to this one.
        private static readonly object KeptGate = new();
        private static readonly List<(string For, FileChannel Files)> _kept = new();
        private const int MaxKept = 8;
        private string? _filesFor;

        private static void Keep(string to, FileChannel files)
        {
            lock (KeptGate)
            {
                int i = _kept.FindIndex(k => k.For == to);
                if (i >= 0)
                {
                    if (_kept[i].Files != files) _kept[i].Files.Dispose();
                    _kept.RemoveAt(i);
                }
                _kept.RemoveAll(k => k.Files.Gone);
                // Nothing going: nothing to carry on, and kept it could push out a PC's paused transfer.
                if (!files.HasWork) { files.Dispose(); return; }
                _kept.Add((to, files));
                while (_kept.Count > MaxKept)
                {
                    // Full: one that has since finished goes first, the oldest only if all are busy.
                    int drop = _kept.FindIndex(k => !k.Files.HasWork);
                    if (drop < 0) drop = 0;
                    _kept[drop].Files.Dispose();
                    _kept.RemoveAt(drop);
                }
            }
        }

        /// <summary>Stop pressed while not connected: transfers waiting for a reconnection are stopped for good.</summary>
        public static void ForgetTransfers()
        {
            lock (KeptGate)
            {
                foreach (var k in _kept) k.Files.Dispose();
                _kept.Clear();
            }
        }

        /// <summary>
        /// One more file lane. Its keys mix in fresh random values from both PCs, so no two
        /// lanes ever encrypt with the same key and counter. Null if it could not be opened.
        /// key is the main login's session key; the host's value is the nonce in this lane's own
        /// hello (bytes 4 to 20), the same bytes the host uses, never the main login's.
        /// </summary>
        private static (TcpClient, SecureLink)? OpenLane(IPEndPoint host, byte[] token, byte[] channel, byte[] key, byte[] hostNonce, byte[] myNonce)
        {
            var tcp = new TcpClient(AddressFamily.InterNetworkV6);
            tcp.Client.DualMode = true;
            try
            {
                if (!tcp.ConnectAsync(host.Address, host.Port).Wait(5000)) throw new TimeoutException();
                var s = tcp.GetStream();
                s.ReadTimeout = 5000;
                byte[] hello = new byte[Protocol.HelloBytes];
                Protocol.ReadExactly(s, hello);
                if (!hello.AsSpan(0, 4).SequenceEqual(Protocol.Magic) || !Protocol.CheckOk(hello)) throw new InvalidOperationException(Protocol.DamagedLogin);
                byte[] laneValue = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
                byte[] answer = new byte[Protocol.AnswerBytes];
                Protocol.FileMagic.CopyTo(answer, 0);
                token.CopyTo(answer, 4);
                channel.CopyTo(answer, 12);
                laneValue.CopyTo(answer, 28);
                Protocol.LaneProof(key, hello.AsSpan(4, 16), channel, laneValue).CopyTo(answer, Protocol.LaneProofAt);
                Protocol.AddCheck(answer);
                s.Write(answer);
                return (tcp, new SecureLink(key, hostNonce, myNonce, isHost: false, FileChannel.LanePurpose(hello.AsSpan(4, 16), laneValue)));
            }
            catch { tcp.Dispose(); return null; }
        }

        /// <summary>Copied files from here to the host's clipboard. Never waits.</summary>
        public void SendClipboardFiles(IReadOnlyList<string> paths)
        {
            var files = _files;
            if (_closed || ListenOnly || files == null) return;
            ThreadPool.QueueUserWorkItem(_ => { try { files.SendFiles(paths, toClipboard: true); } catch { } });
        }

        /// <summary>Send files: into the host's Downloads\TailRemote. Never waits.</summary>
        public void SendFiles(IReadOnlyList<string> paths)
        {
            var files = _files;
            if (_closed || ListenOnly || files == null) return;
            ThreadPool.QueueUserWorkItem(_ => { try { files.SendFiles(paths, toClipboard: false); } catch { } });
        }

        /// <summary>Whether the host takes files into a folder chosen here (Get files' Send files here).</summary>
        public bool CanSendTo => !ListenOnly && (_peerFeatures & Protocol.FeatureSendTo) != 0;

        /// <summary>
        /// Send files into this folder on the host (the one open in Get files). Never waits. False when
        /// nothing was sent: not connected, listening only, or a host too old to take them.
        /// </summary>
        public bool SendFilesTo(IReadOnlyList<string> paths, string folder)
        {
            var files = _files;
            if (_closed || !CanSendTo || files == null) return false;
            ThreadPool.QueueUserWorkItem(_ => { try { files.SendFiles(paths, toClipboard: false, into: folder); } catch { } });
            return true;
        }

        /// <summary>Stops what is going to or coming from the host.</summary>
        public void CancelTransfer() => _files?.CancelSending();

        /// <summary>Test only: the file channel.</summary>
        internal FileChannel? Files => _files;

        /// <summary>Files from the host's clipboard, in the holding folder. Raised on a network thread.</summary>
        public event Action<string[]>? ClipboardFilesReceived;
        /// <summary>How a clipboard batch is going, either way. Raised on a network thread.</summary>
        public event Action<FileChannel.Transfer>? TransferProgress;

        // ---- Pacing files so the sound never queues behind them ----
        // While a batch goes either way, the audio path's ping is measured ten times a second.
        // Its lowest value over the last 30 seconds is the line's own delay; anything above
        // that is data queueing somewhere. Clear (under 15 ms of queue): faster by a quarter.
        // Queueing (over 40 ms): down to 60 percent at once. The host is told the same rate.
        private double _pace = 8 << 20;
        private readonly Queue<(long At, int Ms)> _pings = new();
        private double _paceSent;
        private int _highPings, _lastTrouble;
        private bool _wasBusy;

        private void PaceFrom(int rttMs)
        {
            long now = Environment.TickCount64;
            _pings.Enqueue((now, rttMs));
            while (_pings.Count > 0 && now - _pings.Peek().At > 30_000) _pings.Dequeue();
            bool busy = _files?.Busy == true || Volatile.Read(ref _watching) > 0; // the remote screen is paced like files
            int floor = _pings.Min(p => p.Ms);
            // A LAN, or Tailscale going direct to a PC nearby: the line's own delay is a few ms.
            // There it starts fast and doubles while the sound is fine; further away it starts
            // gently and grows by half.
            bool near = floor <= 8;
            if (busy && !_wasBusy) _pace = Math.Max(_pace, near ? 64 << 20 : 2 << 20); // a new transfer starts brisk, not where the last one ended
            _wasBusy = busy;
            if (!busy) return;
            int queued = rttMs - floor;
            // Slowed only when the sound really suffers (it ran dry or packets came late), or
            // the ping has stayed far up (150 ms of queue, twice running). Over Tailscale and
            // Wi-Fi the ping swings a lot while the sound is fine, and slowing on that alone
            // kept transfers needlessly slow.
            int trouble = _player.Trouble;
            bool hurt = trouble != _lastTrouble;
            _lastTrouble = trouble;
            if (hurt) { _pace = Math.Max(_pace * 0.7, 64 << 10); _highPings = 0; }
            else if (queued > 150) { if (++_highPings >= 2) { _pace = Math.Max(_pace * 0.7, 64 << 10); _highPings = 0; } }
            else
            {
                _highPings = 0;
                _pace = Math.Min(_pace * (queued >= 30 ? 1.1 : near ? 2 : 1.5), 2.0 * (1 << 30));
            }
            if (Math.Abs(_pace - _paceSent) > _paceSent * 0.1)
            {
                _paceSent = _pace;
                Span<byte> m = stackalloc byte[5];
                m[0] = Protocol.FilePace;
                BitConverter.TryWriteBytes(m[1..], (uint)Math.Min(uint.MaxValue, _pace / 1024));
                Write(m);
            }
        }

        private void Start()
        {
            // First: a connection that drops at once closes from these threads, and a Wi-Fi handle made after
            // that close was never let go (scans stayed off for good).
            _wifi = new WlanStreaming("client"); // steady Wi-Fi while connected
            new Thread(SendLoop) { IsBackground = true, Name = "Kova send", Priority = ThreadPriority.AboveNormal }.Start();
            new Thread(TcpLoop) { IsBackground = true, Name = "Kova tcp" }.Start();
            new Thread(UdpLoop) { IsBackground = true, Name = "Kova udp", Priority = ThreadPriority.Highest }.Start();
            new Thread(Heartbeat) { IsBackground = true, Name = "Kova heartbeat" }.Start();
            new Thread(QualityLoop) { IsBackground = true, Name = "Kova quality" }.Start();
        }

        public void Dispose() => Close(null);

        private void Close(string? why)
        {
            if (Interlocked.Exchange(ref _closing, 1) == 1) return;
            // A goodbye that did not happen (a restart someone cancelled on that PC) is not why a
            // connection drops a minute later.
            if (Leaving != 0 && Environment.TickCount64 - _leavingAt > 60_000) Leaving = 0;
            // The host said why it is going: that, not how the socket happened to break.
            if (why != null) why = Leaving switch
            {
                Protocol.LeavingUpdating => "The remote PC is updating Kova" + (LeavingDetail.Length > 0 ? " to version " + LeavingDetail : "") + ".",
                Protocol.LeavingRestarting => "The remote PC is restarting.",
                Protocol.LeavingShutdown => "The remote PC is shutting down or restarting.",
                Protocol.LeavingStopped => "The remote PC stopped hosting.",
                _ => why,
            };
            DiagLog.Write("client: connection closed" + (why != null ? ": " + why : " by this PC"));
            _closed = true;
            // Anything waiting for an answer fails now, not after its time runs out.
            var gone = new System.IO.IOException(why ?? "Disconnected.");
            Interlocked.Exchange(ref _speedRequest, null)?.TrySetException(gone);
            Interlocked.Exchange(ref _infoRequest, null)?.TrySetException(gone);
            foreach (var id in _folderRequests.Keys) if (_folderRequests.TryRemove(id, out var w)) w.TrySetException(gone);
            _toSend.Release();
            try { _tcp.Dispose(); } catch { }
            try { _udp.Dispose(); } catch { }
            if (_files != null)
            {
                // Transfers are never ended by the connection: dropped, they wait (their lanes may even
                // carry on); disconnected on purpose, they pause. Either way they carry on where they
                // were on the next connection to this host, however long that takes. Only Stop ends them.
                if (why == null) _files.Pause(); else _files.Detach();
                Keep(_filesFor!, _files);
            }
            _wifi?.Dispose();
            Action<string>? handlers;
            lock (_disconnectGate) { _closedWhy = why; handlers = _disconnected; }
            if (why != null) handlers?.Invoke(why);
        }

        // ---- Sending: one thread, keys first ----
        // Every send goes through this queue, so the keyboard hook and the window
        // never wait on the network. Keys and commands always go before clipboard
        // text: a large copy can never hold up a keystroke, and a hook that waits
        // gets removed by Windows, after which keys silently stay on this PC.

        private readonly ConcurrentQueue<byte[]> _urgent = new();
        private readonly SemaphoreSlim _toSend = new(0);

        /// <summary>Test only (--chaostest): sends any message, as the client would.</summary>
        internal void TestWrite(byte[] message) => Write(message);
        /// <summary>Test only (--chaostest): writes raw bytes into the connection, outside the encryption.</summary>
        internal void TestRaw(byte[] bytes) { try { _stream.Write(bytes); } catch { } }
        internal bool TestClosed => _closed;
        /// <summary>Test only (--chaostest): sends nothing more, as if the connection had silently broken.</summary>
        internal volatile bool TestSilent;

        private void Write(ReadOnlySpan<byte> message)
        {
            if (_closed) return;
            _urgent.Enqueue(message.ToArray());
            _toSend.Release();
        }

        private void SendLoop()
        {
            while (!_closed)
            {
                _toSend.Wait();
                if (_closed) return;
                if (!_urgent.TryDequeue(out var m)) continue;
                try { _link.Send(_stream, m); }
                catch (Exception e)
                {
                    // Only a send that timed out means the host stopped taking data; a reset or a dropped
                    // network found by a send first was said as that too, unlike the same break found by a read.
                    Close("Disconnected: " + (e is System.IO.IOException { InnerException: SocketException { SocketErrorCode: SocketError.TimedOut } }
                        ? "the host stopped taking data." : Plain(e)));
                    return;
                }
            }
        }

        /// <summary>Sends one key. Called from the keyboard hook: only queues it.</summary>
        public void SendKey(ushort vk, ushort scan, bool up, bool extended)
        {
            if (_closed) return;
            if (!up) Interlocked.Increment(ref _keysSent); // counted only: what is typed is never logged
            lock (_down) { if (up) _down.Remove((vk, extended)); else _down.Add((vk, extended)); }
            Span<byte> f = stackalloc byte[6];
            f[0] = Protocol.Key;
            BitConverter.TryWriteBytes(f[1..], vk);
            BitConverter.TryWriteBytes(f[3..], scan);
            f[5] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
            Write(f);
        }

        /// <summary>The remote PC takes the mouse (moves, wheel; buttons go as keys with Native.VkLeftButton...).</summary>
        public bool CanMouse => !ListenOnly && (_peerFeatures & Protocol.FeatureMouse) != 0;

        /// <summary>A mouse move or wheel turn (Protocol.MouseMove...). Called from the mouse hook: only queues it.</summary>
        public void SendMouse(byte kind, int a, int b)
        {
            if (_closed) return;
            Span<byte> f = stackalloc byte[6];
            f[0] = Protocol.Mouse;
            f[1] = kind;
            BitConverter.TryWriteBytes(f[2..], (short)Math.Clamp(a, short.MinValue, short.MaxValue));
            BitConverter.TryWriteBytes(f[4..], (short)Math.Clamp(b, short.MinValue, short.MaxValue));
            Write(f);
        }

        public void ReleaseAll()
        {
            lock (_down) _down.Clear();
            Write(stackalloc byte[] { Protocol.ReleaseAll });
        }

        // Keys held down on the other PC right now: while there are any, the heartbeat pings several
        // times a second, so the other PC lets them go within 1.5 seconds if the connection breaks.
        private readonly HashSet<(ushort Vk, bool Ext)> _down = new();

        /// <summary>How the sound is reduced right now, for the title; null at full quality.</summary>
        public string? ReducedSound => AudioQuality == 0 ? null : Protocol.OpusSteps[AudioQuality].Kbps + " kbit/s";

        public bool CanRestart => !ListenOnly && (_peerFeatures & Protocol.FeatureRestart) != 0;

        /// <summary>The host's TailRemote version (from its features), or null until it has said.</summary>
        public Version? HostVersion { get; private set; }

        public bool CanRemoteTools => !ListenOnly && (_peerFeatures & Protocol.FeatureRemoteTools) != 0;

        /// <summary>Asks the host to update TailRemote to this version (it answers with a message).</summary>
        public void RequestUpdate(Version v) => Write(Protocol.TextMessage(Protocol.UpdateTo, v.ToString()));

        private readonly ConcurrentDictionary<uint, TaskCompletionSource<string>> _folderRequests = new();
        private int _nextRequest;
        private TaskCompletionSource<string>? _infoRequest;
        private TaskCompletionSource<string>? _speedRequest;

        /// <summary>The host's internet speed test (SpeedTest.RunAsync there). Fails after 90 seconds.</summary>
        public Task<string> RequestSpeedTestAsync()
        {
            if (_closed) return Task.FromException<string>(new System.IO.IOException("Not connected."));
            var wait = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _speedRequest, wait)?.TrySetCanceled();
            Write(stackalloc byte[] { Protocol.SpeedTestRequest });
            return wait.Task.WaitAsync(TimeSpan.FromSeconds(90));
        }

        /// <summary>
        /// A connection waiting in the background (several PCs at once): its sound is not played
        /// (the PC in front is the one heard), and it starts afresh when it comes to the front.
        /// </summary>
        public bool Muted
        {
            get => _muted;
            set
            {
                if (_muted == value) return;
                _muted = value;
                // Not even a few kilobits nobody hears: the host sends no sound at all until unmuted,
                // which saves the data on a phone connection. The UDP hello and its ping carry on.
                try { Write(new[] { Protocol.AudioPause, (byte)(value ? 1 : 0) }); } catch { }
            }
        }
        private volatile bool _muted;

        /// <summary>Back to the best sound at once (coming to the front), unless the quality is locked.</summary>
        public void StartBest()
        {
            _judgeAfresh = true;
            if (_lockedStep < 0) lock (_stepGate) SetStep(0);
        }

        /// <summary>A folder's contents on the host (see RemoteTools.ListFolder), or "" for the drives. Fails after 15 seconds.</summary>
        public async System.Threading.Tasks.Task<string> ListFolderAsync(string path)
        {
            if (_closed) throw new System.IO.IOException("Not connected.");
            uint id = (uint)Interlocked.Increment(ref _nextRequest);
            var wait = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _folderRequests[id] = wait;
            byte[] p = System.Text.Encoding.UTF8.GetBytes(path);
            byte[] m = new byte[5 + p.Length];
            m[0] = Protocol.ListFolder;
            BitConverter.TryWriteBytes(m.AsSpan(1), id);
            p.CopyTo(m, 5);
            Write(m);
            try { return await wait.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { _folderRequests.TryRemove(id, out _); }
        }

        /// <summary>Asks the host to send these files and folders here, like Send files from there.</summary>
        /// False when the connection has closed, so nothing was asked for.
        public bool Fetch(IEnumerable<string> paths)
        {
            if (_closed) return false;
            Write(Protocol.TextMessage(Protocol.Fetch, string.Join('\n', paths)));
            return true;
        }

        /// <summary>About the host PC (RemoteTools.Info). Fails after 15 seconds.</summary>
        public System.Threading.Tasks.Task<string> RequestInfoAsync()
        {
            if (_closed) return Task.FromException<string>(new System.IO.IOException("Not connected."));
            // Asked again while waiting (F5 twice): the same answer, rather than failing the first.
            if (Volatile.Read(ref _infoRequest) is { } waiting) return waiting.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var wait = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _infoRequest, wait)?.TrySetCanceled();
            Write(stackalloc byte[] { Protocol.InfoRequest });
            return wait.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        public bool CanSecureAttention => !ListenOnly && (_peerFeatures & Protocol.FeatureSecureAttention) != 0;

        /// <summary>Asks the host to send Ctrl+Alt+Del (only a host running as the service can).</summary>
        public void SendSecureAttention() => Write(stackalloc byte[] { Protocol.SecureAttention });

        /// <summary>Asks the host PC to restart.</summary>
        public void RestartHost() => Write(stackalloc byte[] { Protocol.RestartPc });

        private long _lastClipboardRequestAt = long.MinValue / 2;

        /// <summary>
        /// Asks the host to send its clipboard here (text or files), the pull that mirrors Send the
        /// clipboard. What comes back arrives on the file lanes like any other clipboard, through
        /// ClipboardReceived / ClipboardFilesReceived, and lands on this PC's clipboard. Not for listeners.
        /// Returns false when it dropped the request (throttled): mashing or holding Ctrl+Shift+B would
        /// otherwise flood the host with one clipboard read + fan-out per press. At most one pull every
        /// half second goes out; extras are dropped quietly.
        /// </summary>
        public bool RequestClipboard()
        {
            if (_closed || ListenOnly) return false;
            long now = Environment.TickCount64;
            long prev = Interlocked.Read(ref _lastClipboardRequestAt);
            if (now - prev < Protocol.ClipboardPullThrottleMs) return false;
            if (Interlocked.CompareExchange(ref _lastClipboardRequestAt, now, prev) != prev) return false;
            Write(stackalloc byte[] { Protocol.ClipboardRequest });
            return true;
        }

        /// <summary>Sends clipboard text to the host's clipboard. Never waits. Not for listeners.</summary>
        public void SendClipboard(string text)
        {
            // Over the file lanes, never the keys' connection: confirmed on arrival, and sent again
            // if a connection breaks on the way.
            var files = _files;
            if (_closed || ListenOnly || files == null) return;
            ThreadPool.QueueUserWorkItem(_ => { try { files.SendText(text); } catch { } });
        }

        private void TcpLoop()
        {
            try
            {
                while (!_closed)
                {
                    byte[] m = _link.Receive(_stream);
                    if (m.Length == 9 && m[0] == Protocol.Pong)
                    {
                        long sent = BitConverter.ToInt64(m, 1);
                        LastPingMs = (int)((Stopwatch.GetTimestamp() - sent) * 1000 / Stopwatch.Frequency);
                        _lastPong = Environment.TickCount64;
                    }
                    else if (m.Length >= 1 && m.Length <= 4097 && m[0] == Protocol.WakeInfo) // a few network cards' addresses; more is nonsense kept out of the settings file
                    {
                        WakeInfo = System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1);
                        WakeInfoKnown?.Invoke(WakeInfo);
                    }
                    else if (m.Length >= 2 && m[0] == Protocol.HealthAlert)
                        HealthSaid(System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1));
                    else if (m.Length >= 1 && m[0] == Protocol.Message)
                    {
                        Leaving = 0; // still here after all (a restart that failed says so this way)
                        Status?.Invoke("Host: " + System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1));
                    }
                    else if (m.Length >= 5 && m[0] == Protocol.FolderList)
                    {
                        uint id = BitConverter.ToUInt32(m, 1);
                        if (_folderRequests.TryRemove(id, out var wait)) wait.TrySetResult(System.Text.Encoding.UTF8.GetString(m, 5, m.Length - 5));
                    }
                    else if (m.Length >= 1 && m[0] == Protocol.SpeedResult)
                        Interlocked.Exchange(ref _speedRequest, null)?.TrySetResult(System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1));
                    else if (m.Length >= 1 && m[0] == Protocol.Info)
                        Interlocked.Exchange(ref _infoRequest, null)?.TrySetResult(System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1));
                    else if (m.Length >= 2 && m[0] == Protocol.Leaving)
                    {
                        LeavingDetail = System.Text.Encoding.UTF8.GetString(m, 2, m.Length - 2);
                        Leaving = m[1];
                        _leavingAt = Environment.TickCount64;
                        DiagLog.Write("client: host is leaving, reason " + m[1] + " " + LeavingDetail);
                    }
                    else if (m.Length >= 5 && m[0] == Protocol.Features)
                    {
                        _peerFeatures = BitConverter.ToUInt32(m, 1);
                        HostVersion = Protocol.FeaturesVersion(m);
                        DiagLog.Write("client: host features " + _peerFeatures + (ListenOnly ? ", listen only" : ", control"));
                        FeaturesKnown?.Invoke();
                    }
                    // Anything else is from a newer version: ignore it.
                }
            }
            catch (Exception e)
            {
                if (!_closed) Close("Disconnected: " + Plain(e));
            }
        }

        /// <summary>Why the connection broke, in words, instead of Windows' "An existing connection was forcibly closed by the remote host".</summary>
        private static string Plain(Exception e)
        {
            if (e is System.IO.EndOfStreamException) return "the remote PC closed the connection.";
            var socket = e as SocketException ?? e.InnerException as SocketException;
            return socket?.SocketErrorCode switch
            {
                SocketError.ConnectionReset or SocketError.ConnectionAborted => "the remote PC's Kova closed suddenly, or the network dropped.",
                SocketError.TimedOut => "the remote PC stopped answering.",
                SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.HostUnreachable => "this PC lost its network connection.",
                _ => e.Message,
            };
        }

        // ---- Receiving audio ----

        /// <summary>Test only (--audiotest): delay each audio packet by a random 0 to N ms, like a bumpy network.</summary>
        public static int TestJitterMs;
        /// <summary>Test only (--audiotest ... lagN): from 5 seconds in, every packet arrives N ms later, like Clumsy's lag.</summary>
        public static int TestLagMs;
        /// <summary>Test only (--audiotest ... stallN): 5 seconds in, nothing arrives for N ms, then all of it at once.</summary>
        public static int TestStallMs;
        /// <summary>Test only (--audiotest ... cellNNN): a phone connection. Sound comes in bunches with up to NNN ms between, now and then a longer stall, and 2 percent never arrives.</summary>
        public static int TestCellMs;
        /// <summary>Test only (--audiotest ... scanNNN): a Wi-Fi card scanning every 10 seconds, holding everything back NNN ms.</summary>
        public static int TestScanMs;
        /// <summary>Test only (--audiotest ... dropN): throws away N percent of audio packets at random, like Clumsy.</summary>
        public static int TestDropPercent;
        /// <summary>Test only (--audiotest ... bwN): from 3 seconds in, audio over N kbit/s is thrown away, like Clumsy's bandwidth limit.</summary>
        public static int TestKbps;
        /// <summary>Test only (--audiotest ... untilN): the bandwidth limit ends N seconds in, to check the climb back.</summary>
        public static int TestKbpsUntil;
        /// <summary>Test only (--audiotest ... dialupN): from the start, an N kbit/s link that queues like a modem (2 s of buffer) instead of dropping.</summary>
        public static int TestDialupKbps;
        private double _modemFreeAt;
        /// <summary>Test only: the length (in 5 ms ticks) of the first audio packet, and how many packets came at another length.</summary>
        public static int TestFirstTicks, TestOtherTicks;
        public static int TestLockStep = -1;
        private double _bucket;
        private long _bucketAt;
        private readonly SortedList<(long Due, long N), byte[]> _jitterQueue = new();
        private readonly Stopwatch _jitterClock = Stopwatch.StartNew();
        private long _jitterN;

        private void JitterLoop()
        {
            while (!_closed)
            {
                byte[]? next = null;
                lock (_jitterQueue)
                {
                    if (_jitterQueue.Count > 0 && _jitterQueue.Keys[0].Due <= _jitterClock.ElapsedMilliseconds)
                    {
                        next = _jitterQueue.Values[0];
                        _jitterQueue.RemoveAt(0);
                    }
                }
                if (next != null) SafeHandle(next); else Thread.Sleep(1);
            }
        }

        private void UdpLoop()
        {
            if (TestJitterMs > 0 || TestLagMs > 0 || TestStallMs > 0 || TestDialupKbps > 0 || TestCellMs > 0 || TestScanMs > 0) new Thread(JitterLoop) { IsBackground = true, Name = "Kova test jitter" }.Start();
            var any = new IPEndPoint(IPAddress.IPv6Any, 0);
            while (!_closed)
            {
                byte[] d;
                try
                {
                    // Something waits behind a missing packet: look every 2 ms whether its wait is over.
                    if (Volatile.Read(ref _waiting) && !_udp.Client.Poll(2000, SelectMode.SelectRead)) { FlushDue(); continue; }
                    d = _udp.Receive(ref any);
                    _udpErrors = 0;
                }
                catch
                {
                    if (_closed) return;
                    if (++_udpErrors > 10) Thread.Sleep(5); // an error that keeps coming must not spin a core
                    continue;
                }
                if (any.Port != _hostUdp.Port) continue; // only TailRemote's port (any of the host's addresses)
                Interlocked.Increment(ref _rxPackets);
                Interlocked.Add(ref _rxBytes, d.Length);
                if (TestDropPercent > 0 && Random.Shared.Next(100) < TestDropPercent) continue;
                if (TestKbps > 0 && _jitterClock.ElapsedMilliseconds > 3000 && (TestKbpsUntil == 0 || _jitterClock.ElapsedMilliseconds < TestKbpsUntil * 1000))
                {
                    long t = _jitterClock.ElapsedMilliseconds;
                    _bucket = Math.Min(TestKbps * 1000 / 8 * 0.05, _bucket + (t - _bucketAt) * TestKbps / 8.0); // bytes; 50 ms of burst
                    _bucketAt = t;
                    if (_bucket < d.Length) continue;
                    _bucket -= d.Length;
                }
                if (TestDialupKbps > 0)
                {
                    double t = _jitterClock.Elapsed.TotalMilliseconds;
                    double start = Math.Max(t, _modemFreeAt);
                    if (start - t > 2000) continue; // the modem's buffer is full: dropped
                    _modemFreeAt = start + (d.Length + Protocol.PacketOverheadBytes - 22) * 8.0 / TestDialupKbps;
                    lock (_jitterQueue) _jitterQueue.Add(((long)_modemFreeAt, _jitterN++), d);
                    continue;
                }
                if (TestCellMs > 0)
                {
                    if (Random.Shared.Next(100) < 2) continue;
                    long at = _jitterClock.ElapsedMilliseconds;
                    // Delivered in bunches, the way a phone network schedules: everything waits for the next slot.
                    long slot = Math.Max(20, TestCellMs / 2);
                    at = (at / slot + 1) * slot + Random.Shared.Next(TestCellMs / 2 + 1);
                    // About every 7 seconds, a stall up to three times as long.
                    if (at % 7000 < 300) at += Random.Shared.Next(TestCellMs * 3);
                    lock (_jitterQueue) _jitterQueue.Add((at, _jitterN++), d);
                    continue;
                }
                if (TestJitterMs > 0 || TestLagMs > 0 || TestStallMs > 0 || TestScanMs > 0)
                {
                    long at = _jitterClock.ElapsedMilliseconds;
                    if (at >= 5000 && at < 5000 + TestStallMs) at = 5000 + TestStallMs;
                    if (TestScanMs > 0 && at % 10_000 < TestScanMs) at += TestScanMs - at % 10_000;
                    at += Random.Shared.Next(TestJitterMs + 1) + (at > 5000 ? TestLagMs : 0);
                    lock (_jitterQueue) _jitterQueue.Add((at, _jitterN++), d);
                    continue;
                }
                SafeHandle(d);
            }
        }

        /// <summary>A bad packet or a player hiccup must never take the whole app down.</summary>
        private void SafeHandle(byte[] d)
        {
            try { lock (_queue) HandlePacket(d); }
            catch (Exception e) { DiagLog.Write("client: audio packet failed: " + e.Message); }
        }

        private int _udpErrors;
        private bool _waiting; // packets wait in _queue behind a gap

        /// <summary>Plays what waited behind a missing packet once its wait is over, with no new packet needed to notice.</summary>
        private void FlushDue()
        {
            try
            {
                lock (_queue)
                {
                    // Just sent to the background: what waited is thrown away, never played into the Player
                    // the PC now in front is using (the packet that would clear it never comes once muted).
                    if (Muted) { _queue.Clear(); _haveNext = false; Volatile.Write(ref _waiting, false); return; }
                    Drain(Player.Now);
                }
            }
            catch (Exception e) { DiagLog.Write("client: audio packet failed: " + e.Message); }
        }

        // ---- Opus, in order ----
        // Packets can arrive out of order (Wi-Fi, Windows' own loopback, Clumsy). They
        // wait in a small queue and are decoded strictly in order. When one is missing,
        // the queue waits for it as long as the buffer's jitter allowance (never more
        // than 40 ms): that time is already in the buffer, so waiting adds no delay.
        // Only if it still has not come is the gap filled in by Opus: rebuilt from the
        // next packet where Opus sent recovery data for it (the speech steps),
        // otherwise its own concealment, which continues the sound smoothly. A packet
        // that comes after its gap was filled is too late and is dropped.

        private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(Protocol.AudioRate, 2);
        private readonly short[] _pcm = new short[5760 * 2];
        private readonly SortedList<uint, (byte[] Data, double At)> _queue = new();
        private double _gapSince;
        private uint _next;
        private bool _haveNext;

        private void HandlePacket(byte[] d)
        {
            if (d.Length == Protocol.UdpPongBytes && d[0] == Protocol.UdpPong)
            {
                // Only the host's, and only newer than the last: a replayed one would fake the ping.
                if (!_link.HelloOk(d)) return;
                long sent = BitConverter.ToInt64(d, 1);
                if (sent <= _lastPongStamp) return;
                _lastPongStamp = sent;
                long ms = (Stopwatch.GetTimestamp() - sent) * 1000 / Stopwatch.Frequency;
                if (ms < 0 || ms >= 10_000) return;
                Volatile.Write(ref _lastUdpPong, Environment.TickCount64);
                AudioPingMs = (int)ms;
                PaceFrom((int)ms);
                return;
            }
            if (d.Length < Protocol.MinAudioPacketBytes || d[0] != Protocol.UdpOpus) return;
            if (!_link.OpenAudio(d, d.Length)) { Interlocked.Increment(ref _rxDamaged); return; } // changed on the way (or not for us): counts as lost
            if (Muted)
            {
                // In the background: nothing is played, and nothing old is kept for when it comes back.
                if (_haveNext) { _queue.Clear(); _haveNext = false; Volatile.Write(ref _waiting, false); }
                return;
            }
            Interlocked.Increment(ref _qPackets);
            Interlocked.Add(ref _qBytes, d.Length);
            if (d[5] is >= 1 and <= 24) Interlocked.Add(ref _qTicks, d[5]);
            int ticks = d[5];
            if (ticks < 1 || ticks > 24) return;
            if (TestFirstTicks == 0) TestFirstTicks = ticks; else if (ticks != TestFirstTicks) TestOtherTicks++;
            uint seq = BitConverter.ToUInt32(d, 1);
            double now = Player.Now;
            if (_haveNext && (int)(seq - _next) < 0) { _player.CountLate(ticks); return; } // its moment has passed
            if (_haveNext && (int)(seq - _next) > 400) { _queue.Clear(); _haveNext = false; } // after a pause: start again from here
            _queue.TryAdd(seq, (d, now));
            Drain(now);
        }

        /// <summary>Decodes what is in order in the queue; what waits behind a gap waits up to ReorderWaitMs.</summary>
        private void Drain(double now)
        {
            while (_queue.Count > 0)
            {
                // The earliest counting from _next, not the smallest number: where the host's packet
                // number wraps (every 248 days of hosting) the small new numbers sorted first.
                int pick = 0;
                if (_haveNext)
                    for (int i = 1; i < _queue.Count; i++)
                        if ((int)(_queue.Keys[i] - _next) < (int)(_queue.Keys[pick] - _next)) pick = i;
                uint first = _queue.Keys[pick];
                var (data, at) = _queue.Values[pick];
                if (_haveNext && first != _next)
                {
                    // A gap before the first one waiting: wait for it, a little.
                    if (_gapSince == 0) _gapSince = at;
                    if (now - _gapSince < _player.ReorderWaitMs && _queue.Count < 32) break;
                }
                _queue.RemoveAt(pick);
                _gapSince = 0;
                Deliver(data, at);
            }
            Volatile.Write(ref _waiting, _queue.Count > 0);
        }

        /// <summary>Decodes and plays one packet that has already been opened (decrypted).</summary>
        private void Deliver(byte[] d, double arrivedAt)
        {
            uint seq = BitConverter.ToUInt32(d, 1);
            int ticks = d[5];
            var opus = d.AsSpan(6, d.Length - 6 - SecureLink.TagSize);
            if (_haveNext)
            {
                int gap = (int)(seq - _next);
                if (gap < 0) { _player.CountLate(ticks); return; } // its moment has passed
                if (gap > 0 && gap <= 24)
                {
                    if (gap == ticks && ticks >= 8)
                    {
                        // Only the packet before this one is missing: Opus's recovery data in this one rebuilds it.
                        int n = _decoder.Decode(opus, _pcm, gap * Protocol.TickFrames, true);
                        if (n > 0) _player.Push(_next, _pcm.AsSpan(0, n * 2), gap, concealed: true);
                    }
                    else
                    {
                        for (int left = gap; left > 0;)
                        {
                            int t = Math.Min(left, 12);
                            int n = _decoder.Decode(ReadOnlySpan<byte>.Empty, _pcm, t * Protocol.TickFrames, false);
                            if (n > 0) _player.Push(seq - (uint)left, _pcm.AsSpan(0, n * 2), t, concealed: true);
                            left -= t;
                        }
                    }
                }
            }
            int frames = _decoder.Decode(opus, _pcm, _pcm.Length / 2, false);
            _next = seq + (uint)ticks;
            _haveNext = true;
            if (frames > 0) _player.Push(seq, _pcm.AsSpan(0, frames * 2), ticks, arrivedAt: arrivedAt);
        }

        // ---- Bitrate steps ----

        private const int QualityTickMs = 200;
        private const int StepEveryMs = 750, StepAllInMs = 3000; // a change takes about 3 s however far: one step at most every 0.75 s, faster for long ways
        private readonly int[] _bad = new int[5], _sent = new int[5], _winBytes = new int[5], _winPackets = new int[5]; // the last second, by 0.2 s tick
        private int _badAt, _cleanTicks, _target, _ceiling, _failures;
        private long _shortSince; // since when the sound has been arriving slower than real time
        private long _silentSince;
        private volatile bool _judgeAfresh; // forget what was measured: set by a lock, a silence or StartBest
        private long _noHelpUntil, _lastTargetAt, _lastMoveAt, _ceilingUntil, _lastFailAt, _lastUpAt;
        private double _lossBefore, _noHelpLoss;
        private int _qPackets, _qBytes, _qTicks, _winFilled;
        private long _fitsSince;
        private readonly int[] _winTicks = new int[5];

        /// <summary>The bitrate step to hold no matter what, or -1 to follow the connection (Variable). Takes effect at once.</summary>
        public int LockedStep
        {
            get => _lockedStep;
            set
            {
                _lockedStep = Math.Min(value, Protocol.OpusSteps.Length - 1);
                if (_lockedStep >= 0) lock (_stepGate) SetStep(_lockedStep);
            }
        }
        private volatile int _lockedStep = -1;
        private readonly object _stepGate = new();

        /// <summary>Test only (--audiotest ... steps): hold the quality where the test puts it.</summary>
        public static bool TestHoldQuality;

        public void TestSetQuality(int q) => SetStep(q);

        private void SetStep(int q)
        {
            if (q == AudioQuality) return;
            DiagLog.Write("client: bitrate step " + AudioQuality + " to " + q + " (" + Protocol.OpusSteps[q].Kbps + " kbit/s)");
            AudioQuality = q;
            Write(stackalloc byte[] { Protocol.AudioQuality, (byte)q });
        }

        private void QualityLoop()
        {
            int tick = 0;
            while (!_closed)
            {
                Thread.Sleep(QualityTickMs);
                try { AdaptQuality(); } catch { }
                if (++tick % (1000 / QualityTickMs) == 0)
                {
                    if (DiagLog.Enabled && !Muted) LogSecond(); // the shared Player's figures are the front PC's
                    else
                    {
                        // Taken every second anyway: left adding up for months, they wrapped round and the
                        // first line after logging was turned on said nonsense.
                        Interlocked.Exchange(ref _rxPackets, 0); Interlocked.Exchange(ref _rxBytes, 0);
                        Interlocked.Exchange(ref _keysSent, 0); Interlocked.Exchange(ref _rxDamaged, 0);
                    }
                }
            }
        }

        /// <summary>
        /// Five times a second, looking at the last second.
        ///
        /// Locked: the chosen step, always. Variable: a target step, and the bitrate
        /// moves toward it one step at a time, the whole way in about 3 seconds (never
        /// faster than one step every 0.75 s for a short way), so it changes over
        /// seconds rather than all at once, yet a dial-up line is found in seconds too.
        ///
        /// Sound arriving slower than real time (under 80 percent) is a starved
        /// connection even with nothing lost: modems and full links queue, not drop.
        ///
        /// More than 10 percent of the sound never arriving (a capped or slow
        /// connection) sets the target to the best step that fits in what is really
        /// arriving, headers included. While it is that bad, it is judged again every
        /// second. A lower step that does not help (2 seconds on, the loss has not
        /// fallen) means random loss, which no bitrate fixes: back to the best, and
        /// that loss cannot lower it again for 30 seconds. Over 40 percent is always
        /// a starved connection.
        ///
        /// Clean for 0.6 s: the target goes back to the best and it climbs, step by
        /// step. Trouble within 10 seconds of climbing a step sets a ceiling at the
        /// step before, held 5 seconds the first time, then 10, 20, up to a minute, so
        /// it does not keep breaking up trying to go higher. A bad patch that is not
        /// caused by climbing never holds it down.
        /// </summary>
        private void AdaptQuality()
        {
            // In the background: the Player and its counts are the front PC's (one Player for all), so
            // taking them here stole that PC's figures and made it judge its line wrongly.
            if (Muted)
            {
                int held = _lockedStep;
                if (held >= 0) lock (_stepGate) SetStep(held);
                _judgeAfresh = true;
                return;
            }
            var (packets, lost, late) = _player.TakeStats();
            int bytes = Interlocked.Exchange(ref _qBytes, 0), arrived = Interlocked.Exchange(ref _qPackets, 0), ticksIn = Interlocked.Exchange(ref _qTicks, 0);
            if (TestHoldQuality) return;
            int locked = _lockedStep;
            // Locked, or silent: nothing to judge. After a lock (a mute, a switch) or a long
            // silence, what was measured before is out of date. Not after a short one: speech
            // pauses all the time, and forgetting then made it climb back into trouble.
            if (locked >= 0) { lock (_stepGate) SetStep(locked); _judgeAfresh = true; return; }
            if (packets == 0)
            {
                if (_silentSince == 0) _silentSince = Environment.TickCount64;
                else if (Environment.TickCount64 - _silentSince > 10_000) _judgeAfresh = true;
                return;
            }
            _silentSince = 0;
            if (_judgeAfresh)
            {
                _judgeAfresh = false;
                Array.Clear(_bad); Array.Clear(_sent); Array.Clear(_winBytes); Array.Clear(_winPackets); Array.Clear(_winTicks);
                _winFilled = 0;
                _shortSince = 0;
                _target = AudioQuality;
                _ceilingUntil = 0;
                _cleanTicks = 0;
                _fitsSince = 0;
                _noHelpUntil = 0;
            }
            // Only sound that never came: what arrived late was counted lost first, then
            // late, and lateness is not a bandwidth problem a lower bitrate could fix.
            int bad = Math.Max(0, lost - late);
            _badAt = (_badAt + 1) % _bad.Length;
            _bad[_badAt] = bad;
            _sent[_badAt] = packets + bad;
            _winBytes[_badAt] = bytes;
            _winPackets[_badAt] = arrived;
            _winTicks[_badAt] = ticksIn;
            if (_winFilled < _bad.Length) _winFilled++;
            int badSecond = 0, sentSecond = 0, bytesSecond = 0, packetsSecond = 0, ticksSecond = 0;
            for (int i = 0; i < _bad.Length; i++) { badSecond += _bad[i]; sentSecond += _sent[i]; bytesSecond += _winBytes[i]; packetsSecond += _winPackets[i]; ticksSecond += _winTicks[i]; }
            double loss = sentSecond == 0 ? 0 : (double)badSecond / sentSecond;
            bool heavy = bad >= 4 && bad * 4 >= packets + bad;
            // Starved: the sound is not even arriving in real time (under 80 percent of it
            // over a whole second, for at least a second running). A modem or a full link
            // queues rather than drops, so nothing looks lost, it just comes too slowly.
            // That is never random loss. A phone network hands sound over in bunches and
            // stalls now and then, then catches up: that is delay, which no bitrate fixes,
            // and judging it on less made the bitrate go down and up every few seconds.
            int expectedTicks = _winFilled * QualityTickMs / (int)Protocol.TickMs;
            long now = Environment.TickCount64;
            if (_winFilled >= _bad.Length && ticksSecond < expectedTicks * 0.8) { if (_shortSince == 0) _shortSince = now; }
            else _shortSince = 0;
            bool starved = _shortSince != 0 && now - _shortSince >= 1000;
            bool struggling = heavy || loss > 0.10 || starved;
            bool clean = loss < 0.07 && !starved;
            int q = AudioQuality, lowest = Protocol.OpusSteps.Length - 1;

            if (struggling && !starved && now < _noHelpUntil && loss < _noHelpLoss * 1.5 && loss < 0.4) struggling = false; // lowering did not help last time
            if (now - _lastFailAt > 30_000) _failures = 0;

            bool noHelp = !starved && _target > 0 && q > 0 && _lossBefore > 0.05 && loss < 0.4 && now - _lastTargetAt >= 2000 && loss >= _lossBefore * 0.9;
            if (noHelp)
            {
                // Lower did not help: random loss, not a full connection.
                _noHelpUntil = now + 30_000;
                _noHelpLoss = _lossBefore;
                _target = 0;
                _ceilingUntil = 0;
                _cleanTicks = 0;
            }
            else if (struggling && now - _lastTargetAt >= 600 && q < lowest)
            {
                if (_target == 0) _lossBefore = loss;
                // The best step that fits in what really arrives (headers included), with room to
                // spare; per second, however much of the window has filled so far.
                double wire = (bytesSecond + packetsSecond * (Protocol.PacketOverheadBytes - 22)) * 8 / 1000.0 * _bad.Length / Math.Max(1, _winFilled);
                int fit = lowest;
                for (int i = 0; i <= lowest; i++) if (Protocol.WireKbps(i) <= wire * 0.85) { fit = i; break; }
                if (fit > q) { _target = Math.Max(_target, fit); _fitsSince = 0; }
                else
                {
                    // What it is sending already fits: a modem is still handing over what it
                    // queued before. Give that 3 seconds to drain before going lower still
                    // (stepping down on it went far too low, then bounced back up).
                    if (_fitsSince == 0) _fitsSince = now;
                    else if (now - _fitsSince > 3000) { _target = Math.Max(_target, Math.Min(lowest, q + 1)); _fitsSince = 0; }
                }
                // What the connection was measured to carry is the most to climb back to for
                // a while: 5 seconds after an ordinary bad patch, so it recovers quickly; when
                // climbing itself caused the trouble, 5 s, then 10, 20, up to a minute. (A modem
                // hides an overload for a few seconds while it queues, so without this it
                // climbed straight back into trouble. Counting every bad second as a failure
                // kept it down for a minute after the network had recovered.)
                // Within 10 seconds of a step up counts as the climb's fault: on a phone network the
                // trouble from going higher often takes several seconds to show.
                bool climbing = now - _lastUpAt < 10_000;
                _ceiling = Math.Max(fit, climbing ? Math.Min(lowest, q + 1) : 0);
                _ceilingUntil = now + (climbing ? Math.Min(60_000, 5000 << Math.Min(_failures, 4)) : 5000);
                if (climbing) { _failures++; _lastFailAt = now; }
                _lastTargetAt = now;
                _cleanTicks = 0;
                Array.Clear(_bad); Array.Clear(_sent); Array.Clear(_winBytes); Array.Clear(_winPackets); Array.Clear(_winTicks); // judge from here on
                _winFilled = 0;
            }
            else if (!clean) _cleanTicks = 0;
            else if (++_cleanTicks >= 3) { _target = now < _ceilingUntil ? Math.Max(_ceiling, 0) : 0; _fitsSince = 0; }

            // One step at a time toward the target, the whole way in about 3 seconds: 0.75 s a
            // step for a short way (128 to 32 kbit/s), quicker steps for a long one (510 to 16).
            int every = Math.Clamp(StepAllInMs / Math.Max(1, Math.Abs(_target - q)), QualityTickMs, StepEveryMs);
            if (_target != q && now - _lastMoveAt >= every - QualityTickMs / 2) // checks come every 0.2 s: do not round up to the next
            {
                _lastMoveAt = now;
                if (_target < q) _lastUpAt = now;
                lock (_stepGate) if (_lockedStep < 0) SetStep(q + Math.Sign(_target - q));
            }
        }

        // ---- The once-a-second log line (only while logging is on) ----
        private int _rxPackets, _rxBytes, _keysSent, _rxDamaged;

        private void LogSecond()
        {
            int packets = Interlocked.Exchange(ref _rxPackets, 0), bytes = Interlocked.Exchange(ref _rxBytes, 0), keys = Interlocked.Exchange(ref _keysSent, 0);
            int damaged = Interlocked.Exchange(ref _rxDamaged, 0);
            DiagLog.Write("client: " + _player.Diagnose() + ", ping " + LastPingMs + " ms, audio ping " + AudioPingMs + " ms, bitrate step " + AudioQuality +
                " (" + Protocol.OpusSteps[AudioQuality].Kbps + " kbit/s" + (LockedStep >= 0 ? ", locked" : "") + ")" +
                ", received " + packets + " packets, " + (bytes * 8 / 1000) + " kbit/s, damaged " + damaged +
                ", keys sent " + keys + ", playing on " + _player.DeviceInfo);
        }

        private void Heartbeat()
        {
            byte[] hello = new byte[Protocol.UdpHelloBytes];
            hello[0] = Protocol.UdpHello;
            _token.CopyTo(hello, 1);
            byte[] ping = new byte[9];
            long lastHello = 0, lastPing = 0;
            _lastPong = Environment.TickCount64;
            Volatile.Write(ref _lastUdpPong, _lastPong);
            while (!_closed)
            {
                if (TestSilent) { Thread.Sleep(50); _lastPong = Environment.TickCount64; continue; }
                long now = Environment.TickCount64;
                if (now - _lastPong > 8000) { Close("Disconnected: the host stopped answering."); return; }
                // The connection works but UDP gets no answer: no sound can come. Said once, plainly,
                // with what to do (and once more when it gets through again).
                bool blocked = now - Volatile.Read(ref _lastUdpPong) > 4000;
                if (blocked != _udpBlocked)
                {
                    _udpBlocked = blocked;
                    DiagLog.Write("client: UDP " + (blocked ? "gets no answer" : "answers again"));
                    Status?.Invoke(blocked
                        ? "No sound: UDP port " + _hostUdp.Port + " is blocked between the PCs, though " + (ListenOnly ? "the connection still works" : "keys still work") + ". On the remote PC, open the port with Port editor, or check its firewall and the network's."
                        : "Sound is getting through again.");
                }
                // The UDP hello (and its pong, the audio ping): once a second, or ten times a second
                // while files are going either way, so the pacing sees queueing at once.
                if (now - lastHello >= (_files?.Busy == true || Volatile.Read(ref _watching) > 0 ? 100 : 1000))
                {
                    lastHello = now;
                    BitConverter.TryWriteBytes(hello.AsSpan(9), Stopwatch.GetTimestamp()); // the host sends it straight back
                    _link.SignHello(hello); // the host ignores a hello without it
                    try { _udp.Send(hello, hello.Length, _hostUdp); } catch { }
                }
                bool holding;
                lock (_down) holding = _down.Count > 0;
                if (now - lastPing >= (holding ? 300 : 2000))
                {
                    lastPing = now;
                    ping[0] = Protocol.Ping;
                    BitConverter.TryWriteBytes(ping.AsSpan(1), Stopwatch.GetTimestamp());
                    Write(ping);
                }
                Thread.Sleep(50);
            }
        }
    }
}
