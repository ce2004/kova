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
        public static string Explain(Wire.Reason r, string name)
        {
            // A name made from the password is never shown: it means nothing to anyone.
            string pc = IsChannel(name) ? "the PC" : name;
            return r switch
            {
                Wire.Reason.NoSuchHost => pc == "the PC"
                    ? "No PC is hosting on this relay with that password right now. Check the password, and that Kova on the other PC is hosting through this relay."
                    : "No PC called " + name + " is connected to the relay right now.",
                Wire.Reason.HostDidNotAnswer => "The relay found " + pc + ", but it did not answer in time. Try again.",
                Wire.Reason.Busy => "The relay is busy, or has had too many connections from this PC. Try again in a minute.",
                Wire.Reason.NameTaken => "Another PC is already hosting on this relay with the same password. Each PC needs its own password.",
                Wire.Reason.NewNamesOff or Wire.Reason.TooManyNames => "The relay is not accepting new PCs. Update Kova Relay to the latest version.",
                Wire.Reason.BadSignature => "The relay could not verify this PC's identity. Try again.",
                Wire.Reason.Replaced => "Another copy of Kova on this PC is hosting through the relay with the same password.",
                _ => "The relay refused the connection.",
            };
        }

        /// <summary>
        /// Relay only that can work: relay only, through a relay, and a relay address that reads. Otherwise a host
        /// listens directly (and opens its port), or nobody could ever reach it.
        /// </summary>
        public static bool OnlyUsable(bool relayOnly, bool relayHosting, string? address, int port) =>
            relayOnly && relayHosting && !string.IsNullOrWhiteSpace(address) && Parse("x@" + address.Trim(), port, out _) != null;

        // ---- 4.6: the password is the meeting point on a relay (as NVDA Remote's key is) ----

        private static readonly ConcurrentDictionary<string, string> Channels = new();

        /// <summary>
        /// What a host registers under on a relay, and what a controlling PC asks the relay for: made from the password
        /// alone, so the PC reached is the one hosting with the password typed, and there are no names to choose. It is
        /// 40 hex characters (a valid relay name), from PBKDF2-SHA256 with 600,000 rounds: the password itself never
        /// reaches the relay, and guessing it back from this is slow. Kept per password for the session (every file
        /// and screen connection asks for it again).
        /// </summary>
        public static string Channel(string password) => Channels.GetOrAdd(password, p =>
        {
            byte[] key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                System.Text.Encoding.UTF8.GetBytes(p), "Kova relay channel 1"u8.ToArray(), 600_000, HashAlgorithmName.SHA256, 20);
            return Convert.ToHexString(key).ToLowerInvariant();
        });

        /// <summary>Whether a relay name is one made from a password (Channel), so never shown to people.</summary>
        public static bool IsChannel(string name)
        {
            if (name.Length != 40) return false;
            foreach (char c in name) if (!char.IsAsciiHexDigitLower(c)) return false;
            return true;
        }

        /// <summary>A relay address with no name in front ("@relay" or "@relay:port"): the PC is found by the password.</summary>
        public static bool IsByPassword(string address) => address.TrimStart().StartsWith('@');

        /// <summary>"@relay" (or "@relay#tag") made into "channel@relay" for this password; anything else is left as it is.</summary>
        public static string WithChannel(string address, string password) =>
            IsByPassword(address) ? Channel(password) + "@" + RelayPart(address) : address;

        /// <summary>How a relay address reads to people: the relay alone ("relay.example.com"), never the code made from the password.</summary>
        public static string Shown(string address) => IsByPassword(address) ? RelayPart(address) : address;

        /// <summary>
        /// A PC reached through a relay by its password, as saved and compared: "@relay#tag". The tag (8 hex characters
        /// from a hash of the password's code) tells two PCs on the same relay apart, so saving, switching to and waking
        /// one never touches another; it is too short to reveal anything about the password.
        /// </summary>
        public static string Tagged(string relayAt, string password)
        {
            byte[] h = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes("Kova saved PC tag 1|" + Channel(password)));
            return "@" + relayAt + "#" + Convert.ToHexString(h, 0, 4).ToLowerInvariant();
        }

        private static string RelayPart(string address)
        {
            string a = address.Trim()[1..];
            int hash = a.IndexOf('#');
            return hash < 0 ? a : a[..hash];
        }

        public static IPEndPoint Resolve(Target t) => new IPEndPoint(ResolveAll(t)[0], t.RelayPort);

        /// <summary>Every address the relay's name has right now, IPv4 first (a relay on a home or cloud PC is reached most reliably there).</summary>
        public static IPAddress[] ResolveAll(Target t)
        {
            IPAddress[] addrs;
            try { addrs = Dns.GetHostAddresses(t.RelayHost); }
            catch (Exception e) when (e is SocketException or ArgumentException) { addrs = Array.Empty<IPAddress>(); }
            if (addrs.Length == 0) throw new InvalidOperationException("Could not find the relay " + t.RelayHost + ". Check its address.");
            var v4 = Array.FindAll(addrs, x => x.AddressFamily == AddressFamily.InterNetwork);
            var rest = Array.FindAll(addrs, x => x.AddressFamily != AddressFamily.InterNetwork);
            return v4.Length == 0 ? rest : rest.Length == 0 ? v4 : [.. v4, .. rest];
        }

        /// <summary>
        /// No Nagle delay, and Windows' TCP keep-alive: a relay connection whose other end vanished without a word (a
        /// router dropping it, sleep) is noticed even while nothing is being sent.
        /// </summary>
        internal static void Tune(Socket s, int idleSeconds, int intervalSeconds, int probes)
        {
            try { s.NoDelay = true; } catch { }
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, idleSeconds);
                s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, intervalSeconds);
                s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, probes);
            }
            catch { } // older Windows: the plain keep-alive above still applies
        }

        /// <summary>Connects to the relay and reads its hello. The challenge is returned for a registration.</summary>
        private static (TcpClient Tcp, NetworkStream Stream, byte[] Challenge) Open(IPEndPoint relay, int timeoutMs)
        {
            var tcp = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
            tcp.Client.DualMode = true;
            Tune(tcp.Client, 30, 5, 6);
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
                            ? new(true, null, "The relay " + where + " is reachable, but declined the check: " + Explain((Wire.Reason)body[0], t.Name))
                            : new(true, null, "The relay " + where + " is reachable. It runs an older version of Kova Relay, so it cannot report which PCs are connected.");
                    if (type != Wire.Ok || body.Length < 1) return new(true, null, "The relay " + where + " is reachable.");
                    return (Wire.PingAnswer)body[0] switch
                    {
                        Wire.PingAnswer.HostHere => new(true, true, IsChannel(t.Name)
                            ? "The relay " + where + " is reachable, and a PC is hosting on it with this password."
                            : "The relay " + where + " is reachable, and " + t.Name + " is connected to it."),
                        Wire.PingAnswer.HostAbsent => new(true, false, IsChannel(t.Name)
                            ? "The relay " + where + " is reachable, but no PC is hosting on it with this password right now."
                            : "The relay " + where + " is reachable, but no PC called " + t.Name + " is connected to it right now."),
                        _ => new(true, null, "The relay " + where + " is reachable."),
                    };
                }
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException)
            {
                return new(false, null, e.Message);
            }
            catch (Exception e) when (e is System.IO.IOException or SocketException or AggregateException or System.IO.InvalidDataException)
            {
                return new(false, null, "The relay " + where + " did not respond correctly. Check its address and port, and that Kova Relay is running there.");
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
                catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException) { throw new InvalidOperationException("The relay closed the connection before " + (IsChannel(name) ? "the PC" : name) + " answered. Try again."); }
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
    ///
    /// Staying reachable (4.7): the line asks the relay "are you there?" every few seconds, so a line a router
    /// silently dropped is noticed in about 15 seconds (a 4.6 relay, which does not answer, within 35). A line
    /// that was up is registered again at once; only repeated failures back off, from 1 up to 15 seconds, with a
    /// little randomness so many hosts do not all knock at the same moment after a relay restart. The relay's
    /// address is looked up afresh on every try, and every address it has is tried. Connections already made
    /// through the relay are separate from the line: they carry on while it reconnects.
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
        private volatile IPEndPoint? _relay;      // the relay's address the current (or last) line used
        private volatile IPEndPoint? _relayUdp;   // the same, written the IPv6 way the host's UDP socket sends to it
        private volatile byte[]? _announce;       // the UDP "here I am" packet for the current registration
        private string? _lastSaid;
        private readonly SemaphoreSlim _accepting = new(64, 64);
        private readonly ManualResetEventSlim _wake = new(false);
        // Offers being dialled back or already taken, by pairing id: a relay offers a waiting connection again on a
        // host's new line, and the host must dial back only once.
        private readonly ConcurrentDictionary<ulong, long> _offered = new();

        private static readonly ConcurrentDictionary<RelayHost, byte> Live = new();
        private static readonly byte[] Probe = { Wire.ProbeAsk };
        [ThreadStatic] private static byte[]? _udpOut;

        /// <summary>Successful registrations by every RelayHost in this process (tests watch it go up after a drop).</summary>
        internal static int Registrations;

        /// <summary>Test switch: cuts every relay control line in this process, as a router dropping them would.</summary>
        internal static int TestDropLines()
        {
            int n = 0;
            foreach (var h in Live.Keys) { var l = h._line; if (l != null) { try { l.Dispose(); } catch { } n++; } }
            return n;
        }

        /// <summary>Whether the host is registered on the relay right now.</summary>
        public bool Registered { get; private set; }

        public string Name => _target.Name;

        public static string? RealAddress(IPAddress a) => Relay.RealAddress(a);

        public RelayHost(Relay.Target target, Host host, HostIdentity identity, Action<byte[], int, IPEndPoint> sendUdp, Action<string> status)
        {
            _target = target; _host = host; _identity = identity; _sendUdp = sendUdp; _status = status;
            Live.TryAdd(this, 0);
            new Thread(Run) { IsBackground = true, Name = "Kova relay line" }.Start();
        }

        /// <summary>Says something once: the same news again (every retry) is not repeated.</summary>
        private void Say(string s)
        {
            if (s == _lastSaid) return;
            _lastSaid = s;
            _status(s);
        }

        private sealed class ReplacedException : Exception { }

        private void Run()
        {
            int failures = 0;
            // How many times in a row another copy of Kova with this identity took the place. Two copies hosting with the
            // same password would otherwise take turns pushing each other off for ever, and say so every time.
            int replacedTimes = 0;
            while (!_stop)
            {
                bool refusedForGood = false, replaced = false;
                long upAt = 0;
                string? why = null;
                try
                {
                    var (tcp, s, challenge, relay) = Dial();
                    _line = tcp;
                    using (tcp)
                    {
                        if (_stop) return;
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
                            var no = (Wire.Reason)reply[0];
                            refusedForGood = no is Wire.Reason.NameTaken or Wire.Reason.NewNamesOff or Wire.Reason.TooManyNames;
                            throw new InvalidOperationException(Relay.Explain(no, _target.Name));
                        }
                        if (type != Wire.Ok || reply.Length != Wire.IdBytes) throw new InvalidOperationException("The relay answered with something Kova does not understand. Update Kova Relay.");
                        _relay = relay;
                        _relayUdp = relay.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(relay.Address.MapToIPv6(), relay.Port) : relay;
                        byte[] announce = new byte[Wire.UdpHead];
                        announce[0] = Wire.Udp0; announce[1] = Wire.Udp1;
                        reply.CopyTo(announce, 2);
                        _announce = announce;
                        AnnounceUdp();
                        Registered = true;
                        Interlocked.Increment(ref Registrations);
                        upAt = Environment.TickCount64;
                        string connected = Relay.IsChannel(_target.Name) ? "Connected to the Kova Relay at " + _target.RelayHost + ". Other computers can now connect through it."
                            : "Reachable through the relay " + _target.RelayHost + " as " + _target.Name + ".";
                        // While taking turns with another copy, coming back is not news: it was said once already.
                        if (replacedTimes == 0) Say(connected); else DiagLog.Write("relay: registered again while another copy of Kova shares this place");
                        Serve(tcp, s);
                    }
                }
                catch (ReplacedException) { replaced = true; }
                catch (Exception e)
                {
                    // Stopping (Dispose closes the line, which ends the read above with an exception): just leave. Any
                    // exception must be caught here: one escaping this thread would end the whole program.
                    if (_stop) return;
                    why = e is InvalidOperationException or TimeoutException ? e.Message
                        : "Lost the connection to the relay " + _target.RelayHost + "; trying again.";
                }
                Registered = false;
                _line = null;
                if (_stop) return;
                int delay;
                // Holding the place for 10 minutes means whatever pushed it off has stopped: a later push is news again.
                if (!replaced && upAt != 0 && Environment.TickCount64 - upAt >= 600_000) replacedTimes = 0;
                if (replaced)
                {
                    // Another copy of Kova with this PC's identity took the place (the relay keeps the newest line).
                    // Coming straight back would only push it off in turn, so each time it waits longer: 30 seconds,
                    // 2 minutes, then 5 minutes. Said once, not every time.
                    replacedTimes++;
                    delay = Jitter(replacedTimes switch { 1 => 30_000, 2 => 120_000, _ => 300_000 });
                    if (replacedTimes == 1)
                        Say("Another copy of Kova on this PC is hosting through the relay " + _target.RelayHost + " with the same password, so this one stepped aside. It keeps checking quietly in the background.");
                    else DiagLog.Write("relay: another copy of Kova took this place again (" + replacedTimes + " times); waiting " + delay / 1000 + " s");
                }
                else if (refusedForGood)
                {
                    // A name another PC holds will not come free by retrying quickly.
                    delay = Jitter(30_000);
                    if (why != null) Say(why);
                }
                else if (upAt != 0 && Environment.TickCount64 - upAt >= 10_000)
                {
                    // A line that was working dropped (a router or Wi-Fi blip, the relay restarting): register again at
                    // once, and say nothing unless that fails too, so a blip goes unnoticed.
                    failures = 0;
                    delay = Random.Shared.Next(0, 250);
                }
                else
                {
                    // The relay is down or unreachable, or lines keep dropping straight away: 1, 2, 4, 8, then every 15 seconds.
                    failures++;
                    delay = Jitter(Math.Min(15_000, 1000 << Math.Min(failures - 1, 4)));
                    Say(why ?? "Lost the connection to the relay " + _target.RelayHost + "; trying again.");
                }
                _wake.Wait(delay);
            }
        }

        /// <summary>The delay give or take a quarter, so hosts that lost the relay together do not all come back together.</summary>
        private static int Jitter(int ms) => ms * 3 / 4 + Random.Shared.Next(0, ms / 2 + 1);

        /// <summary>Looks the relay up afresh (its address may have changed) and connects to the first address that answers.</summary>
        private (TcpClient, NetworkStream, byte[], IPEndPoint) Dial()
        {
            Exception? last = null;
            foreach (var a in Relay.ResolveAll(_target))
            {
                if (_stop) break;
                var ep = new IPEndPoint(a, _target.RelayPort);
                try
                {
                    var (t, s, c) = OpenLine(ep, controlLine: true);
                    return (t, s, c, ep);
                }
                catch (Exception e) { last = e; }
            }
            throw last ?? new InvalidOperationException("Could not find the relay " + _target.RelayHost + ". Check its address.");
        }

        private static (TcpClient, NetworkStream, byte[]) OpenLine(IPEndPoint relay, bool controlLine)
        {
            var tcp = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
            tcp.Client.DualMode = true;
            try
            {
                // A control line carries little but keep-alives, so Windows' own keep-alive can be tight there; a
                // connection that will carry a session gets a gentler one.
                if (controlLine) Relay.Tune(tcp.Client, 15, 3, 5); else Relay.Tune(tcp.Client, 30, 5, 6);
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

        /// <summary>
        /// The control line: a probe out every ProbeMs (a 4.7 relay answers it at once), the UDP announcement every
        /// other probe, and an offer for each controlling PC's connection in. A watchdog closes the line when the
        /// relay has been silent too long, which ends the read below and so this call.
        /// </summary>
        private void Serve(TcpClient tcp, NetworkStream s)
        {
            // Only a backstop: the watchdog below decides much sooner. (A read that times out leaves the socket unusable,
            // so short read timeouts cannot be used to check on the line.)
            s.ReadTimeout = Wire.DeadAfterMs + 10_000;
            var writeGate = new object();
            long lastHeard = Environment.TickCount64;
            bool answers = false;
            int beats = 0;
            using var beat = new Timer(_ =>
            {
                long quiet = Environment.TickCount64 - Volatile.Read(ref lastHeard);
                if (quiet > (Volatile.Read(ref answers) ? Wire.ProbedDeadAfterMs : Wire.SilentDeadAfterMs))
                {
                    DiagLog.Write("relay: no word from the relay for " + quiet / 1000 + " s; connecting again");
                    try { tcp.Dispose(); } catch { }
                    return;
                }
                try { lock (writeGate) Wire.WriteFrame(s, Wire.KeepAlive, Probe); } catch { try { tcp.Dispose(); } catch { } }
                if (Interlocked.Increment(ref beats) % 2 == 0) AnnounceUdp(); // also keeps a home router's UDP mapping to the relay open
            }, null, Wire.ProbeMs, Wire.ProbeMs);
            while (!_stop)
            {
                var (type, body) = Wire.ReadFrame(s);
                Volatile.Write(ref lastHeard, Environment.TickCount64);
                if (type == Wire.KeepAlive)
                {
                    if (body.Length >= 1 && body[0] == Wire.ProbeAnswer) Volatile.Write(ref answers, true);
                    continue;
                }
                if (type == Wire.Refused && body.Length >= 1 && (Wire.Reason)body[0] == Wire.Reason.Replaced) throw new ReplacedException();
                if (type != Wire.Incoming || body.Length < Wire.IdBytes + Wire.TokenBytes + 1) continue; // something newer: ignored
                byte[] id = body[..Wire.IdBytes];
                byte[] token = body[Wire.IdBytes..(Wire.IdBytes + Wire.TokenBytes)];
                int n = body[Wire.IdBytes + Wire.TokenBytes];
                if (body.Length < Wire.IdBytes + Wire.TokenBytes + 1 + n || n > 64) continue;
                string real = System.Text.Encoding.ASCII.GetString(body, Wire.IdBytes + Wire.TokenBytes + 1, n);
                var relay = _relay;
                if (relay == null) continue;
                ulong key = BitConverter.ToUInt64(id);
                long now = Environment.TickCount64;
                if (_offered.Count > 512)
                    foreach (var kv in _offered) if (now - kv.Value > 60_000) _offered.TryRemove(kv.Key, out _);
                if (!_offered.TryAdd(key, now)) continue; // already dialling back for it, or done
                // At most so many dial-backs at once: a hostile relay could otherwise send thousands of offers a second and
                // use up this PC's threads. Past that, an offer is dropped (the relay tells that PC this host did not answer).
                if (!_accepting.Wait(0)) { _offered.TryRemove(key, out _); continue; }
                try { new Thread(() => { try { Accept(relay, id, token, real, key); } finally { _accepting.Release(); } }) { IsBackground = true, Name = "Kova relay accept" }.Start(); }
                catch { _accepting.Release(); _offered.TryRemove(key, out _); }
            }
        }

        /// <summary>Dials back for one offered connection and gives it to the host. Each runs on its own thread, so a slow one never holds up the line or another.</summary>
        private void Accept(IPEndPoint relay, byte[] id, byte[] token, string real, ulong key)
        {
            TcpClient? tcp = null;
            bool remembered = false, taken = false;
            var stand = Relay.Stand(real, id);
            try
            {
                var (t, s, _) = OpenLine(relay, controlLine: false);
                tcp = t;
                byte[] body = new byte[Wire.IdBytes + Wire.TokenBytes];
                id.CopyTo(body, 0);
                token.CopyTo(body, Wire.IdBytes);
                Wire.WriteFrame(s, Wire.Accept, body);
                var (type, _) = Wire.ReadFrame(s);
                if (type != Wire.Ok) { tcp.Dispose(); return; }
                taken = true;
                s.ReadTimeout = Timeout.Infinite;
                s.WriteTimeout = Timeout.Infinite;
                if (!Relay.Remember(id, stand, real, this)) { tcp.Dispose(); return; }
                remembered = true;
                AnnounceUdp(); // so the sound can start at once
                _host.Serve(tcp, stand); // returns when the connection ends
            }
            catch { try { tcp?.Dispose(); } catch { } }
            finally
            {
                if (remembered) Relay.Forget(id, stand, this);
                // A dial-back that never got through may be offered again (on a new line): let it be tried then.
                if (!taken) _offered.TryRemove(key, out _);
            }
        }

        /// <summary>Tells the relay where this host's UDP is (the same socket the sound goes out of).</summary>
        private void AnnounceUdp()
        {
            var to = _relayUdp;
            var p = _announce;
            if (to == null || p == null) return;
            try { _sendUdp(p, p.Length, to); } catch { }
        }

        /// <summary>Sends a UDP packet to a relayed controlling PC: through the relay, with the pairing's header. No allocation per packet.</summary>
        public void SendUdp(ulong id, byte[] data, int length)
        {
            var to = _relayUdp;
            if (to == null || length > Wire.MaxUdpPayload) return;
            // The send below is synchronous, so one buffer per sending thread is enough.
            byte[] p = _udpOut ??= new byte[Wire.UdpHead + Wire.MaxUdpPayload];
            p[0] = Wire.Udp0; p[1] = Wire.Udp1;
            BitConverter.TryWriteBytes(p.AsSpan(2), id);
            Buffer.BlockCopy(data, 0, p, Wire.UdpHead, length);
            try { _sendUdp(p, Wire.UdpHead + length, to); } catch { }
        }

        /// <summary>
        /// A UDP packet that came in: if it came from this relay, its header is taken off and from becomes the
        /// stand-in of the pairing it names (true), or it is dropped (false: not a pairing this host knows).
        /// Anything not from this relay is left alone (true).
        /// </summary>
        public bool Unwrap(ref byte[] d, ref IPEndPoint from)
        {
            var relay = _relayUdp;
            if (relay == null) return true;
            if (!(from.Port == relay.Port && from.Address.Equals(relay.Address)) && !Same(from, relay)) return true;
            if (d.Length <= Wire.UdpHead || d[0] != Wire.Udp0 || d[1] != Wire.Udp1) return false;
            ulong id = BitConverter.ToUInt64(d, 2);
            var route = Relay.RouteOfId(id);
            // Any pairing this process holds on this relay: the listen-only registration shares the relay's address (4.6).
            if (route == null) return false;
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
            Live.TryRemove(this, out _);
            try { _wake.Set(); } catch { }
            try { _line?.Dispose(); } catch { }
        }
    }
}
