using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace KovaRelayWire
{
    /// <summary>
    /// What Kova and Kova Relay say to each other. Compiled into both (kova.csproj links this file), so
    /// the two can never disagree about it.
    ///
    /// The relay pairs connections; it never takes part in Kova's login or encryption. Every TCP
    /// connection a controlling PC makes to a host (the main one, each file lane, each screen lane) is
    /// paired on its own, and once paired the relay only copies bytes both ways, already encrypted by
    /// Kova from end to end. The relay sees: which host names are connected and from which addresses,
    /// which addresses connect to which name, when, and how many bytes go each way. It never sees
    /// passwords, keys, keys pressed, sound, the screen, files or clipboard: those are inside Kova's
    /// AES-GCM, and the login proves the password without sending it.
    ///
    /// TCP, after connecting to the relay:
    ///   relay -> peer   ServerHello: "KRL1" + 32-byte random challenge (36 bytes).
    ///   peer -> relay   one Frame: type byte, body length (2 bytes, big endian, at most MaxBody), body.
    ///   relay -> peer   one Frame in answer: type Ok or Refused (body: one Reason byte).
    ///
    ///   Register (a host, 'R'): body = name length (1), name (UTF-8, NameOk), public key length (2, big
    ///     endian), public key (SubjectPublicKeyInfo, ECDSA P-256), signature (the rest: IEEE P1363,
    ///     64 bytes) over RegisterSignedBytes(challenge, name). The first host to register a name pins
    ///     its key; afterwards only that key may register it. Ok body: 8-byte host UDP key (HostUdpKey).
    ///     The connection then stays open as the host's control line, carrying Frames:
    ///       relay -> host  Incoming 'I': pairing id (8), accept token (16), the client's address as
    ///                      text length (1) + ASCII text (what the relay sees: for the host's own
    ///                      counting of wrong passwords, and for showing who connected).
    ///       both ways      KeepAlive 'K', empty, every KeepAliveMs; a line silent for DeadAfterMs is dropped.
    ///       4.7 additions, both ignored by 4.6 (it reads and drops any frame body, and any frame type it does not know):
    ///       host -> relay  KeepAlive with body { ProbeAsk } every ProbeMs: "are you there?"
    ///       relay -> host  KeepAlive with body { ProbeAnswer } at once in reply. Once each side has seen the other
    ///                      take part, it treats ProbedDeadAfterMs of silence as a dead line (a 4.6 peer keeps the
    ///                      older, slower timing), so a line a router silently dropped is noticed in seconds.
    ///       relay -> host  Refused { Replaced } just before the relay closes a control line because the same host
    ///                      key registered the name again on a newer line (another copy of Kova with this identity).
    ///   Connect (a controlling PC, 'C'): body = name length (1), name. The relay asks the host (Incoming)
    ///     and waits up to PairWaitMs for it to dial back. Ok body: the 8-byte pairing id (for UDP). From
    ///     then on the connection is the host's: the host speaks first, exactly as if connected directly.
    ///   Accept (the host dialling back, 'A'): body = pairing id (8), accept token (16). Ok, then raw.
    ///   Ping (4.5, 'P'): "is the relay up", from Kova's wizards and Check the relay. Body empty, or a name
    ///     (name length + name). Ok body: one PingAnswer byte. Never counted as a bad request. A relay older
    ///     than 4.5 refuses it with BadRequest, which still shows it is up.
    ///
    /// UDP, to the relay's same port number:
    ///   [0x4B 0x52] [8-byte id] [payload]  (UdpHead bytes of header)
    ///   - id is a pairing id: from the controlling PC, sent on to the host's UDP address; from the host's
    ///     UDP address, sent on to the controlling PC's. Unchanged, header included, one packet out for each
    ///     packet in, never bigger. The relay only sends to an address it has received that id from.
    ///   - id is a host UDP key with no payload (exactly UdpHead bytes): the host saying where its UDP
    ///     is (sent on registering and every KeepAliveMs, which also keeps a home router's mapping open).
    /// </summary>
    public static class Wire
    {
        public static readonly byte[] Magic = "KRL1"u8.ToArray();
        /// <summary>The port a relay uses unless its settings say otherwise (and the one Kova assumes for name@relay with no port).</summary>
        public const int DefaultPort = 47200;
        public const int ChallengeBytes = 32;
        public const int ServerHelloBytes = 4 + ChallengeBytes;
        public const int MaxBody = 1024;
        public const int MaxName = 40;

        public const byte Register = (byte)'R', Connect = (byte)'C', Accept = (byte)'A', Ping = (byte)'P';

        /// <summary>The relay's answer to a Ping: whether the named host is connected to it now (NoName when none was asked about).</summary>
        public enum PingAnswer : byte { HostAbsent = 0, HostHere = 1, NoName = 2 }
        public const byte Ok = (byte)'O', Refused = (byte)'X', Incoming = (byte)'I', KeepAlive = (byte)'K';

        public const int IdBytes = 8, TokenBytes = 16, UdpHead = 2 + IdBytes;
        public const byte Udp0 = 0x4B, Udp1 = 0x52;
        public const int MaxUdpPayload = 1500;

        public const int KeepAliveMs = 15_000;   // what a 4.6 relay and host send; the oldest peer a newer one must suit
        public const int DeadAfterMs = 50_000;   // what a 4.6 relay and host allow before dropping a silent line

        // 4.7: quicker dead-line detection, used only once the other side has shown it understands it.
        public const byte ProbeAsk = 1, ProbeAnswer = 2;   // the body byte of a KeepAlive probe and its answer
        public const int ProbeMs = 5_000;                 // a 4.7 host asks this often
        public const int ProbedDeadAfterMs = 15_000;      // three unanswered probes: the line is dead
        public const int RelayKeepAliveMs = 10_000;       // a 4.7 relay's own keep-alive to every host line
        public const int SilentDeadAfterMs = 35_000;      // a peer that does not probe: two missed 15 s keep-alives and some slack
        public const int PairWaitMs = 10_000;
        public const int RequestDeadlineMs = 10_000;

        /// <summary>Why the relay said no. Kova says each in its own words: the relay sends no text of its own.</summary>
        public enum Reason : byte
        {
            BadRequest = 1,      // not a request it understands
            NoSuchHost = 2,      // no host by that name is connected now
            NameTaken = 3,       // another host (another key) already has that name
            NewNamesOff = 4,     // the relay is not taking new names
            Busy = 5,            // too many connections, or too many from this address
            HostDidNotAnswer = 6,// the host did not dial back in time
            BadSignature = 7,    // the host's signature did not check out
            TooManyNames = 8,    // the relay holds as many names as it will
            Replaced = 9,        // 4.7, on a control line: the same host key registered again on a newer line
        }

        /// <summary>1 to 40 characters: letters, digits, dash, underscore, dot. Compared without regard to case.</summary>
        public static bool NameOk(string? name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxName) return false;
            foreach (char c in name) if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) return false;
            return true;
        }

        public static string NameKey(string name) => name.ToLowerInvariant();

        /// <summary>What a host signs to register a name: binds the signature to this relay connection's challenge and the name.</summary>
        public static byte[] RegisterSignedBytes(ReadOnlySpan<byte> challenge, string name)
        {
            byte[] label = "Kova relay register 1"u8.ToArray();
            byte[] n = Encoding.UTF8.GetBytes(NameKey(name));
            byte[] all = new byte[label.Length + challenge.Length + n.Length];
            label.CopyTo(all, 0);
            challenge.CopyTo(all.AsSpan(label.Length));
            n.CopyTo(all, label.Length + challenge.Length);
            return all;
        }

        public static void WriteFrame(Stream s, byte type, ReadOnlySpan<byte> body)
        {
            if (body.Length > MaxBody) throw new ArgumentException("frame too big");
            byte[] f = new byte[3 + body.Length];
            f[0] = type;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(1), (ushort)body.Length);
            body.CopyTo(f.AsSpan(3));
            s.Write(f);
            s.Flush();
        }

        /// <summary>Reads one frame, refusing anything over MaxBody. Throws EndOfStreamException if the line closes.</summary>
        public static (byte Type, byte[] Body) ReadFrame(Stream s)
        {
            byte[] head = new byte[3];
            ReadExactly(s, head);
            int n = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1));
            if (n > MaxBody) throw new InvalidDataException("frame too big");
            byte[] body = new byte[n];
            ReadExactly(s, body);
            return (head[0], body);
        }

        public static void ReadExactly(Stream s, Span<byte> buf)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int r = s.Read(buf[got..]);
                if (r <= 0) throw new EndOfStreamException();
                got += r;
            }
        }

        public static byte[] NameBody(string name)
        {
            byte[] n = Encoding.UTF8.GetBytes(name);
            byte[] b = new byte[1 + n.Length];
            b[0] = (byte)n.Length;
            n.CopyTo(b, 1);
            return b;
        }

        /// <summary>Reads a name (length byte + UTF-8) at the start of body; null if it is not a valid name.</summary>
        public static string? ReadName(ReadOnlySpan<byte> body, out int used)
        {
            used = 0;
            if (body.Length < 1) return null;
            int n = body[0];
            if (n < 1 || n > MaxName || body.Length < 1 + n) return null;
            string name;
            try { name = new UTF8Encoding(false, true).GetString(body.Slice(1, n)); }
            catch { return null; }
            used = 1 + n;
            return NameOk(name) ? name : null;
        }
    }
}
