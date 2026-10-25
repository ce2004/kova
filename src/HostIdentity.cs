using System;
using System.Security.Cryptography;

namespace TailRemote
{
    /// <summary>
    /// A host's lasting identity (4.4): an ECDSA P-256 key made once and kept (DPAPI-protected in the
    /// settings, or in the service's settings). It signs every login hello, so a controlling PC can tell
    /// it is talking to the same PC as last time before it proves anything. Without it, anyone who
    /// answered in place of the host (on the Wi-Fi, a wrong address, a fake relay) got the controller's
    /// proof and could try password guesses against it offline. The controller remembers the key per PC
    /// on first contact and refuses to log in to a different one unless its user says so.
    ///
    /// The same key registers the host's name on a Kova Relay (the relay pins names to keys), and its
    /// private part also makes this host's device tickets: a PC that has logged in is handed a ticket,
    /// and showing it later lets it in even while strangers are being turned away for guessing.
    /// </summary>
    internal sealed class HostIdentity : IDisposable
    {
        public const int PublicBytes = 65, SignatureBytes = 64, TicketBytes = 32;

        private readonly ECDsa _key;
        private readonly byte[] _ticketKey;

        /// <summary>The public key as it travels in the hello: 4, X, Y.</summary>
        public byte[] Public { get; }

        private HostIdentity(ECDsa key)
        {
            _key = key;
            var p = key.ExportParameters(true);
            Public = new byte[PublicBytes];
            Public[0] = 4;
            p.Q.X!.CopyTo(Public, 1);
            p.Q.Y!.CopyTo(Public, 33);
            // Tickets are checked with a key only this host can make, and that stays the same as long as
            // the identity does, so tickets handed out before a restart still work after it.
            _ticketKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, p.D!, 32, null, "Kova device tickets 1"u8.ToArray());
            CryptographicOperations.ZeroMemory(p.D);
        }

        public static HostIdentity Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

        /// <summary>From a saved private key (PKCS#8), or null if it cannot be read as a P-256 key.</summary>
        public static HostIdentity? FromPkcs8(byte[]? pkcs8)
        {
            if (pkcs8 == null || pkcs8.Length == 0) return null;
            try
            {
                var k = ECDsa.Create();
                k.ImportPkcs8PrivateKey(pkcs8, out _);
                // P-256 exactly: another 256-bit curve (secp256k1, brainpool) is not what every check here assumes.
                var oid = k.ExportParameters(false).Curve.Oid;
                bool p256 = oid?.Value == "1.2.840.10045.3.1.7" || oid?.FriendlyName is "nistP256" or "ECDSA_P256" or "secP256r1";
                if (k.KeySize != 256 || !p256) { k.Dispose(); return null; }
                return new HostIdentity(k);
            }
            catch { return null; }
        }

        public byte[] ExportPkcs8() => _key.ExportPkcs8PrivateKey();

        /// <summary>For the relay: the public key in the standard form it pins.</summary>
        public byte[] SubjectPublicKeyInfo() => _key.ExportSubjectPublicKeyInfo();

        public byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        /// <summary>Whether sig is this public key's signature of data. False for anything malformed, never an exception.</summary>
        public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> sig)
        {
            if (publicKey.Length != PublicBytes || publicKey[0] != 4 || sig.Length != SignatureBytes) return false;
            try
            {
                // Importing checks the point is on the curve.
                using var k = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = publicKey[1..33].ToArray(), Y = publicKey[33..65].ToArray() },
                });
                return k.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException or ArgumentException) { return false; }
        }

        /// <summary>A short, readable fingerprint of a public key, for telling a person which key changed.</summary>
        public static string Fingerprint(ReadOnlySpan<byte> publicKey)
        {
            string hex = Convert.ToHexString(SHA256.HashData(publicKey))[..16];
            return string.Join(" ", hex[..4], hex[4..8], hex[8..12], hex[12..16]);
        }

        /// <summary>A new device ticket: 16 random bytes, then the first 16 bytes of their MAC under the ticket key.</summary>
        public byte[] NewTicket()
        {
            byte[] t = new byte[TicketBytes];
            RandomNumberGenerator.Fill(t.AsSpan(0, 16));
            HMACSHA256.HashData(_ticketKey, t.AsSpan(0, 16)).AsSpan(0, 16).CopyTo(t.AsSpan(16));
            return t;
        }

        /// <summary>Whether this is a ticket this host made. An all-zero ticket (none) is never valid.</summary>
        public bool TicketOk(ReadOnlySpan<byte> ticket)
        {
            if (ticket.Length != TicketBytes) return false;
            Span<byte> mac = stackalloc byte[32];
            HMACSHA256.HashData(_ticketKey, ticket[..16], mac);
            return CryptographicOperations.FixedTimeEquals(mac[..16], ticket[16..]);
        }

        public void Dispose() => _key.Dispose();
    }
}

namespace TailRemote
{
    /// <summary>What a controlling PC remembers about the hosts it connects to: each one's lasting identity and the device ticket it gave.</summary>
    internal interface IHostTrust
    {
        /// <summary>The identity remembered for this PC (null: never logged in to it) and its ticket (null: none).</summary>
        (byte[]? Identity, byte[]? Ticket) Known(string pc);

        /// <summary>Remembers this PC's identity, and its ticket when one is given (null keeps the one held, if it is for the same identity).</summary>
        void Remember(string pc, byte[] identity, byte[]? ticket);
    }

    /// <summary>
    /// The PC answered with a different lasting identity than the one this PC knows for it, and nothing was
    /// proven to it. Either Kova was set up again there (a new Windows, its settings wiped), or something is
    /// pretending to be it. Only the person can say which.
    /// </summary>
    internal sealed class HostChangedException : InvalidOperationException
    {
        public string Pc { get; }
        public byte[] NewIdentity { get; }

        public HostChangedException(string pc, string shown, byte[] known, byte[] now)
            : base(shown + " answered with a different identity than the one this PC knows for it, so nothing was sent to it. " +
                   "This happens if Kova was set up again on that PC, or if something is pretending to be it. " +
                   "It was " + HostIdentity.Fingerprint(known) + " and is now " + HostIdentity.Fingerprint(now) + ".")
        {
            Pc = pc;
            NewIdentity = now;
        }
    }
}
