using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Kova.exe --relaytest RELAYPORT [HOSTPORT]: end to end through a real Kova Relay already running on this PC
    /// (127.0.0.1:RELAYPORT). A host here registers on it, a controlling PC here connects through it by name, and
    /// everything is checked across it: login, keys (F24 only, recorded, never typed), the UDP path (sound's hellos
    /// and pongs), the microphone (counted, never played), files and clipboard text both ways, NVDA text; then the
    /// refusals: a wrong password, a second host claiming the same name, and a flood of junk connections. Writes
    /// %TEMP%\kova-relaytest.txt; exit 0 when every check passes.
    /// </summary>
    internal static class RelayTest
    {
        public static int Run(int relayPort, int hostPort)
        {
            var lines = new List<string>();
            bool ok = true;
            void Say(string s) { lines.Add(DateTime.Now.ToString("HH:mm:ss.fff ") + s); }
            void Check(bool good, string what) { Say((good ? "ok   " : "FAIL ") + what); ok &= good; }
            Host.WrongPasswordDelayMs = 0;
            Host.TestLocked = false;
            Names.TestPipes = "RelayTest";
            string dir = Path.Combine(Path.GetTempPath(), "kova-relaytest-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            FileChannel.DownloadsOverride = Path.Combine(dir, "received");
            FileChannel.StagingOverride = Path.Combine(dir, "staging");
            const string Password = "relay-test-password", Name = "relaytest-pc";
            string address = Name + "@127.0.0.1:" + relayPort;
            var hostSaid = new ConcurrentQueue<string>();
            using var identity = HostIdentity.Create();
            Host? host = null, fake = null;
            Client? c = null;
            try
            {
                host = new Host(hostPort, Password, "relay-listen-password", s => hostSaid.Enqueue(s), null, identity);
                var sw = Stopwatch.StartNew();
                host.UseRelay(Relay.Parse(address, relayPort, out _));
                for (int i = 0; i < 100 && !host.RelayRegistered; i++) Thread.Sleep(100);
                Check(host.RelayRegistered, "the host registered on the relay as " + Name + " (" + sw.ElapsedMilliseconds + " ms)");
                if (!host.RelayRegistered) return Finish(lines, false, dir);

                // Login through the relay, end to end.
                var player = new Player(Player.NoDevice, _ => { });
                sw.Restart();
                c = Client.Connect(address, relayPort, Password, player, _ => { });
                Check(!c.ListenOnly, "a controlling PC logged in through the relay (" + sw.ElapsedMilliseconds + " ms)");
                sw.Restart();
                using (var direct = Client.Connect("127.0.0.1", hostPort, Password, new Player(Player.NoDevice, _ => { }), _ => { }))
                    Say("for comparison, logging in directly took " + sw.ElapsedMilliseconds + " ms");
                for (int i = 0; i < 100 && (c.HostVersion == null || c.LastPingMs < 0); i++) Thread.Sleep(50);
                Say("ping through the relay: " + c.LastPingMs + " ms");

                // Keys: recorded at the host, never typed.
                var keys = new ConcurrentQueue<(ushort, bool)>();
                Native.KeySink = (vk, scan, up, ext) => { keys.Enqueue((vk, up)); return true; };
                const ushort F24 = 0x87;
                for (int i = 0; i < 10; i++) { c.SendKey(F24, 0, false, false); c.SendKey(F24, 0, true, false); }
                for (int i = 0; i < 100 && keys.Count < 20; i++) Thread.Sleep(20);
                Native.KeySink = null;
                Check(keys.Count == 20 && keys.All(k => k.Item1 == F24), "20 key presses and releases arrived through the relay (" + keys.Count + ")");

                // UDP both ways through the relay: the sound path's hellos reach the host and its pongs come back.
                long before = c.UdpBytesReceived;
                Thread.Sleep(3500);
                Check(c.UdpBytesReceived > before, "UDP came back through the relay (" + (c.UdpBytesReceived - before) + " bytes in 3.5 s of hellos and pongs, no sound playing)");

                // The microphone, counted at the host (never played).
                int micTicks = 0;
                MicIn.TestHeard = (_, t) => Interlocked.Add(ref micTicks, t);
                c.SetMic(true, null);
                for (int i = 0; i < 100 && micTicks == 0; i++) Thread.Sleep(50);
                Thread.Sleep(1000);
                c.SetMic(false, null);
                MicIn.TestHeard = null;
                Check(micTicks > 0, "the microphone reached the host through the relay (" + micTicks * 5 + " ms of it)" + (micTicks == 0 ? " (no recording device here?)" : ""));

                // Clipboard text both ways.
                string? toHost = null, toClient = null;
                host.ClipboardReceived += t => toHost = t;
                c.ClipboardReceived += t => toClient = t;
                for (int i = 0; i < 100 && host.ControllerFiles == null; i++) Thread.Sleep(50);
                c.SendClipboard("to the host, through the relay");
                for (int i = 0; i < 200 && toHost == null; i++) Thread.Sleep(25);
                c.Files!.ExpectClipboard();
                host.SendClipboard("to the controller, through the relay");
                for (int i = 0; i < 200 && toClient == null; i++) Thread.Sleep(25);
                Check(toHost == "to the host, through the relay" && toClient == "to the controller, through the relay", "clipboard text both ways through the relay");

                // Files both ways (on their own lanes, each paired by the relay).
                string a = Path.Combine(dir, "to-host.bin"), b = Path.Combine(dir, "to-controller.bin");
                var rnd = new Random(7);
                byte[] ba = new byte[24 << 20], bb = new byte[8 << 20];
                rnd.NextBytes(ba); rnd.NextBytes(bb);
                File.WriteAllBytes(a, ba); File.WriteAllBytes(b, bb);
                string[]? landedHost = null, landedClient = null;
                host.ClipboardFilesReceived += p => landedHost = p;
                c.ClipboardFilesReceived += p => landedClient = p;
                sw.Restart();
                c.Files.SendFiles(new[] { a });
                for (int i = 0; i < 1200 && landedHost == null; i++) Thread.Sleep(50);
                long ms = sw.ElapsedMilliseconds;
                c.Files.ExpectClipboard();
                host.ControllerFiles!.SendFiles(new[] { b });
                for (int i = 0; i < 1200 && landedClient == null; i++) Thread.Sleep(50);
                bool sameA = landedHost != null && File.ReadAllBytes(landedHost[0]).AsSpan().SequenceEqual(ba);
                bool sameB = landedClient != null && File.ReadAllBytes(landedClient[0]).AsSpan().SequenceEqual(bb);
                Check(sameA && sameB, "a 24 MB file to the host and an 8 MB file back, through the relay, byte for byte (" + (ms > 0 ? 24 * 1000 / ms : 0) + " MB a second to the host)");

                // The host's NVDA as text.
                string? heard = null;
                c.NvdaSpoke += l => heard = string.Join(" / ", l);
                c.NvdaText = true;
                Thread.Sleep(500);
                host.NvdaLines(new[] { "NVDA through the relay" });
                for (int i = 0; i < 100 && heard == null; i++) Thread.Sleep(20);
                Check(heard == "NVDA through the relay", "the host's NVDA text through the relay");

                // A wrong password through the relay is refused like any other.
                try { Client.Connect(address, relayPort, "not-the-password", new Player(Player.NoDevice, _ => { }), _ => { }).Dispose(); Check(false, "a wrong password through the relay was refused"); }
                catch (InvalidOperationException e) { Check(e.Message.StartsWith("Wrong password"), "a wrong password through the relay was refused (" + e.Message + ")"); }

                // A second host, with another identity, claiming the same name: refused by the relay.
                var fakeSaid = new ConcurrentQueue<string>();
                fake = new Host(hostPort + 1, "another-password", null, s => fakeSaid.Enqueue(s));
                fake.UseRelay(Relay.Parse(address, relayPort, out _));
                for (int i = 0; i < 100 && !fakeSaid.Any(s => s.Contains("already uses the name")); i++) Thread.Sleep(100);
                Check(!fake.RelayRegistered && fakeSaid.Any(s => s.Contains("already uses the name")), "another host claiming the name " + Name + " was refused");
                fake.Dispose(); fake = null;
                // ...and the real host is still the one reached.
                using (var again = Client.Connect(address, relayPort, Password, new Player(Player.NoDevice, _ => { }), _ => { }))
                    Check(!again.ListenOnly, "the real host is still the one reached by that name");

                // A flood of junk connections (from other loopback addresses, so this test's own address stays clear).
                int refused = 0, opened = 0;
                var junk = new List<TcpClient>();
                for (int i = 0; i < 300; i++)
                {
                    try
                    {
                        var t = new TcpClient(new IPEndPoint(IPAddress.Parse("127.0.0." + (10 + i % 40)), 0));
                        t.Connect(IPAddress.Loopback, relayPort);
                        opened++;
                        var s = t.GetStream();
                        if (i % 3 == 0) s.Write(new byte[] { 0x58, 0xff, 0xff }); // a frame claiming 65535 bytes
                        else if (i % 3 == 1) s.Write(System.Text.Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"));
                        junk.Add(t); // the third kind says nothing at all
                    }
                    catch { refused++; }
                }
                // UDP junk too: nothing may ever come back.
                using (var u = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.99"), 0)))
                {
                    for (int i = 0; i < 200; i++) u.Send(new byte[] { 0x4B, 0x52, 1, 2, 3, 4, 5, 6, 7, (byte)i, 9 }, 11, new IPEndPoint(IPAddress.Loopback, relayPort));
                    u.Client.ReceiveTimeout = 1500;
                    var any = new IPEndPoint(IPAddress.Any, 0);
                    bool answered = true;
                    try { u.Receive(ref any); } catch (SocketException) { answered = false; }
                    Check(!answered, "200 junk UDP packets got no answer at all");
                }
                Thread.Sleep(11_000); // past the relay's request deadline
                int stillOpen = 0;
                foreach (var t in junk)
                {
                    try
                    {
                        var s = t.GetStream();
                        s.ReadTimeout = 200;
                        byte[] buf = new byte[256];
                        while (true) { int n = s.Read(buf); if (n <= 0) break; } // drains the hello and any refusal, then sees the close
                    }
                    catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut }) { stillOpen++; }
                    catch { }
                    t.Dispose();
                }
                Check(stillOpen == 0, "300 junk connections (" + opened + " opened, " + refused + " refused at once) were all closed by the relay within its deadline (" + stillOpen + " still open)");
                using (var after = Client.Connect(address, relayPort, Password, new Player(Player.NoDevice, _ => { }), _ => { }))
                    Check(!after.ListenOnly, "after the flood, a real controlling PC still gets in through the relay");
            }
            catch (Exception e) { Check(false, "threw " + e); }
            finally
            {
                Native.KeySink = null;
                MicIn.TestHeard = null;
                c?.Dispose();
                fake?.Dispose();
                host?.Dispose();
                FileChannel.DownloadsOverride = null;
                FileChannel.StagingOverride = null;
            }
            return Finish(lines, ok, dir);
        }

        private static int Finish(List<string> lines, bool ok, string dir)
        {
            lines.Add(ok ? "ALL PASSED" : "SOME FAILED");
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "kova-relaytest.txt"), lines);
            try { Directory.Delete(dir, true); } catch { }
            return ok ? 0 : 1;
        }
    }
}
