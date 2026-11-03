using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// One TCP connection carried over one tunnel lane (Protocol.TunnelMagic), on either PC: bytes from the local socket go
    /// out as TunnelData, and what comes in is written to it. Each direction ends on its own (TunnelEnd is a half-close, so a
    /// program that sends its question and then shuts its sending side still gets the whole answer); the pipe closes once
    /// both have ended, or at once when either side breaks. Two threads while it runs, none after.
    /// </summary>
    internal sealed class TunnelPipe
    {
        private readonly Socket _local;
        private readonly TcpClient _lane;
        private readonly SecureLink _link;
        private int _closed;
        private long _lastAt = Environment.TickCount64;
        private volatile bool _half;

        /// <summary>The connection a controller's pipe goes over (null on the host): when it ends, so does the pipe.</summary>
        public Client? Over { get; init; }

        /// <summary>One side finished and nothing moved for this long: the other side is not coming back, so it is closed.</summary>
        public const int HalfIdleMs = 120_000;

        public TunnelPipe(Socket local, TcpClient lane, SecureLink link)
        {
            _local = local;
            _lane = lane;
            _link = link;
            try
            {
                local.NoDelay = true;
                lane.NoDelay = true;
                // A side that stops taking bytes for a minute is gone: never waited on for ever. No limit on reading:
                // a connection may rightly sit quiet for hours (a page waiting for news), and the session's end closes it.
                local.SendTimeout = 60_000;
                lane.SendTimeout = 60_000;
                lane.GetStream().ReadTimeout = Timeout.Infinite;
                // A lane a router quietly forgot (NATs drop idle connections) is found within about two minutes this way:
                // otherwise a page's idle connection could hold one of the host's places until the whole session ended.
                lane.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                lane.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
                lane.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
                lane.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
            }
            catch { }
        }

        public bool Closed => Volatile.Read(ref _closed) == 1;

        /// <summary>Half-closed and quiet for HalfIdleMs.</summary>
        public bool Stale(long now) => _half && now - Volatile.Read(ref _lastAt) > HalfIdleMs;

        /// <summary>Closes both sockets (quick, from any thread); the threads carrying it then end by themselves.</summary>
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1) return;
            try { _local.Close(); } catch { }
            try { _lane.Close(); } catch { }
        }

        /// <summary>
        /// Carries the connection until both ways have ended, then closes it and lets go of its keys. opened: on the
        /// controller, where the host's first message says whether it reached the port (its code and words); a refusal
        /// ends the pipe. Null on the host, which says that itself before it starts.
        /// </summary>
        public void Run(Action<byte, string>? opened)
        {
            int left = 2;
            void Ended() { if (Interlocked.Decrement(ref left) == 0) Close(); else _half = true; }
            var stream = _lane.GetStream();
            var down = new Thread(() => { try { Down(stream, opened); Ended(); } catch { Close(); } }) { IsBackground = true, Name = "Kova tunnel in" };
            try { down.Start(); }
            catch { Close(); _link.Dispose(); return; }
            try { Up(stream); Ended(); } catch { Close(); }
            down.Join(); // the keys go only once neither thread can use them
            Close();
            _link.Dispose();
        }

        private void Up(NetworkStream stream)
        {
            byte[] buf = new byte[1 + Protocol.TunnelChunk];
            buf[0] = Protocol.TunnelData;
            byte[]? frame = null;
            while (true)
            {
                int n = _local.Receive(buf, 1, Protocol.TunnelChunk, SocketFlags.None);
                if (n <= 0)
                {
                    _link.Send(stream, stackalloc byte[] { Protocol.TunnelEnd }, ref frame);
                    return;
                }
                Volatile.Write(ref _lastAt, Environment.TickCount64);
                _link.Send(stream, buf.AsSpan(0, 1 + n), ref frame);
            }
        }

        private void Down(NetworkStream stream, Action<byte, string>? opened)
        {
            byte[]? buf = null;
            bool first = opened != null;
            while (true)
            {
                int len = _link.Receive(stream, ref buf);
                if (len == 0) throw new InvalidOperationException("An empty tunnel message.");
                Volatile.Write(ref _lastAt, Environment.TickCount64);
                if (first)
                {
                    first = false;
                    if (buf![0] != Protocol.TunnelOpened || len < 2) throw new InvalidOperationException("The host did not say whether it reached the port.");
                    opened!(buf[1], System.Text.Encoding.UTF8.GetString(buf, 2, len - 2));
                    if (buf[1] != Protocol.TunnelOk) throw new InvalidOperationException("The host did not reach the port.");
                    continue;
                }
                switch (buf![0])
                {
                    case Protocol.TunnelData:
                        for (int at = 1; at < len;) at += _local.Send(buf, at, len - at, SocketFlags.None);
                        break;
                    case Protocol.TunnelEnd when len == 1:
                        _local.Shutdown(SocketShutdown.Send);
                        return;
                    default:
                        throw new InvalidOperationException("An unknown tunnel message.");
                }
            }
        }

        /// <summary>
        /// The host's end: a connection to this port on this PC itself, 127.0.0.1 first, then ::1 (some programs listen only
        /// there). Never any other address. Both are tried at once: Windows retries a refused loopback connection for about
        /// two seconds, which one after the other added to every connection to a program listening only on ::1. 127.0.0.1
        /// wins when both answer. Null when nothing answers within 3 seconds.
        /// </summary>
        public static Socket? ConnectHere(int port)
        {
            var tries = new List<(Socket S, System.Threading.Tasks.Task T)>();
            foreach (var at in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                try
                {
                    var s = new Socket(at.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    tries.Add((s, s.ConnectAsync(at, port)));
                }
                catch { } // no IPv6 on this PC
            }
            Socket? Answered() => tries.Where(t => t.T.IsCompletedSuccessfully && t.S.Connected).Select(t => t.S).FirstOrDefault();
            Socket? won = null;
            long until = Environment.TickCount64 + 3000;
            while ((won = Answered()) == null && tries.Any(t => !t.T.IsCompleted) && Environment.TickCount64 < until)
                try { System.Threading.Tasks.Task.WaitAny(tries.Where(t => !t.T.IsCompleted).Select(t => t.T).ToArray(), 50); } catch { }
            foreach (var (s, t) in tries)
                if (s != won) { try { s.Dispose(); } catch { } t.ContinueWith(x => _ = x.Exception, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted); }
            return won;
        }
    }

    /// <summary>
    /// Reach a port on the remote PC, the controlling side: each tunnel listens on this PC's loopback only (127.0.0.1, and
    /// ::1 at the same port so a browser that tries ::1 first for "localhost" reaches it too, never 0.0.0.0 or any other
    /// address), and every connection made to it there goes over its own tunnel lane to that port on the remote PC.
    /// A tunnel belongs to a PC (its address and port, like a PC in the background): it keeps working while that PC waits in
    /// the background, carries on after a reconnect (a new connection waits up to 20 seconds for it), and closes when you
    /// disconnect from that PC or close the tunnel.
    /// </summary>
    internal sealed class TunnelBook : IDisposable
    {
        internal sealed class Tunnel
        {
            public required string Pc { get; init; }
            public required string Name { get; init; }
            public required int RemotePort { get; init; }
            public required int LocalPort { get; init; }
            internal readonly List<TcpListener> Listeners = new();
            internal readonly List<TunnelPipe> Pipes = new(); // under the book's gate
            internal int Waiting; // connections waiting for a lane; under the gate
            internal volatile bool Closed;
            internal long SaidAt = long.MinValue / 2; // when something about it was last said (at most every 30 s)
            /// <summary>What to paste: a web address, except for the ports of well-known programs that are not web pages (Remote Desktop, SSH...).</summary>
            public string Address => NotWeb.Contains(RemotePort) ? "localhost:" + LocalPort : "http://localhost:" + LocalPort;
            private static readonly HashSet<int> NotWeb = new() { 21, 22, 23, 25, 110, 143, 445, 1433, 3306, 3389, 5432, 5900, 6379, 27017 };
        }

        /// <summary>Connections through all tunnels to one PC at once (the host takes a few more, for old ones still closing).</summary>
        public const int MaxPerPc = 32;
        /// <summary>Tunnels open at once.</summary>
        public const int MaxTunnels = 64;
        /// <summary>How long a new connection waits for its PC to reconnect.</summary>
        public static int WaitForPcMs = 20_000;

        private readonly object _gate = new();
        private readonly List<Tunnel> _all = new();
        private readonly Dictionary<string, Client> _clients = new(StringComparer.OrdinalIgnoreCase); // each PC's connection now
        private readonly Timer _watch;
        private bool _watching;

        /// <summary>Something worth saying (nothing answers at that port, the PC is not connected): raised on a network thread.</summary>
        public event Action<string>? Said;

        public TunnelBook() => _watch = new Timer(_ => Watch(), null, Timeout.Infinite, Timeout.Infinite);

        public int Count { get { lock (_gate) return _all.Count; } }

        public IReadOnlyList<Tunnel> All { get { lock (_gate) return _all.ToList(); } }

        public Tunnel? Find(string pc, int remotePort) { lock (_gate) return _all.Find(t => Same(t.Pc, pc) && t.RemotePort == remotePort); }

        /// <summary>Connections going through this tunnel now.</summary>
        public int Connections(Tunnel t) { lock (_gate) return t.Pipes.Count(p => !p.Closed); }

        /// <summary>Whether the tunnel's PC has a connection that works now (not waiting for it to reconnect).</summary>
        public bool Ready(Tunnel t) { lock (_gate) return _clients.TryGetValue(t.Pc, out var c) && !c.Closed; }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>This PC's connection now (a new one after a reconnect): new connections through its tunnels go over it.</summary>
        public void Attach(string pc, Client c) { lock (_gate) _clients[pc] = c; }

        /// <summary>
        /// Opens a tunnel: this PC's localPort (loopback only) to remotePort on the PC. Throws InvalidOperationException
        /// with words to say when the local port is taken, or there are too many.
        /// </summary>
        public Tunnel Open(string pc, string name, int remotePort, int localPort)
        {
            if (remotePort < 1 || remotePort > 65535 || localPort < 1 || localPort > 65535) throw new InvalidOperationException("A port is a number from 1 to 65535.");
            lock (_gate)
            {
                if (_all.Count >= MaxTunnels) throw new InvalidOperationException("There are already " + MaxTunnels + " ports open here. Close one first.");
                if (_all.Find(t => Same(t.Pc, pc) && t.RemotePort == remotePort) is Tunnel already) throw new InvalidOperationException("Port " + remotePort + " on " + name + " is already at localhost " + already.LocalPort + ".");
            }
            if (!LocalFree(localPort)) throw new InvalidOperationException(Taken(localPort));
            var t = new Tunnel { Pc = pc, Name = name, RemotePort = remotePort, LocalPort = localPort };
            try
            {
                t.Listeners.Add(Listen(IPAddress.Loopback, localPort));
                // ::1 as well where this PC has IPv6, at the same port: a browser asked for "localhost" often tries it first.
                if (Socket.OSSupportsIPv6)
                    try { t.Listeners.Add(Listen(IPAddress.IPv6Loopback, localPort)); }
                    catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable) { }
            }
            catch (SocketException)
            {
                foreach (var l in t.Listeners) try { l.Stop(); } catch { }
                throw new InvalidOperationException(Taken(localPort));
            }
            lock (_gate)
            {
                _all.Add(t);
                if (!_watching) { _watching = true; _watch.Change(1000, 1000); }
            }
            foreach (var l in t.Listeners)
            {
                var listener = l;
                new Thread(() => Accept(t, listener)) { IsBackground = true, Name = "Kova tunnel accept" }.Start();
            }
            DiagLog.Write("tunnels: localhost " + localPort + " now reaches port " + remotePort + " on " + name);
            return t;
        }

        private static string Taken(int port) => "Port " + port + " is already used on this PC.";

        private static TcpListener Listen(IPAddress at, int port)
        {
            var l = new TcpListener(at, port) { ExclusiveAddressUse = true };
            l.Start(16);
            return l;
        }

        /// <summary>Whether nothing on this PC listens at this port (on any address), so a tunnel can take it.</summary>
        public static bool LocalFree(int port)
        {
            if (port < 1 || port > 65535) return false;
            try { if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port)) return false; }
            catch { }
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                l.Start();
                l.Stop();
                return true;
            }
            catch { return false; }
        }

        /// <summary>This port if it is free here, else the next free one after it (or one Windows picks).</summary>
        public static int FreeNear(int port)
        {
            for (int p = Math.Clamp(port, 1, 65535), tries = 0; p <= 65535 && tries < 200; p++, tries++)
                if (LocalFree(p)) return p;
            try
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int any = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return any;
            }
            catch { return 0; }
        }

        /// <summary>Closes one tunnel and every connection through it. False if it was already closed.</summary>
        public bool Close(Tunnel t)
        {
            lock (_gate)
            {
                if (!_all.Remove(t)) return false;
                Shut(t);
                IdleCheck();
            }
            DiagLog.Write("tunnels: closed localhost " + t.LocalPort);
            return true;
        }

        /// <summary>Closes the tunnels of every PC this no longer connects to (disconnected, dropped from the background, given up on).</summary>
        public void CloseWhere(Func<string, bool> gone)
        {
            lock (_gate)
            {
                foreach (var t in _all.Where(t => gone(t.Pc)).ToList()) { _all.Remove(t); Shut(t); }
                foreach (string pc in _clients.Keys.Where(pc => !_all.Any(t => Same(t.Pc, pc))).ToList()) _clients.Remove(pc);
                IdleCheck();
            }
        }

        // Under _gate.
        private void Shut(Tunnel t)
        {
            t.Closed = true;
            foreach (var l in t.Listeners) try { l.Stop(); } catch { }
            foreach (var p in t.Pipes) p.Close();
        }

        // Under _gate: the once-a-second check runs only while there are tunnels.
        private void IdleCheck()
        {
            if (_all.Count == 0 && _watching) { _watching = false; _watch.Change(Timeout.Infinite, Timeout.Infinite); }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var t in _all) Shut(t);
                _all.Clear();
                _clients.Clear();
                _watching = false;
            }
            _watch.Dispose();
        }

        /// <summary>Once a second: connections over a connection that has ended, or half-closed and quiet too long, are closed.</summary>
        private void Watch()
        {
            long now = Environment.TickCount64;
            lock (_gate)
                foreach (var t in _all)
                    foreach (var p in t.Pipes)
                        if (p.Over?.Closed == true || p.Stale(now)) p.Close();
        }

        private void Accept(Tunnel t, TcpListener l)
        {
            while (!t.Closed)
            {
                Socket s;
                try { s = l.AcceptSocket(); }
                catch { if (t.Closed) return; Thread.Sleep(50); continue; }
                try { new Thread(() => Carry(t, s)) { IsBackground = true, Name = "Kova tunnel" }.Start(); }
                catch { try { s.Dispose(); } catch { } }
            }
        }

        /// <summary>One connection to the tunnel's local port: over a lane of its own to the PC's port, until it ends.</summary>
        private void Carry(Tunnel t, Socket local)
        {
            bool counted = false;
            TunnelPipe? pipe = null;
            (TcpClient Tcp, SecureLink Link)? lane = null;
            try
            {
                bool full;
                lock (_gate)
                {
                    if (t.Closed) return;
                    full = _all.Where(x => Same(x.Pc, t.Pc)).Sum(x => x.Pipes.Count + x.Waiting) >= MaxPerPc;
                    if (!full) { t.Waiting++; counted = true; }
                }
                if (full) { Tell(t, "Too many connections to " + t.Name + "'s ports at once, so some were turned away. They work again once others close."); return; }
                // Reconnecting: the connection waits for the PC to be back, rather than failing at once.
                Client? c = null;
                for (long until = Environment.TickCount64 + WaitForPcMs; !t.Closed; Thread.Sleep(100))
                {
                    lock (_gate) c = _clients.TryGetValue(t.Pc, out var now) && !now.Closed ? now : null;
                    if (c != null || Environment.TickCount64 > until) break;
                }
                if (t.Closed) return;
                if (c == null) { Tell(t, t.Name + " is not connected right now, so localhost " + t.LocalPort + " cannot reach it."); return; }
                lane = c.OpenTunnelLane(t.RemotePort);
                if (lane == null) { Tell(t, t.Name + " did not answer for localhost " + t.LocalPort + ". Try again in a moment."); return; }
                pipe = new TunnelPipe(local, lane.Value.Tcp, lane.Value.Link) { Over = c };
                lock (_gate)
                {
                    t.Waiting--;
                    counted = false;
                    t.Pipes.Add(pipe);
                    if (t.Closed) pipe.Close(); // closed meanwhile: Run below ends at once
                }
                pipe.Run((code, why) => { if (code != Protocol.TunnelOk) Tell(t, Client.Theirs(why, t.RemotePort)); });
            }
            catch { }
            finally
            {
                lock (_gate)
                {
                    if (counted) t.Waiting--;
                    if (pipe != null) t.Pipes.Remove(pipe);
                }
                if (pipe == null && lane != null) { try { lane.Value.Tcp.Dispose(); } catch { } lane.Value.Link.Dispose(); }
                pipe?.Close();
                try { local.Dispose(); } catch { }
            }
        }

        /// <summary>Said about a tunnel at most every 30 seconds: a page with forty pictures must not say it forty times.</summary>
        private void Tell(Tunnel t, string words)
        {
            long now = Environment.TickCount64;
            lock (_gate) // only the time is looked at under the lock: Said is raised outside it
            {
                if (now - t.SaidAt < 30_000) return;
                t.SaidAt = now;
            }
            DiagLog.Write("tunnels: " + words);
            Said?.Invoke(words);
        }
    }

    /// <summary>
    /// The tests' side of tunnels (--selftest, --relaytest): an echo server on this PC's loopback, and a tunnel's round trip
    /// through it, checked by hash, with both ways of closing.
    /// </summary>
    internal static class TunnelTest
    {
        /// <summary>A server on 127.0.0.1 that sends back everything it gets and half-closes when its peer does. Stop to end it.</summary>
        internal sealed class Echo : IDisposable
        {
            private readonly TcpListener _l = new(IPAddress.Loopback, 0);
            private volatile bool _stop;
            public int Port { get; }
            public int Accepted;
            public int Ended; // connections that ended (peer closed, or this side was closed)
            public Echo()
            {
                _l.Start();
                Port = ((IPEndPoint)_l.LocalEndpoint).Port;
                new Thread(() =>
                {
                    while (!_stop)
                    {
                        Socket s;
                        try { s = _l.AcceptSocket(); } catch { return; }
                        Interlocked.Increment(ref Accepted);
                        new Thread(() =>
                        {
                            try
                            {
                                byte[] b = new byte[65536];
                                int n;
                                while ((n = s.Receive(b)) > 0) for (int at = 0; at < n;) at += s.Send(b, at, n - at, SocketFlags.None);
                                s.Shutdown(SocketShutdown.Send);
                            }
                            catch { }
                            finally { s.Dispose(); Interlocked.Increment(ref Ended); }
                        }) { IsBackground = true }.Start();
                    }
                }) { IsBackground = true }.Start();
            }
            public void Dispose() { _stop = true; try { _l.Stop(); } catch { } }
        }

        /// <summary>
        /// Through a tunnel at localhost localPort: bytes there and back (checked by hash), a half-close that still gets every
        /// byte back, and a close that reaches the far end. Null when all is well, else what went wrong.
        /// </summary>
        internal static string? RoundTrip(int localPort, int megabytes)
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            tcp.Connect(IPAddress.Loopback, localPort);
            tcp.ReceiveTimeout = 20_000;
            var s = tcp.GetStream();
            byte[] data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(megabytes << 20);
            byte[] want = System.Security.Cryptography.SHA256.HashData(data);
            var sender = new Thread(() =>
            {
                try
                {
                    for (int at = 0; at < data.Length; at += 61_440) s.Write(data, at, Math.Min(61_440, data.Length - at));
                    tcp.Client.Shutdown(SocketShutdown.Send); // the half-close: the echo must still send everything back
                }
                catch { }
            }) { IsBackground = true };
            sender.Start();
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            byte[] buf = new byte[65536];
            long got = 0;
            int n;
            try { while ((n = s.Read(buf)) > 0) { hash.AppendData(buf, 0, n); got += n; } }
            catch (Exception e) { return "the tunnel broke after " + got + " of " + data.Length + " bytes: " + e.Message; }
            sender.Join(5000);
            if (got != data.Length) return "the tunnel brought back " + got + " of " + data.Length + " bytes";
            if (!hash.GetHashAndReset().AsSpan().SequenceEqual(want)) return "the tunnel changed the bytes on the way";
            return null;
        }

        internal static bool Until(Func<bool> done, int ms)
        {
            for (int waited = 0; waited < ms; waited += 20)
            {
                if (done()) return true;
                Thread.Sleep(20);
            }
            return done();
        }

        /// <summary>
        /// The whole check on a connected controller c (control password) of a host with an echo server: a tunnel opened,
        /// megabytes both ways by hash, the remote end closing a connection, several connections at once, and closing
        /// the tunnel. Null when all is well.
        /// </summary>
        internal static string? Full(Client c, Func<int> hostTunnels, int megabytes)
        {
            var locked = Host.TestLocked;
            Host.TestLocked = false; // this PC's own console may be locked (a locked host carries no tunnels)
            try { return FullUnlocked(c, hostTunnels, megabytes); }
            finally { Host.TestLocked = locked; }
        }

        private static string? FullUnlocked(Client c, Func<int> hostTunnels, int megabytes)
        {
            using var echo = new Echo();
            using var book = new TunnelBook();
            const string Pc = "test-pc:1";
            book.Attach(Pc, c);
            if (!c.CanTunnel) return "the host did not offer tunnels";
            if (c.ProbeTunnelAsync(echo.Port).GetAwaiter().GetResult() is string no) return "a probe of the echo port said: " + no;
            int closedPort = TunnelBook.FreeNear(40000 + Random.Shared.Next(9000));
            if (c.ProbeTunnelAsync(closedPort).GetAwaiter().GetResult() is not string nothing || !nothing.Contains("Nothing")) return "a probe of a port nothing listens on did not say so";
            int local = TunnelBook.FreeNear(echo.Port + 1);
            var t = book.Open(Pc, "the test PC", echo.Port, local);
            try { book.Open(Pc, "the test PC", echo.Port + 7, local); return "a second tunnel took a local port already in use"; }
            catch (InvalidOperationException) { }
            if (RoundTrip(local, megabytes) is string bad) return bad;
            // Several at once.
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var many = Enumerable.Range(0, 6).Select(_ => new Thread(() => { if (RoundTrip(local, 1) is string e) errors.Enqueue(e); }) { IsBackground = true }).ToList();
            many.ForEach(x => x.Start());
            many.ForEach(x => x.Join(60_000));
            if (!errors.IsEmpty) return "six connections at once: " + errors.First();
            // (The remote end closing was in each round trip: the echo shuts its side once it has sent everything back.)
            // Closing the tunnel closes what goes through it, here and on the remote PC.
            int before = Volatile.Read(ref echo.Ended);
            using (var held = new TcpClient(AddressFamily.InterNetwork))
            {
                held.Connect(IPAddress.Loopback, local);
                var hs = held.GetStream();
                hs.Write(new byte[] { 1, 2, 3 });
                hs.ReadTimeout = 10_000;
                byte[] three = new byte[3];
                int r = 0;
                while (r < 3) { int k = hs.Read(three, r, 3 - r); if (k <= 0) break; r += k; }
                if (r != 3) return "a short message did not come back through the tunnel";
                if (!Until(() => hostTunnels() >= 1, 5000)) return "the host did not count the open tunnel connection";
                book.Close(t); // closing the tunnel closes what goes through it
                try { hs.ReadTimeout = 5000; if (hs.Read(three, 0, 3) > 0) return "a connection went on after its tunnel was closed"; }
                catch (System.IO.IOException) { }
            }
            if (!Until(() => Volatile.Read(ref echo.Ended) > before, 5000)) return "closing the tunnel did not close the connection on the remote PC";
            if (!Until(() => hostTunnels() == 0, 5000)) return "the host still holds " + hostTunnels() + " tunnel connections after they all closed";
            try
            {
                using var gone = new TcpClient(AddressFamily.InterNetwork);
                if (gone.ConnectAsync(IPAddress.Loopback, local).Wait(2000) && gone.Connected) return "a closed tunnel still listens";
            }
            catch { }
            return null;
        }
    }
}
