using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KovaRelayWire;

namespace KovaRelay
{
    /// <summary>
    /// Every limit the relay keeps, in one place.
    ///
    /// The most memory the relay can use under these limits, roughly (managed memory, on top of the .NET runtime's
    /// own 20 to 30 MB):
    ///   - Spliced pairs: a copy buffer (SpliceBuffer) is held only while bytes are actually moving in that direction;
    ///     a pair waiting for data holds none (it waits with a zero-byte read). So memory follows the traffic, not the
    ///     number of connections: a thousand busy directions at once hold 64 MB, idle ones hold nothing. Each
    ///     connection costs about 2 KB of bookkeeping besides.
    ///   - Connections still asking: a 36-byte hello and at most a 1027-byte frame each.
    ///   - Per-address table: at most MaxAddressEntries entries of about 200 bytes with their dictionary slot, about 4 MB.
    ///   - Connected hosts, pending pairings and recently used pairing ids: bounded by connections and by time.
    ///   - UDP: one 64 KB receive buffer on its own thread; packets are forwarded straight from it, never queued.
    ///   - Log: at most 2,000 queued lines, under 1 MB.
    /// Plus Windows' socket buffers in the kernel, which Windows sizes and bounds by itself.
    /// No thread per connection: everything TCP is asynchronous; UDP has one dedicated thread.
    /// </summary>
    internal static class Limits
    {
        public const int MaxConnections = 20_000;       // every TCP connection at once, host control lines included
        public const int PerAddressConnections = 64;    // one controlling PC uses up to about 20 (main, 16 file lanes, screen)
        public const int PerAddressUnpaired = 40;       // still asking (not yet a host line or a pair): a controlling PC opens its main line, 16 file lanes and the screen at once
        public const double BucketSize = 40;            // new connections per address: a burst of 40...
        public const double BucketPerSecond = 5;        // ...then 5 a second
        public const int FailuresToBlock = 20;          // bad frames, bad signatures, wrong tokens, missed deadlines...
        public const long FailureWindowMs = 60_000;     // ...within a minute...
        public const long BlockMs = 10 * 60_000;        // ...block the address for 10 minutes (closed at once, nothing read)
        public const int MaxAddressEntries = 20_000;    // per-address table cap; the longest unused go first
        public const long ForgetAddressAfterMs = 60 * 60_000; // an idle address entry is dropped after an hour
        public const int MissesPerHour = 30;            // DIFFERENT absent hosts asked for (different wrong passwords, in effect), per address per hour
        public const long MissWindowMs = 3_600_000;     // ...after which the address's lookups are turned away until the hour is up
        // An address with a registered host gets more room, because its host dials back once for every lane a controlling
        // PC opens (about 20 each). Registering costs nothing (any password will do), so the extra room comes mostly from
        // live pairs, which need a real controlling PC each, and it stays small: one address can never take a large share.
        public const int HostBaseFactor = 2;            // any registered host: twice the connections and twice the rate bucket...
        public const int PairsPerExtraFactor = 16;      // ...plus once more for every 16 live pairs its hosts carry...
        public const int MaxHostRoomFactor = 8;         // ...up to 8 times in all (512 connections; an office of PCs behind one router)
        public const int MaxHostsPerAddress = 32;       // hosts one address may have registered at once
        public const int PendingPerHost = 128;          // pairings waiting for one host to dial back (two PCs connecting at once ask for about 40)
        public const int PendingPerClient = 40;         // pairings waiting, asked for from one address (one PC asks for about 20 at once)
        public const int SpliceBuffer = 64 * 1024;      // held per direction of a pair only while bytes are moving
        public const long SpliceIdleMs = 15 * 60_000;   // a pair with no bytes either way for 15 minutes is closed
        public const int SendTimeoutMs = 10_000;        // a frame the other side will not take in 10 s ends that line
        public const double UdpBytesPerSecond = 2 * 1024 * 1024; // per pairing, per direction
        public const double UdpBurstBytes = 256 * 1024;
        public const long RefusalNoteMs = 60_000;       // at most one log line about refusals per address per minute
        public const int Ipv6PrefixBits = 64;           // IPv6 addresses count by their /64; IPv4 by the whole address
        // A host whose control line just dropped is usually back within a second or two (a Wi-Fi blip, a router
        // hiccup): a controlling PC asking for it in that gap waits for it instead of being told it is not there.
        public const long HostGoneGraceMs = 15_000;     // how long after its line dropped a host counts as "coming back"
        public const int HostReturnWaitMs = 8_000;      // how long a connect waits for such a host to register again
        public const long RecentIdMs = 60_000;          // a late or repeated dial-back for a pairing that just ended is not a bad request
        public const int UdpReceiveBuffer = 4 << 20;    // kernel room for bursts of sound from many pairs at once
    }

    /// <summary>Counts and rows for the window and the tray tooltip.</summary>
    internal sealed record RelaySnapshot(bool Running, int Port, string? Problem, int Hosts, int ControllingPcs,
        int Connections, IReadOnlyList<HostRow> Rows);

    /// <summary>
    /// One host connected right now: the address its control line came from and when it registered. Never its name:
    /// Kova derives the name from the hosted PC's password, so it means nothing to a person and is not shown.
    /// </summary>
    internal sealed record HostRow(string Address, DateTime SinceUtc, int ControllingPcs);

    /// <summary>
    /// The relay itself: one TCP listener and one UDP socket on the same port, following RelayWire.cs exactly.
    /// There is no list of names: a name belongs to whichever host holds it right now (Kova derives it from the hosted
    /// PC's password, like NVDA Remote's key). A host with a different public key is refused the name only while the
    /// current holder is connected.
    /// Shared state is guarded by one lock (_gate); nothing slow (network, disk) happens while holding it. The two
    /// tables the UDP path reads (pairings and host UDP keys) are concurrent dictionaries, written under _gate but read
    /// without it, so sound never waits for the lock.
    /// Every TCP connection runs on async sockets (no thread per connection) inside its own try/catch, so one bad
    /// connection never affects another.
    /// </summary>
    internal sealed class RelayServer : IDisposable
    {
        private readonly RelayLog _log;
        private readonly object _gate = new();

        private readonly Dictionary<AddrKey, AddrState> _addrs = new();
        private readonly Dictionary<string, HostLine> _hosts = new(StringComparer.Ordinal);   // connected, by name key
        private readonly ConcurrentDictionary<ulong, HostLine> _hostsByUdpKey = new();
        private readonly Dictionary<ulong, Pending> _pending = new();
        private readonly ConcurrentDictionary<ulong, Pairing> _pairings = new();              // spliced, live
        private readonly Dictionary<ulong, (byte[] Token, long At)> _recentIds = new();       // pairings that ended or were claimed lately
        private readonly Dictionary<string, long> _gone = new(StringComparer.Ordinal);         // name key -> when its host's line dropped
        private readonly HashSet<Conn> _conns = new();
        private int _total;
        private readonly Dictionary<(string Name, string Client), int> _pcPairs = new();  // live pairs per host and client address
        private volatile bool _disposed;

        private Socket? _listener, _udp;
        private CancellationTokenSource? _run;

        public int Port { get; private set; }
        public bool Running { get; private set; }
        public string? Problem { get; private set; }
        public bool LoopbackOnly { get; set; }

        /// <summary>Exceptions the relay did not expect (not network or disk trouble). The self-test requires 0.</summary>
        public int UnexpectedErrors;

        public RelayServer(RelayLog log)
        {
            _log = log;
            _ = Task.Run(HousekeepingLoop);
        }

        // ---------------------------------------------------------------- start and stop

        /// <summary>Starts listening on the port. Returns null, or plain words saying why it could not.</summary>
        public string? Start(int port)
        {
            Stop(log: false);
            Socket? tcp = null, udp = null;
            try
            {
                IPAddress any = LoopbackOnly ? IPAddress.Loopback : IPAddress.IPv6Any;
                tcp = new Socket(any.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                udp = new Socket(any.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                if (!LoopbackOnly) { tcp.DualMode = true; udp.DualMode = true; }
                tcp.ExclusiveAddressUse = true;
                udp.ExclusiveAddressUse = true;
                tcp.Bind(new IPEndPoint(any, port));
                udp.Bind(new IPEndPoint(any, port));
                // The system's largest backlog: hundreds of hosts reconnecting at once after the relay restarts must
                // not be turned away by Windows before the relay even sees them.
                tcp.Listen();
                // Without this, Windows reports an ICMP "port unreachable" from some earlier send as an error on the
                // next receive, which would make one vanished PC disturb everyone's UDP.
                const int SIO_UDP_CONNRESET = -1744830452;
                try { udp.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch (Exception) { }
                try { udp.ReceiveBufferSize = Limits.UdpReceiveBuffer; } catch (Exception) { }
                try { udp.SendBufferSize = Limits.UdpReceiveBuffer; } catch (Exception) { }
            }
            catch (SocketException e)
            {
                tcp?.Dispose(); udp?.Dispose();
                string why = e.SocketErrorCode switch
                {
                    SocketError.AddressAlreadyInUse => $"Port {port} is already used by another program. Choose another port.",
                    SocketError.AccessDenied => $"Windows would not let the relay use port {port}. Another program may have it, or Windows may keep it reserved. Choose another port.",
                    _ => $"The relay could not use port {port}: {e.Message} Choose another port.",
                };
                lock (_gate) { Problem = why; Port = port; Running = false; }
                _log.Write($"Could not start on port {port}: {why}");
                return why;
            }
            catch (Exception e)
            {
                tcp?.Dispose(); udp?.Dispose();
                string why = $"The relay could not use port {port}: {e.Message}";
                lock (_gate) { Problem = why; Port = port; Running = false; }
                _log.Write($"Could not start on port {port}: {why}");
                return why;
            }
            var run = new CancellationTokenSource();
            lock (_gate)
            {
                _listener = tcp; _udp = udp; _run = run;
                Port = port; Running = true; Problem = null;
            }
            // The relay only moves bytes between other people's PCs: a little above normal keeps it ahead of whatever
            // else runs on the same PC (a build, a backup), which would otherwise show up as lag at both ends.
            try { using var me = Process.GetCurrentProcess(); if (me.PriorityClass == ProcessPriorityClass.Normal) me.PriorityClass = ProcessPriorityClass.AboveNormal; }
            catch (Exception) { }
            _log.Write($"Started on port {port}" + (LoopbackOnly ? " (this PC only)." : "."));
            // Two accepts outstanding at once, so a burst of connections is taken in as fast as Windows hands them over.
            _ = Task.Run(() => AcceptLoop(tcp, run.Token));
            _ = Task.Run(() => AcceptLoop(tcp, run.Token));
            // UDP on its own thread with blocking calls: no allocation per packet, and never stuck behind busy pool threads.
            new Thread(() => UdpLoop(udp, run.Token)) { IsBackground = true, Name = "Kova Relay UDP", Priority = ThreadPriority.AboveNormal }.Start();
            return null;
        }

        public void Stop() => Stop(log: true);

        private void Stop(bool log)
        {
            Socket? tcp, udp; CancellationTokenSource? run; List<Conn> conns; List<HostLine> hosts; List<Pending> pending;
            bool was;
            lock (_gate)
            {
                was = Running;
                tcp = _listener; udp = _udp; run = _run;
                _listener = null; _udp = null; _run = null;
                Running = false;
                conns = _conns.ToList();
                hosts = _hosts.Values.ToList();
                pending = _pending.Values.ToList();
                foreach (Pending p in pending) RemovePendingLocked(p);
                foreach (HostLine h in hosts) RemoveHostLocked(h);
                _gone.Clear();
            }
            if (run is null) return;
            try { run.Cancel(); } catch (Exception) { }
            tcp?.Dispose();
            udp?.Dispose();
            foreach (Pending p in pending) p.Done.TrySetResult(null);
            foreach (HostLine h in hosts) h.Close();
            foreach (Conn c in conns) c.Close();
            if (was && log) _log.Write($"Stopped (port {Port}).");
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
        }

        // ---------------------------------------------------------------- status

        public RelaySnapshot Snapshot()
        {
            lock (_gate)
            {
                // Controlling PCs = distinct client addresses (the whole address as seen) among live pairs: one PC
                // opens several paired connections (main, file lanes, screen), and this counts it once.
                var clientsByHost = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                var allClients = new HashSet<string>(StringComparer.Ordinal);
                foreach (Pairing p in _pairings.Values)
                {
                    allClients.Add(p.ClientText);
                    if (!clientsByHost.TryGetValue(p.NameKey, out var set)) clientsByHost[p.NameKey] = set = new(StringComparer.Ordinal);
                    set.Add(p.ClientText);
                }
                var rows = new List<HostRow>(_hosts.Count);
                foreach (HostLine h in _hosts.Values.OrderBy(h => h.SinceUtc))
                {
                    int pcs = clientsByHost.TryGetValue(h.NameKey, out var s) ? s.Count : 0;
                    rows.Add(new HostRow(h.Conn.Text, h.SinceUtc, pcs));
                }
                return new RelaySnapshot(Running, Port, Problem, _hosts.Count, allClients.Count, _total, rows);
            }
        }

        // ---------------------------------------------------------------- TCP

        private async Task AcceptLoop(Socket listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Socket s;
                try { s = await listener.AcceptAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { continue; }   // a connection reset before it was accepted
                catch (Exception e) { Unexpected("accepting", e); continue; }
                _ = Task.Run(() => HandleConnection(s, ct));
            }
        }

        /// <summary>
        /// No Nagle delay (every keystroke and sound packet goes at once), and TCP keep-alive so a PC that vanished
        /// without closing (sleep, a dropped router) is noticed by Windows even when nothing is being sent.
        /// </summary>
        internal static void Tune(Socket s, int idleSeconds, int intervalSeconds, int probes)
        {
            try { s.NoDelay = true; } catch (Exception) { }
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, idleSeconds);
                s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, intervalSeconds);
                s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, probes);
            }
            catch (Exception) { }   // older Windows: the plain keep-alive above still applies
        }

        private enum Admit { Yes, Blocked, Busy }

        private async Task HandleConnection(Socket s, CancellationToken run)
        {
            Conn? c = null;
            bool handedOff = false;
            try
            {
                if (s.RemoteEndPoint is not IPEndPoint ep) { s.Dispose(); return; }
                IPAddress ip = Plain(ep.Address);
                AddrKey key = AddrKey.Of(ip);
                Admit verdict;
                AddrState st;
                string? busyWhy = null;
                lock (_gate)
                {
                    long now = Environment.TickCount64;
                    st = GetAddrLocked(key, ip, now);
                    if (st.BlockedUntil > now) { verdict = Admit.Blocked; NoteRefusalLocked(st, "it is blocked for sending bad requests", now); }
                    else
                    {
                        int hostRoom = st.HostLines == 0 ? 1
                            : Math.Min(Limits.HostBaseFactor + (st.LivePairsAsHost + Limits.PairsPerExtraFactor - 1) / Limits.PairsPerExtraFactor, Limits.MaxHostRoomFactor);
                        st.Refill(now, hostRoom);
                        if (st.Tokens < 1) busyWhy = "it opened connections too quickly";
                        else if (_total >= Limits.MaxConnections) busyWhy = "the relay has as many connections as it takes";
                        else if (st.Active >= Limits.PerAddressConnections * hostRoom) busyWhy = "it has too many connections open";
                        else if (st.Unpaired >= Limits.PerAddressUnpaired * hostRoom) busyWhy = "it has too many connections waiting";
                        if (busyWhy is null)
                        {
                            st.Tokens -= 1;
                            c = new Conn(s, st, ip);
                            _conns.Add(c);
                            _total++; st.Active++; st.Unpaired++;
                            verdict = Admit.Yes;
                        }
                        else { verdict = Admit.Busy; NoteRefusalLocked(st, busyWhy, now); }
                    }
                }
                if (verdict == Admit.Blocked) { s.Dispose(); return; }
                if (verdict == Admit.Busy)
                {
                    // Say Busy without reading anything, so a flood costs one small send. Never wait on it.
                    try
                    {
                        s.Blocking = false;
                        s.Send(BusyBytes, SocketFlags.None, out _);
                    }
                    catch (Exception) { }
                    s.Dispose();
                    return;
                }

                // Every connection may become a splice or a host line: both want no delay and a dead peer noticed.
                Tune(s, idleSeconds: 30, intervalSeconds: 5, probes: 6);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(run);
                deadline.CancelAfter(Wire.RequestDeadlineMs);
                byte[] helloBytes = NewHello(out byte[] challenge);
                byte type; byte[] body;
                try
                {
                    await SendAll(s, helloBytes, deadline.Token).ConfigureAwait(false);
                    byte[] head = new byte[3];
                    if (!await ReceiveExactly(s, head, deadline.Token).ConfigureAwait(false)) { Fail(c!, "closed before asking anything"); return; }
                    int len = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1));
                    if (len > Wire.MaxBody)
                    {
                        // Refused before reading a byte of it.
                        await Refuse(c!, Wire.Reason.BadRequest, "it sent a request that was too big", deadline.Token, countFailure: true).ConfigureAwait(false);
                        return;
                    }
                    type = head[0];
                    body = new byte[len];
                    if (!await ReceiveExactly(s, body, deadline.Token).ConfigureAwait(false)) { Fail(c!, "closed in the middle of a request"); return; }
                }
                catch (OperationCanceledException) when (!run.IsCancellationRequested)
                {
                    Fail(c!, "did not finish its request within 10 seconds");
                    return;
                }

                switch (type)
                {
                    case Wire.Register:
                        await HandleRegister(c!, challenge, body, run).ConfigureAwait(false);
                        break;
                    case Wire.Connect:
                        handedOff = await HandleConnect(c!, body, run).ConfigureAwait(false);
                        break;
                    case Wire.Accept:
                        handedOff = await HandleAccept(c!, body, run).ConfigureAwait(false);
                        break;
                    case Wire.Ping:
                        await HandlePing(c!, body, run).ConfigureAwait(false);
                        break;
                    default:
                        await Refuse(c!, Wire.Reason.BadRequest, "it sent a request the relay does not know", run, countFailure: true).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception e) when (Expected(e)) { }
            catch (Exception e) { Unexpected("serving a connection", e); }
            finally
            {
                if (c is null) { if (!handedOff) try { s.Dispose(); } catch (Exception) { } }
                else if (!handedOff) Release(c);
            }
        }

        // ---------------------------------------------------------------- Register

        private async Task HandleRegister(Conn c, byte[] challenge, byte[] body, CancellationToken run)
        {
            string? name = Wire.ReadName(body, out int used);
            if (name is null || body.Length < used + 2)
            { await Refuse(c, Wire.Reason.BadRequest, "it sent a badly formed host registration", run, true).ConfigureAwait(false); return; }
            int keyLen = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(used));
            int keyAt = used + 2;
            if (keyLen < 1 || keyLen > 200 || body.Length < keyAt + keyLen)
            { await Refuse(c, Wire.Reason.BadRequest, "it sent a badly formed host registration", run, true).ConfigureAwait(false); return; }
            byte[] spki = body.AsSpan(keyAt, keyLen).ToArray();
            byte[] sig = body.AsSpan(keyAt + keyLen).ToArray();

            bool goodKey = false, goodSig = false;
            try
            {
                using ECDsa ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(spki, out int read);
                if (read == spki.Length && ec.KeySize == 256 && IsP256(ec))
                {
                    goodKey = true;
                    goodSig = sig.Length == 64 && ec.VerifyData(Wire.RegisterSignedBytes(challenge, name), sig,
                        HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                }
            }
            catch (CryptographicException) { }
            // The name is never written to the log: Kova derives it from the hosted PC's password.
            if (!goodKey) { await Refuse(c, Wire.Reason.BadRequest, "it sent a host key the relay does not accept", run, true).ConfigureAwait(false); return; }
            if (!goodSig) { await Refuse(c, Wire.Reason.BadSignature, "it sent a host signature that did not verify", run, true).ConfigureAwait(false); return; }

            byte[] keyHash = SHA256.HashData(spki);
            string nameKey = Wire.NameKey(name);
            byte[] udpKeyBytes = new byte[Wire.IdBytes];
            bool taken = false, cameBack = false, tooMany = false;
            HostLine? old = null, line = null;
            var moved = new List<Pending>();
            lock (_gate)
            {
                // Nobody holds the name now: this host takes it. The same key: the host reconnected, and its new line
                // replaces the old one. A different key while the holder is connected: refused. Once the holder goes
                // away, any host may take the name.
                if (_hosts.TryGetValue(nameKey, out old) && !CryptographicOperations.FixedTimeEquals(old.KeyHash, keyHash))
                {
                    taken = true;
                    old = null;
                }
                else if (c.Addr.HostLines >= Limits.MaxHostsPerAddress && (old is null || old.Conn.Addr != c.Addr))
                {
                    tooMany = true;
                    old = null;
                }
                else
                {
                    ulong udpKey;
                    do { RandomNumberGenerator.Fill(udpKeyBytes); udpKey = BinaryPrimitives.ReadUInt64BigEndian(udpKeyBytes); }
                    while (udpKey == 0 || _hostsByUdpKey.ContainsKey(udpKey));
                    if (old is not null) RemoveHostLocked(old);
                    line = new HostLine(c, nameKey, keyHash, udpKey);
                    _hosts[nameKey] = line;
                    _hostsByUdpKey[udpKey] = line;
                    c.Addr.HostLines++;
                    MarkPairedLocked(c);
                    cameBack = _gone.Remove(nameKey);
                    // Same name, same key: the same host on a new line. Connections still waiting for its dial-back are
                    // offered again on the new line (they would otherwise fail after their 10 s), and live pairs send
                    // their sound to it; until its UDP announcement arrives, to where its old line said.
                    foreach (Pending q in _pending.Values)
                    {
                        if (q.Host == line || q.Host.NameKey != nameKey || !CryptographicOperations.FixedTimeEquals(q.Host.KeyHash, keyHash)) continue;
                        q.Host.Pending--;
                        q.Host = line;
                        line.Pending++;
                        moved.Add(q);
                    }
                    foreach (Pairing pr in _pairings.Values)
                    {
                        HostLine was = pr.Host;
                        if (was == line || pr.NameKey != nameKey || !CryptographicOperations.FixedTimeEquals(was.KeyHash, keyHash)) continue;
                        if (was.Udp is SocketAddress u) pr.HostUdpBefore = u;
                        pr.Host = line;
                    }
                }
            }
            if (tooMany)
            {
                await Refuse(c, Wire.Reason.Busy, $"it already has {Limits.MaxHostsPerAddress} hosts registered", run, countFailure: false).ConfigureAwait(false);
                return;
            }
            if (taken || line is null)
            {
                await Refuse(c, Wire.Reason.NameTaken, "another PC is already hosting with the same password", run, countFailure: false).ConfigureAwait(false);
                return;
            }

            if (old is not null) { old.Replaced = true; _ = TellReplacedAndClose(old); }
            _log.Write(old is not null
                ? $"A host connected again from {c.Text}. Its previous connection from {old.Conn.Text} was closed."
                : cameBack ? $"A host connected again from {c.Text}." : $"A host connected from {c.Text}.");
            try
            {
                // The line's send lock starts held, so no Incoming can go out before this Ok.
                using (var t = Timeout(run)) await SendFrame(c.S, Wire.Ok, udpKeyBytes, t.Token).ConfigureAwait(false);
                // A control line carries little but keep-alives: tighter TCP keep-alive than a splice gets.
                Tune(c.S, idleSeconds: 15, intervalSeconds: 3, probes: 5);
                line.SendLock.Release();
                foreach (Pending q in moved)
                    if (!await line.Send(Wire.Incoming, IncomingBody(q), run).ConfigureAwait(false)) { line.Close(); break; }
                await RunHostLine(line, run).ConfigureAwait(false);
            }
            finally
            {
                bool wasCurrent;
                lock (_gate)
                {
                    wasCurrent = _hosts.TryGetValue(nameKey, out var cur) && cur == line;
                    if (wasCurrent)
                    {
                        RemoveHostLocked(line);
                        // Remembered briefly, so a connect in the gap before it registers again waits for it. Waiting
                        // pairings stay too: they are offered again if it comes back, or time out as before.
                        if (Running) _gone[nameKey] = Environment.TickCount64;
                    }
                }
                line.Close();
                if (!line.Replaced) _log.Write($"The host at {c.Text} disconnected.");
            }
        }

        /// <summary>Tells a replaced control line why it is being closed (a 4.7 host then waits before trying again), then closes it.</summary>
        private static async Task TellReplacedAndClose(HostLine old)
        {
            try { await old.Send(Wire.Refused, new[] { (byte)Wire.Reason.Replaced }, CancellationToken.None, 2000).ConfigureAwait(false); }
            catch (Exception) { }
            old.Close(true);
        }

        private static bool IsP256(ECDsa ec)
        {
            ECCurve curve = ec.ExportParameters(false).Curve;
            if (!curve.IsNamed) return false;
            string? oid = curve.Oid.Value, friendly = curve.Oid.FriendlyName;
            return oid == "1.2.840.10045.3.1.7" || friendly is "nistP256" or "ECDSA_P256" or "secp256r1";
        }

        /// <summary>
        /// The host's control line: anything the host sends counts as a sign of life; a 4.7 host's probe is answered at
        /// once. The watch loop sends keep-alives and drops a line that went silent (ProbedDeadAfterMs for a host that
        /// probes, SilentDeadAfterMs for a 4.6 one).
        /// </summary>
        private async Task RunHostLine(HostLine line, CancellationToken run)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(run, line.Closed.Token);
            Task watch = WatchHostLine(line, stop.Token);
            try
            {
                byte[] head = new byte[3];
                byte[] body = new byte[Wire.MaxBody];
                while (!stop.IsCancellationRequested)
                {
                    if (!await ReceiveExactly(line.Conn.S, head, stop.Token).ConfigureAwait(false)) return;
                    int len = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1));
                    if (len > Wire.MaxBody) { Fail(line.Conn, "sent a malformed frame on its host line"); return; }
                    if (len > 0 && !await ReceiveExactly(line.Conn.S, body.AsMemory(0, len), stop.Token).ConfigureAwait(false)) return;
                    Volatile.Write(ref line.LastHeard, Environment.TickCount64);
                    if (head[0] == Wire.KeepAlive && len >= 1 && body[0] == Wire.ProbeAsk)
                    {
                        line.Probes = true;
                        _ = AnswerProbe(line, run);
                    }
                    // Otherwise a KeepAlive, or a frame type this relay does not know (a newer Kova): either way the line is alive.
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                stop.Cancel();
                try { await watch.ConfigureAwait(false); } catch (Exception) { }
            }
        }

        private static readonly byte[] ProbeAnswerBody = { Wire.ProbeAnswer };

        private static async Task AnswerProbe(HostLine line, CancellationToken run)
        {
            try { if (!await line.Send(Wire.KeepAlive, ProbeAnswerBody, run).ConfigureAwait(false)) line.Close(); }
            catch (Exception) { line.Close(); }
        }

        private async Task WatchHostLine(HostLine line, CancellationToken ct)
        {
            try
            {
                long lastSent = Environment.TickCount64;
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(2500, ct).ConfigureAwait(false);
                    long now = Environment.TickCount64;
                    long quiet = now - Volatile.Read(ref line.LastHeard);
                    if (quiet > (line.Probes ? Wire.ProbedDeadAfterMs : Wire.SilentDeadAfterMs))
                    {
                        _log.Write($"The host at {line.Conn.Text} stopped answering for {quiet / 1000} seconds; its connection was closed.");
                        line.Close();
                        return;
                    }
                    if (now - lastSent < Wire.RelayKeepAliveMs) continue;
                    lastSent = now;
                    if (!await line.Send(Wire.KeepAlive, Array.Empty<byte>(), ct).ConfigureAwait(false)) { line.Close(); return; }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (Expected(e)) { line.Close(); }
        }

        // ---------------------------------------------------------------- Connect

        private static byte[] IncomingBody(Pending p)
        {
            byte[] text = Encoding.ASCII.GetBytes(p.Client.Text);
            byte[] incoming = new byte[Wire.IdBytes + Wire.TokenBytes + 1 + text.Length];
            p.IdBytes.CopyTo(incoming, 0);
            p.Token.CopyTo(incoming, Wire.IdBytes);
            incoming[Wire.IdBytes + Wire.TokenBytes] = (byte)text.Length;
            text.CopyTo(incoming, Wire.IdBytes + Wire.TokenBytes + 1);
            return incoming;
        }

        /// <returns>true if the connection's life was handed to the splice (it releases it).</returns>
        private async Task<bool> HandleConnect(Conn c, byte[] body, CancellationToken run)
        {
            string? name = Wire.ReadName(body, out int used);
            if (name is null || used != body.Length)
            { await Refuse(c, Wire.Reason.BadRequest, "it sent a badly formed connect request", run, true).ConfigureAwait(false); return false; }
            string nameKey = Wire.NameKey(name);
            Pending? p = null;
            HostLine? target = null;
            Wire.Reason? no = null;
            string why = "";
            long waitUntil = 0;
            while (true)
            {
                bool wait = false;
                lock (_gate)
                {
                    long now = Environment.TickCount64;
                    if (!MayAskLocked(c.Addr, nameKey)) { no = Wire.Reason.Busy; why = "it asked for too many different hosts that were not connected this hour"; }
                    else if (!_hosts.TryGetValue(nameKey, out HostLine? host))
                    {
                        // Its host dropped its line moments ago and is most likely registering again right now.
                        if (_gone.TryGetValue(nameKey, out long goneAt) && now - goneAt < Limits.HostGoneGraceMs && (waitUntil == 0 || now < waitUntil))
                        {
                            if (waitUntil == 0) waitUntil = now + Limits.HostReturnWaitMs;
                            wait = true;
                        }
                        else { no = Wire.Reason.NoSuchHost; why = "it asked for a host that is not connected (no host is using that password)"; MissLocked(c.Addr, nameKey); }
                    }
                    else if (host.Pending >= Limits.PendingPerHost) { no = Wire.Reason.Busy; why = $"it asked for the host at {host.Conn.Text}, which has too many connections waiting"; }
                    else if (c.Addr.PendingAsClient >= Limits.PendingPerClient) { no = Wire.Reason.Busy; why = "it has too many connection requests waiting"; }
                    else
                    {
                        byte[] idBytes = new byte[Wire.IdBytes];
                        ulong id;
                        do { RandomNumberGenerator.Fill(idBytes); id = BinaryPrimitives.ReadUInt64BigEndian(idBytes); }
                        while (id == 0 || _pending.ContainsKey(id) || _pairings.ContainsKey(id) || _recentIds.ContainsKey(id) || _hostsByUdpKey.ContainsKey(id));
                        p = new Pending(id, idBytes, RandomNumberGenerator.GetBytes(Wire.TokenBytes), host, c, now);
                        _pending[id] = p;
                        host.Pending++;
                        c.Addr.PendingAsClient++;
                        target = host;
                    }
                }
                if (!wait) break;
                await Task.Delay(100, run).ConfigureAwait(false);
            }
            if (p is null || target is null) { await Refuse(c, no!.Value, why, run, countFailure: false).ConfigureAwait(false); return false; }

            // If this send fails, the line is closed; should the host register again in time, the offer goes out again on its new line.
            if (!await target.Send(Wire.Incoming, IncomingBody(p), run).ConfigureAwait(false)) target.Close();

            Conn? hostSide = null;
            Task<Conn?> done = p.Done.Task;
            if (await Task.WhenAny(done, Task.Delay(Wire.PairWaitMs, run)).ConfigureAwait(false) == done) hostSide = done.Result;
            else
            {
                bool claimed;
                lock (_gate) claimed = _pending.ContainsKey(p.Id) && RemovePendingLocked(p);
                // If an Accept claimed it at the last moment, it hands its connection over right away.
                if (!claimed) hostSide = await done.ConfigureAwait(false);
            }
            if (hostSide is null)
            {
                await Refuse(c, Wire.Reason.HostDidNotAnswer, $"the host at {p.Host.Conn.Text} did not answer in time", run, countFailure: false).ConfigureAwait(false);
                return false;
            }
            await Splice(c, hostSide, p, run).ConfigureAwait(false);
            return true;
        }

        // ---------------------------------------------------------------- Ping

        /// <summary>Kova checking the relay is up, and maybe whether a host is connected. Not a bad request, so never counted against the address.</summary>
        private async Task HandlePing(Conn c, byte[] body, CancellationToken run)
        {
            Wire.PingAnswer answer = Wire.PingAnswer.NoName;
            if (body.Length > 0)
            {
                string? name = Wire.ReadName(body, out int used);
                if (name is null || used != body.Length)
                { await Refuse(c, Wire.Reason.BadRequest, "it sent a badly formed check", run, true).ConfigureAwait(false); return; }
                bool spent;
                lock (_gate)
                {
                    string key = Wire.NameKey(name);
                    spent = !MayAskLocked(c.Addr, key);
                    if (!spent)
                    {
                        answer = _hosts.ContainsKey(key) ? Wire.PingAnswer.HostHere : Wire.PingAnswer.HostAbsent;
                        if (answer == Wire.PingAnswer.HostAbsent) MissLocked(c.Addr, key);
                    }
                }
                // Out of lookups: no answer about any host at all, so the relay cannot be used to test passwords.
                if (spent) { await Refuse(c, Wire.Reason.Busy, "it asked about too many hosts that were not connected this hour", run, false).ConfigureAwait(false); return; }
            }
            try
            {
                using var t = Timeout(run);
                await SendFrame(c.S, Wire.Ok, new[] { (byte)answer }, t.Token).ConfigureAwait(false);
                c.S.Shutdown(SocketShutdown.Send);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Every host's name is made from its password, so asking for a host that is not there is, in effect, a wrong
        /// password. An address may ask after Limits.MissesPerHour DIFFERENT absent hosts an hour (connects and checks
        /// together); then it gets no answer about any host it has not already asked about, until the hour is up. Asking
        /// again for the same one is never counted: a controlling PC reconnecting every second while its host restarts
        /// asks for the same host each time (4.6.0 counted each try, and locked the owner out for an hour). Under _gate.
        /// </summary>
        private static bool MayAskLocked(AddrState a, string nameKey)
        {
            long now = Environment.TickCount64;
            if (now - a.MissSince > Limits.MissWindowMs) { a.MissSince = now; a.MissNames?.Clear(); }
            if (a.MissNames != null && a.MissNames.Contains(nameKey)) return true;
            return (a.MissNames?.Count ?? 0) < Limits.MissesPerHour;
        }

        private static void MissLocked(AddrState a, string nameKey) => (a.MissNames ??= new HashSet<string>(StringComparer.Ordinal)).Add(nameKey);

        // ---------------------------------------------------------------- Accept

        /// <returns>true if the connection was handed to the waiting Connect.</returns>
        private async Task<bool> HandleAccept(Conn c, byte[] body, CancellationToken run)
        {
            if (body.Length != Wire.IdBytes + Wire.TokenBytes)
            { await Refuse(c, Wire.Reason.BadRequest, "it sent a badly formed accept", run, true).ConfigureAwait(false); return false; }
            ulong id = BinaryPrimitives.ReadUInt64BigEndian(body);
            byte[] token = body.AsSpan(Wire.IdBytes, Wire.TokenBytes).ToArray();
            Pending? claimed = null;
            bool late = false;
            lock (_gate)
            {
                if (_pending.TryGetValue(id, out Pending? p)
                    && CryptographicOperations.FixedTimeEquals(p.Token, token)
                    && Environment.TickCount64 - p.Created <= Wire.PairWaitMs
                    && RemovePendingLocked(p))
                    claimed = p;
                // A dial-back with the right token for a pairing that was just claimed or timed out: the host was slow,
                // or was offered it twice (once on a line that dropped, again on its new one). Not a bad request.
                else if (_recentIds.TryGetValue(id, out var r) && CryptographicOperations.FixedTimeEquals(r.Token, token))
                    late = true;
            }
            if (claimed is null)
            { await Refuse(c, Wire.Reason.BadRequest, "it sent an accept that matched no waiting connection", run, countFailure: !late).ConfigureAwait(false); return false; }
            if (!claimed.Done.TrySetResult(c)) return false;
            return true;
        }

        // ---------------------------------------------------------------- splice

        private async Task Splice(Conn client, Conn host, Pending p, CancellationToken run)
        {
            Pairing pair;
            bool first;
            lock (_gate)
            {
                pair = new Pairing(p.Id, p.Host, p.Host.Conn.Text, client.Text);
                MarkPairedLocked(client);
                MarkPairedLocked(host);
                host.Addr.LivePairsAsHost++;
                var k = (pair.NameKey, pair.ClientText);
                _pcPairs.TryGetValue(k, out int had);
                _pcPairs[k] = had + 1;
                first = had == 0;
                _pairings[pair.Id] = pair;
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(run);
            pair.Cts = cts;
            try
            {
                if (first) _log.Write($"A controlling PC at {client.Text} connected to the host at {pair.HostText}.");
                using (var t = Timeout(run))
                {
                    // The client's Ok goes first: nothing the host sends can reach the client before it.
                    await SendFrame(client.S, Wire.Ok, p.IdBytes, t.Token).ConfigureAwait(false);
                    await SendFrame(host.S, Wire.Ok, ReadOnlyMemory<byte>.Empty, t.Token).ConfigureAwait(false);
                }
                Task a = Pump(client.S, host.S, pair, cts.Token);
                Task b = Pump(host.S, client.S, pair, cts.Token);
                await Task.WhenAny(a, b).ConfigureAwait(false);
                cts.Cancel();
                client.Close();
                host.Close();
                try { await Task.WhenAll(a, b).ConfigureAwait(false); } catch (Exception) { }
            }
            catch (Exception e) when (Expected(e)) { }
            catch (Exception e) { Unexpected("relaying a pair", e); }
            finally
            {
                bool last;
                lock (_gate)
                {
                    _pairings.TryRemove(pair.Id, out _);
                    host.Addr.LivePairsAsHost--;
                    var k = (pair.NameKey, pair.ClientText);
                    int left = _pcPairs.TryGetValue(k, out int had) ? had - 1 : 0;
                    if (left <= 0) _pcPairs.Remove(k); else _pcPairs[k] = left;
                    last = left <= 0;
                }
                Release(client);
                Release(host);
                if (last) _log.Write($"The controlling PC at {client.Text} disconnected from the host at {pair.HostText}.");
            }
        }

        /// <summary>
        /// Copies one direction. While nothing arrives it waits with a zero-byte read, holding no buffer; when bytes
        /// arrive it borrows one, copies everything that is there, and gives it back as soon as the line goes quiet. So
        /// thousands of idle lanes cost no memory, and a busy one copies 64 KB at a time with no allocation. No queue
        /// anywhere: a slow receiver slows its sender (TCP's own flow control), it never piles up here.
        /// </summary>
        private static async Task Pump(Socket from, Socket to, Pairing pair, CancellationToken ct)
        {
            byte[]? buf = null;
            try
            {
                while (true)
                {
                    if (buf is null)
                    {
                        await from.ReceiveAsync(Memory<byte>.Empty, SocketFlags.None, ct).ConfigureAwait(false);
                        buf = ArrayPool<byte>.Shared.Rent(Limits.SpliceBuffer);
                    }
                    int n = await from.ReceiveAsync(buf.AsMemory(0, Limits.SpliceBuffer), SocketFlags.None, ct).ConfigureAwait(false);
                    if (n <= 0) return;
                    await SendAll(to, buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    Volatile.Write(ref pair.LastActivity, Environment.TickCount64);
                    if (from.Available == 0)
                    {
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = null;
                    }
                }
            }
            catch (Exception) { }   // either side closing or failing ends the pair; the caller closes both
            finally { if (buf is not null) ArrayPool<byte>.Shared.Return(buf); }
        }

        // ---------------------------------------------------------------- UDP

        /// <summary>
        /// One thread, blocking calls, one buffer, no locks and no allocation per packet: each packet is read, its
        /// pairing looked up in a concurrent table, and sent straight on. Addresses are compared and kept as Windows'
        /// own SocketAddress bytes, so nothing is converted per packet either.
        /// </summary>
        private void UdpLoop(Socket udp, CancellationToken ct)
        {
            byte[] buf = GC.AllocateUninitializedArray<byte>(65536, pinned: true);
            var from = new SocketAddress(udp.AddressFamily);
            int fullSize = from.Size;
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    from.Size = fullSize;
                    n = udp.ReceiveFrom(buf, SocketFlags.None, from);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (ct.IsCancellationRequested) return; continue; }
                catch (Exception e) { if (ct.IsCancellationRequested) return; Unexpected("receiving UDP", e); continue; }
                try
                {
                    if (n < Wire.UdpHead || n > Wire.UdpHead + Wire.MaxUdpPayload || buf[0] != Wire.Udp0 || buf[1] != Wire.Udp1) continue;
                    ulong id = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(2));
                    if (n == Wire.UdpHead && _hostsByUdpKey.TryGetValue(id, out HostLine? h))
                    {
                        // The host saying where its UDP is. Copied only when it changed.
                        if (h.Udp is not SocketAddress was || !was.Equals(from)) h.Udp = Copy(from);
                        continue;
                    }
                    if (!_pairings.TryGetValue(id, out Pairing? p)) continue;
                    // The pair's host: the line it was paired on, or the newer line the same host registered since.
                    SocketAddress? hostUdp = p.Host.Udp ?? p.HostUdpBefore;
                    SocketAddress? dest = null;
                    long now = Environment.TickCount64;
                    if (hostUdp is not null && hostUdp.Equals(from))
                    {
                        if (p.ClientUdp is SocketAddress cu && p.ToClient.Take(n, now)) dest = cu;
                    }
                    else
                    {
                        // The latest address the controlling PC sent this id from.
                        if (p.ClientUdp is not SocketAddress cu || !cu.Equals(from)) p.ClientUdp = Copy(from);
                        if (hostUdp is not null && p.ToHost.Take(n, now)) dest = hostUdp;
                    }
                    if (dest is not null) udp.SendTo(buf.AsSpan(0, n), SocketFlags.None, dest);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { }
                catch (Exception e) { Unexpected("relaying UDP", e); }
            }
        }

        private static SocketAddress Copy(SocketAddress a)
        {
            var c = new SocketAddress(a.Family, a.Size);
            a.Buffer.Span[..a.Size].CopyTo(c.Buffer.Span);
            return c;
        }

        // ---------------------------------------------------------------- housekeeping

        private async Task HousekeepingLoop()
        {
            int tick = 0;
            while (!_disposed)
            {
                try { await Task.Delay(1000).ConfigureAwait(false); } catch (Exception) { }
                // Every 5 seconds is plenty (idle pairs are closed after 15 minutes, notes once a minute), and it keeps
                // this walk over tens of thousands of entries out of the lock most of the time.
                if (++tick % 5 != 0) continue;
                try
                {
                    var notes = new List<string>();
                    var idle = new List<Pairing>();
                    long now = Environment.TickCount64;
                    foreach (Pairing p in _pairings.Values)
                        if (now - Volatile.Read(ref p.LastActivity) > Limits.SpliceIdleMs) idle.Add(p);
                    lock (_gate)
                    {
                        List<AddrKey>? forget = null;
                        foreach (var kv in _addrs)
                        {
                            AddrState a = kv.Value;
                            if (a.Unnoted > 0 && now - a.LastNoteAt >= Limits.RefusalNoteMs)
                            {
                                notes.Add(a.Unnoted == 1
                                    ? $"Refused 1 more connection from {a.Text} in the last minute ({a.UnnotedWhy})."
                                    : $"Refused {a.Unnoted} more connections from {a.Text} in the last minute (the latest because {a.UnnotedWhy}).");
                                a.Unnoted = 0;
                                a.LastNoteAt = now;
                            }
                            if (a.Active == 0 && a.PendingAsClient == 0 && a.Unnoted == 0 && a.BlockedUntil < now
                                && now - a.LastUsed > Limits.ForgetAddressAfterMs)
                                (forget ??= new()).Add(kv.Key);
                        }
                        if (forget is not null) foreach (AddrKey k in forget) _addrs.Remove(k);
                        List<ulong>? oldIds = null;
                        foreach (var kv in _recentIds) if (now - kv.Value.At > Limits.RecentIdMs) (oldIds ??= new()).Add(kv.Key);
                        if (oldIds is not null) foreach (ulong id in oldIds) _recentIds.Remove(id);
                        List<string>? back = null;
                        foreach (var kv in _gone) if (now - kv.Value > Limits.HostGoneGraceMs) (back ??= new()).Add(kv.Key);
                        if (back is not null) foreach (string k in back) _gone.Remove(k);
                    }
                    foreach (Pairing p in idle)
                    {
                        _log.Write($"Closed an idle connection between {p.ClientText} and the host at {p.HostText} after 15 minutes without traffic.");
                        try { p.Cts?.Cancel(); } catch (Exception) { }
                    }
                    foreach (string n in notes) _log.Write(n);
                }
                catch (Exception e) { Unexpected("housekeeping", e); }
            }
        }

        // ---------------------------------------------------------------- per-address bookkeeping (call holding _gate)

        private AddrState GetAddrLocked(AddrKey key, IPAddress ip, long now)
        {
            if (!_addrs.TryGetValue(key, out AddrState? st))
            {
                if (_addrs.Count >= Limits.MaxAddressEntries) EvictLocked();
                st = new AddrState(TextOf(ip), now);
                _addrs[key] = st;
            }
            st.LastUsed = now;
            return st;
        }

        /// <summary>Drops the longest-unused tenth of entries that have nothing open (so their counts cannot go wrong).</summary>
        private void EvictLocked()
        {
            var victims = _addrs.Where(kv => kv.Value.Active == 0 && kv.Value.PendingAsClient == 0)
                .OrderBy(kv => kv.Value.LastUsed).Take(Limits.MaxAddressEntries / 10).Select(kv => kv.Key).ToList();
            foreach (AddrKey k in victims) _addrs.Remove(k);
        }

        private void NoteRefusalLocked(AddrState st, string why, long now)
        {
            if (now - st.LastNoteAt >= Limits.RefusalNoteMs && st.Unnoted == 0)
            {
                st.LastNoteAt = now;
                _log.Write($"Refused a connection from {st.Text} because {why}.");
            }
            else { st.Unnoted++; st.UnnotedWhy = why; }
        }

        private void MarkPairedLocked(Conn c)
        {
            if (!c.Unpaired) return;
            c.Unpaired = false;
            c.Addr.Unpaired--;
        }

        private bool RemovePendingLocked(Pending p)
        {
            if (!_pending.Remove(p.Id)) return false;
            p.Host.Pending--;
            p.Client.Addr.PendingAsClient--;
            _recentIds[p.Id] = (p.Token, Environment.TickCount64);
            return true;
        }

        private void RemoveHostLocked(HostLine h)
        {
            if (_hosts.TryGetValue(h.NameKey, out var cur) && cur == h) { _hosts.Remove(h.NameKey); h.Conn.Addr.HostLines--; }
            if (_hostsByUdpKey.TryGetValue(h.UdpKey, out var u) && u == h) _hostsByUdpKey.TryRemove(h.UdpKey, out _);
        }

        private void Release(Conn c)
        {
            lock (_gate)
            {
                if (c.Released) return;
                c.Released = true;
                _conns.Remove(c);
                _total--;
                c.Addr.Active--;
                if (c.Unpaired) { c.Unpaired = false; c.Addr.Unpaired--; }
            }
            c.Close();
        }

        private void Fail(Conn c, string why)
        {
            bool blockedNow = false;
            lock (_gate)
            {
                long now = Environment.TickCount64;
                AddrState a = c.Addr;
                if (now - a.FailSince > Limits.FailureWindowMs) { a.FailSince = now; a.Fails = 0; }
                a.Fails++;
                if (a.Fails >= Limits.FailuresToBlock && a.BlockedUntil < now)
                {
                    a.BlockedUntil = now + Limits.BlockMs;
                    a.Fails = 0;
                    blockedNow = true;
                }
                NoteRefusalLocked(a, why, now);
            }
            if (blockedNow) _log.Write($"Blocked {c.Text} for 10 minutes after {Limits.FailuresToBlock} bad requests within a minute.");
        }

        private async Task Refuse(Conn c, Wire.Reason reason, string why, CancellationToken run, bool countFailure)
        {
            if (countFailure) Fail(c, why);
            else lock (_gate) NoteRefusalLocked(c.Addr, why, Environment.TickCount64);
            try
            {
                using var t = Timeout(run);
                await SendFrame(c.S, Wire.Refused, new[] { (byte)reason }, t.Token).ConfigureAwait(false);
                // Let the refusal reach the other side before the close (a close with unread bytes can reset it away).
                c.S.Shutdown(SocketShutdown.Send);
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>A hello and a Busy refusal in one send, for connections turned away before anything is read.</summary>
        private static readonly byte[] BusyBytes = MakeBusy();

        private static byte[] MakeBusy()
        {
            byte[] b = new byte[Wire.ServerHelloBytes + 4];
            Wire.Magic.CopyTo(b, 0);
            RandomNumberGenerator.Fill(b.AsSpan(4, Wire.ChallengeBytes));   // never used: nothing is read after it
            b[Wire.ServerHelloBytes] = Wire.Refused;
            b[Wire.ServerHelloBytes + 2] = 1;
            b[Wire.ServerHelloBytes + 3] = (byte)Wire.Reason.Busy;
            return b;
        }

        private static byte[] NewHello(out byte[] challenge)
        {
            challenge = RandomNumberGenerator.GetBytes(Wire.ChallengeBytes);
            byte[] hello = new byte[Wire.ServerHelloBytes];
            Wire.Magic.CopyTo(hello, 0);
            challenge.CopyTo(hello, 4);
            return hello;
        }

        private static CancellationTokenSource Timeout(CancellationToken run, int ms = Limits.SendTimeoutMs)
        {
            var t = CancellationTokenSource.CreateLinkedTokenSource(run);
            t.CancelAfter(ms);
            return t;
        }

        internal static async Task SendFrame(Socket s, byte type, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            byte[] f = new byte[3 + body.Length];
            f[0] = type;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(1), (ushort)body.Length);
            body.CopyTo(f.AsMemory(3));
            await SendAll(s, f, ct).ConfigureAwait(false);
        }

        private static async Task SendAll(Socket s, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            while (data.Length > 0)
            {
                int n = await s.SendAsync(data, SocketFlags.None, ct).ConfigureAwait(false);
                if (n <= 0) throw new IOException("send failed");
                data = data[n..];
            }
        }

        /// <summary>false if the other side closed first.</summary>
        private static async Task<bool> ReceiveExactly(Socket s, Memory<byte> buf, CancellationToken ct)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int n = await s.ReceiveAsync(buf[got..], SocketFlags.None, ct).ConfigureAwait(false);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private static bool Expected(Exception e) =>
            e is SocketException or IOException or OperationCanceledException or ObjectDisposedException;

        private void Unexpected(string what, Exception e)
        {
            Interlocked.Increment(ref UnexpectedErrors);
            _log.Write($"An unexpected error occurred while {what}: {e.GetType().Name}: {e.Message}");
        }

        /// <summary>IPv4-mapped addresses as plain IPv4, IPv6 without a scope.</summary>
        internal static IPAddress Plain(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) return a.MapToIPv4();
            if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.ScopeId != 0) return new IPAddress(a.GetAddressBytes());
            return a;
        }

        internal static string TextOf(IPAddress a) => Plain(a).ToString();

        // ---------------------------------------------------------------- types

        /// <summary>The per-address key: IPv4 by the whole address, IPv6 by its /64.</summary>
        private readonly record struct AddrKey(ulong Hi, ulong Lo)
        {
            public static AddrKey Of(IPAddress ip)
            {
                Span<byte> b = stackalloc byte[16];
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    ip.TryWriteBytes(b, out _);
                    return new AddrKey(0, 0x1_0000_0000UL | BinaryPrimitives.ReadUInt32BigEndian(b));
                }
                ip.TryWriteBytes(b, out _);
                return new AddrKey(BinaryPrimitives.ReadUInt64BigEndian(b), 0);   // the /64 (Limits.Ipv6PrefixBits)
            }
        }

        private sealed class AddrState
        {
            public readonly string Text;
            public long LastUsed;
            public int Active, Unpaired, PendingAsClient;
            // Registered hosts on this address right now (each proved its key). A host dials back once for every lane a
            // controlling PC opens (about 20 each), so its address gets far more room: each dial-back still needs its
            // one-time token, and a wrong one counts as a failure as always.
            public int HostLines;
            public int LivePairsAsHost;        // live pairs whose host side dialled back from this address
            public HashSet<string>? MissNames; // the different absent hosts asked for in this window (Limits.MissesPerHour)
            public long MissSince;
            public double Tokens = Limits.BucketSize;
            public long TokensAt;
            public int Fails;
            public long FailSince;
            public long BlockedUntil;
            public long LastNoteAt = long.MinValue / 2;
            public int Unnoted;
            public string UnnotedWhy = "";

            public AddrState(string text, long now) { Text = text; LastUsed = now; TokensAt = now; FailSince = now; }

            /// <summary>factor: the address's room (1 for most; more for an address with hosts), which scales both the burst and the rate.</summary>
            public void Refill(long now, int factor)
            {
                Tokens = Math.Min(Limits.BucketSize * factor, Tokens + (now - TokensAt) * Limits.BucketPerSecond * factor / 1000.0);
                TokensAt = now;
            }
        }

        private sealed class Conn
        {
            public readonly Socket S;
            public readonly AddrState Addr;
            public readonly string Text;
            public bool Unpaired = true, Released;
            private int _closed;

            public Conn(Socket s, AddrState addr, IPAddress ip) { S = s; Addr = addr; Text = TextOf(ip); }

            public void Close()
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                try { S.Dispose(); } catch (Exception) { }
            }
        }

        private sealed class HostLine
        {
            public readonly Conn Conn;
            public readonly string NameKey;
            public readonly byte[] KeyHash;           // SHA-256 of the host's public key
            public readonly DateTime SinceUtc = DateTime.UtcNow;
            public readonly ulong UdpKey;
            public readonly SemaphoreSlim SendLock = new(0, 1);   // starts held: the Ok goes first
            public readonly CancellationTokenSource Closed = new();
            public volatile SocketAddress? Udp;   // where the host's UDP is, from its announcements (read by the UDP thread without a lock)
            public int Pending;                   // guarded by _gate
            public long LastHeard = Environment.TickCount64;
            public volatile bool Probes;          // a 4.7 host: it asks "are you there?", so silence is judged sooner
            public volatile bool Replaced;
            private int _closed;

            public HostLine(Conn conn, string nameKey, byte[] keyHash, ulong udpKey) { Conn = conn; NameKey = nameKey; KeyHash = keyHash; UdpKey = udpKey; }

            public async Task<bool> Send(byte type, byte[] body, CancellationToken run, int timeoutMs = Limits.SendTimeoutMs)
            {
                try
                {
                    using var t = Timeout(run, timeoutMs);
                    await SendLock.WaitAsync(t.Token).ConfigureAwait(false);
                    try { await SendFrame(Conn.S, type, body, t.Token).ConfigureAwait(false); }
                    finally { SendLock.Release(); }
                    return true;
                }
                catch (Exception) { return false; }
            }

            public void Close(bool replaced = false)
            {
                if (replaced) Replaced = true;
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                try { Closed.Cancel(); } catch (Exception) { }
                Conn.Close();
            }
        }

        private sealed class Pending
        {
            public readonly ulong Id;
            public readonly byte[] IdBytes, Token;
            public HostLine Host;                // guarded by _gate: moves to the host's newer line if it registers again
            public readonly Conn Client;
            public readonly long Created;
            public readonly TaskCompletionSource<Conn?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Pending(ulong id, byte[] idBytes, byte[] token, HostLine host, Conn client, long created)
            { Id = id; IdBytes = idBytes; Token = token; Host = host; Client = client; Created = created; }
        }

        private sealed class Pairing
        {
            public readonly ulong Id;
            public readonly string NameKey, HostText, ClientText;
            public volatile HostLine Host;               // the host's current line (written under _gate, read by the UDP thread)
            public volatile SocketAddress? HostUdpBefore; // where its previous line said its UDP was, until the new one says
            public long LastActivity = Environment.TickCount64;
            public CancellationTokenSource? Cts;
            public volatile SocketAddress? ClientUdp;    // written and read only by the UDP thread
            public readonly Bucket ToHost = new(), ToClient = new();

            public Pairing(ulong id, HostLine host, string hostText, string clientText) { Id = id; Host = host; NameKey = host.NameKey; HostText = hostText; ClientText = clientText; }
        }

        /// <summary>UDP bytes allowed per direction of a pair: about 2 MB a second, beyond that packets are dropped. Only the UDP thread uses it.</summary>
        private sealed class Bucket
        {
            private double _tokens = Limits.UdpBurstBytes;
            private long _at = Environment.TickCount64;

            public bool Take(int bytes, long now)
            {
                _tokens = Math.Min(Limits.UdpBurstBytes, _tokens + (now - _at) * Limits.UdpBytesPerSecond / 1000.0);
                _at = now;
                if (_tokens < bytes) return false;
                _tokens -= bytes;
                return true;
            }
        }
    }
}
