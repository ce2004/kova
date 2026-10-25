using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
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
    ///   - Spliced pairs: each paired connection has one 32 KB copy buffer (one per direction of a pair), so 600
    ///     connections hold at most 600 x 32 KB = about 19 MB (they come from the shared array pool and go back).
    ///   - Connections still asking: a 36-byte hello and at most a 1027-byte frame each, 600 x ~1.1 KB = under 1 MB.
    ///   - Per-address table: at most 20,000 entries of about 200 bytes with their dictionary slot, about 4 MB.
    ///   - Pinned names: at most 1,000 of about 300 bytes, under 1 MB. Pending pairings: bounded by connections.
    ///   - UDP: one 64 KB receive buffer; packets are forwarded straight from it, never queued.
    ///   - Log: at most 2,000 queued lines, under 1 MB.
    /// So about 25 MB of the relay's own at the very worst, plus Windows' socket buffers (in the kernel, by default
    /// up to 64 KB each way per connection: 600 x 128 KB = about 77 MB of kernel memory at the absolute worst,
    /// which Windows bounds by itself).
    /// </summary>
    internal static class Limits
    {
        public const int MaxConnections = 600;          // every TCP connection at once, host control lines included
        public const int PerAddressConnections = 64;    // one controlling PC uses up to about 20 (main, 16 file lanes, screen)
        public const int PerAddressUnpaired = 40;       // still asking (not yet a host line or a pair): a controlling PC opens its main line, 16 file lanes and the screen at once
        public const double BucketSize = 40;            // new connections per address: a burst of 40...
        public const double BucketPerSecond = 5;        // ...then 5 a second
        public const int FailuresToBlock = 20;          // bad frames, bad signatures, wrong tokens, missed deadlines...
        public const long FailureWindowMs = 60_000;     // ...within a minute...
        public const long BlockMs = 10 * 60_000;        // ...block the address for 10 minutes (closed at once, nothing read)
        public const int MaxAddressEntries = 20_000;    // per-address table cap; the longest unused go first
        public const long ForgetAddressAfterMs = 60 * 60_000; // an idle address entry is dropped after an hour
        public const int MaxPinnedNames = 1000;
        public const int NewNamesPerAddressPerHour = 5;
        public const int HostAddressFactor = 8;         // an address with a registered host: 8 times the connections, no rate bucket (HostLines)
        public const int PendingPerHost = 64;           // pairings waiting for one host to dial back (two PCs connecting at once ask for about 40)
        public const int PendingPerClient = 40;         // pairings waiting, asked for from one address (one PC asks for about 20 at once)
        public const int SpliceBuffer = 32 * 1024;      // one per direction of a pair
        public const long SpliceIdleMs = 15 * 60_000;   // a pair with no bytes either way for 15 minutes is closed
        public const int SendTimeoutMs = 10_000;        // a frame the other side will not take in 10 s ends that line
        public const double UdpBytesPerSecond = 2 * 1024 * 1024; // per pairing, per direction
        public const double UdpBurstBytes = 256 * 1024;
        public const long RefusalNoteMs = 60_000;       // at most one log line about refusals per address per minute
        public const int Ipv6PrefixBits = 64;           // IPv6 addresses count by their /64; IPv4 by the whole address
    }

    /// <summary>Counts and rows for the window and the tray tooltip.</summary>
    internal sealed record RelaySnapshot(bool Running, int Port, string? Problem, int Hosts, int ControllingPcs,
        int Connections, IReadOnlyList<HostRow> Rows);

    internal sealed record HostRow(string Name, bool Connected, int ControllingPcs, DateTime LastSeenUtc);

    /// <summary>
    /// The relay itself: one TCP listener and one UDP socket on the same port, following RelayWire.cs exactly.
    /// All shared state is guarded by one lock (_gate); nothing slow (network, disk) happens while holding it.
    /// Every connection runs on async sockets (no thread per connection) inside its own try/catch, so one bad
    /// connection never affects another.
    /// </summary>
    internal sealed class RelayServer : IDisposable
    {
        private readonly RelayFiles _files;
        private readonly RelayLog _log;
        private readonly object _gate = new();

        private readonly Dictionary<AddrKey, AddrState> _addrs = new();
        private readonly Dictionary<string, HostLine> _hosts = new(StringComparer.Ordinal);   // connected, by name key
        private readonly Dictionary<ulong, HostLine> _hostsByUdpKey = new();
        private readonly Dictionary<string, Pin> _pins;                                       // by name key
        private readonly Dictionary<ulong, Pending> _pending = new();
        private readonly Dictionary<ulong, Pairing> _pairings = new();                        // spliced, live
        private readonly HashSet<Conn> _conns = new();
        private int _total;
        private readonly Dictionary<(string Name, string Client), int> _pcPairs = new();  // live pairs per host and client address
        private volatile bool _disposed;

        private Socket? _listener, _udp;
        private CancellationTokenSource? _run;
        private bool _pinsDirty;
        private long _pinsSavedAt;

        public int Port { get; private set; }
        public bool Running { get; private set; }
        public string? Problem { get; private set; }
        public bool LoopbackOnly { get; set; }

        /// <summary>Exceptions the relay did not expect (not network or disk trouble). The self-test requires 0.</summary>
        public int UnexpectedErrors;

        private volatile bool _allowNewNames;
        public bool AllowNewNames
        {
            get => _allowNewNames;
            set => _allowNewNames = value; // in memory: settings.json is written only by an elevated copy (RelayService)
        }

        /// <summary>An administrator changed settings.json (through an elevated copy): take it, and log it.</summary>
        public void SetAllowNewNamesFromFile(bool allow)
        {
            if (_allowNewNames == allow) return;
            _allowNewNames = allow;
            _log.Write(allow ? "New host names are allowed." : "New host names are turned off.");
        }

        public RelayServer(RelayFiles files, RelayLog log)
        {
            _files = files;
            _log = log;
            _pins = files.LoadPins(Limits.MaxPinnedNames);
            _allowNewNames = files.LoadSettings().AllowNewNames;
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
                tcp.Listen(512);
                // Without this, Windows reports an ICMP "port unreachable" from some earlier send as an error on the
                // next receive, which would make one vanished PC disturb everyone's UDP.
                const int SIO_UDP_CONNRESET = -1744830452;
                try { udp.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch (Exception) { }
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
            _log.Write($"Started on port {port}" + (LoopbackOnly ? " (this PC only)." : "."));
            _ = Task.Run(() => AcceptLoop(tcp, run.Token));
            _ = Task.Run(() => UdpLoop(udp, run.Token));
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
            SavePinsIfDirty(force: true);
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
                var rows = new List<HostRow>(_pins.Count);
                foreach (var kv in _pins.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    bool on = _hosts.ContainsKey(kv.Key);
                    int pcs = clientsByHost.TryGetValue(kv.Key, out var s) ? s.Count : 0;
                    rows.Add(new HostRow(kv.Value.Name, on, pcs, kv.Value.LastSeen));
                }
                return new RelaySnapshot(Running, Port, Problem, _hosts.Count, allClients.Count, _total, rows);
            }
        }

        /// <summary>Removes a pinned name (and drops that host if connected) so another host can take it.</summary>
        public bool Forget(string name)
        {
            string key = Wire.NameKey(name);
            HostLine? line;
            lock (_gate)
            {
                if (!_pins.Remove(key)) return false;
                _pinsDirty = true;
                _hosts.TryGetValue(key, out line);
                if (line is not null) RemoveHostLocked(line);
            }
            line?.Close();
            SavePinsIfDirty(force: true);
            _log.Write($"Host name {name} was forgotten" + (line is not null ? ", and that host was disconnected." : "."));
            return true;
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
                        st.Refill(now);
                        int hostRoom = st.HostLines > 0 ? Limits.HostAddressFactor : 1;
                        if (st.Tokens < 1 && st.HostLines == 0) busyWhy = "it opened connections too quickly";
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
                        byte[] hello = NewHello(out _);
                        byte[] refusal = { Wire.Refused, 0, 1, (byte)Wire.Reason.Busy };
                        s.Send(hello.Concat(refusal).ToArray(), SocketFlags.None, out _);
                    }
                    catch (Exception) { }
                    s.Dispose();
                    return;
                }

                s.NoDelay = true;
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
            if (!goodKey) { await Refuse(c, Wire.Reason.BadRequest, $"sent a host key the relay does not take (name {name})", run, true).ConfigureAwait(false); return; }
            if (!goodSig) { await Refuse(c, Wire.Reason.BadSignature, $"sent a host signature that did not check out (name {name})", run, true).ConfigureAwait(false); return; }

            string keyHash = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
            string nameKey = Wire.NameKey(name);
            byte[] udpKeyBytes = new byte[Wire.IdBytes];
            Wire.Reason? no = null;
            string why = "";
            HostLine? old = null, line = null;
            lock (_gate)
            {
                long now = Environment.TickCount64;
                _pins.TryGetValue(nameKey, out Pin? pin);
                if (pin is not null)
                {
                    if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pin.KeyHash), Encoding.ASCII.GetBytes(keyHash)))
                    { no = Wire.Reason.NameTaken; why = $"it tried to register the name {name}, which another host has"; }
                }
                else if (!_allowNewNames) { no = Wire.Reason.NewNamesOff; why = $"it tried to register the new name {name} while new names are turned off"; }
                else if (_pins.Count >= Limits.MaxPinnedNames) { no = Wire.Reason.TooManyNames; why = $"it tried to register the name {name}, but the relay holds as many names as it will"; }
                else
                {
                    AddrState a = c.Addr;
                    if (now - a.NewNamesSince > 3_600_000) { a.NewNamesSince = now; a.NewNames = 0; }
                    if (a.NewNames >= Limits.NewNamesPerAddressPerHour) { no = Wire.Reason.Busy; why = $"it tried to register the name {name}, but that address registered too many new names this hour"; }
                    else
                    {
                        a.NewNames++;
                        pin = new Pin { Name = name, KeyHash = keyHash, FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow };
                        _pins[nameKey] = pin;
                    }
                }
                if (no is null)
                {
                    pin!.LastSeen = DateTime.UtcNow;
                    _pinsDirty = true;
                    ulong udpKey;
                    do { RandomNumberGenerator.Fill(udpKeyBytes); udpKey = BinaryPrimitives.ReadUInt64BigEndian(udpKeyBytes); }
                    while (udpKey == 0 || _hostsByUdpKey.ContainsKey(udpKey));
                    if (_hosts.TryGetValue(nameKey, out old)) RemoveHostLocked(old);
                    line = new HostLine(c, pin.Name, nameKey, udpKey);
                    _hosts[nameKey] = line;
                    _hostsByUdpKey[udpKey] = line;
                    c.Addr.HostLines++;
                    MarkPairedLocked(c);
                }
            }
            if (no is not null) { await Refuse(c, no.Value, why, run, countFailure: false).ConfigureAwait(false); return; }

            old?.Close(true);
            _log.Write(old is not null
                ? $"Host {line!.Name} connected again from {c.Text}; its old connection was closed."
                : $"Host {line!.Name} connected from {c.Text}.");
            try
            {
                // The line's send lock starts held, so no Incoming can go out before this Ok.
                using (var t = Timeout(run)) await SendFrame(c.S, Wire.Ok, udpKeyBytes, t.Token).ConfigureAwait(false);
                line.SendLock.Release();
                await RunHostLine(line, run).ConfigureAwait(false);
            }
            finally
            {
                bool wasCurrent;
                List<Pending> dropped;
                lock (_gate)
                {
                    wasCurrent = _hosts.TryGetValue(nameKey, out var cur) && cur == line;
                    if (wasCurrent) RemoveHostLocked(line);
                    dropped = _pending.Values.Where(p => p.Host == line).ToList();
                    foreach (Pending p in dropped) RemovePendingLocked(p);
                    if (_pins.TryGetValue(nameKey, out Pin? pin)) { pin.LastSeen = DateTime.UtcNow; _pinsDirty = true; }
                }
                foreach (Pending p in dropped) p.Done.TrySetResult(null);
                line.Close();
                if (!line.Replaced) _log.Write($"Host {line.Name} went away ({c.Text}).");
            }
        }

        private static bool IsP256(ECDsa ec)
        {
            ECCurve curve = ec.ExportParameters(false).Curve;
            if (!curve.IsNamed) return false;
            string? oid = curve.Oid.Value, friendly = curve.Oid.FriendlyName;
            return oid == "1.2.840.10045.3.1.7" || friendly is "nistP256" or "ECDSA_P256" or "secp256r1";
        }

        /// <summary>The host's control line: KeepAlive out every KeepAliveMs, anything in within DeadAfterMs, or it is dropped.</summary>
        private async Task RunHostLine(HostLine line, CancellationToken run)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(run, line.Closed.Token);
            Task keepAlive = KeepAliveLoop(line, stop.Token);
            try
            {
                byte[] head = new byte[3];
                byte[] body = new byte[Wire.MaxBody];
                while (!stop.IsCancellationRequested)
                {
                    using var dead = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    dead.CancelAfter(Wire.DeadAfterMs);
                    if (!await ReceiveExactly(line.Conn.S, head, dead.Token).ConfigureAwait(false)) return;
                    int len = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1));
                    if (len > Wire.MaxBody) { Fail(line.Conn, "sent a malformed frame on its host line"); return; }
                    if (len > 0 && !await ReceiveExactly(line.Conn.S, body.AsMemory(0, len), dead.Token).ConfigureAwait(false)) return;
                    // KeepAlive, or a frame type this relay does not know (a newer Kova): either way the line is alive.
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                stop.Cancel();
                try { await keepAlive.ConfigureAwait(false); } catch (Exception) { }
            }
        }

        private async Task KeepAliveLoop(HostLine line, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(Wire.KeepAliveMs, ct).ConfigureAwait(false);
                    if (!await line.Send(Wire.KeepAlive, Array.Empty<byte>(), ct).ConfigureAwait(false)) { line.Close(); return; }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (Expected(e)) { line.Close(); }
        }

        // ---------------------------------------------------------------- Connect

        /// <returns>true if the connection's life was handed to the splice (it releases it).</returns>
        private async Task<bool> HandleConnect(Conn c, byte[] body, CancellationToken run)
        {
            string? name = Wire.ReadName(body, out int used);
            if (name is null || used != body.Length)
            { await Refuse(c, Wire.Reason.BadRequest, "it sent a badly formed connect request", run, true).ConfigureAwait(false); return false; }
            string nameKey = Wire.NameKey(name);
            Pending? p = null;
            Wire.Reason? no = null;
            string why = "";
            lock (_gate)
            {
                if (!_hosts.TryGetValue(nameKey, out HostLine? host)) { no = Wire.Reason.NoSuchHost; why = $"asked for host {name}, which is not connected"; }
                else if (host.Pending >= Limits.PendingPerHost) { no = Wire.Reason.Busy; why = $"asked for host {name}, which has too many connections waiting"; }
                else if (c.Addr.PendingAsClient >= Limits.PendingPerClient) { no = Wire.Reason.Busy; why = $"asked for host {name} with too many requests waiting"; }
                else
                {
                    byte[] idBytes = new byte[Wire.IdBytes];
                    ulong id;
                    do { RandomNumberGenerator.Fill(idBytes); id = BinaryPrimitives.ReadUInt64BigEndian(idBytes); }
                    while (id == 0 || _pending.ContainsKey(id) || _pairings.ContainsKey(id));
                    p = new Pending(id, idBytes, RandomNumberGenerator.GetBytes(Wire.TokenBytes), host, c, Environment.TickCount64);
                    _pending[id] = p;
                    host.Pending++;
                    c.Addr.PendingAsClient++;
                }
            }
            if (p is null) { await Refuse(c, no!.Value, why, run, countFailure: false).ConfigureAwait(false); return false; }

            byte[] text = Encoding.ASCII.GetBytes(c.Text);
            byte[] incoming = new byte[Wire.IdBytes + Wire.TokenBytes + 1 + text.Length];
            p.IdBytes.CopyTo(incoming, 0);
            p.Token.CopyTo(incoming, Wire.IdBytes);
            incoming[Wire.IdBytes + Wire.TokenBytes] = (byte)text.Length;
            text.CopyTo(incoming, Wire.IdBytes + Wire.TokenBytes + 1);
            if (!await p.Host.Send(Wire.Incoming, incoming, run).ConfigureAwait(false)) p.Host.Close();

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
                await Refuse(c, Wire.Reason.HostDidNotAnswer, $"asked for host {name}, which did not answer in time", run, countFailure: false).ConfigureAwait(false);
                return false;
            }
            await Splice(c, hostSide, p, run).ConfigureAwait(false);
            return true;
        }

        // ---------------------------------------------------------------- Accept

        /// <returns>true if the connection was handed to the waiting Connect.</returns>
        private async Task<bool> HandleAccept(Conn c, byte[] body, CancellationToken run)
        {
            if (body.Length != Wire.IdBytes + Wire.TokenBytes)
            { await Refuse(c, Wire.Reason.BadRequest, "it sent a badly formed accept", run, true).ConfigureAwait(false); return false; }
            ulong id = BinaryPrimitives.ReadUInt64BigEndian(body);
            Pending? claimed = null;
            lock (_gate)
            {
                if (_pending.TryGetValue(id, out Pending? p)
                    && CryptographicOperations.FixedTimeEquals(p.Token, body.AsSpan(Wire.IdBytes, Wire.TokenBytes))
                    && Environment.TickCount64 - p.Created <= Wire.PairWaitMs
                    && RemovePendingLocked(p))
                    claimed = p;
            }
            if (claimed is null)
            { await Refuse(c, Wire.Reason.BadRequest, "it sent an accept that matched no waiting connection", run, true).ConfigureAwait(false); return false; }
            if (!claimed.Done.TrySetResult(c)) return false;
            return true;
        }

        // ---------------------------------------------------------------- splice

        private async Task Splice(Conn client, Conn host, Pending p, CancellationToken run)
        {
            var pair = new Pairing(p.Id, p.Host.NameKey, p.Host.Name, client.Text);
            bool first;
            lock (_gate)
            {
                MarkPairedLocked(client);
                MarkPairedLocked(host);
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
                if (first) _log.Write($"A controlling PC at {client.Text} connected to host {pair.Name}.");
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
                    _pairings.Remove(pair.Id);
                    var k = (pair.NameKey, pair.ClientText);
                    int left = _pcPairs.TryGetValue(k, out int had) ? had - 1 : 0;
                    if (left <= 0) _pcPairs.Remove(k); else _pcPairs[k] = left;
                    last = left <= 0;
                }
                Release(client);
                Release(host);
                if (last) _log.Write($"The controlling PC at {client.Text} left host {pair.Name}.");
            }
        }

        /// <summary>Copies one direction with one fixed 32 KB buffer: read, write all of it, repeat. No queue anywhere.</summary>
        private static async Task Pump(Socket from, Socket to, Pairing pair, CancellationToken ct)
        {
            byte[] buf = ArrayPool<byte>.Shared.Rent(Limits.SpliceBuffer);
            try
            {
                Memory<byte> mem = buf.AsMemory(0, Limits.SpliceBuffer);
                while (true)
                {
                    int n = await from.ReceiveAsync(mem, SocketFlags.None, ct).ConfigureAwait(false);
                    if (n <= 0) return;
                    await SendAll(to, mem[..n], ct).ConfigureAwait(false);
                    Volatile.Write(ref pair.LastActivity, Environment.TickCount64);
                }
            }
            catch (Exception) { }   // either side closing or failing ends the pair; the caller closes both
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }

        // ---------------------------------------------------------------- UDP

        private async Task UdpLoop(Socket udp, CancellationToken ct)
        {
            byte[] buf = new byte[65536];
            EndPoint any = new IPEndPoint(udp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult r;
                try { r = await udp.ReceiveFromAsync(buf, SocketFlags.None, any, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { continue; }
                catch (Exception e) { Unexpected("receiving UDP", e); continue; }
                try
                {
                    int n = r.ReceivedBytes;
                    if (n < Wire.UdpHead || n > Wire.UdpHead + Wire.MaxUdpPayload || buf[0] != Wire.Udp0 || buf[1] != Wire.Udp1) continue;
                    if (r.RemoteEndPoint is not IPEndPoint src) continue;
                    ulong id = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(2));
                    IPEndPoint? dest = null;
                    lock (_gate)
                    {
                        if (n == Wire.UdpHead && _hostsByUdpKey.TryGetValue(id, out HostLine? h)) { h.Udp = src; continue; }
                        if (!_pairings.TryGetValue(id, out Pairing? p)) continue;
                        // The pair's host by name now (the same key; it may have reconnected its control line).
                        _hosts.TryGetValue(p.NameKey, out HostLine? host);
                        long now = Environment.TickCount64;
                        if (host?.Udp is not null && host.Udp.Equals(src))
                        {
                            if (p.ClientUdp is not null && p.ToClient.Take(n, now)) dest = p.ClientUdp;
                        }
                        else
                        {
                            p.ClientUdp = src;   // the latest address the controlling PC sent this id from
                            if (host?.Udp is not null && p.ToHost.Take(n, now)) dest = host.Udp;
                        }
                    }
                    if (dest is not null)
                        await udp.SendToAsync(buf.AsMemory(0, r.ReceivedBytes), SocketFlags.None, dest, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { }
                catch (Exception e) { Unexpected("relaying UDP", e); }
            }
        }

        // ---------------------------------------------------------------- housekeeping

        private async Task HousekeepingLoop()
        {
            while (!_disposed)
            {
                try { await Task.Delay(1000).ConfigureAwait(false); } catch (Exception) { }
                try
                {
                    var notes = new List<string>();
                    List<Pairing> idle;
                    lock (_gate)
                    {
                        long now = Environment.TickCount64;
                        idle = _pairings.Values.Where(p => now - Volatile.Read(ref p.LastActivity) > Limits.SpliceIdleMs).ToList();
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
                    }
                    foreach (Pairing p in idle)
                    {
                        _log.Write($"Closed an idle connection between {p.ClientText} and host {p.Name} after 15 minutes without traffic.");
                        try { p.Cts?.Cancel(); } catch (Exception) { }
                    }
                    foreach (string n in notes) _log.Write(n);
                    SavePinsIfDirty(force: false);
                }
                catch (Exception e) { Unexpected("housekeeping", e); }
            }
        }

        private void SavePinsIfDirty(bool force)
        {
            List<Pin> pins;
            lock (_gate)
            {
                long now = Environment.TickCount64;
                // At most one write every 5 seconds, so a host reconnecting in a loop cannot keep the disk busy.
                if (!_pinsDirty || (!force && now - _pinsSavedAt < 5000)) return;
                _pinsDirty = false;
                _pinsSavedAt = now;
                pins = _pins.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new Pin { Name = p.Name, KeyHash = p.KeyHash, FirstSeen = p.FirstSeen, LastSeen = p.LastSeen }).ToList();
            }
            _files.SavePins(pins);
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
            return true;
        }

        private void RemoveHostLocked(HostLine h)
        {
            if (_hosts.TryGetValue(h.NameKey, out var cur) && cur == h) { _hosts.Remove(h.NameKey); h.Conn.Addr.HostLines--; }
            if (_hostsByUdpKey.TryGetValue(h.UdpKey, out var u) && u == h) _hostsByUdpKey.Remove(h.UdpKey);
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

        private static byte[] NewHello(out byte[] challenge)
        {
            challenge = RandomNumberGenerator.GetBytes(Wire.ChallengeBytes);
            byte[] hello = new byte[Wire.ServerHelloBytes];
            Wire.Magic.CopyTo(hello, 0);
            challenge.CopyTo(hello, 4);
            return hello;
        }

        private static CancellationTokenSource Timeout(CancellationToken run)
        {
            var t = CancellationTokenSource.CreateLinkedTokenSource(run);
            t.CancelAfter(Limits.SendTimeoutMs);
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
            _log.Write($"Something unexpected went wrong while {what}: {e.GetType().Name}: {e.Message}");
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
            public double Tokens = Limits.BucketSize;
            public long TokensAt;
            public int Fails;
            public long FailSince;
            public long BlockedUntil;
            public int NewNames;
            public long NewNamesSince = long.MinValue / 2;
            public long LastNoteAt = long.MinValue / 2;
            public int Unnoted;
            public string UnnotedWhy = "";

            public AddrState(string text, long now) { Text = text; LastUsed = now; TokensAt = now; FailSince = now; }

            public void Refill(long now)
            {
                Tokens = Math.Min(Limits.BucketSize, Tokens + (now - TokensAt) * Limits.BucketPerSecond / 1000.0);
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
            public readonly string Name, NameKey;
            public readonly ulong UdpKey;
            public readonly SemaphoreSlim SendLock = new(0, 1);   // starts held: the Ok goes first
            public readonly CancellationTokenSource Closed = new();
            public IPEndPoint? Udp;      // where the host's UDP is, from its announcements (guarded by _gate)
            public int Pending;          // guarded by _gate
            public volatile bool Replaced;
            private int _closed;

            public HostLine(Conn conn, string name, string nameKey, ulong udpKey) { Conn = conn; Name = name; NameKey = nameKey; UdpKey = udpKey; }

            public async Task<bool> Send(byte type, byte[] body, CancellationToken run)
            {
                try
                {
                    using var t = Timeout(run);
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
            public readonly HostLine Host;
            public readonly Conn Client;
            public readonly long Created;
            public readonly TaskCompletionSource<Conn?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Pending(ulong id, byte[] idBytes, byte[] token, HostLine host, Conn client, long created)
            { Id = id; IdBytes = idBytes; Token = token; Host = host; Client = client; Created = created; }
        }

        private sealed class Pairing
        {
            public readonly ulong Id;
            public readonly string NameKey, Name, ClientText;
            public long LastActivity = Environment.TickCount64;
            public CancellationTokenSource? Cts;
            public IPEndPoint? ClientUdp;                // guarded by _gate
            public readonly Bucket ToHost = new(), ToClient = new();

            public Pairing(ulong id, string nameKey, string name, string clientText) { Id = id; NameKey = nameKey; Name = name; ClientText = clientText; }
        }

        /// <summary>UDP bytes allowed per direction of a pair: about 2 MB a second, beyond that packets are dropped.</summary>
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
