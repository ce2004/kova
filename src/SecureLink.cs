using System;
using System.IO;
using System.Security.Cryptography;

namespace TailRemote
{
    /// <summary>
    /// Encrypts everything after the password check, so a connection is private
    /// with or without Tailscale.
    ///
    /// Each direction and each transport gets its own AES-GCM key, derived with
    /// HKDF from the login's session key (the key exchange salted with the
    /// password key, see Protocol) and both sides' login nonces, so keys are new
    /// for every connection. TCP frames use a counter as the nonce (TCP keeps
    /// order). Audio packets use their sequence number, which never repeats in
    /// a session; the packet type and number travel in the clear but are
    /// authenticated. A frame that fails to decrypt ends the connection; an
    /// audio packet that fails is dropped.
    /// </summary>
    internal sealed class SecureLink : IDisposable
    {
        public const int TagSize = 16;

        private readonly AesGcm _send, _recv, _audio, _mic;
        private readonly object _sendLock = new();
        private ulong _sendCounter, _recvCounter;
        private readonly int _maxReceive, _maxSend;
        private readonly byte[] _recvHead = new byte[8];
        private const int HeadBytes = 8; // u32 length, then the length with every bit flipped

        /// <summary>purpose keeps a second connection (files) on keys of its own.</summary>
        public SecureLink(byte[] key, byte[] hostNonce, byte[] clientNonce, bool isHost, string purpose = "", int maxReceive = MaxMessage, int maxSend = MaxMessage)
        {
            _maxReceive = maxReceive;
            _maxSend = maxSend;
            byte[] salt = new byte[32];
            hostNonce.CopyTo(salt, 0);
            clientNonce.CopyTo(salt, 16);
            byte[] Derive(string info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, System.Text.Encoding.ASCII.GetBytes(purpose + info));
            byte[] toHost = Derive("Kova tcp to host"), toClient = Derive("Kova tcp to client");
            _send = new AesGcm(isHost ? toClient : toHost, TagSize);
            _recv = new AesGcm(isHost ? toHost : toClient, TagSize);
            _audio = new AesGcm(Derive("Kova audio"), TagSize);
            // The microphone goes the other way, numbered from 0 per connection: under the sound's key its numbers
            // would meet the host's own, and a nonce used twice under AES-GCM gives the key away.
            _mic = new AesGcm(Derive("Kova microphone"), TagSize);
            _helloKey = Derive("Kova udp hello");
        }

        // The UDP hello tells the host where to send the sound. Its token used to be enough, and
        // the token travels in the clear, so anyone who saw one could send the audio elsewhere.
        private readonly byte[] _helloKey;
        public const int HelloMacBytes = 16;

        /// <summary>Fills the last HelloMacBytes of a UDP hello with a MAC of everything before them.</summary>
        public void SignHello(byte[] hello)
        {
            Span<byte> mac = stackalloc byte[32];
            HMACSHA256.HashData(_helloKey, hello.AsSpan(0, hello.Length - HelloMacBytes), mac);
            mac[..HelloMacBytes].CopyTo(hello.AsSpan(hello.Length - HelloMacBytes));
        }

        /// <summary>True if a UDP hello's MAC is right: it came from the PC that logged in on this link.</summary>
        public bool HelloOk(ReadOnlySpan<byte> hello)
        {
            if (hello.Length <= HelloMacBytes) return false;
            Span<byte> mac = stackalloc byte[32];
            HMACSHA256.HashData(_helloKey, hello[..^HelloMacBytes], mac);
            return CryptographicOperations.FixedTimeEquals(mac[..HelloMacBytes], hello[^HelloMacBytes..]);
        }

        public void Dispose()
        {
            _send.Dispose();
            _recv.Dispose();
            _audio.Dispose();
            _mic.Dispose();
        }

        private static void CounterNonce(Span<byte> nonce, ulong counter)
        {
            nonce.Clear();
            BitConverter.TryWriteBytes(nonce[4..], counter);
        }

        public const int MaxMessage = 8 * 1024 * 1024;
        // The most the remote screen's line may send in one message (a whole lossless picture of a big
        // screen). Every other link sends at most MaxMessage, as the other side receives no more.
        public const int MaxSend = 64 * 1024 * 1024;

        /// <summary>Sends one message as [u32 length][ciphertext][tag]. Safe from any thread.</summary>
        public void Send(Stream s, ReadOnlySpan<byte> message)
        {
            if (message.Length > _maxSend) throw new ArgumentException("Message too large.");
            byte[] frame = Frame(message.Length);
            Span<byte> nonce = stackalloc byte[12];
            lock (_sendLock)
            {
                if (_broken) throw new IOException("The connection was cut part-way through a message.");
                CounterNonce(nonce, _sendCounter++);
                _send.Encrypt(nonce, message, frame.AsSpan(HeadBytes, message.Length), frame.AsSpan(HeadBytes + message.Length, TagSize));
                try { s.Write(frame); }
                catch
                {
                    // A write that failed (a send timeout, often) may have sent part of the frame: the next
                    // one from another thread would land after it and the other PC would call the line
                    // damaged. The link ends here instead, and both PCs reconnect the usual way.
                    _broken = true;
                    try { if (s is System.Net.Sockets.NetworkStream ns) ns.Socket.Close(); } catch { }
                    throw;
                }
            }
        }

        private bool _broken; // under _sendLock

        /// <summary>
        /// Encrypts one message into a ready-to-send frame, without sending it. The
        /// frames must then be written in the order they were sealed, and Send must
        /// not be used on this link at the same time. (Files: one thread seals while
        /// another writes, so the network never waits for the encryption.)
        /// </summary>
        public byte[] Seal(ReadOnlySpan<byte> message)
        {
            if (message.Length > _maxSend) throw new ArgumentException("Message too large.");
            byte[] frame = Frame(message.Length);
            Span<byte> nonce = stackalloc byte[12];
            lock (_sendLock)
            {
                CounterNonce(nonce, _sendCounter++);
                _send.Encrypt(nonce, message, frame.AsSpan(HeadBytes, message.Length), frame.AsSpan(HeadBytes + message.Length, TagSize));
            }
            return frame;
        }

        private static byte[] Frame(int length)
        {
            if (length > MaxSend) throw new ArgumentException("Message too large.");
            byte[] frame = new byte[HeadBytes + length + TagSize];
            BitConverter.TryWriteBytes(frame.AsSpan(0, 4), length);
            BitConverter.TryWriteBytes(frame.AsSpan(4, 4), ~length);
            return frame;
        }

        /// <summary>
        /// Send, reusing the caller's frame buffer (grown when needed) instead of a new one per
        /// message. For file lanes: a new megabyte for every piece, sent and received, left .NET
        /// holding hundreds of megabytes after a big transfer.
        /// </summary>
        public void Send(Stream s, ReadOnlySpan<byte> message, ref byte[]? frame)
        {
            if (message.Length > _maxSend) throw new ArgumentException("Message too large.");
            int size = HeadBytes + message.Length + TagSize;
            if (frame == null || frame.Length < size) frame = new byte[Math.Max(size, 64 << 10)];
            BitConverter.TryWriteBytes(frame.AsSpan(0, 4), message.Length);
            BitConverter.TryWriteBytes(frame.AsSpan(4, 4), ~message.Length);
            Span<byte> nonce = stackalloc byte[12];
            lock (_sendLock)
            {
                CounterNonce(nonce, _sendCounter++);
                _send.Encrypt(nonce, message, frame.AsSpan(HeadBytes, message.Length), frame.AsSpan(HeadBytes + message.Length, TagSize));
                s.Write(frame, 0, size);
            }
        }

        /// <summary>Reads and decrypts one message into the caller's buffer (grown when needed), in place; returns its length. Only one thread may receive.</summary>
        public int Receive(Stream s, ref byte[]? buffer)
        {
            Protocol.ReadExactly(s, _recvHead);
            int len = BitConverter.ToInt32(_recvHead);
            if (BitConverter.ToInt32(_recvHead, 4) != ~len || len < 0 || len > _maxReceive)
                throw new InvalidOperationException("The connection damaged data on the way, so it reconnected.");
            if (buffer == null || buffer.Length < len + TagSize) buffer = new byte[Math.Max(len + TagSize, 64 << 10)];
            Protocol.ReadExactly(s, buffer.AsSpan(0, len + TagSize));
            Span<byte> nonce = stackalloc byte[12];
            CounterNonce(nonce, _recvCounter++);
            var body = buffer.AsSpan(0, len);
            try { _recv.Decrypt(nonce, body, buffer.AsSpan(len, TagSize), body); }
            catch (CryptographicException) { throw new InvalidOperationException("The connection damaged data on the way, so it reconnected."); }
            return len;
        }

        /// <summary>Reads and decrypts one message. Only one thread may receive.</summary>
        public byte[] Receive(Stream s)
        {
            Protocol.ReadExactly(s, _recvHead);
            int len = BitConverter.ToInt32(_recvHead);
            if (BitConverter.ToInt32(_recvHead, 4) != ~len || len < 0 || len > _maxReceive)
                throw new InvalidOperationException("The connection damaged data on the way, so it reconnected.");
            byte[] body = new byte[len + TagSize];
            Protocol.ReadExactly(s, body);
            byte[] message = new byte[len];
            Span<byte> nonce = stackalloc byte[12];
            CounterNonce(nonce, _recvCounter++);
            try { _recv.Decrypt(nonce, body.AsSpan(0, len), body.AsSpan(len, TagSize), message); }
            catch (CryptographicException) { throw new InvalidOperationException("The connection damaged data on the way, so it reconnected."); }
            return message;
        }

        // The audio nonce is the wire sequence number (a u32), and the audio key never rotates within
        // a connection, so reusing a nonce under AES-GCM would be catastrophic. The numbers come from
        // the host's capture counter, which runs for as long as it hosts, so the limit is how far this
        // connection has gone from its own first packet: a limit on the number itself silenced every
        // connection, new ones too, after about 233 days of hosting. The host ends a connection that
        // nears it (AudioWornOut), and it reconnects with new keys.
        private const uint AudioSeqSpan = 0xF0000000;
        private uint _audioFirst;
        private bool _audioStarted;

        /// <summary>True when this connection's audio has come so far that it needs new keys. Capture thread only.</summary>
        public bool AudioWornOut(uint seq) => _audioStarted && seq - _audioFirst >= AudioSeqSpan;

        /// <summary>Seals an audio packet in place: [type][u32 seq][payload][tag]. Capture thread only.</summary>
        public void SealAudio(byte[] packet, int payloadLength)
        {
            uint seq = BitConverter.ToUInt32(packet, 1);
            if (!_audioStarted) { _audioFirst = seq; _audioStarted = true; }
            else if (seq - _audioFirst >= AudioSeqSpan) throw new InvalidOperationException("Audio key must be refreshed before the nonce counter wraps.");
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            packet.AsSpan(1, 4).CopyTo(nonce);
            var payload = packet.AsSpan(5, payloadLength);
            _audio.Encrypt(nonce, payload, payload, packet.AsSpan(5 + payloadLength, TagSize), packet.AsSpan(0, 5));
        }

        /// <summary>Opens an audio packet in place; false if it was forged or damaged. Receive thread only.</summary>
        public bool OpenAudio(byte[] packet, int length)
        {
            int payloadLength = length - 5 - TagSize;
            if (payloadLength < 0) return false;
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            packet.AsSpan(1, 4).CopyTo(nonce);
            var payload = packet.AsSpan(5, payloadLength);
            try { _audio.Decrypt(nonce, payload, packet.AsSpan(5 + payloadLength, TagSize), payload, packet.AsSpan(0, 5)); return true; }
            catch (CryptographicException) { return false; }
        }

        /// <summary>How far a connection's microphone may count before it must reconnect for new keys (its numbers start at 0).</summary>
        public const uint MicSeqSpan = 0xF0000000;

        /// <summary>
        /// Seals a microphone packet in place: [type][token 8][u32 seq][payload][tag], the head authenticated. Its
        /// number is the nonce: the caller never seals two with one number (they only count up, from 0, and the
        /// connection ends before MicSeqSpan). Microphone thread only.
        /// </summary>
        public void SealMic(byte[] packet, int payloadLength)
        {
            uint seq = BitConverter.ToUInt32(packet, 9);
            if (seq >= MicSeqSpan) throw new InvalidOperationException("Microphone key must be refreshed before the nonce counter wraps.");
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            packet.AsSpan(9, 4).CopyTo(nonce);
            var payload = packet.AsSpan(Protocol.MicHeadBytes, payloadLength);
            _mic.Encrypt(nonce, payload, payload, packet.AsSpan(Protocol.MicHeadBytes + payloadLength, TagSize), packet.AsSpan(0, Protocol.MicHeadBytes));
        }

        /// <summary>Opens a microphone packet in place; false if it was forged or damaged. The host's UDP thread only.</summary>
        public bool OpenMic(byte[] packet, int length)
        {
            int payloadLength = length - Protocol.MicHeadBytes - TagSize;
            if (payloadLength < 1) return false;
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            packet.AsSpan(9, 4).CopyTo(nonce);
            var payload = packet.AsSpan(Protocol.MicHeadBytes, payloadLength);
            try { _mic.Decrypt(nonce, payload, packet.AsSpan(Protocol.MicHeadBytes + payloadLength, TagSize), payload, packet.AsSpan(0, Protocol.MicHeadBytes)); return true; }
            catch (CryptographicException) { return false; }
        }
    }
}
