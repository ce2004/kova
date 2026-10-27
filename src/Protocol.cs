using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TailRemote
{
    /// <summary>
    /// The wire format.
    ///
    /// TCP (no Nagle) carries the handshake and the keys: every key must arrive,
    /// in order. UDP carries the audio: a late audio packet is worthless, so it is
    /// never waited for or resent.
    ///
    /// Handshake (every login message ends with a 4-byte damage check, see AddCheck):
    ///   host:   "TRM9" + host nonce (16) + salt (16) + host P-256 public key (65)
    ///   client: "TRM9" + client nonce (16) + client P-256 public key (65)
    ///           + HMAC(session, "C" + host nonce + client nonce)
    ///   host:   0 after a 2 s pause (wrong password, same size), or
    ///           1 + role (1) + session token (8) + HMAC(session, "H" + client nonce + host nonce)
    /// where
    ///   password key = PBKDF2-SHA256(password, the host's salt, 200000 rounds)
    ///   session      = HKDF(ECDH shared secret, salt: password key,
    ///                       info: SHA-256 of the host's whole hello + the client's answer up to its proof)
    /// Both public keys are fresh for every login and thrown away after it, so a
    /// recording of the traffic is no help in guessing the password (nothing in it
    /// can be checked without the shared secret), and a password that leaks later
    /// does not unlock recorded sessions. The salt is random each time hosting
    /// starts, so no work done against one host carries over to another.
    /// The role is Control or Listen, decided by which of the host's two
    /// passwords the client proved. Then each side sends Features: the extras
    /// it supports. Unknown message types are ignored, so newer versions can
    /// add messages without breaking older ones.
    /// The client checks the host's proof too, so a PC that does not know the
    /// password never receives a single key. After that, everything is
    /// encrypted by SecureLink, keyed from the session, so the connection is
    /// private without Tailscale. Both sides send encrypted frames of [type][payload].
    /// </summary>
    internal static class Protocol
    {
        public const int DefaultPort = 47120;
        public static readonly byte[] Magic = "TRMA"u8.ToArray(); // A (4.4): the host signs its hello with its lasting identity, the answer carries a device ticket; 9: file lanes prove the session key (2.3.0)
        /// <summary>A file lane (FileChannel): "TRFA", session token (8), channel id (16), the lane's fresh random value (16), LaneProof (32), padded, then the check.</summary>
        public static readonly byte[] FileMagic = "TRFA"u8.ToArray();
        public const int LaneProofAt = 44;

        /// <summary>
        /// A video lane (the remote screen): laid out like a file lane ("KVD1", session token, 16 zero
        /// bytes, the lane's fresh random value, LaneProof), on keys of its own (VideoPurpose). Then the
        /// host sends pictures, each confirmed (VideoGot) before the next is sent, so they never queue up
        /// on a slow line; how good they are follows the line by itself (VideoSettings.Ladder).
        /// </summary>
        public static readonly byte[] VideoMagic = "KVD1"u8.ToArray();
        // 1 was going to be the viewer's own settings: never use it (the picture adapts by itself)
        public const byte VideoGot = 2;             // viewer: u32 picture number, now on its screen: the next may come
        public const byte VideoAgain = 3;           // viewer: a whole new picture, please
        public const byte VideoPause = 4;           // viewer: u8 1 = nothing to look at (minimized): no pictures taken or sent; 0 = carry on
        public const byte VideoNoH264 = 5;          // viewer: H.264 cannot be shown here (no decoder, or it keeps failing): still pictures only
        public const byte VideoChooseScreen = 6;    // viewer: u8 which of the host's screens to show (0 = the first, the main one); an older host ignores it
        public const byte VideoPicture = 0x81;      // host: u32 number, then an update (ScreenVideo)
        public const byte VideoNote = 0x82;         // host: UTF-8, why there is no picture
        public const byte VideoStill = 0x83;        // host: nothing changed, the line is alive
        public const byte VideoScreen = 0x84;       // host: which screen is shown and how many there are (ScreenChoice), before its first picture and after each choice
        public const int MaxViewers = 8;

        public static string VideoPurpose(ReadOnlySpan<byte> hostValue, ReadOnlySpan<byte> clientValue) =>
            "video " + Convert.ToHexString(hostValue) + Convert.ToHexString(clientValue) + " ";

        // Client to host
        public const byte Key = 1;      // vk u16, scan u16, flags u8 (1 = up, 2 = extended)
        public const byte Ping = 2;     // stamp i64
        public const byte ReleaseAll = 3;
        public const byte RestartPc = 4;     // controller asks the host PC to restart
        public const byte SecureAttention = 5; // controller asks for Ctrl+Alt+Del (service hosts only)
        public const byte FilePace = 7;      // u32 KB/s: how fast the host may send files (the client sets it from the audio ping)
        public const byte AudioQuality = 6;  // u8: the bitrate step the client wants (an index into OpusSteps; 0 = the best)
        public const byte UpdateTo = 8;      // UTF-8 version: the controller asks the host to update TailRemote to it (the newest on GitHub, and only that)
        public const byte ListFolder = 9;    // u32 request id, UTF-8 path ("" = the drives and the usual folders)
        public const byte Fetch = 10;        // UTF-8 paths, one per line: the host sends them to this controller like Send files
        public const byte InfoRequest = 11;  // the host answers with Info
        public const byte SpeedTestRequest = 12; // the host runs an internet speed test and answers with SpeedResult
        public const byte AudioPause = 14; // u8: 1 = send this PC no sound at all (muted while not controlling, or waiting in the background), 0 = send it again
        public const byte Mouse = 15;     // u8 kind (MouseMove: i16 dx, i16 dy; MouseWheel / MouseSideWheel: i16 amount, i16 0). Buttons go as Key (Native.VkLeftButton...)
        public const byte MouseMove = 0, MouseWheel = 1, MouseSideWheel = 2;
        public const byte WakeRequest = 16; // 6-byte network card address: send a Wake-on-LAN packet on the host's network (Wake)
        public const byte NvdaWant = 17;  // u8 flags (NvdaSpeechFlag, NvdaBrailleFlag): what of the host's NVDA this PC wants as text; 0 = nothing
        public const byte NvdaHelperInstall = 19; // the controller asks the host to install (or update) the Kova Helper NVDA add-on there; the answer is an NvdaNote
        public const byte MicState = 18;  // u8 1 = this PC's microphone is on (its packets follow over UDP), 0 = off: the host lets go of the device at once
        public const byte NvdaSpeechFlag = 1, NvdaBrailleFlag = 2;
        public const byte ClipboardRequest = 13; // the controller asks the host to send its clipboard here (text or files), like Send the clipboard but pulled
        // A clipboard pull reads the clipboard and fans a transfer out to every controller; the host
        // window's message pump drowns under the progress updates if the controller mashes or holds
        // Ctrl+Shift+B. Both ends gate on this: the controller sends at most one pull this often, and
        // the host coalesces any that still arrive closer together into one read + send.
        public const long ClipboardPullThrottleMs = 500;
        // Either way
        public const byte Features = 0x40;   // u32 flags
        // 0x41 was clipboard text (up to 1.8.11; it now goes over the file lanes): never reuse it
        // Host to client
        public const byte Pong = 0x81;  // stamp i64
        public const byte Message = 0x82; // UTF-8 text (the frame gives the length)
        public const byte FolderList = 0x85; // u32 request id, UTF-8 lines: one per entry, tab-separated: D name, F name bytes modified-ticks, or E problem
        public const byte Info = 0x86;       // UTF-8 lines about the host PC, for Remote PC info
        public const byte SpeedResult = 0x87; // UTF-8 lines: the host's internet speed test
        public const byte WakeInfo = 0x88;    // UTF-8 (Wake.Describe): the host's network cards, kept with the saved PC to wake it later
        public const byte HealthAlert = 0x89; // UTF-8 sentence: something about the host PC worth saying (HealthWatch)
        public const byte MicNote = 0x8A;     // UTF-8 sentence about this PC's microphone on the host (where it plays, or why it cannot)
        public const byte MicQuality = 0x8B;  // u8: the bitrate step the host wants the microphone sent at (it follows how the microphone arrives)
        // 0x8C and 0x8D were NVDA's Speech Viewer text and braille (4.3): never reuse them.
        public const byte NvdaNote = 0x8E;    // UTF-8 sentence about the host's NVDA text (installing the Kova Helper there, and how it went)
        public const byte NvdaEventMsg = 0x90; // one thing the host's NVDA did (NvdaEvent.Encode: speech with pitch and capitals, a cut-off, a beep, a sound, braille), from the Kova Helper add-on
        public const byte NvdaAvailable = 0x91; // u8 1 = the Kova Helper add-on is talking to the host, so NVDA comes as text; 0 = it is not (only the sound carries NVDA); NvdaSecureScreen = the host shows a secure screen (sign-in, lock, UAC), where NVDA runs without add-ons: only the sound carries it (4.6.2 and older take it as 0)
        public const byte NvdaSecureScreen = 2;
        public const byte DeviceTicket = 0x8F; // 32 bytes (HostIdentity.NewTicket): shown in later logins, it lets this PC in while strangers are being turned away
        public const byte Leaving = 0x83; // u8 why (Leaving*), then UTF-8 detail (the new version when updating): sent just before the host closes on purpose
        // 0x84 was CaptureBurst (1.7.x): no longer sent, never reuse it

        // Why the host is going away, so the controlling PC can say so instead of "forcibly closed"
        public const byte LeavingUpdating = 1, LeavingRestarting = 2, LeavingStopped = 3, LeavingShutdown = 4;

        // UDP
        public const byte UdpHello = 0xA0;  // token[8] stamp[8] mac[16] (SecureLink.SignHello), client to host, every second
        public const int UdpHelloBytes = 1 + 8 + 8 + SecureLink.HelloMacBytes;
        public const int UdpPongBytes = 1 + 8 + SecureLink.HelloMacBytes; // the hello's stamp, signed by the host the same way (SignHello)
        public const byte UdpPong = 0xA8;   // stamp[8] mac[16], host to client: the hello's stamp straight back, so the audio path's own ping is measured
        // 0xA1 to 0xA5 were the lossless formats (up to 1.7): never reuse them
        public const byte UdpOpus = 0xA6; // u32 sequence (5 ms ticks), then sealed: u8 ticks, Opus packet
        // The controller's microphone, controller to host: token (8, so the host knows whose), u32 sequence (5 ms ticks, from 0
        // per connection), then sealed with the microphone's own key: u8 ticks, Opus packet. Never from a listen-only connection.
        public const byte UdpMic = 0xA7;
        public const int MicHeadBytes = 1 + 8 + 4;
        public const int MinMicPacketBytes = MicHeadBytes + 1 + SecureLink.TagSize;
        /// <summary>The microphone's best step (128 kbit/s in 5 ms packets: low delay, and plenty for a voice) and where it starts.</summary>
        public const int MicBestStep = 4;

        // Roles
        public const byte RoleControl = 1, RoleListen = 2;

        // Feature flags
        public const uint FeatureClipboard = 1;
        public const uint FeatureFiles = 2;
        // 4, 32 and 64 were the lossless formats (up to 1.7)
        public const uint FeatureRestart = 8;
        public const uint FeatureSecureAttention = 16; // only a host running as the service
        public const uint FeatureRemoteTools = 128;    // UpdateTo, ListFolder, Fetch, InfoRequest
        // Send files into a folder the controller chose (FileChannel's KindTo). An older host ignores
        // an offer of a kind it does not know without answering, so the controller only sends one to
        // a host that says it takes them.
        public const uint FeatureSendTo = 256;
        public const uint FeatureVideo = 512; // the remote screen (video lanes)
        public const uint FeatureMouse = 1024; // Mouse messages and mouse buttons as keys
        public const uint FeatureWake = 2048;  // WakeRequest
        public const uint FeatureMic = 4096;   // takes the controller's microphone (UdpMic, MicState)
        public const uint FeatureNvda = 8192;  // sends its NVDA's speech and braille as text (NvdaWant)
        public const uint FeatureNvdaSecureScreen = 16384; // a controller that understands NvdaAvailable = NvdaSecureScreen (older ones are never sent it)

        /// <summary>
        /// The bitrate steps, best first. 5 ms packets down to 128 kbit/s (Opus's
        /// low-delay mode, 2.5 ms of look-ahead); below that, longer packets, because
        /// each one carries about 110 bytes of headers on Tailscale (176 kbit/s at
        /// 5 ms), which a slow connection cannot afford. Tuned for music above
        /// 16 kbit/s, for speech from there down.
        /// </summary>
        public static readonly (int Kbps, int Ms)[] OpusSteps =
        {
            (510, 5), (384, 5), (256, 5), (192, 5), (128, 5), (96, 10), (64, 10),
            (48, 20), (32, 20), (24, 20), (16, 40), (12, 40), (8, 60), (6, 60),
        };

        /// <summary>Headers on every audio packet over Tailscale: IP, UDP, WireGuard, inner IP and UDP, ours.</summary>
        public const int PacketOverheadBytes = 110;

        /// <summary>What a step really costs on the wire, headers included.</summary>
        public static int WireKbps(int step) => OpusSteps[step].Kbps + 1000 / OpusSteps[step].Ms * PacketOverheadBytes * 8 / 1000;

        /// <summary>What this version supports, sent to the other side after login.</summary>
        public const uint OurFeatures = FeatureClipboard | FeatureFiles | FeatureRestart | FeatureRemoteTools | FeatureSendTo | FeatureVideo | FeatureMouse | FeatureWake | FeatureMic | FeatureNvda | FeatureNvdaSecureScreen;


        // ---- The login, damage-proof ----
        // Before the keys exist nothing is encrypted, so a damaged byte (Clumsy's tamper,
        // a bad link) used to look exactly like a wrong password: the client gave up and
        // the host counted it toward blocking the address. Every login message now ends
        // with a 4-byte check of the rest. A wrong check means damage: never counted as a
        // wrong password, and the client simply tries again.
        public const int SaltBytes = 16, PublicKeyBytes = 65; // P-256, uncompressed: 4, X, Y
        public const int HelloSaltAt = 20, HelloPublicAt = 36;
        public const int HelloIdentityAt = HelloPublicAt + PublicKeyBytes, HelloSignatureAt = HelloIdentityAt + HostIdentity.PublicBytes;
        // magic 4, host nonce 16, salt 16, key exchange public key 65, the host's lasting identity key 65, its signature of all that 64, check 4
        public const int HelloBytes = HelloSignatureAt + HostIdentity.SignatureBytes + 4;
        public const int AnswerPublicAt = 20, AnswerTicketAt = AnswerPublicAt + PublicKeyBytes, AnswerProofAt = AnswerTicketAt + HostIdentity.TicketBytes;
        // magic 4, client nonce 16, public key 65, device ticket 32 (zeros if none), proof 32, check 4 (file lanes: magic, token 8, channel 16, lane value 16, LaneProof 32, zeros)
        public const int AnswerBytes = AnswerProofAt + 32 + 4;

        /// <summary>The first byte of a reply that turns a login away before any password is checked: too many wrong passwords from there for now.</summary>
        public const byte ReplyTurnedAway = 2;

        /// <summary>
        /// What the device ticket is masked with in the answer: from this login's own key exchange secret (which a relay or
        /// anyone watching cannot work out, the host's key exchange key being signed), so the ticket never travels readable.
        /// </summary>
        public static byte[] TicketPad(byte[] shared, ReadOnlySpan<byte> hostNonce, ReadOnlySpan<byte> clientNonce)
        {
            byte[] salt = new byte[hostNonce.Length + clientNonce.Length];
            hostNonce.CopyTo(salt);
            clientNonce.CopyTo(salt.AsSpan(hostNonce.Length));
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, HostIdentity.TicketBytes, salt, "Kova ticket A"u8.ToArray());
        }

        /// <summary>What the host's identity signs in every hello: everything before the signature, labelled.</summary>
        public static byte[] HelloSigned(ReadOnlySpan<byte> hello)
        {
            byte[] label = "Kova host hello A"u8.ToArray();
            byte[] all = new byte[label.Length + HelloSignatureAt];
            label.CopyTo(all, 0);
            hello[..HelloSignatureAt].CopyTo(all.AsSpan(label.Length));
            return all;
        }

        /// <summary>Fills a hello: magic, nonce, salt, key exchange key, identity, signature, check.</summary>
        public static byte[] MakeHello(byte[] nonce, byte[] salt, ECDiffieHellman exchange, HostIdentity identity)
        {
            byte[] hello = new byte[HelloBytes];
            Magic.CopyTo(hello, 0);
            nonce.CopyTo(hello, 4);
            salt.CopyTo(hello, HelloSaltAt);
            WritePublic(exchange, hello.AsSpan(HelloPublicAt, PublicKeyBytes));
            identity.Public.CopyTo(hello, HelloIdentityAt);
            identity.Sign(HelloSigned(hello)).CopyTo(hello, HelloSignatureAt);
            AddCheck(hello);
            return hello;
        }

        /// <summary>Whether a hello carries a valid signature by the identity in it (checked before anything is proven to it).</summary>
        public static bool HelloSignedOk(byte[] hello) =>
            hello.Length == HelloBytes && HostIdentity.Verify(hello.AsSpan(HelloIdentityAt, HostIdentity.PublicBytes), HelloSigned(hello), hello.AsSpan(HelloSignatureAt, HostIdentity.SignatureBytes));
        public const int ReplyBytes = 46;  // ok 1, role 1, token 8, host proof 32, check 4 (a refusal is the same size)

        /// <summary>Fills the last 4 bytes with a check of everything before them.</summary>
        public static void AddCheck(byte[] message)
        {
            Span<byte> h = stackalloc byte[32];
            SHA256.HashData(message.AsSpan(0, message.Length - 4), h);
            h[..4].CopyTo(message.AsSpan(message.Length - 4));
        }

        /// <summary>True if the last 4 bytes match the rest: the message was not damaged on the way.</summary>
        public static bool CheckOk(byte[] message)
        {
            Span<byte> h = stackalloc byte[32];
            SHA256.HashData(message.AsSpan(0, message.Length - 4), h);
            return h[..4].SequenceEqual(message.AsSpan(message.Length - 4));
        }

        /// <summary>The message a client gets when its login was damaged on the way: it tries again.</summary>
        public const string DamagedLogin = "The connection damaged the login on the way.";

        /// <summary>The features, then this copy's version (so the other side knows when it is older).</summary>
        public static byte[] FeaturesMessage(uint extra = 0)
        {
            byte[] v = Encoding.UTF8.GetBytes(Updater.Current.ToString());
            byte[] m = new byte[5 + v.Length];
            m[0] = Features;
            BitConverter.TryWriteBytes(m.AsSpan(1), OurFeatures | extra);
            v.CopyTo(m, 5);
            return m;
        }

        /// <summary>The version at the end of a Features message, or null (an older copy sends none).</summary>
        public static Version? FeaturesVersion(byte[] m) =>
            m.Length > 5 && Version.TryParse(Encoding.UTF8.GetString(m, 5, m.Length - 5), out var v) ? v : null;

        public static byte[] TextMessage(byte type, string text)
        {
            byte[] utf = Encoding.UTF8.GetBytes(text);
            byte[] m = new byte[1 + utf.Length];
            m[0] = type;
            utf.CopyTo(m, 1);
            return m;
        }

        public const int AudioRate = 48000;  // Opus's own rate, and most sound devices'
        public const int TickFrames = 240;   // 5 ms: the unit packets are counted in
        public const double TickMs = 5;
        public const int MaxOpusBytes = 1275; // Opus's largest packet; at 510 kbit/s and 5 ms it is 319, under Tailscale's 1280
        public const int MinAudioPacketBytes = 5 + 1 + SecureLink.TagSize;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Keys = new();
        private const int MaxCachedKeys = 256; // a modest bound; only local password guesses ever add to this

        /// <summary>
        /// Slow on purpose (200,000 rounds), so it is worked out once per password and host salt
        /// and kept. The salt is the host's own (random each time it starts hosting): with one
        /// fixed salt for every install, a table of guesses built once worked against them all.
        /// </summary>
        public static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt)
        {
            // Simple fixed bound instead of unbounded growth: a long-lived process that has had many
            // different passwords (or hosts' salts) at it should not keep every derived key forever.
            // Exceeding the cap just starts the cache over; a race here only costs an extra (slow, by
            // design) derivation, never correctness. The salt is fixed-length hex, so the two parts
            // of the cache key can never run into each other.
            if (Keys.Count >= MaxCachedKeys) Keys.Clear();
            byte[] s = salt.ToArray();
            return Keys.GetOrAdd(Convert.ToHexString(s) + ":" + password, _ =>
                Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), s, 200_000, HashAlgorithmName.SHA256, 32));
        }

        /// <summary>A fresh key pair for one login's key exchange; disposed as soon as the session key exists.</summary>
        public static ECDiffieHellman NewExchange() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        /// <summary>Writes this side's public key (uncompressed: 4, X, Y) into the login message.</summary>
        public static void WritePublic(ECDiffieHellman mine, Span<byte> to)
        {
            var q = mine.ExportParameters(false).Q;
            to[0] = 4;
            q.X.AsSpan().CopyTo(to[1..33]);
            q.Y.AsSpan().CopyTo(to[33..65]);
        }

        /// <summary>The shared secret with the other side's public key, or null if it is not a valid P-256 point.</summary>
        public static byte[]? SharedSecret(ECDiffieHellman mine, ReadOnlySpan<byte> theirs)
        {
            if (theirs.Length != PublicKeyBytes || theirs[0] != 4) return null;
            try
            {
                // Importing checks the point is on the curve, so a forged key cannot pull out a weak secret.
                using var other = ECDiffieHellman.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = theirs[1..33].ToArray(), Y = theirs[33..65].ToArray() },
                });
                return mine.DeriveRawSecretAgreement(other.PublicKey);
            }
            // Windows reports a point that is not on the curve as PlatformNotSupportedException, not
            // CryptographicException: caught only as the latter, a forged key ended the login without
            // the wrong-password pause, and a client heard "not valid for this platform".
            catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException or ArgumentException) { return null; }
        }

        /// <summary>
        /// The login's session key: the key exchange's secret, salted with the password key, bound
        /// to every byte both sides sent before the proofs (the host's hello, the client's answer up
        /// to its proof), so nothing in the login can be swapped without the proofs failing. The
        /// proofs and every SecureLink key come from this, never from the password key alone.
        /// </summary>
        public static byte[] SessionKey(byte[] passwordKey, byte[] shared, ReadOnlySpan<byte> hello, ReadOnlySpan<byte> answerHead)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            h.AppendData("Kova login 8 "u8);
            h.AppendData(hello);
            h.AppendData(answerHead);
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, passwordKey, h.GetHashAndReset());
        }

        /// <summary>
        /// A file lane's proof that it comes from the PC that logged in: the session token travels in
        /// the clear (in every UDP hello), and a lane opened on it alone could stop that PC's transfers.
        /// Over this lane's own hello nonce, so a recorded one is no use on another lane.
        /// </summary>
        public static byte[] LaneProof(byte[] sessionKey, ReadOnlySpan<byte> laneHelloNonce, ReadOnlySpan<byte> channel, ReadOnlySpan<byte> laneValue)
        {
            byte[] msg = new byte[1 + laneHelloNonce.Length + channel.Length + laneValue.Length];
            msg[0] = (byte)'L';
            laneHelloNonce.CopyTo(msg.AsSpan(1));
            channel.CopyTo(msg.AsSpan(1 + laneHelloNonce.Length));
            laneValue.CopyTo(msg.AsSpan(1 + laneHelloNonce.Length + channel.Length));
            return HMACSHA256.HashData(sessionKey, msg);
        }

        public static byte[] Proof(byte[] key, char side, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            byte[] msg = new byte[1 + first.Length + second.Length];
            msg[0] = (byte)side;
            first.CopyTo(msg.AsSpan(1));
            second.CopyTo(msg.AsSpan(1 + first.Length));
            return HMACSHA256.HashData(key, msg);
        }

        public static void ReadExactly(Stream s, Span<byte> buf)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int n = s.Read(buf[got..]);
                if (n <= 0) throw new EndOfStreamException("The connection closed.");
                got += n;
            }
        }

        public static void SendMessage(SecureLink link, Stream s, string text)
        {
            byte[] utf = Encoding.UTF8.GetBytes(text);
            byte[] f = new byte[1 + Math.Min(utf.Length, 4000)];
            f[0] = Message;
            utf.AsSpan(0, f.Length - 1).CopyTo(f.AsSpan(1));
            link.Send(s, f);
        }

        /// <summary>
        /// Why a port cannot be used, or null if it is fine. Refuses ports that
        /// other remote and network programs depend on, even when they happen to
        /// be free on this PC, so TailRemote never takes over someone else's.
        /// Whether the port is free right now is checked when hosting starts.
        /// </summary>
        public static string? PortProblem(string text, out int port)
        {
            if (!int.TryParse(text, out port) || port < 1 || port > 65535)
                return "The port must be a number from 1024 to 65535. The usual one is " + DefaultPort + ".";
            if (port < 1024)
                return "Port " + port + " is reserved for Windows services. Choose one from 1024 to 65535, such as " + DefaultPort + ".";
            string? owner = port switch
            {
                1900 => "Windows device discovery",
                3389 => "Remote Desktop",
                5353 => "Windows network name lookups",
                5355 => "Windows network name lookups",
                5900 => "VNC",
                6837 => "NVDA Remote",
                41641 => "a VPN program",
                _ => null,
            };
            return owner == null ? null : "Port " + port + " belongs to " + owner + ". Choose another, such as " + DefaultPort + ".";
        }

        /// <summary>Whether this PC has a Tailscale address, i.e. Tailscale is up and signed in.</summary>
        public static bool LocalTailscaleUp()
        {
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var a in nic.GetIPProperties().UnicastAddresses)
                        if (!IPAddress.IsLoopback(a.Address) && IsTailscale(a.Address)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>True for Tailscale's 100.64.0.0/10 and fd7a:115c:a1e0::/48, and loopback.</summary>
        public static bool IsTailscale(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            byte[] b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 100 && (b[1] & 0xC0) == 64;
            return b.Length == 16 && b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
        }
    }
}
