using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using KovaRelayWire;

namespace KovaRelay
{
    /// <summary>
    /// KovaRelay.exe --selftest: runs a relay in this process on a free 127.0.0.1 port with its files in a temp
    /// folder, and plays hosts, controlling PCs and attackers against it with raw sockets that follow RelayWire.cs.
    /// Writes %TEMP%\kova-relay-selftest.txt ("ok, ..." or "FAIL: ...") and exits 0 or 1. No window, no tray.
    /// Junk comes from other loopback addresses (127.0.0.x) so the per-address limits it trips do not block the
    /// real pairings, which come from 127.0.0.1.
    /// </summary>
    internal static class SelfTest
    {
        private static int _port;

        public static int Run()
        {
            string outFile = Path.Combine(Path.GetTempPath(), "kova-relay-selftest.txt");
            string dir = Path.Combine(Path.GetTempPath(), "kova-relay-selftest-" + Guid.NewGuid().ToString("N")[..8]);
            var done = new List<string>();
            int thrown = 0;
            string firstThrown = "";
            UnhandledExceptionEventHandler onCrash = (_, e) => { Interlocked.Increment(ref thrown); firstThrown = e.ExceptionObject.ToString() ?? ""; };
            EventHandler<UnobservedTaskExceptionEventArgs> onTask = (_, e) => { Interlocked.Increment(ref thrown); firstThrown = e.Exception.ToString(); e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += onCrash;
            TaskScheduler.UnobservedTaskException += onTask;
            RelayLog? log = null;
            RelayServer? server = null;
            try
            {
                Directory.CreateDirectory(dir);
                var files = new RelayFiles(dir, dir, null);
                log = new RelayLog(dir);
                server = new RelayServer(files, log) { LoopbackOnly = true };
                StartOnFreePort(server, files);
                done.Add($"listening on 127.0.0.1:{_port}");
                RunAll(server, files, done);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Thread.Sleep(200);
                Check(server.UnexpectedErrors == 0, $"the relay hit {server.UnexpectedErrors} unexpected errors (see its log)");
                Check(thrown == 0, "an exception escaped: " + firstThrown);
                log.Flush();
                string logText = File.ReadAllText(log.Path);
                Check(logText.Contains("connected from") && logText.Contains("Blocked 127.0.0.250"), "the log is missing expected lines");
                done.Add("no unexpected errors, nothing thrown");
                File.WriteAllText(outFile, "ok, " + string.Join("; ", done) + Environment.NewLine);
                return 0;
            }
            catch (Exception e)
            {
                string msg = e is TestFailure ? e.Message : e.ToString();
                File.WriteAllText(outFile, "FAIL: " + msg + Environment.NewLine + "passed before that: " + string.Join("; ", done) + Environment.NewLine);
                return 1;
            }
            finally
            {
                try { server?.Dispose(); } catch (Exception) { }
                try { log?.Dispose(); } catch (Exception) { }
                AppDomain.CurrentDomain.UnhandledException -= onCrash;
                TaskScheduler.UnobservedTaskException -= onTask;
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        /// <summary>settings.json round trip, the default with nothing saved, and 4.4.0's files read until settings.json exists.</summary>
        private static void CheckSettings(RelayFiles files)
        {
            string old = Path.Combine(files.HostsFolder, "old");
            Directory.CreateDirectory(old);
            var legacy = new RelayFiles(files.HostsFolder, Path.Combine(files.HostsFolder, "settings"), old);
            Check(legacy.LoadSettings() == new RelaySettings(47200, true), "with nothing saved, the relay did not use port 47200");
            File.WriteAllText(Path.Combine(old, "port.txt"), "47321\r\n");
            File.WriteAllText(Path.Combine(old, "relay-settings.txt"), "allow-new-host-names=no\r\n");
            Check(legacy.HasOldSettings, "4.4.0's port.txt was not noticed");
            Check(legacy.LoadSettings() == new RelaySettings(47321, false), "4.4.0's port.txt and relay-settings.txt were not read");
            Directory.CreateDirectory(legacy.SettingsFolder);
            Check(legacy.SaveSettings(new RelaySettings(47555, true)), "settings.json could not be written");
            Check(legacy.LoadSettings() == new RelaySettings(47555, true), "settings.json did not win over 4.4.0's files");
            File.WriteAllText(legacy.SettingsFile, "{ \"Port\": 99999, \"AllowNewHostNames\": false }");
            Check(legacy.LoadSettings() == new RelaySettings(47200, false), "a bad port in settings.json was not replaced by 47200");
            File.WriteAllText(legacy.SettingsFile, "not json");
            Check(legacy.LoadSettings().Port == 47321, "a broken settings.json did not fall back");
        }

        private static void StartOnFreePort(RelayServer server, RelayFiles files)
        {
            for (int i = 0; i < 20; i++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int p = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                if (p == 47120) continue;   // never the live Kova service's port
                if (server.Start(p) is null) { _port = p; return; }
            }
            throw new TestFailure("could not find a free port to test on");
        }

        private static void RunAll(RelayServer server, RelayFiles files, List<string> done)
        {
            using ECDsa keyA = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using ECDsa keyB = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            // 1. A host registers and gets Ok with a UDP key.
            var host = new FakeHost("office-pc", keyA);
            var (t, b) = host.Register();
            Check(t == Wire.Ok && b.Length == Wire.IdBytes, $"host registration got {Describe(t, b)}");
            host.StartReading();
            done.Add("host registered with a P-256 key and got a UDP key");

            // 2. A controlling PC connects by name (different case); the host is told and dials back; bytes flow both ways.
            var (client, hostSide, pairId) = Pair(host, "Office-PC", expectAddr: "127.0.0.1");
            Exchange(client, hostSide, 4 << 20);
            done.Add("paired by name and moved 4 MB each way unchanged");

            // 3. UDP through the pairing.
            using (var hostUdp = Udp())
            using (var clientUdp = Udp())
            {
                var relay = new IPEndPoint(IPAddress.Loopback, _port);
                hostUdp.SendTo(UdpPacket(host.UdpKey!, 0), relay);
                Thread.Sleep(250);
                byte[] up = UdpPacket(pairId, 1000);
                clientUdp.SendTo(up, relay);
                byte[]? got = UdpReceive(hostUdp, 2000);
                Check(got is not null && got.AsSpan().SequenceEqual(up), "a UDP packet from the controlling PC did not reach the host unchanged");
                byte[] down = UdpPacket(pairId, 1200);
                hostUdp.SendTo(down, relay);
                got = UdpReceive(clientUdp, 2000);
                Check(got is not null && got.AsSpan().SequenceEqual(down), "a UDP packet from the host did not reach the controlling PC unchanged");
                // Biggest allowed packet goes through.
                byte[] max = UdpPacket(pairId, Wire.MaxUdpPayload);
                clientUdp.SendTo(max, relay);
                got = UdpReceive(hostUdp, 2000);
                Check(got is not null && got.Length == max.Length, "the largest allowed UDP packet was not forwarded");
                // Wrong id, too big, too short, wrong header: none forwarded.
                byte[] wrongId = RandomNumberGenerator.GetBytes(Wire.IdBytes);
                clientUdp.SendTo(UdpPacket(wrongId, 100), relay);
                clientUdp.SendTo(UdpPacket(pairId, Wire.MaxUdpPayload + 1), relay);
                clientUdp.SendTo(new byte[] { Wire.Udp0, Wire.Udp1, 1, 2, 3 }, relay);
                byte[] badHead = UdpPacket(pairId, 50); badHead[0] = 0;
                clientUdp.SendTo(badHead, relay);
                Check(UdpReceive(hostUdp, 700) is null, "the relay forwarded a UDP packet it should have dropped");
                // Nothing comes back to a stranger who sends a host key or junk.
                using var stranger = Udp();
                stranger.SendTo(UdpPacket(wrongId, 10), relay);
                stranger.SendTo(UdpPacket(pairId[..4].Concat(new byte[4]).ToArray(), 10), relay);
                Check(UdpReceive(stranger, 500) is null, "the relay answered a stranger on UDP");
            }
            done.Add("UDP went both ways unchanged; wrong id, too big, too short and bad header packets were dropped");
            Check(server.Snapshot().ControllingPcs == 1, "the status did not count the controlling PC");
            client.Dispose(); hostSide.Dispose();
            WaitFor(() => server.Snapshot().ControllingPcs == 0, 3000, "the pair did not go away when one side closed");
            done.Add("closing one side closed the pair");

            // 4. Another key claiming the same name is refused.
            var squatter = new FakeHost("OFFICE-pc", keyB);
            (t, b) = squatter.Register();
            Check(IsRefused(t, b, Wire.Reason.NameTaken), $"a second key for the same name got {Describe(t, b)}");
            squatter.Dispose();
            done.Add("another key for a taken name was refused NameTaken");

            // 5. The right key registering again replaces the old line.
            var host2 = new FakeHost("office-pc", keyA);
            (t, b) = host2.Register();
            Check(t == Wire.Ok, $"re-registering with the right key got {Describe(t, b)}");
            host2.StartReading();
            Check(host.LineClosed.Wait(3000), "the old host line was not closed when the host registered again");
            host.Dispose();
            var (c2, h2, _) = Pair(host2, "office-pc", "127.0.0.1");
            Exchange(c2, h2, 64 << 10);
            c2.Dispose(); h2.Dispose();
            done.Add("re-registering replaced the old line, and the new one pairs");

            // 6. A bad signature, and a key that is not P-256.
            var forger = new FakeHost("forged", keyA);
            (t, b) = forger.Register(corruptSignature: true);
            Check(IsRefused(t, b, Wire.Reason.BadSignature), $"a bad signature got {Describe(t, b)}");
            forger.Dispose();
            using (ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384))
            {
                var odd = new FakeHost("p384-host", p384);
                (t, b) = odd.Register();
                Check(IsRefused(t, b, Wire.Reason.BadRequest), $"a P-384 key got {Describe(t, b)}");
                odd.Dispose();
            }
            done.Add("bad signature refused BadSignature, P-384 key refused");

            // 7. Accept with the wrong token is refused; the right one still works afterwards.
            {
                var clientTask = Task.Run(() => ClientConnect("office-pc"));
                var inc = host2.TakeIncoming(5000);
                byte[] wrong = inc.Token.ToArray(); wrong[3] ^= 0x40;
                var (ws, wns, _) = Open();
                var (at, ab) = Ask(wns, Wire.Accept, inc.Id.Concat(wrong).ToArray());
                Check(IsRefused(at, ab, Wire.Reason.BadRequest), $"an accept with a wrong token got {Describe(at, ab)}");
                ws.Dispose();
                var (rs, rns, _) = Open();
                (at, ab) = Ask(rns, Wire.Accept, inc.Id.Concat(inc.Token).ToArray());
                Check(at == Wire.Ok && ab.Length == 0, $"the right accept got {Describe(at, ab)}");
                var (ct, cb, cs) = clientTask.Result;
                Check(ct == Wire.Ok && cb.AsSpan().SequenceEqual(inc.Id), $"the controlling PC got {Describe(ct, cb)}");
                Exchange(cs, rns, 1000);
                cs.Dispose(); rs.Dispose();
                // A random id and token too.
                var (xs, xns, _) = Open();
                (at, ab) = Ask(xns, Wire.Accept, RandomNumberGenerator.GetBytes(24));
                Check(IsRefused(at, ab, Wire.Reason.BadRequest), $"an accept for no pairing got {Describe(at, ab)}");
                xs.Dispose();
            }
            done.Add("accept with a wrong token or unknown id refused BadRequest");

            // 8. Connect to a name nobody has.
            {
                var (ct, cb, cs) = ClientConnect("nobody-here");
                Check(IsRefused(ct, cb, Wire.Reason.NoSuchHost), $"connecting to an unknown name got {Describe(ct, cb)}");
                cs.Dispose();
            }
            done.Add("unknown name refused NoSuchHost");

            // 9. New names off.
            server.AllowNewNames = false;
            using (ECDsa keyC = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var newcomer = new FakeHost("brand-new", keyC);
                (t, b) = newcomer.Register();
                Check(IsRefused(t, b, Wire.Reason.NewNamesOff), $"a new name with new names off got {Describe(t, b)}");
                newcomer.Dispose();
            }
            server.AllowNewNames = true;
            CheckSettings(files);
            done.Add("settings.json saved and read back; 4.4.0's port.txt and relay-settings.txt read until moved");
            done.Add("new name refused NewNamesOff while new names were off");

            // 10. A frame with length 65535 is refused at once, without the relay waiting for its body.
            {
                var (s, ns, _) = Open("127.0.0.2");
                var sw = Stopwatch.StartNew();
                ns.Write(new byte[] { Wire.Register, 0xFF, 0xFF });
                var (ft, fb) = Wire.ReadFrame(ns);
                Check(IsRefused(ft, fb, Wire.Reason.BadRequest) && sw.ElapsedMilliseconds < 3000, $"a 65535-byte frame got {Describe(ft, fb)} after {sw.ElapsedMilliseconds} ms");
                s.Dispose();
            }
            done.Add("a 65535-byte frame was refused without being read");

            // 11. A host that never dials back: HostDidNotAnswer (runs alongside the junk test, both take 10 s).
            using ECDsa keyD = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var sleepy = new FakeHost("sleepy", keyD);
            (t, b) = sleepy.Register();
            Check(t == Wire.Ok, $"the sleepy host got {Describe(t, b)}");
            sleepy.StartReading();
            var sleepyTask = Task.Run(() => { var sw = Stopwatch.StartNew(); var r = ClientConnect("sleepy"); return (r, sw.ElapsedMilliseconds); });

            // 12. 300 junk connections from 30 addresses: garbage, nothing at all, or a trickle. All closed within the deadline.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memBefore = GC.GetTotalMemory(true);
            int connsBefore = server.Snapshot().Connections;
            var junk = new List<Task<long>>();
            for (int i = 0; i < 300; i++)
            {
                string from = "127.0.0." + (10 + i % 30);
                int kind = i % 3;
                junk.Add(Task.Run(() => JunkConnection(from, kind)));
            }
            Task.WaitAll(junk.ToArray(), 30_000);
            Check(junk.All(j => j.IsCompletedSuccessfully), "some junk connections were never closed by the relay");
            long slowest = junk.Max(j => j.Result);
            Check(slowest < Wire.RequestDeadlineMs + 2500, $"a junk connection stayed open for {slowest} ms");
            WaitFor(() => server.Snapshot().Connections <= connsBefore, 3000, "the relay still counts junk connections");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long grew = GC.GetTotalMemory(true) - memBefore;
            Check(grew < 16 << 20, $"memory grew {grew >> 20} MB over 300 junk connections");
            done.Add($"300 junk connections all closed (slowest {slowest} ms), memory grew {Math.Max(0, grew) >> 10} KB");

            var ((st, sb, ss), sleptMs) = sleepyTask.Result;
            Check(IsRefused(st, sb, Wire.Reason.HostDidNotAnswer) && sleptMs >= Wire.PairWaitMs - 500, $"a host that never answered gave {Describe(st, sb)} after {sleptMs} ms");
            ss.Dispose();
            sleepy.Dispose();
            done.Add("a host that never dialled back gave HostDidNotAnswer");

            // ...and a real pairing still works after all that.
            var (c3, h3, _) = Pair(host2, "office-pc", "127.0.0.1");
            Exchange(c3, h3, 256 << 10);
            c3.Dispose(); h3.Dispose();
            done.Add("a real pairing works after the junk");

            // 13. An address that keeps sending junk is blocked: closed at once, not even a hello.
            {
                int answered = 0;
                for (int i = 0; i < Limits.FailuresToBlock; i++)
                {
                    var (s, ns, _) = Open("127.0.0.250");
                    var (jt, jb) = Ask(ns, (byte)'Z', new byte[] { 1, 2, 3 });
                    if (IsRefused(jt, jb, Wire.Reason.BadRequest)) answered++;
                    s.Dispose();
                }
                Check(answered == Limits.FailuresToBlock, "junk requests were not refused BadRequest");
                using var blocked = Dial("127.0.0.250");
                blocked.ReceiveTimeout = 3000;
                int n;
                var sw = Stopwatch.StartNew();
                try { n = blocked.Receive(new byte[64]); } catch (SocketException) { n = 0; }
                Check(n == 0 && sw.ElapsedMilliseconds < 2000, $"a blocked address still got {n} bytes");
                // Other addresses are not affected.
                var (os, ons, och) = Open("127.0.0.251");
                os.Dispose();
            }
            done.Add("an address sending repeated junk was blocked; others were not");

            // 14. Too many connections from one address at once get Busy.
            {
                var held = new List<Socket>();
                for (int i = 0; i < Limits.PerAddressUnpaired; i++) { var (s, _, _) = Open("127.0.0.252"); held.Add(s); }
                var (xs, xns, _) = Open("127.0.0.252");
                var (bt, bb) = Wire.ReadFrame(xns);
                Check(IsRefused(bt, bb, Wire.Reason.Busy), $"one connection too many got {Describe(bt, bb)}");
                xs.Dispose();
                foreach (Socket s in held) s.Dispose();
            }
            done.Add("one waiting connection too many from an address got Busy");

            // 15. Forgetting a name drops that host and frees the name for another key.
            Check(server.Forget("Office-PC"), "forgetting the name failed");
            Check(host2.LineClosed.Wait(3000), "the forgotten host was not disconnected");
            host2.Dispose();
            var newOwner = new FakeHost("office-pc", keyB);
            (t, b) = newOwner.Register();
            Check(t == Wire.Ok, $"another key taking a forgotten name got {Describe(t, b)}");
            newOwner.Dispose();
            done.Add("forgetting a name disconnected its host and freed the name");

            // 16. hosts.txt holds the names with key hashes.
            server.Dispose();   // saves hosts.txt now
            string hosts = File.ReadAllText(files.HostsFile);
            string hashB = Convert.ToHexString(SHA256.HashData(keyB.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
            Check(hosts.Contains("office-pc," + hashB) && hosts.Contains("sleepy,"), "hosts.txt does not hold the pinned names");
            var reloaded = files.LoadPins(1000);
            Check(reloaded.ContainsKey("office-pc") && reloaded["office-pc"].KeyHash == hashB, "hosts.txt did not load back");
            done.Add("hosts.txt saved and loaded back");
        }

        // ------------------------------------------------------------------ helpers

        private static (NetworkStream client, NetworkStream host, byte[] pairId) Pair(FakeHost host, string name, string expectAddr)
        {
            var clientTask = Task.Run(() => ClientConnect(name));
            var inc = host.TakeIncoming(5000);
            Check(inc.Addr == expectAddr, $"the host was told the client is at {inc.Addr}");
            var (s, ns, _) = Open();
            var (at, ab) = Ask(ns, Wire.Accept, inc.Id.Concat(inc.Token).ToArray());
            Check(at == Wire.Ok && ab.Length == 0, $"the host's accept got {Describe(at, ab)}");
            Check(clientTask.Wait(5000), "the controlling PC never heard back");
            var (ct, cb, cs) = clientTask.Result;
            Check(ct == Wire.Ok && cb.AsSpan().SequenceEqual(inc.Id), $"the controlling PC got {Describe(ct, cb)}");
            return (cs, ns, inc.Id);
        }

        private static (byte, byte[], NetworkStream) ClientConnect(string name)
        {
            var (_, ns, _) = Open();
            ns.ReadTimeout = 20_000;
            var (t, b) = Ask(ns, Wire.Connect, Wire.NameBody(name));
            return (t, b, ns);
        }

        private static void Exchange(NetworkStream a, NetworkStream b, int bytes)
        {
            byte[] da = RandomNumberGenerator.GetBytes(bytes), db = RandomNumberGenerator.GetBytes(bytes);
            var w1 = Task.Run(() => a.Write(da));
            var w2 = Task.Run(() => b.Write(db));
            var r1 = Task.Run(() => ReadN(b, bytes));
            var r2 = Task.Run(() => ReadN(a, bytes));
            Check(Task.WaitAll(new Task[] { w1, w2, r1, r2 }, 60_000), "moving bytes through the pair timed out");
            Check(SHA256.HashData(da).AsSpan().SequenceEqual(SHA256.HashData(r1.Result)), "bytes from the controlling PC arrived changed");
            Check(SHA256.HashData(db).AsSpan().SequenceEqual(SHA256.HashData(r2.Result)), "bytes from the host arrived changed");
        }

        private static byte[] ReadN(NetworkStream s, int n)
        {
            byte[] buf = new byte[n];
            Wire.ReadExactly(s, buf);
            return buf;
        }

        /// <summary>Opens a junk connection and returns how long the relay took to close it.</summary>
        private static async Task<long> JunkConnection(string from, int kind)
        {
            var sw = Stopwatch.StartNew();
            using Socket s = Dial(from);
            byte[] buf = new byte[4096];
            try
            {
                if (kind == 0) await s.SendAsync(RandomNumberGenerator.GetBytes(1 + RandomNumberGenerator.GetInt32(60)), SocketFlags.None);
                else if (kind == 2) await s.SendAsync(new byte[] { Wire.Connect, 0, 50, 5 }, SocketFlags.None);   // promises 50 bytes, sends 1
                using var cts = new CancellationTokenSource(25_000);
                while (await s.ReceiveAsync(buf, SocketFlags.None, cts.Token) > 0) { }
            }
            catch (SocketException) { }
            catch (OperationCanceledException) { return long.MaxValue / 2; }
            return sw.ElapsedMilliseconds;
        }

        private static Socket Dial(string from)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Bind(new IPEndPoint(IPAddress.Parse(from), 0));
            s.Connect(new IPEndPoint(IPAddress.Loopback, _port));
            s.NoDelay = true;
            return s;
        }

        internal static (Socket, NetworkStream, byte[] challenge) Open(string from = "127.0.0.1")
        {
            Socket s = Dial(from);
            var ns = new NetworkStream(s, ownsSocket: true) { ReadTimeout = 15_000, WriteTimeout = 15_000 };
            byte[] hello = new byte[Wire.ServerHelloBytes];
            Wire.ReadExactly(ns, hello);
            Check(hello.AsSpan(0, 4).SequenceEqual(Wire.Magic), "the relay's hello had the wrong magic");
            return (s, ns, hello[4..]);
        }

        internal static (byte, byte[]) Ask(NetworkStream ns, byte type, byte[] body)
        {
            Wire.WriteFrame(ns, type, body);
            return Wire.ReadFrame(ns);
        }

        private static Socket Udp()
        {
            var u = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            u.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return u;
        }

        private static byte[] UdpPacket(byte[] id, int payload)
        {
            byte[] p = new byte[Wire.UdpHead + payload];
            p[0] = Wire.Udp0; p[1] = Wire.Udp1;
            id.CopyTo(p, 2);
            RandomNumberGenerator.Fill(p.AsSpan(Wire.UdpHead));
            return p;
        }

        private static byte[]? UdpReceive(Socket u, int ms)
        {
            if (!u.Poll(ms * 1000, SelectMode.SelectRead)) return null;
            byte[] buf = new byte[65536];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n = u.ReceiveFrom(buf, ref from);
            return buf[..n];
        }

        private static bool IsRefused(byte t, byte[] b, Wire.Reason r) => t == Wire.Refused && b.Length == 1 && b[0] == (byte)r;

        private static string Describe(byte t, byte[] b) =>
            t == Wire.Refused && b.Length == 1 ? $"Refused {(Wire.Reason)b[0]}" : $"frame '{(char)t}' with {b.Length} bytes";

        private static void WaitFor(Func<bool> cond, int ms, string failure)
        {
            var sw = Stopwatch.StartNew();
            while (!cond()) { if (sw.ElapsedMilliseconds > ms) throw new TestFailure(failure); Thread.Sleep(50); }
        }

        private static void Check(bool ok, string failure) { if (!ok) throw new TestFailure(failure); }

        private sealed class TestFailure : Exception { public TestFailure(string m) : base(m) { } }

        /// <summary>A pretend Kova host: registers a name with its key and reads its control line.</summary>
        private sealed class FakeHost : IDisposable
        {
            private readonly string _name;
            private readonly ECDsa _key;
            private Socket? _socket;
            private NetworkStream? _line;
            public byte[]? UdpKey;
            public readonly ManualResetEventSlim LineClosed = new(false);
            private readonly BlockingCollection<(byte[] Id, byte[] Token, string Addr)> _incoming = new();

            public FakeHost(string name, ECDsa key) { _name = name; _key = key; }

            public (byte, byte[]) Register(bool corruptSignature = false)
            {
                var (s, ns, challenge) = Open();
                _socket = s; _line = ns;
                byte[] spki = _key.ExportSubjectPublicKeyInfo();
                byte[] sig = _key.SignData(Wire.RegisterSignedBytes(challenge, _name), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                if (corruptSignature) sig[10] ^= 1;
                byte[] name = Wire.NameBody(_name);
                byte[] body = new byte[name.Length + 2 + spki.Length + sig.Length];
                name.CopyTo(body, 0);
                body[name.Length] = (byte)(spki.Length >> 8); body[name.Length + 1] = (byte)spki.Length;
                spki.CopyTo(body, name.Length + 2);
                sig.CopyTo(body, name.Length + 2 + spki.Length);
                var (t, b) = Ask(ns, Wire.Register, body);
                if (t == Wire.Ok) UdpKey = b;
                return (t, b);
            }

            public void StartReading()
            {
                NetworkStream line = _line!;
                line.ReadTimeout = Timeout.Infinite;
                new Thread(() =>
                {
                    try
                    {
                        while (true)
                        {
                            var (t, b) = Wire.ReadFrame(line);
                            if (t == Wire.Incoming && b.Length >= 25)
                            {
                                int n = b[24];
                                string addr = System.Text.Encoding.ASCII.GetString(b, 25, Math.Min(n, b.Length - 25));
                                _incoming.Add((b[..8], b[8..24], addr));
                            }
                        }
                    }
                    catch (Exception) { }
                    LineClosed.Set();
                }) { IsBackground = true }.Start();
            }

            public (byte[] Id, byte[] Token, string Addr) TakeIncoming(int ms)
            {
                if (!_incoming.TryTake(out var inc, ms)) throw new TestFailure($"host {_name} was never told about the controlling PC");
                return inc;
            }

            public void Dispose() { try { _socket?.Dispose(); } catch (Exception) { } }
        }
    }
}
