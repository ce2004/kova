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
                var files = new RelayFiles(dir, null);
                log = new RelayLog(dir);
                server = new RelayServer(log) { LoopbackOnly = true };
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
                Check(logText.Contains("another PC is already hosting with the same password"), "the log does not say a second PC tried to host with the same password");
                Check(!logText.Contains("office-pc", StringComparison.OrdinalIgnoreCase), "the log shows a host name");
                Check(!File.Exists(Path.Combine(dir, "hosts.txt")), "the relay wrote a hosts.txt");
                done.Add("no unexpected errors, nothing thrown, no host names in the log, no hosts.txt");
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

        /// <summary>settings.json round trip, the default with nothing saved, 4.4.0's port.txt read until settings.json
        /// exists, and an older settings.json that still has AllowNewHostNames loads fine.</summary>
        private static void CheckSettings(RelayFiles files)
        {
            string old = Path.Combine(files.SettingsFolder, "old");
            Directory.CreateDirectory(old);
            var legacy = new RelayFiles(Path.Combine(files.SettingsFolder, "settings"), old);
            Check(legacy.LoadSettings() == new RelaySettings(47200), "with nothing saved, the relay did not use port 47200");
            File.WriteAllText(Path.Combine(old, "port.txt"), "47321\r\n");
            File.WriteAllText(Path.Combine(old, "relay-settings.txt"), "allow-new-host-names=no\r\n");
            Check(legacy.HasOldSettings, "4.4.0's port.txt was not noticed");
            Check(legacy.LoadSettings() == new RelaySettings(47321), "4.4.0's port.txt was not read");
            Directory.CreateDirectory(legacy.SettingsFolder);
            Check(legacy.SaveSettings(new RelaySettings(47555)), "settings.json could not be written");
            Check(legacy.LoadSettings() == new RelaySettings(47555), "settings.json did not win over 4.4.0's files");
            Check(!File.ReadAllText(legacy.SettingsFile).Contains("AllowNewHostNames"), "settings.json still has AllowNewHostNames");
            File.WriteAllText(legacy.SettingsFile, "{ \"Port\": 47600, \"AllowNewHostNames\": false }");
            Check(legacy.LoadSettings() == new RelaySettings(47600), "an older settings.json with AllowNewHostNames did not load");
            File.WriteAllText(legacy.SettingsFile, "{ \"Port\": 99999, \"AllowNewHostNames\": true }");
            Check(legacy.LoadSettings() == new RelaySettings(47200), "a bad port in settings.json was not replaced by 47200");
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

            // 4. Another key claiming the same name while its host is connected is refused, and the first host keeps it.
            var squatter = new FakeHost("OFFICE-pc", keyB);
            (t, b) = squatter.Register();
            Check(IsRefused(t, b, Wire.Reason.NameTaken), $"a second key for a name in use got {Describe(t, b)}");
            squatter.Dispose();
            Check(!host.LineClosed.Wait(300), "the first host was disconnected when another key asked for its name");
            done.Add("another key for a name in use was refused NameTaken, and the first host stayed connected");

            // 5. The same key registering again replaces the old line.
            var host2 = new FakeHost("office-pc", keyA);
            (t, b) = host2.Register();
            Check(t == Wire.Ok, $"re-registering with the same key got {Describe(t, b)}");
            host2.StartReading();
            Check(host.LineClosed.Wait(3000), "the old host line was not closed when the host registered again");
            host.Dispose();
            var (c2, h2, _) = Pair(host2, "office-pc", "127.0.0.1");
            Exchange(c2, h2, 64 << 10);
            c2.Dispose(); h2.Dispose();
            WaitFor(() => server.Snapshot().Hosts == 1, 3000, "the relay did not count exactly one host after it re-registered");
            {
                var rows = server.Snapshot().Rows;
                Check(rows.Count == 1 && rows[0].Address == "127.0.0.1", "the list of connected hosts is wrong");
                string line = OptionsForm.RowText(rows[0]);
                Check(line.StartsWith("Host connected from 127.0.0.1 since ", StringComparison.Ordinal) && !line.Contains("office", StringComparison.OrdinalIgnoreCase),
                    $"a host line in the window reads \"{line}\"");
            }
            done.Add("re-registering with the same key replaced the old line, and the new one pairs; the window lists it by address only");

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

            // 9. Settings.
            CheckSettings(files);
            done.Add("settings.json saved and read back; an older one with AllowNewHostNames loads; 4.4.0's port.txt read until moved");

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

            // 15. Once the host holding a name disconnects, a host with a different key can take it, and pairs.
            host2.Dispose();
            WaitFor(() => server.Snapshot().Hosts == 0, 5000, "the relay still counts a host after it disconnected");
            var newOwner = new FakeHost("office-pc", keyB);
            (t, b) = newOwner.Register();
            Check(t == Wire.Ok, $"another key taking a name nobody holds now got {Describe(t, b)}");
            newOwner.StartReading();
            var (c4, h4, _) = Pair(newOwner, "Office-PC", "127.0.0.1");
            Exchange(c4, h4, 64 << 10);
            c4.Dispose(); h4.Dispose();
            done.Add("after the first host disconnected, a host with a different key took the name and paired");

            // 16. ...and now the first key is the one refused, while the new holder is connected.
            var former = new FakeHost("office-pc", keyA);
            (t, b) = former.Register();
            Check(IsRefused(t, b, Wire.Reason.NameTaken), $"the former key asking for a name in use got {Describe(t, b)}");
            former.Dispose();
            Check(!newOwner.LineClosed.Wait(300), "the new holder was disconnected when the former key asked for the name");
            newOwner.Dispose();
            done.Add("the former key was refused NameTaken while the new holder was connected");

            Hardening(done);
            Bench(done);
            LoadCheck(server, done);
            HostCap(server, done);
        }

        /// <summary>
        /// Registering costs nothing (any password will do), so it must not buy one address a large share of the relay:
        /// at most MaxHostsPerAddress hosts, and the rate bucket still applies to an address with hosts.
        /// </summary>
        private static void HostCap(RelayServer server, List<string> done)
        {
            const string From = "127.0.0.240";
            var keep = new List<Socket>();
            var keys = Enumerable.Range(0, Limits.MaxHostsPerAddress + 1).Select(_ => ECDsa.Create(ECCurve.NamedCurves.nistP256)).ToArray();
            try
            {
                int ok = 0;
                for (int i = 0; i < Limits.MaxHostsPerAddress; i++)
                    if (LoadHost(From, "cap-" + i, keys[i], keep, CancellationToken.None).Wait(10_000)) ok++;
                Check(ok == Limits.MaxHostsPerAddress, $"only {ok} of {Limits.MaxHostsPerAddress} hosts could register from one address");
                var extra = LoadHost(From, "cap-extra", keys[^1], keep, CancellationToken.None);
                Check(extra.Wait(10_000) && !extra.Result, $"host number {Limits.MaxHostsPerAddress + 1} from one address was let in");
                done.Add($"one address could register {Limits.MaxHostsPerAddress} hosts and no more");

                // Even with hosts registered, a burst of connections from that address runs out of room quickly.
                // A refused one gets Busy and an immediate close, which may arrive as a reset: either way it is turned away.
                int kept = 0, refused = 0;
                for (int i = 0; i < 400; i++)
                {
                    try
                    {
                        var (sk, ns, _) = Open(From);
                        lock (keep) keep.Add(sk);
                        bool turnedAway = false;
                        if (sk.Poll(100_000, SelectMode.SelectRead))
                        {
                            try { var (bt, bb) = Wire.ReadFrame(ns); turnedAway = IsRefused(bt, bb, Wire.Reason.Busy); } catch (Exception) { turnedAway = true; }
                        }
                        if (turnedAway) refused++; else kept++;
                    }
                    catch (Exception) { refused++; }
                }
                Check(refused > 0 && kept <= Limits.PerAddressConnections * Limits.MaxHostRoomFactor,
                    $"an address with hosts was not limited ({kept} kept open, {refused} turned away)");
                done.Add($"an address with {Limits.MaxHostsPerAddress} hosts opening 400 connections at once: {kept} let in, {refused} turned away");
            }
            finally
            {
                lock (keep) foreach (Socket sk in keep) try { sk.Dispose(); } catch (Exception) { }
                foreach (var k in keys) k.Dispose();
            }
            WaitFor(() => server.Snapshot().Hosts == 0, 10_000, "the relay still counts the capped hosts after they left");
        }

        // ------------------------------------------------------------------ 4.7: staying connected

        /// <summary>
        /// A host's line coming and going must not cost anyone their connection: probes are answered, a replaced line is
        /// told why, offers still waiting move to the host's new line, live pairs (TCP and UDP) carry on, and a
        /// controlling PC asking in the gap waits for the host instead of being told it is not there.
        /// </summary>
        private static void Hardening(List<string> done)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var a = new FakeHost("moving-pc", key);
            var (t, b) = a.Register();
            Check(t == Wire.Ok, $"the moving host got {Describe(t, b)}");
            a.StartReading();

            // A probe is answered at once.
            var sw = Stopwatch.StartNew();
            a.Send(Wire.KeepAlive, new[] { Wire.ProbeAsk });
            WaitFor(() => a.ProbeAnswers > 0, 3000, "the relay did not answer a host's probe");
            done.Add($"a host's probe was answered in {sw.ElapsedMilliseconds} ms");

            // An offer the host has not taken yet moves to its new line when it registers again.
            var clientTask = Task.Run(() => ClientConnect("moving-pc"));
            var first = a.TakeIncoming(5000);
            var a2 = new FakeHost("moving-pc", key);
            (t, b) = a2.Register();
            Check(t == Wire.Ok, $"the moving host registering again got {Describe(t, b)}");
            a2.StartReading();
            Check(a.LineClosed.Wait(3000), "the moving host's old line was not closed");
            Check(a.GotReplaced, "the replaced line was not told it was replaced");
            var again = a2.TakeIncoming(5000);
            Check(again.Id.AsSpan().SequenceEqual(first.Id) && again.Token.AsSpan().SequenceEqual(first.Token), "the waiting offer was not offered again on the new line");
            var (s1, ns1, _) = Open();
            var (at, ab) = Ask(ns1, Wire.Accept, again.Id.Concat(again.Token).ToArray());
            Check(at == Wire.Ok, $"accepting the moved offer got {Describe(at, ab)}");
            Check(clientTask.Wait(5000), "the controlling PC never heard back after its host's line moved");
            var (ct, cb, cs) = clientTask.Result;
            Check(ct == Wire.Ok, $"the controlling PC whose offer moved got {Describe(ct, cb)}");
            Exchange(cs, ns1, 64 << 10);
            // The same offer dialled back a second time (a 4.6 host that heard it on both lines): refused, not counted.
            var (s2, ns2, _) = Open();
            (at, ab) = Ask(ns2, Wire.Accept, again.Id.Concat(again.Token).ToArray());
            Check(IsRefused(at, ab, Wire.Reason.BadRequest), $"a second dial-back for a taken offer got {Describe(at, ab)}");
            s2.Dispose();
            done.Add("a host registering again was told its old line was replaced, got the waiting offer again on its new line, and it paired");

            // The live pair (TCP and UDP) carries on while its host's line is replaced again.
            using (var hostUdp = Udp())
            using (var clientUdp = Udp())
            {
                var relay = new IPEndPoint(IPAddress.Loopback, _port);
                hostUdp.SendTo(UdpPacket(a2.UdpKey!, 0), relay);
                Thread.Sleep(200);
                byte[] up = UdpPacket(again.Id, 300);
                clientUdp.SendTo(up, relay);
                Check(UdpReceive(hostUdp, 2000) is byte[] u1 && u1.AsSpan().SequenceEqual(up), "UDP did not reach the moving host");
                var a3 = new FakeHost("moving-pc", key);
                (t, b) = a3.Register();
                Check(t == Wire.Ok, $"the moving host registering a third time got {Describe(t, b)}");
                a3.StartReading();
                Check(a2.LineClosed.Wait(3000), "the second line was not closed");
                Exchange(cs, ns1, 64 << 10);
                // Before the new line announces its UDP, sound still goes where the old one said.
                up = UdpPacket(again.Id, 400);
                clientUdp.SendTo(up, relay);
                Check(UdpReceive(hostUdp, 2000) is byte[] u2 && u2.AsSpan().SequenceEqual(up), "UDP stopped while the host's line was replaced");
                byte[] down = UdpPacket(again.Id, 500);
                hostUdp.SendTo(down, relay);
                Check(UdpReceive(clientUdp, 2000) is byte[] d1 && d1.AsSpan().SequenceEqual(down), "UDP back to the controlling PC stopped while the host's line was replaced");
                cs.Dispose(); s1.Dispose();

                // The host's line drops (not replaced): a controlling PC asking in the gap waits, and pairs when it is back.
                a3.Dispose();
                Thread.Sleep(300);
                sw.Restart();
                var gapTask = Task.Run(() => ClientConnect("moving-pc"));
                Thread.Sleep(1000);
                var a4 = new FakeHost("moving-pc", key);
                (t, b) = a4.Register();
                Check(t == Wire.Ok, $"the moving host coming back got {Describe(t, b)}");
                a4.StartReading();
                var inc = a4.TakeIncoming(5000);
                var (s4, ns4, _) = Open();
                (at, ab) = Ask(ns4, Wire.Accept, inc.Id.Concat(inc.Token).ToArray());
                Check(at == Wire.Ok, $"accepting after coming back got {Describe(at, ab)}");
                Check(gapTask.Wait(5000), "the controlling PC that asked in the gap never heard back");
                var (gt, gb, gs) = gapTask.Result;
                Check(gt == Wire.Ok, $"a controlling PC asking while its host reconnected got {Describe(gt, gb)}");
                Exchange(gs, ns4, 16 << 10);
                done.Add($"a live pair kept moving TCP and UDP while its host's line was replaced; a PC asking during a {sw.ElapsedMilliseconds} ms gap waited and paired");
                gs.Dispose(); s4.Dispose();
                a4.Dispose();
            }
            a.Dispose(); a2.Dispose();
        }

        // ------------------------------------------------------------------ 4.7: how fast (informational)

        /// <summary>Round trips and throughput through the relay on loopback, against the same done directly. Printed, never failed on.</summary>
        private static void Bench(List<string> done)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var h = new FakeHost("bench-pc", key);
            var (t, b) = h.Register();
            Check(t == Wire.Ok, $"the bench host got {Describe(t, b)}");
            h.StartReading();

            var (client, hostSide, _) = Pair(h, "bench-pc", "127.0.0.1");
            Echo(hostSide);
            var (med, p99) = RoundTrips(client, 3000);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var direct = new TcpClient { NoDelay = true };
            direct.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var directFar = listener.AcceptTcpClient();
            directFar.NoDelay = true;
            listener.Stop();
            Echo(directFar.GetStream());
            var (dmed, dp99) = RoundTrips(direct.GetStream(), 3000);
            client.Dispose(); hostSide.Dispose();

            // One way, 256 MB, in 1 MB writes.
            var (c2, h2, c2Id) = Pair(h, "bench-pc", "127.0.0.1");
            const int Mb = 256;
            var reader = Task.Run(() => { byte[] buf = new byte[1 << 20]; long got = 0; while (got < (long)Mb << 20) { int n = h2.Read(buf); if (n <= 0) break; got += n; } return got; });
            byte[] chunk = new byte[1 << 20];
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Mb; i++) c2.Write(chunk);
            Check(reader.Wait(60_000) && reader.Result == (long)Mb << 20, "the throughput run did not deliver every byte");
            double mbps = Mb * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds);

            // UDP round trips: the host echoes every packet back through the relay.
            double umed, up99;
            using (var hostUdp = Udp())
            using (var clientUdp = Udp())
            {
                var relay = new IPEndPoint(IPAddress.Loopback, _port);
                hostUdp.SendTo(UdpPacket(h.UdpKey!, 0), relay);
                Thread.Sleep(200);
                var udpEcho = new Thread(() =>
                {
                    byte[] buf = new byte[2048];
                    EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                    try { while (true) { int n = hostUdp.ReceiveFrom(buf, ref any); hostUdp.SendTo(buf, n, SocketFlags.None, relay); } } catch (Exception) { }
                }) { IsBackground = true };
                udpEcho.Start();
                byte[] pkt = UdpPacket(c2Id, 160);
                byte[] back = new byte[2048];
                var times = new List<double>();
                clientUdp.ReceiveTimeout = 1000;
                for (int i = 0; i < 1100; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    clientUdp.SendTo(pkt, relay);
                    try { clientUdp.Receive(back); } catch (SocketException) { continue; }
                    if (i >= 100) times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000);
                }
                Check(times.Count > 900, $"only {times.Count} of 1000 UDP packets came back through the relay");
                times.Sort();
                umed = times[times.Count / 2]; up99 = times[(int)(times.Count * 0.99)];
                hostUdp.Close();
            }
            c2.Dispose(); h2.Dispose(); h.Dispose();
            done.Add($"speed on loopback (informational): TCP round trip through the relay median {med:F0} microseconds, 99th percentile {p99:F0} " +
                     $"(direct: {dmed:F0} and {dp99:F0}); UDP round trip through the relay median {umed:F0}, 99th percentile {up99:F0}; " +
                     $"one way through the relay {mbps:F0} MB a second");
        }

        private static void Echo(NetworkStream s) =>
            new Thread(() => { byte[] buf = new byte[65536]; try { int n; while ((n = s.Read(buf)) > 0) s.Write(buf, 0, n); } catch (Exception) { } }) { IsBackground = true }.Start();

        /// <summary>64-byte messages there and back, one at a time; the first 100 warm up. Returns microseconds.</summary>
        private static (double Median, double P99) RoundTrips(NetworkStream s, int count)
        {
            byte[] msg = new byte[64], back = new byte[64];
            var times = new List<double>(count);
            for (int i = 0; i < count + 100; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                s.Write(msg);
                Wire.ReadExactly(s, back);
                if (i >= 100) times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000);
            }
            times.Sort();
            return (times[times.Count / 2], times[(int)(times.Count * 0.99)]);
        }

        // ------------------------------------------------------------------ 4.7: many at once

        /// <summary>
        /// 200 hosts registered from 200 addresses, and 200 controlling PCs (from 200 more) each paired with one and
        /// echoing 256 KB at once. All peers here are async, so the thread count is the relay's (plus this test's pool).
        /// </summary>
        private static void LoadCheck(RelayServer server, List<string> done)
        {
            const int N = 200, Bytes = 1 << 20;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var me = Process.GetCurrentProcess();
            me.Refresh();
            int threadsBefore = me.Threads.Count;
            long memBefore = GC.GetTotalMemory(true), wsBefore = me.WorkingSet64;
            int connsBefore = server.Snapshot().Connections;
            var cts = new CancellationTokenSource(120_000);
            var keys = Enumerable.Range(0, N).Select(_ => ECDsa.Create(ECCurve.NamedCurves.nistP256)).ToArray();
            var hosts = new List<Socket>();
            var sw = Stopwatch.StartNew();
            try
            {
                var regs = Enumerable.Range(0, N).Select(i => LoadHost($"127.0.1.{1 + i}", "load-" + i, keys[i], hosts, cts.Token)).ToArray();
                Check(Task.WaitAll(regs, 60_000) && regs.All(r => r.Result), "not all 200 load hosts registered");
                long regMs = sw.ElapsedMilliseconds;
                WaitFor(() => server.Snapshot().Hosts >= N, 5000, "the relay does not count 200 hosts");
                sw.Restart();
                int peakThreads = 0; long peakWs = 0, peakMem = 0; int peakConns = 0;
                var allPaired = new LoadGate(N);
                var clients = Enumerable.Range(0, N).Select(i => LoadClient($"127.0.2.{1 + i}", "load-" + i, Bytes, allPaired, cts.Token)).ToArray();
                var sampler = Task.Run(async () =>
                {
                    while (!clients.All(c => c.IsCompleted))
                    {
                        me.Refresh();
                        peakThreads = Math.Max(peakThreads, me.Threads.Count);
                        peakWs = Math.Max(peakWs, me.WorkingSet64);
                        peakMem = Math.Max(peakMem, GC.GetTotalMemory(false));
                        peakConns = Math.Max(peakConns, server.Snapshot().Connections);
                        await Task.Delay(50);
                    }
                });
                bool all = Task.WaitAll(clients, 90_000);
                sampler.Wait(5000);
                Check(all && clients.All(c => c.Result), $"not all 200 pairs echoed {Bytes >> 20} MB ({clients.Count(c => c.IsCompletedSuccessfully && c.Result)} did)");
                long pairMs = sw.ElapsedMilliseconds;
                WaitFor(() => server.Snapshot().Connections <= connsBefore + N, 10_000, "the relay still counts the load pairs after they closed");
                done.Add($"load: 200 hosts registered in {regMs} ms; 200 pairs echoed {Bytes >> 20} MB each, all at once, in {pairMs} ms " +
                         $"(peak {peakConns} connections; process threads {threadsBefore} before, {peakThreads} at most; managed memory {memBefore >> 20} MB before, {peakMem >> 20} MB at most; " +
                         $"working set {wsBefore >> 20} MB before, {peakWs >> 20} MB at most)");
            }
            finally
            {
                cts.Cancel();
                lock (hosts) foreach (Socket s in hosts) try { s.Dispose(); } catch (Exception) { }
                foreach (var k in keys) k.Dispose();
            }
            WaitFor(() => server.Snapshot().Hosts == 0 && server.Snapshot().Connections <= connsBefore, 10_000, "the relay still counts load hosts after they left");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long grew = GC.GetTotalMemory(true) - memBefore;
            Check(grew < 32 << 20, $"managed memory stayed {grew >> 20} MB higher after the load left");
            done.Add($"after the load left, every connection was released and managed memory was within {Math.Max(0, grew) >> 10} KB of before");
        }

        /// <summary>A pretend host, all async: registers, then for every offer dials back from its own address and echoes.</summary>
        private static async Task<bool> LoadHost(string from, string name, ECDsa key, List<Socket> keep, CancellationToken ct)
        {
            Socket s = await DialAsync(from, ct);
            lock (keep) keep.Add(s);
            byte[] hello = new byte[Wire.ServerHelloBytes];
            await ReadExactlyAsync(s, hello, ct);
            byte[] spki = key.ExportSubjectPublicKeyInfo();
            byte[] sig = key.SignData(Wire.RegisterSignedBytes(hello.AsSpan(4), name), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            byte[] nb = Wire.NameBody(name);
            byte[] body = new byte[nb.Length + 2 + spki.Length + sig.Length];
            nb.CopyTo(body, 0);
            body[nb.Length] = (byte)(spki.Length >> 8); body[nb.Length + 1] = (byte)spki.Length;
            spki.CopyTo(body, nb.Length + 2);
            sig.CopyTo(body, nb.Length + 2 + spki.Length);
            await WriteFrameAsync(s, Wire.Register, body, ct);
            var (t, _) = await ReadFrameAsync(s, ct);
            if (t != Wire.Ok) return false;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var (ft, fb) = await ReadFrameAsync(s, ct);
                        if (ft != Wire.Incoming || fb.Length < 24) continue;
                        byte[] accept = fb[..24];
                        _ = Task.Run(async () =>
                        {
                            Socket d = await DialAsync(from, ct);
                            lock (keep) keep.Add(d);
                            await ReadExactlyAsync(d, new byte[Wire.ServerHelloBytes], ct);
                            await WriteFrameAsync(d, Wire.Accept, accept, ct);
                            var (at, _) = await ReadFrameAsync(d, ct);
                            if (at != Wire.Ok) return;
                            byte[] buf = new byte[16 << 10];
                            int n;
                            while ((n = await d.ReceiveAsync(buf, SocketFlags.None, ct)) > 0)
                                await SendAllAsync(d, buf.AsMemory(0, n), ct);
                            d.Dispose();
                        });
                    }
                }
                catch (Exception) { }
            });
            return true;
        }

        /// <summary>A pretend controlling PC, all async: connects by name, sends bytes and checks every one comes back.</summary>
        private static async Task<bool> LoadClient(string from, string name, int bytes, LoadGate allPaired, CancellationToken ct)
        {
            using Socket s = await DialAsync(from, ct);
            await ReadExactlyAsync(s, new byte[Wire.ServerHelloBytes], ct);
            await WriteFrameAsync(s, Wire.Connect, Wire.NameBody(name), ct);
            var (t, _) = await ReadFrameAsync(s, ct);
            if (t != Wire.Ok) { allPaired.Arrive(); return false; }
            // Every pair is set up before any data moves, so all of them are open and busy at the same time.
            allPaired.Arrive();
            await allPaired.All.WaitAsync(ct);
            byte[] data = RandomNumberGenerator.GetBytes(bytes);
            var send = Task.Run(async () => { for (int at = 0; at < bytes; at += 8192) await SendAllAsync(s, data.AsMemory(at, Math.Min(8192, bytes - at)), ct); });
            byte[] back = new byte[bytes];
            await ReadExactlyAsync(s, back, ct);
            await send;
            return back.AsSpan().SequenceEqual(data);
        }

        private sealed class LoadGate
        {
            private int _left;
            private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public LoadGate(int n) => _left = n;
            public Task All => _all.Task;
            public void Arrive() { if (Interlocked.Decrement(ref _left) == 0) _all.TrySetResult(); }
        }

        private static async Task<Socket> DialAsync(string from, CancellationToken ct)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            s.Bind(new IPEndPoint(IPAddress.Parse(from), 0));
            await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _port), ct);
            return s;
        }

        private static async Task ReadExactlyAsync(Socket s, Memory<byte> buf, CancellationToken ct)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int n = await s.ReceiveAsync(buf[got..], SocketFlags.None, ct);
                if (n <= 0) throw new EndOfStreamException();
                got += n;
            }
        }

        private static async Task SendAllAsync(Socket s, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            while (data.Length > 0) data = data[await s.SendAsync(data, SocketFlags.None, ct)..];
        }

        private static async Task WriteFrameAsync(Socket s, byte type, byte[] body, CancellationToken ct)
        {
            byte[] f = new byte[3 + body.Length];
            f[0] = type;
            f[1] = (byte)(body.Length >> 8); f[2] = (byte)body.Length;
            body.CopyTo(f, 3);
            await SendAllAsync(s, f, ct);
        }

        private static async Task<(byte, byte[])> ReadFrameAsync(Socket s, CancellationToken ct)
        {
            byte[] head = new byte[3];
            await ReadExactlyAsync(s, head, ct);
            byte[] body = new byte[(head[1] << 8) | head[2]];
            await ReadExactlyAsync(s, body, ct);
            return (head[0], body);
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
            public int ProbeAnswers;
            public volatile bool GotReplaced;
            private readonly BlockingCollection<(byte[] Id, byte[] Token, string Addr)> _incoming = new();

            public FakeHost(string name, ECDsa key) { _name = name; _key = key; }

            public void Send(byte type, byte[] body) { lock (_incoming) Wire.WriteFrame(_line!, type, body); }

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
                            if (t == Wire.KeepAlive && b.Length >= 1 && b[0] == Wire.ProbeAnswer) Interlocked.Increment(ref ProbeAnswers);
                            if (t == Wire.Refused && b.Length == 1 && b[0] == (byte)Wire.Reason.Replaced) GotReplaced = true;
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
