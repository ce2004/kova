using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using KovaRelayWire;

namespace TailRemote
{
    /// <summary>
    /// Kova's side of a Kova Relay (4.4; the relay itself is relay\, KovaRelay.exe). A host behind a home
    /// router, or with no port open, connects out to the relay and stays registered under a name; a
    /// controlling PC connects to the relay asking for that name, and the relay pairs them. Every
    /// connection a controlling PC makes (the main one, each file lane, each screen lane) is paired on its
    /// own: the relay tells the host, the host dials back with a one-time token, and the relay then only
    /// copies bytes both ways. Kova's own login and encryption run end to end through it unchanged: the
    /// relay never sees the password or anything inside a session, and the host's signed hello means a
    /// relay cannot pretend to be the host either. UDP (sound and the microphone) goes through the relay's
    /// same port with a small header naming the pairing (Wire.UdpHead).
    /// </summary>
    internal static class Relay
    {
        /// <summary>A relay address as typed: "name@relay" or "name@relay:port" (the port otherwise comes from the Port setting).</summary>
        public sealed record Target(string Name, string RelayHost, int RelayPort)
        {
            // As a controlling PC types it: the port only when it is not the relay's usual one.
            public override string ToString() => Name + "@" + (RelayHost.Contains(':') ? "[" + RelayHost + "]" : RelayHost) + (RelayPort == KovaRelayWire.Wire.DefaultPort ? "" : ":" + RelayPort);
        }

        /// <summary>Reads "name@relay[:port]". Null when the text is not a relay address (no @). problem: why it is not a usable one.</summary>
        public static Target? Parse(string text, int defaultPort, out string? problem)
        {
            problem = null;
            text = text.Trim();
            int at = text.IndexOf('@');
            if (at < 0) return null;
            string name = text[..at].Trim(), host = text[(at + 1)..].Trim();
            int port = defaultPort;
            // "relay:port", but not an IPv6 address on its own ("[fd7a::1]:47200" or a bare one with several colons).
            if (host.StartsWith('[') && host.IndexOf("]:", StringComparison.Ordinal) is int close and > 0)
            {
                if (!int.TryParse(host[(close + 2)..], out port)) { problem = "The relay's port after the colon is not a number."; return null; }
                host = host[1..close];
            }
            else if (host.Count(c => c == ':') == 1)
            {
                int colon = host.IndexOf(':');
                if (!int.TryParse(host[(colon + 1)..], out port)) { problem = "The relay's port after the colon is not a number."; return null; }
                host = host[..colon];
            }
            host = host.Trim('[', ']');
            if (!Wire.NameOk(name)) { problem = "The name before the @ must be 1 to 40 letters, digits, dashes, dots or underscores, like office-pc."; return null; }
            if (host.Length == 0) { problem = "Put the relay's address after the @, like office-pc@relay.example.com."; return null; }
            if (port is < 1 or > 65535) { problem = "The relay's port must be from 1 to 65535."; return null; }
            return new Target(name, host, port);
        }

        private static int Count(this string s, Func<char, bool> f) { int n = 0; foreach (char c in s) if (f(c)) n++; return n; }

        /// <summary>What Kova says for each of the relay's refusals (the relay sends a code, never words of its own).</summary>
        public static string Explain(Wire.Reason r, string name) => r switch
        {
            Wire.Reason.NoSuchHost => "No PC called " + name + " is connected to the relay right now. Check the name, and that Kova on that PC is hosting through this relay.",
            Wire.Reason.HostDidNotAnswer => name + " is connected to the relay but did not answer in time. Try again.",
            Wire.Reason.Busy => "The relay is busy, or has had too many connections from here. Try again in a minute.",
            Wire.Reason.NameTaken => "Another PC already uses the name " + name + " on this relay. Choose another name in Host through a relay, or ask whoever runs the relay to forget the old one.",
            Wire.Reason.NewNamesOff => "The relay is not taking new names. Ask whoever runs it to turn on Allow new host names, or use a name it already knows.",
            Wire.Reason.TooManyNames => "The relay holds as many names as it will. Ask whoever runs it to forget some.",
            Wire.Reason.BadSignature => "The relay could not check this PC's identity. Try again.",
            _ => "The relay refused the connection.",
        };

        /// <summary>
        /// Relay only that can work: relay only, through a relay, and a relay address and name that read. Otherwise a host
        /// listens directly (and opens its port), or nobody could ever reach it.
        /// </summary>
        public static bool OnlyUsable(bool relayOnly, bool relayHosting, string? address, string? name, int port) =>
            relayOnly && relayHosting && !string.IsNullOrWhiteSpace(address) && !string.IsNullOrWhiteSpace(name)
            && Parse(name.Trim() + "@" + address.Trim(), port, out _) != null;

        public static IPEndPoint Resolve(Target t)
        {
            IPAddress[] addrs;
            try { addrs = Dns.GetHostAddresses(t.RelayHost); }
            catch (Exception e) when (e is SocketException or ArgumentException) { addrs = Array.Empty<IPAddress>(); }
            if (addrs.Length == 0) throw new InvalidOperationException("Could not find the relay " + t.RelayHost + ". Check its address.");
            // IPv4 first when there is one: a relay on a home or cloud PC is reached most reliably there.
            var a = Array.Find(addrs, x => x.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0];
            return new IPEndPoint(a, t.RelayPort);
        }

        /// <summary>Connects to the relay and reads its hello. The challenge is returned for a registration.</summary>
        private static (TcpClient Tcp, NetworkStream Stream, byte[] Challenge) Open(IPEndPoint relay, int timeoutMs)
        {
            var tcp = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
            tcp.Client.DualMode = true;
            try
            {
                bool done;
                try { done = tcp.ConnectAsync(relay.Address, relay.Port).Wait(timeoutMs); }
                catch (AggregateException e) when (e.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused)
                { throw new InvalidOperationException("The relay at " + relay.Address + " is not running on port " + relay.Port + "."); }
                if (!done) throw new TimeoutException("No answer from the relay at " + relay.Address + ", port " + relay.Port + ".");
                var s = tcp.GetStream();
                s.ReadTimeout = timeoutMs;
                s.WriteTimeout = timeoutMs;
                byte[] hello = new byte[Wire.ServerHelloBytes];
                try { Wire.ReadExactly(s, hello); }
                catch (Exception e) when (e is System.IO.IOException) { throw new InvalidOperationException("That is not a Kova Relay, or its port is wrong."); }
                if (!hello.AsSpan(0, 4).SequenceEqual(Wire.Magic)) throw new InvalidOperationException("That is not a Kova Relay, or its port is wrong.");
                return (tcp, s, hello[4..]);
            }
            catch { tcp.Dispose(); throw; }
        }

        /// <summary>What a relay check found: whether it is up, and (when a name was asked about) whether that PC is connected to it.</summary>
        public sealed record CheckResult(bool Up, bool? HostHere, string Said);

        /// <summary>
        /// Is the relay up? relayAddress: "relay" or "relay:port". name: a host to ask about, or null. Never throws;
        /// takes up to about 6 seconds when the relay does not answer. Uses the relay's Ping, which it never counts
        /// against this PC (an older relay refuses it, which still shows it is up).
        /// </summary>
        public static CheckResult Check(string relayAddress, string? name)
        {
            string asName = string.IsNullOrWhiteSpace(name) ? "x" : name.Trim();
            if (Parse(asName + "@" + relayAddress, Wire.DefaultPort, out string? problem) is not Target t)
                return new(false, null, problem ?? "That is not a relay address.");
            string where = t.RelayHost + (t.RelayPort == Wire.DefaultPort ? "" : ", port " + t.RelayPort);
            try
            {
                var (tcp, s, _) = Open(Resolve(t), 6000);
                using (tcp)
                {
                    Wire.WriteFrame(s, Wire.Ping, string.IsNullOrWhiteSpace(name) ? ReadOnlySpan<byte>.Empty : Wire.NameBody(t.Name));
                    var (type, body) = Wire.ReadFrame(s);
                    if (type == Wire.Refused)
                        return body.Length >= 1 && (Wire.Reason)body[0] != Wire.Reason.BadRequest
                            ? new(true, null, "The relay " + where + " is up, but turned this check away: " + Explain((Wire.Reason)body[0], t.Name))
                            : new(true, null, "The relay " + where + " is up. It is an older Kova Relay, so it cannot say which PCs are connected; updating it lets it.");
                    if (type != Wire.Ok || body.Length < 1) return new(true, null, "The relay " + where + " is up.");
                    return (Wire.PingAnswer)body[0] switch
                    {
                        Wire.PingAnswer.HostHere => new(true, true, "The relay " + where + " is up, and " + t.Name + " is connected to it."),
                        Wire.PingAnswer.HostAbsent => new(true, false, "The relay " + where + " is up, but no PC called " + t.Name + " is connected to it right now."),
                        _ => new(true, null, "The relay " + where + " is up."),
                    };
                }
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException)
            {
                return new(false, null, e.Message);
            }
            catch (Exception e) when (e is System.IO.IOException or SocketException or AggregateException or System.IO.InvalidDataException)
            {
                return new(false, null, "The relay " + where + " did not answer properly. Check its address and port, and that Kova Relay is running there.");
            }
        }

        /// <summary>
        /// A controlling PC's connection to a host through the relay: once this returns, the stream is the
        /// host's, exactly as if connected directly (the host's hello comes next). id: the pairing's id, for UDP.
        /// </summary>
        public static TcpClient Dial(IPEndPoint relay, string name, int timeoutMs, out byte[] id)
        {
            var (tcp, s, _) = Open(relay, timeoutMs);
            try
            {
                Wire.WriteFrame(s, Wire.Connect, Wire.NameBody(name));
                // The relay waits up to PairWaitMs for the host to dial back.
                s.ReadTimeout = Wire.PairWaitMs + timeoutMs;
                (byte type, byte[] body) f;
                try { f = Wire.ReadFrame(s); }
                catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException) { throw new InvalidOperationException("The relay hung up before " + name + " answered. Try again."); }
                if (f.type == Wire.Refused && f.body.Length >= 1) throw new InvalidOperationException(Explain((Wire.Reason)f.body[0], name));
                if (f.type != Wire.Ok || f.body.Length != Wire.IdBytes) throw new InvalidOperationException("The relay answered with something Kova does not understand. Update Kova Relay.");
                id = f.body;
                s.ReadTimeout = Timeout.Infinite; // the caller sets its own from here
                s.WriteTimeout = Timeout.Infinite;
                return tcp;
            }
            catch { tcp.Dispose(); throw; }
        }

        // ---- Relayed controlling PCs, as the host sees them ----

        /// <summary>The relay's pairings this process has accepted: the address each stands for, and the real one behind it.</summary>
        private static readonly ConcurrentDictionary<ulong, (IPAddress Stand, string Real, RelayHost Via)> Pairings = new();

        /// <summary>
        /// The address a relayed connection stands for inside the host: fd6c, then six bytes from the
        /// controlling PC's real address block, then the pairing id. Every limit and match by address
        /// block (wrong passwords, the sound's address check) then works per real PC, and the pairing id
        /// in the low half tells the UDP path which pairing a packet goes back on.
        /// </summary>
        internal static IPAddress Stand(string realAddress, ReadOnlySpan<byte> id)
        {
            byte[] block;
            if (IPAddress.TryParse(realAddress, out var real))
            {
                if (real.IsIPv4MappedToIPv6) real = real.MapToIPv4();
                block = real.GetAddressBytes();
                if (block.Length == 16) block = block[..8]; // its /64, as the host counts IPv6
            }
            else block = System.Text.Encoding.UTF8.GetBytes(realAddress);
            byte[] h = SHA256.HashData(block);
            byte[] b = new byte[16];
            b[0] = 0xfd; b[1] = 0x6c;
            h.AsSpan(0, 6).CopyTo(b.AsSpan(2));
            id.CopyTo(b.AsSpan(8));
            return new IPAddress(b);
        }

        internal static bool IsStand(IPAddress a)
        {
            if (a.AddressFamily != AddressFamily.InterNetworkV6) return false;
            byte[] b = a.GetAddressBytes();
            return b[0] == 0xfd && b[1] == 0x6c;
        }

        internal static ulong IdOf(IPAddress stand) => BitConverter.ToUInt64(stand.GetAddressBytes(), 8);

        /// <summary>A pairing this host accepted. False if the relay handed out an id already in use (then that connection is refused).</summary>
        internal static bool Remember(ReadOnlySpan<byte> id, IPAddress stand, string real, RelayHost via) => Pairings.TryAdd(BitConverter.ToUInt64(id), (stand, real, via));

        /// <summary>Forgets a pairing, only if it is still the one remembered (a repeated id must never end another's).</summary>
        internal static void Forget(ReadOnlySpan<byte> id, IPAddress stand, RelayHost via) =>
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<ulong, (IPAddress, string, RelayHost)>>)Pairings)
                .Remove(new(BitConverter.ToUInt64(id), (stand, RealOf(id), via)));

        private static string RealOf(ReadOnlySpan<byte> id) => Pairings.TryGetValue(BitConverter.ToUInt64(id), out var p) ? p.Real : "";

        /// <summary>The real address behind a relayed connection's stand-in, or null if it is not one.</summary>
        internal static string? RealAddress(IPAddress a) => IsStand(a) && Pairings.TryGetValue(IdOf(a), out var p) ? p.Real : null;

        /// <summary>The relay a stand-in's UDP goes back through, with the pairing id: null if it is not a live relayed pairing.</summary>
        internal static (RelayHost Via, ulong Id)? RouteOf(IPAddress a) => IsStand(a) && Pairings.TryGetValue(IdOf(a), out var p) ? (p.Via, IdOf(a)) : null;

        /// <summary>The relay and stand-in of a live pairing, by its id: null if this process has no such pairing.</summary>
        internal static (RelayHost Via, IPAddress Stand)? RouteOfId(ulong id) => Pairings.TryGetValue(id, out var p) ? (p.Via, p.Stand) : null;
    }

    /// <summary>
    /// A host's registration on a relay: keeps a control line open (registering again whenever it drops),
    /// dials back for every connection the relay offers, and hands each to the host as if it had come in
    /// directly (Host.Serve with the address it stands for). One per hosting Host.
    /// </summary>
    internal sealed class RelayHost : IDisposable
    {
        private readonly Relay.Target _target;
        private readonly Host _host;
        private readonly HostIdentity _identity;
        private readonly Action<byte[], int, IPEndPoint> _sendUdp;
        private readonly Action<string> _status;
        private volatile bool _stop;
        private volatile TcpClient? _line;
        private volatile IPEndPoint? _relay;
        private byte[]? _udpKey;
        private string? _lastSaid;
        private readonly SemaphoreSlim _accepting = new(64, 64);

        /// <summary>Whether the host is registered on the relay right now.</summary>
        public bool Registered { get; private set; }

        public string Name => _target.Name;

        public static string? RealAddress(IPAddress a) => Relay.RealAddress(a);

        public RelayHost(Relay.Target target, Host host, HostIdentity identity, Action<byte[], int, IPEndPoint> sendUdp, Action<string> status)
        {
            _target = target; _host = host; _identity = identity; _sendUdp = sendUdp; _status = status;
            new Thread(Run) { IsBackground = true, Name = "Kova relay line" }.Start();
        }

        /// <summary>Says something once: the same news again (every retry) is not repeated.</summary>
        private void Say(string s)
        {
            if (s == _lastSaid) return;
            _lastSaid = s;
            _status(s);
        }

        private void Run()
        {
            int wait = 2000;
            while (!_stop)
            {
                bool refusedForGood = false;
                try
                {
                    var relay = Relay.Resolve(_target);
                    _relay = relay;
                    var (tcp, s, challenge) = OpenLine(relay);
                    _line = tcp;
                    using (tcp)
                    {
                        byte[] spki = _identity.SubjectPublicKeyInfo();
                        byte[] sig = _identity.Sign(Wire.RegisterSignedBytes(challenge, _target.Name));
                        byte[] name = Wire.NameBody(_target.Name);
                        byte[] body = new byte[name.Length + 2 + spki.Length + sig.Length];
                        name.CopyTo(body, 0);
                        body[name.Length] = (byte)(spki.Length >> 8);
                        body[name.Length + 1] = (byte)spki.Length;
                        spki.CopyTo(body, name.Length + 2);
                        sig.CopyTo(body, name.Length + 2 + spki.Length);
                        Wire.WriteFrame(s, Wire.Register, body);
                        var (type, reply) = Wire.ReadFrame(s);
                        if (type == Wire.Refused && reply.Length >= 1)
                        {
                            var why = (Wire.Reason)reply[0];
                            refusedForGood = why is Wire.Reason.NameTaken or Wire.Reason.NewNamesOff or Wire.Reason.TooManyNames;
                            throw new InvalidOperationException(Relay.Explain(why, _target.Name));
                        }
                        if (type != Wire.Ok || reply.Length != Wire.IdBytes) throw new InvalidOperationException("The relay answered with something Kova does not understand. Update Kova Relay.");
                        _udpKey = reply;
                        AnnounceUdp();
                        Registered = true;
                        wait = 2000;
                        Say("Reachable through the relay " + _target.RelayHost + " as " + _target.Name + ".");
                        Serve(tcp, s);
                    }
                }
                catch (Exception e)
                {
                    // Stopping (Dispose closes the line, which ends the read above with an exception): just leave. Any
                    // exception must be caught here: one escaping this thread would end the whole program.
                    if (_stop) return;
                    string why = e is InvalidOperationException or TimeoutException ? e.Message
                        : "Lost the connection to the relay " + _target.RelayHost + "; trying again.";
                    Say(why);
                }
                Registered = false;
                _line = null;
                if (_stop) return;
                // A name another PC holds will not come free by retrying quickly; a relay that is down is tried
                // every few seconds at first, then once a minute.
                Thread.Sleep(refusedForGood ? 60_000 : wait);
                wait = Math.Min(wait * 2, 60_000);
            }
        }

        private static (TcpClient, NetworkStream, byte[]) OpenLine(IPEndPoint relay)
        {
            var tcp = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
            tcp.Client.DualMode = true;
            try
            {
                if (!tcp.ConnectAsync(relay.Address, relay.Port).Wait(8000)) throw new TimeoutException("No answer from the relay at " + relay.Address + ", port " + relay.Port + ".");
                var s = tcp.GetStream();
                s.ReadTimeout = 8000;
                s.WriteTimeout = 8000;
                byte[] hello = new byte[Wire.ServerHelloBytes];
                Wire.ReadExactly(s, hello);
                if (!hello.AsSpan(0, 4).SequenceEqual(Wire.Magic)) throw new InvalidOperationException("That is not a Kova Relay, or its port is wrong.");
                return (tcp, s, hello[4..]);
            }
            catch (AggregateException e) { tcp.Dispose(); throw new InvalidOperationException("Could not reach the relay at " + relay.Address + ", port " + relay.Port + ": " + (e.InnerException?.Message ?? e.Message)); }
            catch { tcp.Dispose(); throw; }
        }

        /// <summary>The control line: keep-alives both ways, and an offer for each controlling PC's connection.</summary>
        private void Serve(TcpClient tcp, NetworkStream s)
        {
            s.ReadTimeout = Wire.DeadAfterMs;
            var writeGate = new object();
            using var beat = new Timer(_ =>
            {
                try { lock (writeGate) Wire.WriteFrame(s, Wire.KeepAlive, ReadOnlySpan<byte>.Empty); } catch { try { tcp.Dispose(); } catch { } }
                AnnounceUdp(); // also keeps a home router's UDP mapping to the relay open
            }, null, Wire.KeepAliveMs, Wire.KeepAliveMs);
            while (!_stop)
            {
                var (type, body) = Wire.ReadFrame(s);
                if (type == Wire.KeepAlive) continue;
                if (type != Wire.Incoming || body.Length < Wire.IdBytes + Wire.TokenBytes + 1) continue; // something newer: ignored
                byte[] id = body[..Wire.IdBytes];
                byte[] token = body[Wire.IdBytes..(Wire.IdBytes + Wire.TokenBytes)];
                int n = body[Wire.IdBytes + Wire.TokenBytes];
                if (body.Length < Wire.IdBytes + Wire.TokenBytes + 1 + n || n > 64) continue;
                string real = System.Text.Encoding.ASCII.GetString(body, Wire.IdBytes + Wire.TokenBytes + 1, n);
                var relay = _relay;
                if (relay == null) continue;
                // At most so many dial-backs at once: a hostile relay could otherwise send thousands of offers a second and
                // use up this PC's threads. Past that, an offer is dropped (the relay tells that PC this host did not answer).
                if (!_accepting.Wait(0)) continue;
                try { new Thread(() => { try { Accept(relay, id, token, real); } finally { _accepting.Release(); } }) { IsBackground = true, Name = "Kova relay accept" }.Start(); }
                catch { _accepting.Release(); }
            }
        }

        /// <summary>Dials back for one offered connection and gives it to the host.</summary>
        private void Accept(IPEndPoint relay, byte[] id, byte[] token, string real)
        {
            TcpClient? tcp = null;
            bool remembered = false;
            var stand = Relay.Stand(real, id);
            try
            {
                var (t, s, _) = OpenLine(relay);
                tcp = t;
                byte[] body = new byte[Wire.IdBytes + Wire.TokenBytes];
                id.CopyTo(body, 0);
                token.CopyTo(body, Wire.IdBytes);
                Wire.WriteFrame(s, Wire.Accept, body);
                var (type, _) = Wire.ReadFrame(s);
                if (type != Wire.Ok) { tcp.Dispose(); return; }
                s.ReadTimeout = Timeout.Infinite;
                s.WriteTimeout = Timeout.Infinite;
                if (!Relay.Remember(id, stand, real, this)) { tcp.Dispose(); return; }
                remembered = true;
                AnnounceUdp(); // so the sound can start at once
                _host.Serve(tcp, stand); // returns when the connection ends
            }
            catch { try { tcp?.Dispose(); } catch { } }
            finally { if (remembered) Relay.Forget(id, stand, this); }
        }

        /// <summary>Tells the relay where this host's UDP is (the same socket the sound goes out of).</summary>
        private void AnnounceUdp()
        {
            var relay = _relay;
            var key = _udpKey;
            if (relay == null || key == null) return;
            byte[] p = new byte[Wire.UdpHead];
            p[0] = Wire.Udp0; p[1] = Wire.Udp1;
            key.CopyTo(p, 2);
            try { _sendUdp(p, p.Length, relay); } catch { }
        }

        /// <summary>Sends a UDP packet to a relayed controlling PC: through the relay, with the pairing's header.</summary>
        public void SendUdp(ulong id, byte[] data, int length)
        {
            var relay = _relay;
            if (relay == null || length > Wire.MaxUdpPayload) return;
            byte[] p = new byte[Wire.UdpHead + length];
            p[0] = Wire.Udp0; p[1] = Wire.Udp1;
            BitConverter.TryWriteBytes(p.AsSpan(2), id);
            Buffer.BlockCopy(data, 0, p, Wire.UdpHead, length);
            try { _sendUdp(p, p.Length, relay); } catch { }
        }

        /// <summary>
        /// A UDP packet that came in: if it came from this relay, its header is taken off and from becomes the
        /// stand-in of the pairing it names (true), or it is dropped (false: not a pairing this host knows).
        /// Anything not from this relay is left alone (true).
        /// </summary>
        public bool Unwrap(ref byte[] d, ref IPEndPoint from)
        {
            var relay = _relay;
            if (relay == null || !Same(from, relay)) return true;
            if (d.Length <= Wire.UdpHead || d[0] != Wire.Udp0 || d[1] != Wire.Udp1) return false;
            ulong id = BitConverter.ToUInt64(d, 2);
            var route = Relay.RouteOfId(id);
            if (route == null || route.Value.Via != this) return false;
            from = new IPEndPoint(route.Value.Stand, 1);
            d = d[Wire.UdpHead..];
            return true;
        }

        private static bool Same(IPEndPoint a, IPEndPoint b)
        {
            var x = a.Address.IsIPv4MappedToIPv6 ? a.Address.MapToIPv4() : a.Address;
            var y = b.Address.IsIPv4MappedToIPv6 ? b.Address.MapToIPv4() : b.Address;
            return a.Port == b.Port && x.Equals(y);
        }

        public void Dispose()
        {
            _stop = true;
            try { _line?.Dispose(); } catch { }
        }
    }
}
