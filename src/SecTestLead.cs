using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Security tests (--securitytest Lead): the login (signed hello, remembered identities, device tickets), listen-only
    /// limits, Get files refusals, the service's location check, signed updates and relay addresses. Each check runs the
    /// real code in this process over loopback; null when every one passes.
    /// </summary>
    internal static class SecTestLead
    {
        private const int Port = 47361;

        internal static string? Run()
        {
            int delay = Host.WrongPasswordDelayMs;
            Host.WrongPasswordDelayMs = 0;
            string pipes = Names.TestPipes;
            Names.TestPipes = pipes + "SecLead";
            try
            {
                return UnsignedHello() ?? ChangedIdentity() ?? TicketsAndIdentity() ?? ListenerNvda() ?? NoHelperTold() ?? LockedAndPaths()
                    ?? NvdaChunks() ?? ServiceLocation() ?? SignedUpdates() ?? RelayAddresses() ?? Tickets();
            }
            finally
            {
                Host.WrongPasswordDelayMs = delay;
                Host.TestLocked = null;
                Names.TestPipes = pipes;
            }
        }

        private sealed class MemoryTrust : IHostTrust
        {
            public readonly ConcurrentDictionary<string, (byte[]? Id, byte[]? Ticket)> All = new();
            public (byte[]? Identity, byte[]? Ticket) Known(string pc) => All.TryGetValue(pc, out var k) ? k : (null, null);
            public void Remember(string pc, byte[] identity, byte[]? ticket)
            {
                All.TryGetValue(pc, out var old);
                bool same = old.Id != null && old.Id.AsSpan().SequenceEqual(identity);
                All[pc] = (identity, ticket ?? (same ? old.Ticket : null));
            }
        }

        private static Player Silent() => new(Player.NoDevice, _ => { });

        /// <summary>A fake host that sends a hello and counts every byte the client then sends it.</summary>
        private static (TcpListener, Func<int>) FakeHost(int port, Func<byte[]> hello)
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            int got = 0;
            new Thread(() =>
            {
                try
                {
                    using var t = l.AcceptTcpClient();
                    var s = t.GetStream();
                    s.Write(hello());
                    s.ReadTimeout = 3000;
                    byte[] buf = new byte[4096];
                    int n;
                    while ((n = s.Read(buf)) > 0) Interlocked.Add(ref got, n);
                }
                catch { }
            }) { IsBackground = true }.Start();
            return (l, () => Volatile.Read(ref got));
        }

        // 1. A hello not signed by the identity in it: the client refuses it and sends nothing at all (no proof to guess from).
        private static string? UnsignedHello()
        {
            var (l, got) = FakeHost(Port, () =>
            {
                using var x = Protocol.NewExchange();
                using var id = HostIdentity.Create();
                byte[] h = Protocol.MakeHello(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16), x, id);
                h[Protocol.HelloSignatureAt + 5] ^= 1; // the signature no longer matches
                Protocol.AddCheck(h);
                return h;
            });
            try
            {
                try { Client.Connect("127.0.0.1", Port, "secret-password", Silent(), _ => { }).Dispose(); return "a hello with a bad signature was accepted"; }
                catch (InvalidOperationException e) when (e.Message.Contains("not signed properly")) { }
                Thread.Sleep(300);
                return got() == 0 ? null : "the client sent " + got() + " bytes to a host whose hello was not signed";
            }
            finally { l.Stop(); }
        }

        // 2. A properly signed hello from a different identity than the one remembered: refused before anything is proven.
        private static string? ChangedIdentity()
        {
            using var real = HostIdentity.Create();
            var trust = new MemoryTrust();
            trust.Remember(Client.TrustKey("127.0.0.1", Port + 1), real.Public, null);
            var (l, got) = FakeHost(Port + 1, () =>
            {
                using var x = Protocol.NewExchange();
                using var impostor = HostIdentity.Create();
                return Protocol.MakeHello(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16), x, impostor);
            });
            try
            {
                try { Client.Connect("127.0.0.1", Port + 1, "secret-password", Silent(), _ => { }, trust: trust).Dispose(); return "a host with a changed identity was logged in to"; }
                catch (HostChangedException) { }
                Thread.Sleep(300);
                return got() == 0 ? null : "the client proved its password to a host with a changed identity (" + got() + " bytes)";
            }
            finally { l.Stop(); }
        }

        // 3. The real thing: first login remembers the identity and gets a ticket; a second login shows it.
        private static string? TicketsAndIdentity()
        {
            using var identity = HostIdentity.Create();
            using var host = new Host(Port + 2, "secret-password", null, _ => { }, null, identity);
            var trust = new MemoryTrust();
            string pc = Client.TrustKey("127.0.0.1", Port + 2);
            using (var c = Client.Connect("127.0.0.1", Port + 2, "secret-password", Silent(), _ => { }, trust: trust))
            {
                for (int i = 0; i < 50 && trust.Known(pc).Ticket == null; i++) Thread.Sleep(20);
            }
            var (id, ticket) = trust.Known(pc);
            if (id == null || !id.AsSpan().SequenceEqual(identity.Public)) return "the host's identity was not remembered after logging in";
            if (ticket == null || !identity.TicketOk(ticket)) return "no valid device ticket was kept after logging in";
            // The identity saved and read back is the same key: else every start would make a new one, and every controlling PC would warn.
            using (var back = HostIdentity.FromPkcs8(identity.ExportPkcs8()))
                if (back == null || !back.Public.AsSpan().SequenceEqual(identity.Public)) return "a saved host identity did not read back as the same key";
            return null;
        }

        // 4. A PC that logged in before (with its ticket) still gets in while its address is blocked for guessing.
        private static string? Tickets()
        {
            using var identity = HostIdentity.Create();
            using var host = new Host(Port + 3, "secret-password", null, _ => { }, null, identity);
            var trust = new MemoryTrust();
            using (Client.Connect("127.0.0.1", Port + 3, "secret-password", Silent(), _ => { }, trust: trust))
                for (int i = 0; i < 50 && trust.Known(Client.TrustKey("127.0.0.1", Port + 3)).Ticket == null; i++) Thread.Sleep(20);
            // Five wrong passwords: 127.0.0.1 is now blocked for a minute.
            for (int i = 0; i < 6; i++) try { Client.Connect("127.0.0.1", Port + 3, "wrong-password", Silent(), _ => { }).Dispose(); } catch { }
            try { Client.Connect("127.0.0.1", Port + 3, "secret-password", Silent(), _ => { }).Dispose(); return "a blocked address without a ticket still got in"; }
            catch { }
            try { using var c = Client.Connect("127.0.0.1", Port + 3, "secret-password", Silent(), _ => { }, trust: trust); }
            catch (Exception e) { return "a PC holding a device ticket was turned away while its address was blocked: " + e.Message; }
            // A ticket made by another host is worth nothing.
            using var other = HostIdentity.Create();
            if (identity.TicketOk(other.NewTicket()) || identity.TicketOk(new byte[HostIdentity.TicketBytes])) return "a ticket from another host (or none) was taken as valid";
            return null;
        }

        // 5. A listen-only login never gets the host's NVDA text (4.4); a controller does.
        private static string? ListenerNvda()
        {
            using var host = new Host(Port + 4, "secret-password", "listen-password", _ => { });
            using var ctrl = Client.Connect("127.0.0.1", Port + 4, "secret-password", Silent(), _ => { });
            using var lis = Client.Connect("127.0.0.1", Port + 4, "listen-password", Silent(), _ => { });
            for (int i = 0; i < 50 && (!ctrl.CanNvda || !lis.CanNvda); i++) Thread.Sleep(20);
            int heardC = 0, heardL = 0;
            ctrl.NvdaSpoke += _ => Interlocked.Increment(ref heardC);
            lis.NvdaSpoke += _ => Interlocked.Increment(ref heardL);
            ctrl.NvdaText = true;
            lis.NvdaText = true;
            Thread.Sleep(300);
            host.NvdaLines(new[] { "a line only controllers may have" });
            for (int i = 0; i < 50 && heardC == 0; i++) Thread.Sleep(20);
            Thread.Sleep(200);
            if (heardC == 0) return "a controller did not get the host's NVDA text";
            return heardL == 0 ? null : "a listen-only login got the host's NVDA text";
        }

        // 6. A host without the Kova Helper add-on: the controller is told, and its text-only mode keeps the sound (NVDA speaks there).
        private static string? NoHelperTold()
        {
            using var host = new Host(Port + 5, "secret-password", null, _ => { }) { TestHelperConnected = false };
            using var ctrl = Client.Connect("127.0.0.1", Port + 5, "secret-password", Silent(), _ => { });
            for (int i = 0; i < 50 && !ctrl.CanNvda; i++) Thread.Sleep(20);
            bool? there = null;
            ctrl.NvdaHelperThere += x => there = x;
            ctrl.SoundOff = true;
            ctrl.NvdaText = true;
            for (int i = 0; i < 50 && there == null; i++) Thread.Sleep(20);
            if (there != false) return "the controller was not told the remote PC has no Kova Helper";
            return null;
        }
        // 7. Get files: refused while the screen is locked, and never a network or device path.
        private static string? LockedAndPaths()
        {
            using var host = new Host(Port + 6, "secret-password", null, _ => { });
            using var ctrl = Client.Connect("127.0.0.1", Port + 6, "secret-password", Silent(), _ => { });
            Host.TestLocked = true;
            string locked = ctrl.ListFolderAsync(@"C:\").GetAwaiter().GetResult();
            if (!locked.Contains("locked")) return "Get files listed a folder while the screen was locked: " + locked;
            Host.TestLocked = false;
            foreach (string bad in new[] { @"\\203.0.113.9\share", @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\Windows", @"\\.\PhysicalDrive0" })
            {
                string r = ctrl.ListFolderAsync(bad).GetAwaiter().GetResult();
                if (!r.StartsWith("E\t")) return "Get files listed " + bad;
            }
            string ok = ctrl.ListFolderAsync(Path.GetTempPath()).GetAwaiter().GetResult();
            if (ok.StartsWith("E\t")) return "Get files refused an ordinary folder: " + ok;
            return null;
        }

        // 8. The biggest NVDA event the add-on can hand over still fits what the controller takes, and a bad one is refused.
        private static string? NvdaChunks()
        {
            var parts = Enumerable.Range(0, NvdaEvent.MaxParts).Select(i => new NvdaSpeechPart(new string('\u4e2d', NvdaEvent.MaxText / NvdaEvent.MaxParts), (sbyte)(i % 50))).ToArray();
            byte[] body = NvdaEvent.Speak(parts).Encode();
            if (1 + body.Length > Client.MaxSaidMessage) return "the biggest NVDA event (" + body.Length + " bytes) would be refused by the controller";
            if (NvdaEvent.Decode(new byte[] { 99, 0, 0 }) != null) return "a malformed NVDA event was taken";
            return null;
        }
        // 9. The service runs a copy in place only where ordinary users cannot change it.
        private static string? ServiceLocation()
        {
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            if (File.Exists(notepad) && !ServiceHost.ProtectedFromUsers(notepad)) return "System32's notepad.exe was not taken as protected";
            string mine = Path.Combine(Path.GetTempPath(), "kova-sectest-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllBytes(mine, new byte[] { 1 });
            try { if (ServiceHost.ProtectedFromUsers(mine)) return "a file in the user's temp folder was taken as protected from users"; }
            finally { File.Delete(mine); }
            return null;
        }

        // 10. Updates: only a list signed with Kova's key, for this version, gives a fingerprint.
        private static string? SignedUpdates()
        {
            using var test = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string testKey = Convert.ToBase64String(test.ExportSubjectPublicKeyInfo());
            string hash = new string('a', 64);
            byte[] manifest = Encoding.UTF8.GetBytes("{\"version\":\"9.9.9\",\"files\":{\"Kova-x64.exe\":\"" + hash + "\"}}");
            byte[] sig = test.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var v = new Version(9, 9, 9);
            if (Updater.SignedHash(manifest, sig, v, "Kova-x64.exe", testKey) != hash) return "a properly signed list was not accepted";
            if (Updater.SignedHash(manifest, sig, v, "Kova-x64.exe") != null) return "a list signed with a key that is not Kova's was accepted";
            byte[] tampered = (byte[])manifest.Clone();
            tampered[^5] ^= 1;
            if (Updater.SignedHash(tampered, sig, v, "Kova-x64.exe", testKey) != null) return "a changed list was accepted";
            if (Updater.SignedHash(manifest, sig, new Version(9, 9, 8), "Kova-x64.exe", testKey) != null) return "a list for another version was accepted";
            if (Updater.SignedHash(manifest, sig, v, "Kova-arm64.exe", testKey) != null) return "a file not in the list got a fingerprint";
            return null;
        }

        // 11. Relay addresses as typed, and the stand-in addresses relayed PCs are counted by.
        private static string? RelayAddresses()
        {
            if (Relay.Parse("office-pc@relay.example.com:47300", 47200, out _) is not { Name: "office-pc", RelayHost: "relay.example.com", RelayPort: 47300 }) return "name@relay:port was not read right";
            if (Relay.Parse("office-pc@relay.example.com", 47200, out _) is not { RelayPort: 47200 }) return "name@relay did not take the default port";
            if (Relay.Parse("bad name@relay", 47200, out string? why) != null || why == null) return "a name with a space was taken";
            if (Relay.Parse("plain.address", 47200, out _) != null) return "an address without @ was read as a relay address";
            var a = Relay.Stand("203.0.113.5", new byte[8] { 1, 0, 0, 0, 0, 0, 0, 0 });
            var b = Relay.Stand("203.0.113.5", new byte[8] { 2, 0, 0, 0, 0, 0, 0, 0 });
            var c = Relay.Stand("203.0.113.6", new byte[8] { 1, 0, 0, 0, 0, 0, 0, 0 });
            static string Block(IPAddress x) => Convert.ToHexString(x.GetAddressBytes(), 0, 8);
            if (Block(a) != Block(b)) return "two connections from one PC through the relay were not counted together";
            if (Block(a) == Block(c)) return "two PCs through the relay were counted as one";
            if (!Relay.IsStand(a) || Relay.IsStand(IPAddress.Parse("fd6b::1"))) return "relayed stand-in addresses are not told apart";
            return null;
        }
    }
}
