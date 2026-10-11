using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TailRemote
{
    /// <summary>
    /// --chaostest: a host and its clients on this PC, put through everything nasty a
    /// network, a broken or hostile PC, or a user could do. No keys are ever sent (the
    /// host would type them into this PC) and the real clipboard is never touched (this
    /// tests the connection, not Windows). Each scenario has a time limit, so a freeze
    /// shows as FROZE rather than hanging the test. Results: %TEMP%\tailremote-chaostest.txt.
    /// </summary>
    internal static class ChaosTest
    {
        private const int Port = 48100;
        private const string Password = "chaos-pass", ListenPassword = "listen-pass";
        private static readonly List<string> Report = new();
        private static readonly List<string> Log = new();
        private static int _failures;
        private static string Root = Path.Combine(Path.GetTempPath(), "tailremote-chaos");

        public static int Run()
        {
            Host.WrongPasswordDelayMs = 0;
            Names.TestPipes = "Chaos"; // never the real service's pipes, on a PC that runs it
            if (Environment.GetEnvironmentVariable("CHAOS_TRACE") != null) DiagLog.Enabled = true;
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            Directory.CreateDirectory(Root);
            FileChannel.StagingOverride = Path.Combine(Root, "stage");
            FileChannel.DownloadsOverride = Path.Combine(Root, "downloads");
            var host = NewHost();
            Thread.Sleep(500);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memBefore = GC.GetTotalMemory(true);
            // Threads of our own, not .NET's worker pool: the pool keeps idle workers a while after work
            // spread over the cores (the remote screen's colour conversion), which is reuse, not a leak.
            int threadsBefore = Process.GetCurrentProcess().Threads.Count - ThreadPool.ThreadCount;

            Scenario("time left in words, up to days", 5, () =>
            {
                var cases = new (double Seconds, string Says)[]
                {
                    (12, "12 seconds"), (61, "1 minute 1 second"), (3600, "1 hour"), (3600 * 5 + 60 * 20, "5 hours 20 minutes"),
                    (86_400 * 2 + 3600 * 3, "2 days 3 hours"), (86_400, "1 day"),
                };
                var wrong = cases.Where(c => MainForm.Duration(TimeSpan.FromSeconds(c.Seconds)) != c.Says).Select(c => c.Says + " came out as " + MainForm.Duration(TimeSpan.FromSeconds(c.Seconds))).ToList();
                return wrong.Count == 0 ? "seconds, minutes, hours and days all read right" : "FAIL: " + string.Join("; ", wrong);
            });

            Scenario("junk instead of a login, 50 times", 30, () =>
            {
                var rnd = new Random(1);
                for (int i = 0; i < 50; i++)
                {
                    using var t = new TcpClient("127.0.0.1", Port);
                    var junk = new byte[rnd.Next(1, 4000)];
                    rnd.NextBytes(junk);
                    try { t.GetStream().Write(junk); } catch { }
                }
                return Healthy(host, "after the junk");
            });

            Scenario("300 connections that never say anything, then a real login", 30, () =>
            {
                var silent = new List<TcpClient>();
                for (int i = 0; i < 300; i++) try { silent.Add(new TcpClient("127.0.0.1", Port)); } catch { }
                var clock = Stopwatch.StartNew();
                string result;
                try { using var c = Connect(Password); result = "logged in after " + clock.ElapsedMilliseconds + " ms"; }
                catch (Exception e) { result = "FAIL: " + e.Message + " after " + clock.ElapsedMilliseconds + " ms"; }
                foreach (var t in silent) t.Dispose();
                Thread.Sleep(300);
                return result.StartsWith("FAIL") ? result : result + "; " + Healthy(host, "after they went");
            });

            Scenario("10,000 junk audio packets at the host", 30, () =>
            {
                using var u = new UdpClient();
                var rnd = new Random(2);
                for (int i = 0; i < 10_000; i++)
                {
                    var d = new byte[rnd.Next(0, 1400)];
                    rnd.NextBytes(d);
                    if (i % 3 == 0 && d.Length > 0) d[0] = Protocol.UdpHello; // look almost right
                    try { u.Send(d, d.Length, "127.0.0.1", Port); } catch { }
                }
                return Healthy(host, "after the flood");
            });

            Scenario("30 connects and disconnects as fast as possible", 60, () =>
            {
                for (int i = 0; i < 30; i++) { using var c = Connect(Password); }
                Thread.Sleep(1500);
                return "sessions left: " + host.TestSessions + "; " + Healthy(host, "after the storm");
            });

            Scenario("20 controllers with the same password, all at once: nobody is kicked off", 60, () =>
            {
                var all = Enumerable.Range(0, 20).Select(_ => Connect(Password)).ToList();
                Thread.Sleep(2500);
                int up = all.Count(c => !c.TestClosed);
                int sessions = host.TestSessions;
                foreach (var c in all) c.Dispose();
                Thread.Sleep(1500);
                return up == 20 && sessions == 20 ? "all 20 stayed connected together" : "FAIL: " + up + " of 20 still connected, " + sessions + " sessions";
            });

            Scenario("100 listeners at once, plus one too many", 120, () =>
            {
                var listeners = new List<Client>();
                Exception? extra = null;
                for (int i = 0; i < 100; i++) listeners.Add(Connect(ListenPassword));
                Client? hundredFirst = null;
                var told = new System.Collections.Concurrent.ConcurrentQueue<string>();
                try { hundredFirst = Client.Connect("127.0.0.1", Port, ListenPassword, new Player(Player.NoDevice, _ => { }), told.Enqueue); } catch (Exception e) { extra = e; }
                Thread.Sleep(500);
                bool refused = extra != null || (hundredFirst != null && WaitClosed(hundredFirst, 3000));
                // It must hear why, not just "the remote PC closed the connection" (the goodbye used to be cut off).
                bool saidWhy = WaitFor(() => told.Any(t => t.Contains("already 100 people listening")), 3000) || (extra?.Message.Contains("already 100") ?? false);
                hundredFirst?.Dispose();
                int sessions = host.TestSessions;
                foreach (var l in listeners) l.Dispose();
                Thread.Sleep(1500);
                return (refused ? "the 101st was turned away" : "FAIL: a 101st listener got in") + (saidWhy ? " and told why" : "; FAIL: it was never told why (" + string.Join(" | ", told) + ")") +
                    "; sessions at the peak " + sessions + ", after " + host.TestSessions;
            });

            Scenario("nonsense messages after login", 30, () =>
            {
                using var c = Connect(Password);
                c.TestWrite(new byte[] { Protocol.AudioQuality, 255 });       // an impossible bitrate step
                c.TestWrite(new byte[] { Protocol.FilePace, 0, 0, 0, 0 });    // a file speed of zero
                c.TestWrite(new byte[] { Protocol.FilePace, 255, 255, 255, 255 });
                c.TestWrite(new byte[] { 0xEE, 1, 2, 3 });                     // a message type nobody knows
                c.TestWrite(new byte[] { Protocol.AudioQuality });            // too short
                c.TestWrite(new byte[] { Protocol.Features });
                for (int i = 0; i < 1000; i++) c.TestWrite(new byte[] { 8, (byte)(i & 1) }); // a retired message type, a thousand times
                Thread.Sleep(1500);
                return c.TestClosed ? "FAIL: the host dropped the connection over nonsense" : "the connection stayed up and ignored all of it";
            });

            Scenario("a listener sending clipboard and Control Alt Delete", 20, () =>
            {
                string? got = null;
                host.ClipboardReceived += t => got = t;
                using var l = Connect(ListenPassword);
                l.TestWrite(Protocol.TextMessage(0x41, "a listener should never set the clipboard")); // the retired clipboard message
                l.SendClipboard("nor this way"); // a listener has no file lanes, so this goes nowhere
                l.TestWrite(new byte[] { Protocol.SecureAttention });
                l.TestWrite(new byte[] { Protocol.RestartPc }); // must never restart this PC
                Thread.Sleep(1000);
                return got == null ? "ignored, as it should be (and nothing restarted)" : "FAIL: a listener set the clipboard";
            });

            Scenario("damaged data in the middle of a connection, then reconnecting", 30, () =>
            {
                var c = Connect(Password);
                string? why = null;
                c.Disconnected += w => why = w;
                c.TestRaw(new byte[] { 0x55, 0x55, 0x55, 0x55, 0x12, 0x34, 0x56, 0x78, 1, 2, 3 }); // a frame header that does not check out
                bool dropped = WaitClosed(c, 12_000);
                c.Dispose();
                using var again = Connect(Password);
                return dropped ? "the host hung up at once (" + (why ?? "") + ") and reconnecting worked" : "FAIL: the damaged connection was not noticed";
            });

            using (var ctrl = Connect(Password))
            {
                for (int i = 0; i < 100 && (ctrl.Files == null || host.ControllerFiles == null); i++) Thread.Sleep(50);

                Scenario("2,000 tiny files, nested folders, empty files, emoji, Arabic, Chinese and very long names", 120, () =>
                {
                    string src = Path.Combine(Root, "src", "Mess");
                    var names = new[] { "plain.txt", "empty.bin", "\U0001F600 smile.txt", "مرحبا.txt", "你好.txt", new string('x', 180) + ".txt", "spaces   in name.txt", "dots...txt" };
                    long bytes = 0;
                    int files = 0;
                    for (int d = 0; d < 20; d++)
                    {
                        string dir = Path.Combine(src, "level" + d, d % 3 == 0 ? "deeper" : "");
                        Directory.CreateDirectory(dir);
                        for (int f = 0; f < 100; f++)
                        {
                            string name = f < names.Length ? names[f] : "file" + f + ".dat";
                            byte[] data = name == "empty.bin" ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(name + d + f);
                            File.WriteAllBytes(Path.Combine(dir, name), data);
                            bytes += data.Length; files++;
                        }
                    }
                    Directory.CreateDirectory(Path.Combine(src, "empty folder"));
                    string[]? landed = null;
                    string? said = null;
                    Action<string[]> gotFiles = p => landed = p;
                    host.ClipboardFilesReceived += gotFiles;
                    Action<FileChannel.Transfer> note = t => { if (t.Finished && t.What == "Mess") said = t.Result; };
                    host.TransferProgress += note;
                    ctrl.Files!.SendFiles(new[] { src });
                    for (int i = 0; i < 1000 && landed == null; i++) Thread.Sleep(20);
                    host.TransferProgress -= note;
                    host.ClipboardFilesReceived -= gotFiles;
                    Console.WriteLine("the receiving PC said: " + said);
                    if (landed == null) return "FAIL: nothing arrived";
                    var got = Directory.EnumerateFiles(landed[0], "*", SearchOption.AllDirectories).ToList();
                    long gotBytes = got.Sum(f => new FileInfo(f).Length);
                    bool emptyFolder = Directory.Exists(Path.Combine(landed[0], "empty folder"));
                    return got.Count == files && gotBytes == bytes && emptyFolder ? files + " files and every folder arrived exactly" : "FAIL: " + got.Count + " of " + files + " files, " + gotBytes + " of " + bytes + " bytes, empty folder " + emptyFolder;
                });

                if (Only != null) Scenario("100,000 files in one folder tree: speed, and memory given back afterwards (only when asked for by name)", 900, () =>
                {
                    string src = Path.Combine(Root, "src", "Huge");
                    for (int d = 0; d < 100; d++)
                    {
                        string dir = Path.Combine(src, "dir" + d);
                        Directory.CreateDirectory(dir);
                        for (int f = 0; f < 1000; f++) File.WriteAllText(Path.Combine(dir, "f" + f + ".txt"), "file " + d + "/" + f);
                    }
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long privateBefore = Process.GetCurrentProcess().PrivateMemorySize64 >> 20;
                    string[]? landed = null;
                    host.ClipboardFilesReceived += p => landed = p;
                    long peak = 0;
                    var clock = Stopwatch.StartNew();
                    var send = Task.Run(() => ctrl.Files!.SendFiles(new[] { src }));
                    while (!send.IsCompleted || landed == null)
                    {
                        Thread.Sleep(200);
                        peak = Math.Max(peak, Process.GetCurrentProcess().PrivateMemorySize64 >> 20);
                        if (clock.Elapsed.TotalSeconds > 850) break;
                    }
                    double secs = clock.Elapsed.TotalSeconds;
                    if (landed == null) return "FAIL: nothing arrived in " + secs.ToString("0") + " s";
                    int count = Directory.EnumerateFiles(landed[0], "*", SearchOption.AllDirectories).Count();
                    Thread.Sleep(4000); // the memory is given back a second after the end
                    var me = Process.GetCurrentProcess();
                    me.Refresh();
                    long privateAfter = me.PrivateMemorySize64 >> 20;
                    string line = count + " files in " + secs.ToString("0") + " s (" + (count / secs).ToString("0") + " a second); private memory " + privateBefore + " MB before, " + peak + " MB at the peak, " + privateAfter + " MB after";
                    return count == 100_000 && privateAfter < privateBefore + 150 ? line : "FAIL: " + line;
                });

                if (Only != null) Scenario("a 500 MB folder of big files: memory given back afterwards (only when asked for by name)", 600, () =>
                {
                    string src = Path.Combine(Root, "src", "BigFolder");
                    Directory.CreateDirectory(src);
                    for (int i = 0; i < 5; i++) File.Move(Big("big" + i + ".bin", 100), Path.Combine(src, "big" + i + ".bin"));
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long before = Process.GetCurrentProcess().PrivateMemorySize64 >> 20, peak = 0;
                    string[]? landed = null;
                    host.ClipboardFilesReceived += p => landed = p;
                    var clock = Stopwatch.StartNew();
                    var send = Task.Run(() => ctrl.Files!.SendFiles(new[] { src }));
                    while ((!send.IsCompleted || landed == null) && clock.Elapsed.TotalSeconds < 550)
                    {
                        Thread.Sleep(200);
                        peak = Math.Max(peak, Process.GetCurrentProcess().PrivateMemorySize64 >> 20);
                    }
                    double secs = clock.Elapsed.TotalSeconds;
                    Thread.Sleep(4000);
                    var me = Process.GetCurrentProcess();
                    me.Refresh();
                    long after = me.PrivateMemorySize64 >> 20;
                    string line = "500 MB in " + secs.ToString("0.0") + " s; private memory " + before + " MB before, " + peak + " MB at the peak, " + after + " MB after";
                    return landed != null && after < before + 60 ? line : "FAIL: " + line;
                });

                Scenario("64 MB each way at the same moment", 120, () =>
                {
                    string a = Big("both-a.bin", 64), b = Big("both-b.bin", 64);
                    string[]? toHost = null, toClient = null;
                    host.ClipboardFilesReceived += p => toHost = p;
                    ctrl.ClipboardFilesReceived += p => toClient = p;
                    var t1 = Task.Run(() => ctrl.Files!.SendFiles(new[] { a }));
                    var t2 = Task.Run(() => host.ControllerFiles!.SendFiles(new[] { b }));
                    Task.WaitAll(t1, t2);
                    for (int i = 0; i < 500 && (toHost == null || toClient == null); i++) Thread.Sleep(20);
                    bool ok = toHost != null && toClient != null && Same(a, toHost[0]) && Same(b, toClient[0]);
                    return ok ? "both arrived whole" : "FAIL: to host " + (toHost != null) + ", to client " + (toClient != null);
                });

                Scenario("both PCs agree: 64 MB from the host, then 64 MB to it", 120, () =>
                {
                    var (hostSays, clientSays) = BothSay(host, ctrl, "agree-down.bin", () => host.ControllerFiles!.SendFiles(new[] { Big("agree-down.bin", 64) }));
                    var (hostSays2, clientSays2) = BothSay(host, ctrl, "agree-up.bin", () => ctrl.Files!.SendFiles(new[] { Big("agree-up.bin", 64) }));
                    bool ok = hostSays?.Failed == false && clientSays?.Failed == false && hostSays2?.Failed == false && clientSays2?.Failed == false;
                    return ok ? "both sides said it worked, both ways" : "FAIL: down: host " + hostSays?.Result + " / client " + clientSays?.Result + "; up: host " + hostSays2?.Result + " / client " + clientSays2?.Result;
                });

                Scenario("the receiving PC stalls for 8 seconds in the middle of 128 MB", 120, () =>
                {
                    string file = Big("stall.bin", 128);
                    var clock = Stopwatch.StartNew();
                    var (hostSays, clientSays) = BothSay(host, ctrl, "stall.bin", () =>
                    {
                        ctrl.Files!.TestPause(8000); // the client stops reading first, as a busy or frozen PC would
                        host.ControllerFiles!.SendFiles(new[] { file });
                    });
                    if (clock.ElapsedMilliseconds < 7500) return "FAIL: the stall did not happen (" + clock.ElapsedMilliseconds + " ms)";
                    bool ok = hostSays?.Failed == false && clientSays?.Failed == false;
                    return ok ? "it waited out the stall and finished; both sides said so" : "FAIL: host said " + hostSays?.Result + ", client said " + clientSays?.Result;
                });

                Scenario("speed both ways (paced from the sound)", 120, () =>
                {
                    double down = Timed(host, ctrl, "speed-down.bin", 256, () => host.ControllerFiles!.SendFiles(new[] { Big("speed-down.bin", 256) }));
                    double up = Timed(host, ctrl, "speed-up.bin", 256, () => ctrl.Files!.SendFiles(new[] { Big("speed-up.bin", 256) }));
                    return down > 50 && up > 50 ? "256 MB from the host at " + down.ToString("0") + " MB/s, to it at " + up.ToString("0") + " MB/s" : "FAIL: only " + down.ToString("0") + " and " + up.ToString("0") + " MB/s";
                });

                Scenario("copying something new while 256 MB is still going", 120, () =>
                {
                    string big = Big("replaced.bin", 256), small = Big("replacement.bin", 1);
                    var results = new List<FileChannel.Transfer>();
                    ctrl.TransferProgress += t => { if (t.Finished) lock (results) results.Add(t); };
                    var hostResults = new List<FileChannel.Transfer>();
                    host.TransferProgress += t => { if (t.Finished) lock (hostResults) hostResults.Add(t); };
                    string[]? landed = null;
                    host.ClipboardFilesReceived += p => landed = p;
                    host.ControllerFiles!.TestPause(1500); // the receiving side holds the first copy up, so it is surely still going
                    var first = Task.Run(() => ctrl.Files!.SendFiles(new[] { big }));
                    Thread.Sleep(300);
                    ctrl.Files!.SendFiles(new[] { small });
                    first.Wait();
                    for (int i = 0; i < 300 && landed == null; i++) Thread.Sleep(20);
                    bool replaced = landed != null && Path.GetFileName(landed[0]) == "replacement.bin" && Same(small, landed[0]);
                    bool stopped = results.Any(r => r.Failed && r.What == "replaced.bin");
                    Thread.Sleep(300);
                    // The receiver either said the old one stopped, or never started it (its stop came first): never that it arrived.
                    bool hostAgrees = hostResults.All(r => r.What != "replaced.bin" || r.Cancelled) && results.Any(r => r.Cancelled && r.What == "replaced.bin");
                    return replaced && stopped && hostAgrees ? "the old one stopped (both sides said so), the new one arrived" : "FAIL: replaced " + replaced + " (landed: " + (landed == null ? "nothing" : string.Join(", ", landed)) + "), sender said stopped " + stopped + ", receiver said stopped " + hostAgrees;
                });

                Scenario("a file another program has locked", 30, () =>
                {
                    string locked = Big("locked.bin", 1);
                    using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    FileChannel.Transfer? result = null;
                    ctrl.TransferProgress += t => { if (t.Finished && t.What == "locked.bin") result = t; };
                    ctrl.Files!.SendFiles(new[] { locked });
                    hold.Dispose();
                    string after = Big("after-lock.bin", 1);
                    string[]? landed = null;
                    host.ClipboardFilesReceived += p => landed = p;
                    ctrl.Files.SendFiles(new[] { after });
                    for (int i = 0; i < 300 && landed == null; i++) Thread.Sleep(20);
                    return result?.Failed == true && landed != null ? "said it could not send it (" + result.Result + "), and the next copy still worked" : "FAIL: result " + result?.Result + ", next copy arrived " + (landed != null);
                });

                Scenario("50 MB of clipboard text", 60, () =>
                {
                    // Built without string.Concat, which leaves 100+ MB buffers in .NET's shared pool.
                    var chars = new char[52_250_000];
                    const string line = "Fifty megabytes of clipboard, every character checked. ";
                    for (int i = 0; i < chars.Length; i++) chars[i] = line[i % line.Length];
                    string text = new string(chars);
                    chars = null!;
                    string? got = null;
                    Action<string> took = t => got = t;
                    host.ClipboardReceived += took;
                    ctrl.SendClipboard(text);
                    for (int i = 0; i < 1500 && got == null; i++) Thread.Sleep(20);
                    host.ClipboardReceived -= took;
                    string result = got == text ? (text.Length / 1_000_000) + " million characters arrived exactly" : "FAIL: " + (got?.Length.ToString() ?? "nothing");
                    got = null; // let it go, so the leak check sees TailRemote's own memory
                    return result;
                });

                Scenario("50 MB of clipboard text three times: memory must not grow", 120, () =>
                {
                    // Nothing here keeps the text: only its length is noted. If TailRemote held on to
                    // what it shared, memory would climb by about 100 MB a round.
                    var sizes = new List<long>();
                    for (int round = 0; round < 3; round++)
                    {
                        int length = -1;
                        Action<string> took = t => length = t.Length;
                        host.ClipboardReceived += took;
                        ctrl.SendClipboard(new string((char)('a' + round), 60_000_000)); // made directly: string.Concat keeps big buffers in .NET's shared pool, which looked like a leak
                        for (int i = 0; i < 1500 && length < 0; i++) Thread.Sleep(20);
                        host.ClipboardReceived -= took;
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        sizes.Add(GC.GetTotalMemory(true) >> 20);
                    }                    long growth = sizes[2] - sizes[0];
                    return growth < 30 ? "memory after each round: " + string.Join(", ", sizes) + " MB, so nothing is kept" : "FAIL: memory grew " + growth + " MB over three rounds (" + string.Join(", ", sizes) + ")";
                });

                Scenario("a hostile PC sending files that try to escape the holding folder", 30, () =>
                {
                    string outside = Path.Combine(Root, "escaped.txt");
                    string? arrived = null;
                    host.ClipboardFilesReceived += p => arrived = string.Join(",", p);
                    foreach (string evil in new[] { "..\\escaped.txt", "../escaped.txt", "C:\\Windows\\escaped.txt", "\\\\server\\share\\x.txt", "a\\..\\..\\escaped.txt", "con.txt\u0000x", "" })
                        ctrl.Files!.TestSendEntries(new List<(string, byte[]?)> { (evil, Encoding.UTF8.GetBytes("pwned")) });
                    Thread.Sleep(1500);
                    string? acceptedEvil = arrived; // before the normal copy below, which should arrive
                    bool escaped = File.Exists(outside) || File.Exists("C:\\Windows\\escaped.txt");
                    string next = Big("still-fine.bin", 1);
                    string[]? landed = null;
                    host.ClipboardFilesReceived += p => landed = p;
                    ctrl.Files!.SendFiles(new[] { next });
                    for (int i = 0; i < 300 && landed == null; i++) Thread.Sleep(20);
                    return !escaped && acceptedEvil == null && landed != null ? "every escape was refused, and normal copies still work" : "FAIL: escaped " + escaped + ", accepted " + acceptedEvil;
                });

                Scenario("a hostile PC sending names Windows keeps for itself (CON, NUL.txt, COM1, names ending in a dot or a space): refused, saying why", 30, () =>
                {
                    string? arrived = null;
                    Action<string[]> gotFiles = p => arrived = string.Join(",", p);
                    var said = new List<string>();
                    Action<FileChannel.Transfer> note = t => { if (t.Finished && t.Outgoing && t.What == "reserved") lock (said) said.Add(t.Result ?? ""); };
                    host.ClipboardFilesReceived += gotFiles;
                    ctrl.TransferProgress += note;
                    var names = new[] { "CON", "nul.txt", "sub\\AUX", "sub\\com1.log", "LPT9.dat", "trailing dot.", "trailing space ", "folder.\\x.txt" };
                    try
                    {
                        foreach (string bad in names) ctrl.Files!.TestSendEntries(new List<(string, byte[]?)> { (bad, Encoding.UTF8.GetBytes("x")) }, "reserved");
                        WaitFor(() => { lock (said) return said.Count >= names.Length; }, 3000);
                    }
                    finally { host.ClipboardFilesReceived -= gotFiles; ctrl.TransferProgress -= note; }
                    List<string> results;
                    lock (said) results = said.ToList();
                    bool clear = results.Count == names.Length && results.All(r => r.Contains("Windows"));
                    return arrived == null && clear ? "all " + names.Length + " refused, each saying why (" + results[0] + ")"
                        : "FAIL: arrived " + arrived + "; " + results.Count + " of " + names.Length + " said why: " + string.Join(" | ", results);
                });

                Scenario("over a million files and folders in one go: refused cleanly by both PCs, never left waiting", 120, () =>
                {
                    var sent = new System.Collections.Concurrent.ConcurrentQueue<FileChannel.Transfer>();
                    var there = new System.Collections.Concurrent.ConcurrentQueue<FileChannel.Transfer>();
                    Action<FileChannel.Transfer> onCtrl = t => { if (t.Finished && t.What == "million") sent.Enqueue(t); };
                    Action<FileChannel.Transfer> onHost = t => { if (t.Finished && t.What == "million") there.Enqueue(t); };
                    ctrl.TransferProgress += onCtrl;
                    host.TransferProgress += onHost;
                    try
                    {
                        var entries = new List<(string, byte[]?)>(FileChannel.MaxEntries + 1);
                        for (int i = 0; i <= FileChannel.MaxEntries; i++) entries.Add(("million\\d" + i, null));
                        // The receiver's own check: offered anyway, as an old or hostile PC could (this blocks until it ends).
                        ctrl.Files!.TestSendEntries(entries, "million", checkCount: false);
                        if (!WaitFor(() => sent.Count >= 1, 3000)) return "FAIL: the receiver never answered an offer of over a million";
                        // This side's own check: refused before anything is offered, and the other PC told.
                        ctrl.Files!.TestSendEntries(entries, "million");
                        WaitFor(() => sent.Count >= 2 && there.Count >= 1, 5000);
                    }
                    finally { ctrl.TransferProgress -= onCtrl; host.TransferProgress -= onHost; }
                    var s = sent.ToArray();
                    var h = there.ToArray();
                    bool ok = s.Length == 2 && s.All(t => t.Failed && !t.Cancelled && t.Result!.Contains("million")) && h.Length == 1 && h[0].Failed && h[0].Result!.Contains("million");
                    return ok ? "refused by the receiver (" + s[0].Result + "), and by the sender before offering, with the other PC told (" + h[0].Result + ")"
                        : "FAIL: sender said " + string.Join(" | ", s.Select(t => t.Result)) + "; receiver said " + string.Join(" | ", h.Select(t => t.Result));
                });

                Scenario("a folder with one file another program has locked: both PCs say it failed, and why", 60, () =>
                {
                    string src = Path.Combine(Root, "src", "Locked folder");
                    Directory.CreateDirectory(src);
                    for (int i = 0; i < 3; i++) File.Move(Big("locked-folder" + i + ".bin", 1), Path.Combine(src, "file" + i + ".bin"), true);
                    FileChannel.Transfer? h, c;
                    using (new FileStream(Path.Combine(src, "file1.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        (h, c) = BothSay(host, ctrl, "Locked folder", () => ctrl.Files!.SendFiles(new[] { src }, toClipboard: false));
                    bool ok = h != null && c != null && h.Failed && c.Failed && !h.Cancelled && !c.Cancelled
                        && h.Result!.Contains("could not send", StringComparison.OrdinalIgnoreCase) && c.Result!.Contains("could not send", StringComparison.OrdinalIgnoreCase);
                    return ok ? "both said it failed; sender: " + c!.Result + "; receiver: " + h!.Result
                        : "FAIL: sender said " + c?.Result + " (cancelled " + c?.Cancelled + "); receiver said " + h?.Result + " (cancelled " + h?.Cancelled + ")";
                });

                Scenario("getting something that is not there any more: both PCs see it end within 3 seconds", 20, () =>
                {
                    string name = "not-there-" + Guid.NewGuid().ToString("N") + ".bin";
                    string missing = Path.Combine(Root, "src", name);
                    FileChannel.Transfer? got = null, hostSaid = null;
                    Action<FileChannel.Transfer> onCtrl = t => { if (t.Finished && !t.Outgoing && t.What == name) got = t; };
                    Action<FileChannel.Transfer> onHost = t => { if (t.Finished && t.Outgoing && t.What == name) hostSaid = t; };
                    ctrl.TransferProgress += onCtrl;
                    host.TransferProgress += onHost;
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        if (!ctrl.Fetch(new[] { missing })) return "FAIL: could not ask";
                        WaitFor(() => got != null && hostSaid != null, 3000);
                    }
                    finally { ctrl.TransferProgress -= onCtrl; host.TransferProgress -= onHost; }
                    long ms = clock.ElapsedMilliseconds;
                    return got?.Failed == true && hostSaid?.Failed == true && ms <= 3100 ? "ended on both PCs in " + ms + " ms; the controlling PC said: " + got.Result
                        : "FAIL: after " + ms + " ms the controlling PC said " + (got?.Result ?? "nothing") + ", the host said " + (hostSaid?.Result ?? "nothing");
                });

                Scenario("pressing Stop during 2,000 small files: every file not marked incomplete is exactly its source", 120, () =>
                {
                    string src = Path.Combine(Root, "src", "Small stop");
                    var rnd = new Random(7);
                    for (int d = 0; d < 20; d++)
                    {
                        string dir = Path.Combine(src, "d" + d);
                        Directory.CreateDirectory(dir);
                        for (int f = 0; f < 100; f++)
                        {
                            var data = new byte[rnd.Next(1, 96 << 10)];
                            rnd.NextBytes(data);
                            File.WriteAllBytes(Path.Combine(dir, "f" + f + ".bin"), data);
                        }
                    }
                    FileChannel.Transfer? hostEnd = null;
                    bool moving = false;
                    Action<FileChannel.Transfer> note = t => { if (!t.Outgoing && t.What.StartsWith("Small stop")) { if (t.Finished) hostEnd = t; else if (t.Done > 0) moving = true; } };
                    host.TransferProgress += note;
                    try
                    {
                        var send = Task.Run(() => ctrl.Files!.SendFiles(new[] { src }, toClipboard: false));
                        WaitFor(() => moving || hostEnd != null, 20_000);
                        ctrl.CancelTransfer();
                        send.Wait(20_000);
                        WaitFor(() => hostEnd != null, 5000);
                    }
                    finally { host.TransferProgress -= note; }
                    if (hostEnd == null) return "FAIL: the receiver never said how it ended";
                    string landed = Path.Combine(FileChannel.Downloads, hostEnd.What);
                    if (!Directory.Exists(landed)) return "FAIL: nothing kept (" + hostEnd.Result + ")";
                    int whole = 0, incomplete = 0;
                    var wrong = new List<string>();
                    foreach (string f in Directory.EnumerateFiles(landed, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(landed, f);
                        if (Path.GetFileName(f).Contains("(incomplete")) { incomplete++; continue; }
                        string source = Path.Combine(src, rel);
                        if (File.Exists(source) && Same(source, f)) whole++; else wrong.Add(rel);
                    }
                    return wrong.Count == 0 && whole + incomplete > 0
                        ? whole + " whole files all exactly their sources, " + incomplete + " kept marked incomplete (" + (hostEnd.Cancelled ? "stopped part-way" : "it finished before Stop") + ")"
                        : "FAIL: " + wrong.Count + " files look whole but are not their sources (" + string.Join(", ", wrong.Take(5)) + "); " + whole + " right, " + incomplete + " incomplete";
                });

                Scenario("Send files: into Downloads, never over what is there, and the right kind of transfer", 60, () =>
                {
                    string file = Big("report.bin", 2);
                    var ends = new List<FileChannel.Transfer>();
                    Action<FileChannel.Transfer> note = t => { if (t.Finished) lock (ends) ends.Add(t); };
                    ctrl.TransferProgress += note;
                    host.TransferProgress += note;
                    for (int round = 0; round < 2; round++)
                    {
                        int before = ends.Count;
                        host.ControllerFiles!.SendFiles(new[] { file }, toClipboard: false);
                        for (int i = 0; i < 300 && ends.Count < before + 2; i++) Thread.Sleep(20);
                    }
                    ctrl.Files!.SendText(new string('q', 50_000)); // clipboard text, the long way
                    for (int i = 0; i < 300 && ends.Count < 6; i++) Thread.Sleep(20);
                    ctrl.TransferProgress -= note;
                    host.TransferProgress -= note;
                    bool both = File.Exists(Path.Combine(FileChannel.Downloads, "report.bin")) && File.Exists(Path.Combine(FileChannel.Downloads, "report (2).bin"))
                        && Same(file, Path.Combine(FileChannel.Downloads, "report (2).bin"));
                    bool kinds = ends.Where(t => t.What == "report.bin").All(t => !t.Clipboard) && ends.Where(t => t.What == "clipboard text").All(t => t.Clipboard)
                        && ends.Count(t => t.What == "clipboard text") == 2;
                    return both && kinds ? "both copies kept (report, report (2)), and files and clipboard each told apart" : "FAIL: both copies " + both + ", kinds right " + kinds + " (" + ends.Count + " ends)";
                });

                Scenario("every file lane cut 5 times in the middle of 256 MB: it carries on", 120, () =>
                {
                    string file = Big("cut.bin", 256);
                    string[]? landed = null;
                    host.ClipboardFilesReceived += p => landed = p;
                    int cuts = 0;
                    var (hostSays, clientSays) = BothSay(host, ctrl, "cut.bin", () =>
                    {
                        var send = Task.Run(() => ctrl.Files!.SendFiles(new[] { file }));
                        for (; cuts < 5 && !send.IsCompleted; cuts++)
                        {
                            Thread.Sleep(150);
                            if (cuts % 2 == 0) ctrl.Files!.TestCutLanes(); else host.ControllerFiles!.TestCutLanes();
                        }
                        send.Wait();
                    });
                    bool ok = hostSays?.Failed == false && clientSays?.Failed == false && landed != null && Same(file, landed[0]);
                    return ok ? "cut " + cuts + " times, arrived whole, both sides said so (" + ctrl.Files!.TestLanes + " lanes open after)"
                        : "FAIL: after " + cuts + " cuts host said " + hostSays?.Result + ", client said " + clientSays?.Result;
                });

                Scenario("the receiving PC stalls for 20 seconds: lanes are reopened and it finishes", 120, () =>
                {
                    string file = Big("stall20.bin", 64);
                    var clock = Stopwatch.StartNew();
                    var (hostSays, clientSays) = BothSay(host, ctrl, "stall20.bin", () =>
                    {
                        ctrl.Files!.TestPause(20_000);
                        host.ControllerFiles!.SendFiles(new[] { file });
                    });
                    bool ok = hostSays?.Failed == false && clientSays?.Failed == false && clock.ElapsedMilliseconds > 19_000;
                    return ok ? "finished " + clock.Elapsed.TotalSeconds.ToString("0") + " s after the stall began; both sides said so" : "FAIL: host said " + hostSays?.Result + ", client said " + clientSays?.Result;
                });

                Scenario("clipboard text sent the moment every lane is cut", 30, () =>
                {
                    string? got = null;
                    Action<string> took = t => got = t;
                    host.ClipboardReceived += took;
                    ctrl.Files!.TestCutLanes();
                    ctrl.SendClipboard("redundant hello");
                    for (int i = 0; i < 1000 && got == null; i++) Thread.Sleep(20);
                    host.ClipboardReceived -= took;
                    return got == "redundant hello" ? "it arrived once the lanes came back" : "FAIL: got " + (got ?? "nothing");
                });

                Scenario("300 files at once with Send files", 120, () =>
                {
                    var items = new List<string>();
                    for (int i = 0; i < 300; i++)
                    {
                        string p = Path.Combine(Root, "many", "file" + i + ".dat");
                        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                        File.WriteAllBytes(p, Encoding.UTF8.GetBytes(new string((char)('a' + i % 26), 1000 + i * 37)));
                        items.Add(p);
                    }
                    var (hostSays, clientSays) = BothSay(host, ctrl, "300 items", () => ctrl.Files!.SendFiles(items, toClipboard: false));
                    int same = items.Count(p => File.Exists(Path.Combine(FileChannel.Downloads, Path.GetFileName(p))) && Same(p, Path.Combine(FileChannel.Downloads, Path.GetFileName(p))));
                    return same == 300 && hostSays?.Failed == false && clientSays?.Failed == false ? "all 300 arrived exactly" : "FAIL: " + same + " of 300 arrived; host said " + hostSays?.Result + ", client said " + clientSays?.Result;
                });

                Scenario("ten 32 MB files at once, on every lane and core", 120, () =>
                {
                    var items = Enumerable.Range(0, 10).Select(i => Big("ten" + i + ".bin", 32)).ToList();
                    string[]? landed = null;
                    host.ClipboardFilesReceived += p => landed = p;
                    int together = 0, togetherThere = 0;
                    Action<FileChannel.Transfer> watch = t => together = Math.Max(together, t.Files.Count);
                    Action<FileChannel.Transfer> watchThere = t => togetherThere = Math.Max(togetherThere, t.Files.Count);
                    ctrl.TransferProgress += watch;
                    host.TransferProgress += watchThere;
                    ctrl.Files!.TestPause(300); // confirmations held up briefly, so the files are seen moving side by side
                    ctrl.Files!.TestMostActive = 0; // counted for this transfer only
                    var clock = Stopwatch.StartNew();
                    var (hostSays, clientSays) = BothSay(host, ctrl, "10 items", () => ctrl.Files!.SendFiles(items));
                    ctrl.TransferProgress -= watch;
                    host.TransferProgress -= watchThere;
                    double mbs = 320 / clock.Elapsed.TotalSeconds;
                    for (int i = 0; i < 250 && landed == null; i++) Thread.Sleep(20); // put on the clipboard just after it says it arrived
                    bool ok = landed != null && landed.Length == 10 && items.All(p => Same(p, Path.Combine(Path.GetDirectoryName(landed[0])!, Path.GetFileName(p))));
                    together = Math.Max(together, ctrl.Files!.TestMostActive); // counted by the engine itself: a fast transfer can end between two progress reports
                    if (together < 2) return "FAIL: the files went one at a time (at most " + together + " moving at once)";
                    return ok && hostSays?.Failed == false && clientSays?.Failed == false ? "all ten arrived exactly, at " + mbs.ToString("0") + " MB/s on " + ctrl.Files!.TestLanes + " lanes, up to " + together + " files moving at once (" + togetherThere + " seen arriving at once)" : "FAIL: host said " + hostSays?.Result + ", client said " + clientSays?.Result;
                });

                Scenario("the host sends a file to 10 controlling PCs at once: every one gets it, and the host says so for each", 120, () =>
                {
                    var others = Enumerable.Range(0, 9).Select(_ => Connect(Password)).ToList();
                    var everyone = new List<Client>(others) { ctrl };
                    for (int i = 0; i < 200 && host.Connected.Controlling < 10; i++) Thread.Sleep(20);
                    for (int i = 0; i < 200 && everyone.Any(c => c.Files!.TestLanes == 0); i++) Thread.Sleep(20);
                    Thread.Sleep(500); // every controller's first lane has reached the host
                    string file = Big("to-everyone.bin", 8);
                    var got = new System.Collections.Concurrent.ConcurrentBag<string>();
                    var handlers = everyone.Select(c => { Action<string[]> h = p => { if (Path.GetFileName(p[0]) == "to-everyone.bin" && Same(file, p[0])) got.Add(p[0]); }; c.ClipboardFilesReceived += h; return (c, h); }).ToList();
                    var hostSaid = new System.Collections.Concurrent.ConcurrentBag<FileChannel.Transfer>();
                    Action<FileChannel.Transfer> note = t => { if (t.Finished && t.Outgoing && t.What == "to-everyone.bin") hostSaid.Add(t); };
                    host.TransferProgress += note;
                    host.SendClipboardFiles(new[] { file });
                    for (int i = 0; i < 3000 && (got.Count < 10 || hostSaid.Count < 10); i++) Thread.Sleep(20);
                    host.TransferProgress -= note;
                    foreach (var (c, h) in handlers) c.ClipboardFilesReceived -= h;
                    int connectedThen = host.Connected.Controlling;
                    foreach (var o in others) o.Dispose();
                    var wrong = hostSaid.Where(t => t.Failed || t.Done != t.Total || t.Total != 8 << 20).Select(t => t.Result).ToList();
                    return got.Count == 10 && hostSaid.Count == 10 && wrong.Count == 0
                        ? "all 10 got it whole, and the host said sent, with its size, for every one (" + connectedThen + " controlling)"
                        : "FAIL: " + got.Count + " of 10 got it; the host said " + hostSaid.Count + " results, wrong: " + string.Join(" | ", wrong);
                });

                Scenario("the connection going silent while a key is held: the host lets it go within 2 seconds (F24 only, recorded, never typed)", 30, () =>
                {
                    long gen = CurrentGeneration; // so a late finally (this scenario timed out but keeps running) does not clobber a later one
                    var pressed = new System.Collections.Concurrent.ConcurrentQueue<(ushort Vk, bool Up, long At)>();
                    var clock = Stopwatch.StartNew();
                    Native.KeySink = (vk, scan, up, ext) => { pressed.Enqueue((vk, up, clock.ElapsedMilliseconds)); return true; };
                    var c5 = Connect(Password);
                    try
                    {
                        const ushort F24 = 0x87; // does nothing on any PC
                        Thread.Sleep(300);
                        c5.SendKey(F24, 0, false, false); // held down...
                        Thread.Sleep(1000);
                        bool stillHeld = !pressed.Any(k => k.Up);
                        long silentAt = clock.ElapsedMilliseconds;
                        c5.TestSilent = true; // ...and then the connection goes quiet, never sending the release
                        for (int i = 0; i < 200 && !pressed.Any(k => k.Up); i++) Thread.Sleep(20);
                        var release = pressed.Where(k => k.Up && k.Vk == F24).Select(k => (long?)k.At).FirstOrDefault();
                        return stillHeld && release is long at && at - silentAt < 2500
                            ? "held while the connection was alive, and let go " + (at - silentAt) + " ms after it went quiet"
                            : "FAIL: held while alive " + stillHeld + ", released " + (release is long a ? (a - silentAt) + " ms after going quiet" : "never");
                    }
                    finally { if (CurrentGeneration == gen) Native.KeySink = null; c5.Dispose(); }
                });

                Scenario("the mouse: a held button let go when the line goes quiet, a listener can't touch the mouse, nonsense mouse messages ignored (recorded, never moved)", 30, () =>
                {
                    long gen = CurrentGeneration;
                    var buttons = new System.Collections.Concurrent.ConcurrentQueue<(ushort Vk, bool Up)>();
                    var mice = new System.Collections.Concurrent.ConcurrentQueue<(byte Kind, int A, int B)>();
                    Native.KeySink = (vk, scan, up, ext) => { buttons.Enqueue((vk, up)); return true; };
                    Native.MouseSink = (kind, a, b) => { mice.Enqueue((kind, a, b)); return true; };
                    var listener = Connect(ListenPassword);
                    var c6 = Connect(Password);
                    try
                    {
                        if (!c6.CanMouse) return "FAIL: the host did not offer the mouse";
                        if (listener.CanMouse) return "FAIL: a listener was offered the mouse";
                        Thread.Sleep(300);
                        // A listener tries everything: nothing may happen.
                        listener.SendMouse(Protocol.MouseMove, 500, 500);
                        listener.SendKey(Native.VkLeftButton, 0, false, false);
                        // Nonsense: an unknown kind, a too-short and a too-long message.
                        c6.TestWrite(new byte[] { Protocol.Mouse, 77, 1, 0, 1, 0 });
                        c6.TestWrite(new byte[] { Protocol.Mouse, 0 });
                        c6.TestWrite(new byte[] { Protocol.Mouse, 0, 1, 0, 1, 0, 9, 9 });
                        // Huge moves are clamped, not wrapped round.
                        c6.SendMouse(Protocol.MouseMove, 100_000, -100_000);
                        c6.SendKey(Native.VkLeftButton, 0, false, false); // a drag starts...
                        Thread.Sleep(800);
                        bool listenerIgnored = !buttons.Any(b => b.Vk == Native.VkLeftButton && b.Up) && buttons.Count(b => b.Vk == Native.VkLeftButton) == 1;
                        var moves = mice.ToArray();
                        bool clamped = moves.Any(x => x == (Protocol.MouseMove, short.MaxValue, short.MinValue));
                        bool onlyGood = moves.All(x => x.Kind != 77) && !moves.Any(x => x == (Protocol.MouseMove, 500, 500));
                        c6.TestSilent = true; // ...and the line goes quiet in the middle of it
                        for (int i = 0; i < 200 && !buttons.Any(b => b.Up); i++) Thread.Sleep(20);
                        bool letGo = buttons.Any(b => b.Vk == Native.VkLeftButton && b.Up);
                        return listenerIgnored && clamped && onlyGood && letGo
                            ? "the held button was let go when the line went quiet; the listener and the nonsense did nothing; huge moves were clamped"
                            : "FAIL: listener ignored " + listenerIgnored + ", clamped " + clamped + ", only good moves " + onlyGood + ", let go " + letGo + " (" + moves.Length + " moves)";
                    }
                    finally { if (CurrentGeneration == gen) { Native.KeySink = null; Native.MouseSink = null; } c6.Dispose(); listener.Dispose(); }
                });

                Scenario("hosting as the service: keys go from the host to the agent in the session (F24 only, recorded, never typed)", 30, () =>
                {
                    long gen = CurrentGeneration; // so a late finally (this scenario timed out but keeps running) does not clobber a later one
                    AgentLink.TestAnyOwner = true;
                    var server = new AgentLink.Server();
                    var got = new System.Collections.Concurrent.ConcurrentQueue<(ushort Vk, bool Up)>();
                    string? text = null;
                    var agentScreen = new TestScreen();
                    var mice = new System.Collections.Concurrent.ConcurrentQueue<(byte Kind, int A, int B)>();
                    new Thread(() => AgentLink.Run((vk, scan, up, ext) => got.Enqueue((vk, up)), (kind, a, b) => mice.Enqueue((kind, a, b)), t => text = t, _ => { }, null, new ScreenVideo(agentScreen.Take))) { IsBackground = true }.Start();
                    Thread.Sleep(1500); // the agent connects
                    Native.KeySink = server.Key; // every key the host types goes to the agent from here
                    Native.MouseSink = server.Mouse; // and every mouse move
                    try
                    {
                        const ushort F24 = 0x87; // does nothing on any PC
                        for (int i = 0; i < 20; i++) { ctrl.SendKey(F24, 0, false, false); ctrl.SendKey(F24, 0, true, false); }
                        // The mouse: a right click (as keys, recorded), a move each way and a wheel notch each way.
                        ctrl.SendKey(Native.VkRightButton, 0, false, false);
                        ctrl.SendKey(Native.VkRightButton, 0, true, false);
                        ctrl.SendMouse(Protocol.MouseMove, -300, 25);
                        ctrl.SendMouse(Protocol.MouseWheel, -120, 0);
                        ctrl.SendMouse(Protocol.MouseSideWheel, 120, 0);
                        server.Text("for the clipboard, with no window open");
                        for (int i = 0; i < 150 && (got.Count < 42 || mice.Count < 3 || text == null); i++) Thread.Sleep(20);
                    }
                    finally { if (CurrentGeneration == gen) { Native.KeySink = null; Native.MouseSink = null; } }
                    bool ok = got.Count == 42 && got.Take(40).All(k => k.Vk == 0x87) && got.Skip(40).All(k => k.Vk == Native.VkRightButton) && text == "for the clipboard, with no window open";
                    if (!ok) return "FAIL: " + got.Count + " of 42 keys and buttons, text " + (text ?? "none");
                    var m = mice.ToArray();
                    if (m.Length != 3 || m[0] != (Protocol.MouseMove, -300, 25) || m[1] != (Protocol.MouseWheel, -120, 0) || m[2] != (Protocol.MouseSideWheel, 120, 0))
                        return "FAIL: the mouse reached the agent as " + string.Join(", ", m.Select(x => x.Kind + " " + x.A + " " + x.B));
                    // The remote screen, which only the agent can see: pictures come from it, through the service.
                    host.VideoUpdate = server.Video;
                    host.VideoForget = server.ForgetVideo;
                    using (var watch = ctrl.WatchScreen()!)
                    {
                        if (!WaitFor(() => watch.Pictures >= 1, 10_000)) return "FAIL: no picture came from the agent";
                        agentScreen.SquareX = 420;
                        if (!WaitFor(() => TestScreen.Near(watch.Canvas.At(430, 110), agentScreen.At(430, 110), 40), 10_000)) return "FAIL: a change at the agent's screen did not show";
                    }
                    return "all 40 key presses and releases reached the agent, in order, a right click, mouse moves and wheel turns too, the clipboard text too, and the agent's screen was seen and followed";
                });

                Scenario("hosting as the service: the window sends and receives through the agent", 60, () =>
                {
                    ServiceLink.TestAnyOwner = true; // this test plays the agent itself, so its pipe is not SYSTEM's
                    var server = new ServiceLink.Server(host);
                    string? toWindow = null, toController = null;
                    string[]? windowFiles = null, controllerFiles = null;
                    FileChannel.Transfer? windowSaw = null;
                    host.ClipboardReceived += t => { if (!server.Text(t)) toWindow = "agent kept it"; };
                    host.ClipboardFilesReceived += p => server.Files(p);
                    host.TransferProgress += t => server.Transfer(t);
                    using var window = new ServiceLink.Client();
                    window.TextArrived += t => toWindow = t;
                    window.FilesArrived += p => windowFiles = p;
                    window.TransferProgress += t => { if (t.Finished) windowSaw = t; };
                    Action<string> took = t => toController = t;
                    Action<string[]> tookFiles = p => controllerFiles = p;
                    ctrl.ClipboardReceived += took;
                    ctrl.ClipboardFilesReceived += tookFiles;
                    for (int i = 0; i < 200 && !(window.Connected && window.HasController); i++) Thread.Sleep(20);
                    if (!window.Connected || !window.HasController) return "FAIL: the window never linked up (connected " + window.Connected + ")";
                    window.SendClipboard("from the window, through the agent");
                    ctrl.SendClipboard("from the controller, to the window");
                    // Each side's next send would replace its text still on the way, so the files wait for it.
                    for (int i = 0; i < 500 && (toWindow == null || toController == null); i++) Thread.Sleep(20);
                    string file = Big("via-service.bin", 3);
                    window.SendClipboardFiles(new[] { file });
                    for (int i = 0; i < 500 && controllerFiles == null; i++) Thread.Sleep(20);
                    // Checked before the next one: in this test both sides share one holding folder, and
                    // each arrival clears the older ones out of it.
                    bool sameFile = controllerFiles != null && Same(file, controllerFiles[0]);
                    ctrl.Files!.SendFiles(new[] { Big("to-window.bin", 2) });
                    for (int i = 0; i < 500 && (windowFiles == null || windowSaw == null); i++) Thread.Sleep(20);
                    ctrl.ClipboardReceived -= took;
                    ctrl.ClipboardFilesReceived -= tookFiles;
                    // A pipe of this name not owned by SYSTEM (as here, where the test plays the agent) is a
                    // stand-in for an impostor: with the check on, the window must refuse it.
                    ServiceLink.TestAnyOwner = false;
                    using var fooled = new ServiceLink.Client();
                    Thread.Sleep(3000);
                    bool refusedImpostor = !fooled.Connected;
                    ServiceLink.TestAnyOwner = true;
                    if (!refusedImpostor) return "FAIL: the window linked up with a pipe that is not the service's";
                    bool ok = toController == "from the window, through the agent" && toWindow == "from the controller, to the window"
                        && sameFile && windowFiles != null && windowSaw != null;
                    return ok ? "text both ways, a file each way, the window saw the transfer finish, and a pipe not owned by SYSTEM was refused"
                        : "FAIL: controller got " + toController + ", window got " + toWindow + ", files to controller " + (controllerFiles != null) + ", files to window " + (windowFiles != null) + ", window saw the end " + (windowSaw != null);
                });

                Scenario("hosting as the service: after switching user, the old session's window gets nothing more and its folder is forgotten", 30, () =>
                {
                    // A pipe name of its own: the scenario above leaves its link listening on the usual test name.
                    string pipes = Names.TestPipes;
                    Names.TestPipes = pipes + "Switch";
                    ServiceLink.TestAnyOwner = true;
                    try
                    {
                        uint mine = (uint)Process.GetCurrentProcess().SessionId;
                        uint atScreen = mine;
                        var folders = new System.Collections.Concurrent.ConcurrentQueue<string>();
                        var server = new ServiceLink.Server(host, () => atScreen) { FolderChosen = f => folders.Enqueue(f ?? "(usual)") };
                        string? got = null;
                        using (var window = new ServiceLink.Client())
                        {
                            window.TextArrived += t => got = t;
                            // The window says where files go on linking up even when it is the usual place.
                            if (!WaitFor(() => window.Connected && folders.Contains("(usual)"), 10_000))
                                return "FAIL: the window did not link up and say where files go (connected " + window.Connected + ", told " + string.Join(", ", folders) + ")";
                            if (!server.Text("before switching") || !WaitFor(() => got == "before switching", 5000)) return "FAIL: the window did not get text before switching (got " + got + ")";
                            int toldBefore = folders.Count;
                            atScreen = mine + 1000; // someone else is at the screen now
                            bool handed = server.Text("after switching");
                            bool forgot = WaitFor(() => folders.Skip(toldBefore).Contains("(usual)"), 5000);
                            if (handed || got == "after switching") return "FAIL: the old session's window was still handed text after switching (handed " + handed + ", got " + got + ")";
                            if (!forgot) return "FAIL: the folder was not forgotten when the old session's window went";
                        }
                        Thread.Sleep(2500); // the window's last try to link up again is over before the name goes back
                        return "the window said where files go on linking up; after switching user it was handed nothing, was closed, and received files went back to the usual place";
                    }
                    finally { Names.TestPipes = pipes; }
                });

                Scenario("the remote screen: its line cut 3 times, it comes back each time with the picture right", 60, () =>
                {
                    var test = new TestScreen();
                    var screen = new ScreenVideo(test.Take);
                    host.VideoUpdate = screen.Update;
                    host.VideoForget = screen.Forget;
                    using var watch = ctrl.WatchScreen();
                    if (watch == null) return "FAIL: the host did not offer its screen";
                    if (!WaitFor(() => watch.Pictures >= 1, 5000)) return "FAIL: no first picture";
                    for (int k = 1; k <= 3; k++)
                    {
                        int before = watch.Pictures;
                        test.SquareX = 60 * k;
                        watch.TestCut();
                        if (!WaitFor(() => watch.Reconnects >= k && watch.Pictures > before, 10_000)) return "FAIL: it did not come back after cut " + k;
                    }
                    bool right = WaitFor(() => TestScreen.Near(watch.Canvas.At(185, 105), test.At(185, 105), 40) && TestScreen.Near(watch.Canvas.At(5, 5), test.At(5, 5), 40), 5000);
                    return right ? "came back all 3 times, with the picture right" : "FAIL: the picture was wrong after coming back";
                });

                Scenario("the remote screen on a slow line (20 KB/s) with the screen always changing: it steps down and keeps up; then a fast line and a still screen: it climbs back to every pixel exact", 180, () =>
                {
                    var test = new TestScreen();
                    var screen = new ScreenVideo(test.Take) { NoVideo = true }; // the still pictures' steps are what this is about
                    host.VideoUpdate = screen.Update;
                    host.VideoForget = screen.Forget;
                    bool moving = true;
                    new Thread(() => { int i = 0; var rnd = new Random(5); while (moving) { test.SquareX = rnd.Next(0, 560); test.SquareY = rnd.Next(0, 300); Thread.Sleep(30); i++; } }) { IsBackground = true }.Start();
                    try
                    {
                        using var watch = ctrl.WatchScreen()!;
                        if (!WaitFor(() => watch.Pictures >= 1, 10_000)) return "FAIL: no first picture";
                        if (!WaitFor(() => host.TestVideoStep <= VideoSettings.Start, 10_000)) return "FAIL: a fast line did not start at a good step (" + host.TestVideoStep + ")";
                        Host.TestPaceCap = 20 << 10; // now the line slows down, mid-stream
                        if (!WaitFor(() => host.TestVideoStep > VideoSettings.Start + 1, 20_000)) return "FAIL: it never stepped down on a slow line (step " + host.TestVideoStep + ")";
                        int slowStep = host.TestVideoStep;
                        int before = watch.Pictures;
                        Thread.Sleep(5000);
                        int perSecond = (watch.Pictures - before) / 5;
                        if (perSecond < 2) return "FAIL: only " + perSecond + " pictures a second at step " + slowStep + " on a slow line";
                        moving = false;
                        Host.TestPaceCap = double.MaxValue;
                        var climbing = Stopwatch.StartNew();
                        bool exact = WaitFor(() => host.TestVideoStep == 0 &&
                            watch.Canvas.At(test.SquareX + 5, test.SquareY + 5) == test.At(test.SquareX + 5, test.SquareY + 5) && watch.Canvas.At(5, 5) == test.At(5, 5), 120_000);
                        return exact ? "on the slow line it went down to step " + slowStep + " and kept " + perSecond + " pictures a second; on a fast line it climbed back to every pixel exact in " + (int)climbing.Elapsed.TotalSeconds + " s"
                            : "FAIL: it did not climb back to exact (step " + host.TestVideoStep + ")";
                    }
                    finally { moving = false; Host.TestPaceCap = double.MaxValue; }
                });

                Scenario("the remote screen while things move: H.264, its line cut in the middle, it comes back with a key frame, and is exact once still", 90, () =>
                {
                    using (var probe = H264Encoder.Create(64, 64, 30, 500)) if (probe == null) return "no H.264 encoder on this PC: still pictures only (nothing to test)";
                    var test = new TestScreen();
                    var screen = new ScreenVideo(test.Take);
                    host.VideoUpdate = screen.Update;
                    host.VideoForget = screen.Forget;
                    bool go = true;
                    var mover = new Thread(() => { int i = 0; while (go) { i++; test.SquareX = 20 + i * 11 % 560; test.SquareY = 30 + i * 7 % 280; Thread.Sleep(16); } }) { IsBackground = true };
                    try
                    {
                        using var watch = ctrl.WatchScreen()!;
                        mover.Start();
                        if (!WaitFor(() => watch.VideoFrames >= 20, 10_000)) return "FAIL: moving things did not go as H.264";
                        int before = watch.VideoFrames;
                        watch.TestCut();
                        if (!WaitFor(() => watch.Reconnects >= 1 && watch.VideoFrames > before + 20, 15_000)) return "FAIL: after the cut, H.264 did not come back";
                        int peak = 0;
                        for (int i = 0; i < 20; i++) { Thread.Sleep(100); peak = Math.Max(peak, watch.PerSecond); }
                        go = false;
                        mover.Join();
                        bool exact = WaitFor(() => watch.Canvas.At(test.SquareX + 5, test.SquareY + 5) == test.At(test.SquareX + 5, test.SquareY + 5) && watch.Canvas.At(5, 5) == test.At(5, 5), 10_000);
                        if (!exact) return "FAIL: once still, the picture did not become exact";
                        return "H.264 at up to " + peak + " frames a second, back after the cut with a key frame, exact once still";
                    }
                    finally { go = false; }
                });

                Scenario("the remote screen on a viewer that cannot decode H.264: it asks for still pictures only, and they follow the moving picture", 60, () =>
                {
                    using (var probe = H264Encoder.Create(64, 64, 30, 500)) if (probe == null) return "no H.264 encoder on this PC: still pictures only (nothing to test)";
                    var test = new TestScreen();
                    var screen = new ScreenVideo(test.Take);
                    host.VideoUpdate = screen.Update;
                    host.VideoForget = screen.Forget;
                    bool go = true;
                    var mover = new Thread(() => { int i = 0; while (go) { i++; test.SquareX = 20 + i * 11 % 560; test.SquareY = 30 + i * 7 % 280; Thread.Sleep(16); } }) { IsBackground = true };
                    VideoCanvas.TestFailVideo = true;
                    try
                    {
                        using var watch = ctrl.WatchScreen()!;
                        mover.Start();
                        if (!WaitFor(() => watch.NoH264, 10_000)) return "FAIL: the viewer never gave up on H.264 (" + watch.VideoFrames + " frames failed)";
                        Thread.Sleep(1000); // any frames already on their way arrive
                        int frames = watch.VideoFrames, pictures = watch.Pictures;
                        Thread.Sleep(2000);
                        int moreFrames = watch.VideoFrames - frames, morePictures = watch.Pictures - pictures;
                        if (moreFrames > 0) return "FAIL: the host kept sending H.264 (" + moreFrames + " frames in 2 s) after the viewer said it cannot show it";
                        if (morePictures < 5) return "FAIL: only " + morePictures + " still pictures in 2 s while things moved";
                        go = false;
                        mover.Join();
                        bool exact = WaitFor(() => watch.Canvas.At(test.SquareX + 5, test.SquareY + 5) == test.At(test.SquareX + 5, test.SquareY + 5) && watch.Canvas.At(5, 5) == test.At(5, 5), 10_000);
                        if (!exact) return "FAIL: once still, the picture did not become exact";
                        return "after " + frames + " frames it could not show, the viewer asked for still pictures: " + morePictures + " in 2 s while moving, none as H.264, exact once still";
                    }
                    finally { go = false; VideoCanvas.TestFailVideo = false; }
                });

                Scenario("9 PCs watch the screen at once: 8 see it, the 9th is told why", 60, () =>
                {
                    var test = new TestScreen();
                    var screen = new ScreenVideo(test.Take);
                    host.VideoUpdate = screen.Update;
                    host.VideoForget = screen.Forget;
                    var pcs = Enumerable.Range(0, 9).Select(_ => Connect(Password)).ToList();
                    var streams = new List<VideoStream>();
                    string? told = null;
                    try
                    {
                        for (int i = 0; i < 8; i++) streams.Add(pcs[i].WatchScreen()!);
                        bool all = WaitFor(() => streams.All(v => v.Pictures >= 1), 15_000);
                        var ninth = pcs[8].WatchScreen()!;
                        ninth.Note += t => told ??= t;
                        streams.Add(ninth);
                        WaitFor(() => told != null, 8000);
                        if (!all) return "FAIL: only " + streams.Take(8).Count(v => v.Pictures >= 1) + " of 8 saw the screen";
                        if (told == null || !told.Contains("already watching") || ninth.Pictures != 0) return "FAIL: the 9th was told " + (told ?? "nothing") + " and saw " + ninth.Pictures + " pictures";
                        return "8 saw the screen; the 9th was told: " + told;
                    }
                    finally
                    {
                        foreach (var v in streams) v.Dispose();
                        foreach (var pc in pcs) pc.Dispose();
                    }
                });

                Scenario("the remote screen while 128 MB moves: pictures keep coming, the file arrives whole, the line stays quick", 120, () =>
                {
                    var test = new TestScreen();
                    var screen = new ScreenVideo(test.Take);
                    host.VideoUpdate = screen.Update;
                    host.VideoForget = screen.Forget;
                    bool moving = true;
                    new Thread(() => { int i = 0; while (moving) { test.SquareX = 20 + i++ * 7 % 560; Thread.Sleep(40); } }) { IsBackground = true }.Start();
                    try
                    {
                        using var watch = ctrl.WatchScreen()!;
                        if (!WaitFor(() => watch.Pictures >= 1, 5000)) return "FAIL: no first picture";
                        int before = watch.Pictures;
                        var clock = Stopwatch.StartNew();
                        double speed = Timed(host, ctrl, "with-the-screen.bin", 128, () => ctrl.Files!.SendFiles(new[] { Big("with-the-screen.bin", 128) }));
                        int during = watch.Pictures - before;
                        int ping = ctrl.LastPingMs;
                        if (speed <= 0) return "FAIL: the file did not arrive whole";
                        if (during < 3) return "FAIL: only " + during + " pictures while the file moved";
                        if (ping > 500) return "FAIL: the line was slow, ping " + ping + " ms";
                        return "the file arrived at " + speed.ToString("0") + " MB/s with " + during + " pictures meanwhile, ping " + ping + " ms";
                    }
                    finally { moving = false; }
                });

                Scenario("the remote screen of a PC with 3 screens: screen 2 chosen shows exactly its pixels once still, screen 9 is refused and screen 2 stays; through the service's agent too", 150, () =>
                {
                    // One run: told 3 screens (the main one shown), then screen 2 chosen and shown exactly,
                    // then screen 9 asked for, refused, and screen 2 still shown. Null when all is well.
                    string? Check(Func<int, bool, VideoSettings, byte[]?> update, Action<int> forget, TestScreens three, string how)
                    {
                        host.VideoUpdate = update;
                        host.VideoForget = forget;
                        using var watch = ctrl.WatchScreen();
                        if (watch == null) return "FAIL: the host did not offer its screen";
                        var told = new System.Collections.Concurrent.ConcurrentQueue<ScreenChoice>();
                        watch.ScreenTold += c => told.Enqueue(c);
                        if (!WaitFor(() => watch.Pictures >= 1 && watch.Screens != null, 10_000)) return "FAIL" + how + ": no first picture, or not told which screen (" + watch.Pictures + " pictures)";
                        var first = watch.Screens!;
                        if (first.Count != 3 || first.Shown != 0 || !first.Screen.Main || first.Refused) return "FAIL" + how + ": first told " + first.Say();
                        var two = three.All[1];
                        // Exactly screen 2: its size (480 by 320: nothing at 480 across) and its pixels, square and all.
                        bool Exact() => watch.Canvas.At(479, 319) != System.Drawing.Color.Empty && watch.Canvas.At(480, 0) == System.Drawing.Color.Empty
                            && new[] { (5, 5), (two.SquareX + 5, two.SquareY + 5), (479, 319), (200, 100) }.All(p => watch.Canvas.At(p.Item1, p.Item2) == two.At(p.Item1, p.Item2));
                        watch.ChooseScreen(1);
                        if (!WaitFor(Exact, 60_000)) return "FAIL" + how + ": screen 2 was not shown exactly (told " + (watch.Screens?.Say() ?? "nothing") + ")";
                        var shown = watch.Screens!;
                        if (shown.Shown != 1 || shown.Refused || shown.Say() != "Screen 2 of 3, 480 by 320, right of the main screen.") return "FAIL" + how + ": after choosing screen 2 it said " + shown.Say();
                        int before = told.Count;
                        watch.ChooseScreen(8);
                        if (!WaitFor(() => told.Count > before && told.Last().Refused, 5000)) return "FAIL" + how + ": screen 9 was not refused";
                        var refused = told.Last();
                        if (refused.Shown != 1 || refused.Asked != 8) return "FAIL" + how + ": refusing screen 9 it said " + refused.Say();
                        Thread.Sleep(1500);
                        if (!Exact()) return "FAIL" + how + ": after screen 9 was refused, screen 2 was no longer shown";
                        return null;
                    }
                    var three = new TestScreens();
                    var screen = new ScreenVideo(three.Take, three.Screens);
                    string? direct = Check(screen.Update, screen.Forget, three, "");
                    if (direct != null) return direct;
                    // The same through the service: its agent, at the screen, takes the pictures. A pipe name of
                    // its own: the agent scenario above leaves its link connected on the usual test name.
                    string pipes = Names.TestPipes;
                    Names.TestPipes = pipes + "Screens";
                    AgentLink.TestAnyOwner = true;
                    try
                    {
                        var server = new AgentLink.Server();
                        var agentThree = new TestScreens();
                        new Thread(() => AgentLink.Run((_, _, _, _) => { }, (_, _, _) => { }, _ => { }, _ => { }, null, new ScreenVideo(agentThree.Take, agentThree.Screens))) { IsBackground = true }.Start();
                        Thread.Sleep(1500); // the agent connects
                        string? viaAgent = Check(server.Video, server.ForgetVideo, agentThree, " through the agent");
                        if (viaAgent != null) return viaAgent;
                    }
                    finally { Names.TestPipes = pipes; }
                    return "told 3 screens with the main one shown; screen 2 was shown exactly once still and said as \"Screen 2 of 3, 480 by 320, right of the main screen.\"; screen 9 was refused and screen 2 stayed; the same through the service's agent";
                });

                Scenario("the main connection damaged in the middle of 256 MB: it reconnects and the transfer carries on", 120, () =>
                {
                    string big = Big("resume.bin", 256);
                    var c3 = Connect(Password);
                    var files = c3.Files!;
                    for (int i = 0; i < 100 && files.TestLanes == 0; i++) Thread.Sleep(50);
                    string[]? landed = null;
                    host.ClipboardFilesReceived += p => landed = p;
                    FileChannel.Transfer? sent = null;
                    Action<FileChannel.Transfer> note = t => { if (t.Finished && t.What == "resume.bin") sent = t; };
                    c3.TransferProgress += note;
                    var send = Task.Run(() => files.SendFiles(new[] { big }));
                    Thread.Sleep(300);
                    c3.TestRaw(new byte[] { 0x55, 0x55, 0x55, 0x55, 0x12, 0x34, 0x56, 0x78, 1, 2, 3 }); // the host hangs up the main connection
                    files.TestCutLanes(); // and the lanes go down with it
                    bool dropped = WaitClosed(c3, 12_000);
                    c3.Dispose();
                    using var c4 = Connect(Password); // as the window does: reconnect to the same PC
                    c4.TransferProgress += note;
                    send.Wait(90_000);
                    for (int i = 0; i < 250 && (sent == null || landed == null); i++) Thread.Sleep(20);
                    bool same = ReferenceEquals(c4.Files, files);
                    bool ok = dropped && same && sent?.Failed == false && landed != null && Same(big, landed[0]);
                    return ok ? "the transfer picked up on the new connection and arrived whole" : "FAIL: dropped " + dropped + ", same channel " + same + ", sender said " + sent?.Result + ", arrived " + (landed != null);
                });

                Scenario("pressing Stop part-way through a folder: what arrived is kept, and a cut-off file is marked incomplete", 120, () =>
                {
                    string src = Path.Combine(Root, "src", "Stopped");
                    Directory.CreateDirectory(src);
                    for (int i = 0; i < 8; i++) File.Move(Big("stopped" + i + ".bin", 64), Path.Combine(src, "stopped" + i + ".bin"));
                    FileChannel.Transfer? hostEnd = null;
                    bool moving = false;
                    Action<FileChannel.Transfer> note = t => { if (t.What == "Stopped") { if (t.Finished) hostEnd = t; else if (t.Done > 0) moving = true; } };
                    host.TransferProgress += note;
                    var send = Task.Run(() => ctrl.Files!.SendFiles(new[] { src }, toClipboard: false));
                    for (int i = 0; i < 500 && !moving; i++) Thread.Sleep(5);
                    ctrl.CancelTransfer();
                    send.Wait(20_000);
                    for (int i = 0; i < 250 && hostEnd == null; i++) Thread.Sleep(20);
                    host.TransferProgress -= note;
                    Thread.Sleep(1000); // a file still being written is marked incomplete a moment later
                    string landed = Path.Combine(FileChannel.Downloads, "Stopped");
                    var kept = Directory.Exists(landed) ? Directory.GetFiles(landed) : Array.Empty<string>();
                    bool marked = kept.Any(f => f.Contains("(incomplete)")), empty = kept.Any(f => new FileInfo(f).Length == 0);
                    bool ok = hostEnd?.Cancelled == true && kept.Length > 0 && !empty && (marked || kept.Length == 8);
                    return ok ? "kept " + kept.Length + " files (" + kept.Count(f => f.Contains("(incomplete)")) + " marked incomplete); the receiver said: " + hostEnd!.Result
                        : "FAIL: receiver said " + hostEnd?.Result + "; kept " + kept.Length + ", marked incomplete " + marked + ", empty ones left " + empty;
                });

                Scenario("disconnecting in the middle of a 512 MB folder: what arrived stays, and it carries on when connected again", 120, () =>
                {
                    string src = Path.Combine(Root, "src", "Resume");
                    Directory.CreateDirectory(src);
                    for (int i = 0; i < 8; i++) File.Move(Big("resume" + i + ".bin", 64), Path.Combine(src, "resume" + i + ".bin"));
                    FileChannel.Transfer? hostEnd = null;
                    bool moving = false;
                    Action<FileChannel.Transfer> note = t => { if (t.What == "Resume") { if (t.Finished) hostEnd = t; else if (t.Done > 0) moving = true; } };
                    host.TransferProgress += note;
                    var c2 = Connect(Password); // a second controller, disconnected part-way
                    for (int i = 0; i < 100 && c2.Files!.TestLanes == 0; i++) Thread.Sleep(50);
                    var files = c2.Files!;
                    var send = Task.Run(() => files.SendFiles(new[] { src }, toClipboard: false));
                    for (int i = 0; i < 500 && !moving; i++) Thread.Sleep(5);
                    c2.Dispose(); // Disconnect pressed
                    Thread.Sleep(2000);
                    string landed = Path.Combine(FileChannel.Downloads, "Resume");
                    bool waited = hostEnd == null && !send.IsCompleted && Directory.Exists(landed);
                    using var c3 = Connect(Password); // connected again
                    send.Wait(60_000);
                    for (int i = 0; i < 500 && hostEnd == null; i++) Thread.Sleep(20);
                    host.TransferProgress -= note;
                    bool whole = Enumerable.Range(0, 8).All(i => File.Exists(Path.Combine(landed, "resume" + i + ".bin")) && Same(Path.Combine(src, "resume" + i + ".bin"), Path.Combine(landed, "resume" + i + ".bin")));
                    return waited && hostEnd?.Failed == false && whole ? "it waited through the disconnection with what had arrived, then finished all 8 files exactly"
                        : "FAIL: waited " + waited + ", receiver said " + hostEnd?.Result + ", all 8 whole " + whole;
                });
            }

            Scenario("stopping the host in the middle of everything, then starting it again", 60, () =>
            {
                var c = Connect(Password);
                var ls = Enumerable.Range(0, 10).Select(_ => Connect(ListenPassword)).ToList();
                for (int i = 0; i < 100 && c.Files == null; i++) Thread.Sleep(50);
                var send = Task.Run(() => { try { c.Files?.SendFiles(new[] { Big("mid-stop.bin", 128) }); } catch { } });
                Thread.Sleep(300);
                host.Dispose();
                bool allNoticed = WaitClosed(c, 12_000) && ls.All(l => WaitClosed(l, 12_000));
                send.Wait(10_000);
                c.Dispose();
                foreach (var l in ls) l.Dispose();
                Thread.Sleep(500);
                host = NewHost();
                Thread.Sleep(500);
                using var back = Connect(Password);
                return allNoticed ? "every PC noticed, the port was freed, and hosting again worked" : "FAIL: not every PC noticed the host stopping";
            });

            Scenario("a login trickled in one byte a second: cut off at the login deadline, not minutes later", 30, () =>
            {
                using var t = new TcpClient("127.0.0.1", Port);
                var st = t.GetStream();
                var hello = new byte[Protocol.HelloBytes];
                Protocol.ReadExactly(st, hello);
                var clock = Stopwatch.StartNew();
                try
                {
                    // Each byte comes well inside the 3 second read timeout, so only an overall deadline ends it.
                    for (int i = 0; i < Protocol.AnswerBytes - 1 && clock.ElapsedMilliseconds < 25_000; i++)
                    {
                        st.WriteByte(0x55);
                        Thread.Sleep(1000);
                    }
                }
                catch { }
                long at = clock.ElapsedMilliseconds;
                return at < Host.LoginDeadlineMs + 3000 ? "cut off after " + at / 1000 + " s; " + Healthy(host, "afterwards") : "FAIL: still connected after " + at / 1000 + " s";
            });

            Scenario("the remote PC's health: battery alerts heard once each, a full drive told to a PC that connects later (fake readings)", 30, () =>
            {
                long gen = CurrentGeneration;
                var reading = new HealthSample(true, false, 80, "C:", 100L << 30, 200L << 30, 10, 40);
                Host.TestHealth = () => reading;
                Host.HealthEveryMs = 200;
                var heard = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var c7 = Connect(Password);
                try
                {
                    c7.HealthAlert += heard.Enqueue;
                    Thread.Sleep(800);
                    reading = reading with { OnBattery = true, BatteryPercent = 15 };
                    if (!WaitFor(() => heard.Count >= 2, 5000)) return "FAIL: heard " + string.Join(" / ", heard);
                    Thread.Sleep(1000); // a few more looks: nothing new to say
                    var first = heard.ToArray();
                    bool once = first.Length == 2 && first[0].Contains("on its battery, 15") && first[1].Contains("battery is low, 15");
                    reading = reading with { DiskFree = 1L << 29 };
                    Thread.Sleep(600);
                    var late = new System.Collections.Concurrent.ConcurrentQueue<string>();
                    var p = new Player(Player.NoDevice, _ => { });
                    // Subscribed from the very start, through the status callback the window gets too? No: health has its own event.
                    using var c8 = Client.Connect("127.0.0.1", Port, Password, p, _ => { });
                    c8.HealthAlert += late.Enqueue;
                    // What was already wrong arrives just after the login: maybe before the handler above, so wait for the
                    // next change too (the battery going critical) to be sure the line works, and look for the drive in either.
                    reading = reading with { BatteryPercent = 5 };
                    bool lateHeard = WaitFor(() => late.Any(l => l.Contains("very low")), 5000) && late.Any(l => l.Contains("nearly full")); // told on connecting, kept until it subscribed
                    return once && lateHeard
                        ? "heard on battery and battery low once each; a PC connecting later heard the next change (" + string.Join(" / ", late) + ")"
                        : "FAIL: first PC heard " + string.Join(" / ", first) + "; the later one heard " + string.Join(" / ", late);
                }
                finally
                {
                    if (CurrentGeneration == gen) { Host.TestHealth = null; Host.HealthEveryMs = HealthWatch.EveryMs; }
                    c7.Dispose();
                }
            });

            Scenario("waking a PC through the host: the wake signal goes out on its network, and a listener cannot send one", 30, () =>
            {
                using var c9 = Connect(Password);
                using var l9 = Connect(ListenPassword);
                if (!WaitFor(() => c9.CanWake, 3000)) return "FAIL: the host did not offer waking";
                if (l9.CanWake) return "FAIL: a listener was offered waking";
                bool told = WaitFor(() => c9.WakeInfo != null, 3000);
                UdpClient? ear;
                try { ear = new UdpClient(new IPEndPoint(IPAddress.Any, 9)) { EnableBroadcast = true }; }
                catch { return "skipped listening (port 9 is taken on this PC); the host offered waking" + (told ? " and told its network cards" : ""); }
                using (ear)
                {
                    byte[] mac = Convert.FromHexString("0A1B2C3D4E5F");
                    ear.Client.ReceiveTimeout = 1500;
                    l9.SendWake(mac); // ignored
                    var any = new IPEndPoint(IPAddress.Any, 0);
                    bool fromListener = false;
                    try { ear.Receive(ref any); fromListener = true; } catch { }
                    c9.SendWake(mac);
                    byte[]? got = null;
                    ear.Client.ReceiveTimeout = 3000;
                    try { got = ear.Receive(ref any); } catch { }
                    bool right = got != null && got.AsSpan().SequenceEqual(Wake.MagicPacket(mac));
                    return right && !fromListener
                        ? "the wake signal went out on this network" + (told ? ", the host told its network cards," : "") + " and the listener's request did nothing"
                        : "FAIL: wake packet heard " + (got != null) + ", right " + right + ", from the listener " + fromListener;
                }
            });

            Scenario("the connection drops before the window is listening: it still hears about it", 30, () =>
            {
                // A second host of its own, so stopping it disturbs nothing else.
                var brief = new Host(Port + 7, Password, null, _ => { });
                try
                {
                    using var c = Client.Connect("127.0.0.1", Port + 7, Password, new Player(Player.NoDevice, _ => { }), _ => { });
                    brief.Dispose(); // gone before anyone subscribed, like a host stopping just as the login finished
                    if (!WaitClosed(c, 10_000)) return "FAIL: the client never noticed the host stopping";
                    string? heard = null;
                    c.Disconnected += why => heard = why; // subscribed late
                    return heard != null ? "a late listener heard it at once: " + heard : "FAIL: a handler added after the drop never heard about it";
                }
                finally { brief.Dispose(); }
            });

            Scenario("six wrong passwords, then the right one", 30, () =>
            {
                int refused = 0;
                for (int i = 0; i < 6; i++) try { using var c = Connect("wrong" + i); } catch (Exception e) when (e.Message.StartsWith("Wrong password") || e.Message == Protocol.DamagedLogin || e.Message.Contains("closed")) { refused++; }
                string right;
                try { using var c = Connect(Password); right = "the right one got in (it should have been blocked for a minute)"; }
                catch (Exception e) { right = "the right one was blocked for now: " + e.Message; }
                return refused == 6 ? "all 6 refused; " + right : "FAIL: only " + refused + " refused";
            });

            Scenario("20 wrong passwords sent at the same moment: no more than 5 are even checked", 60, () =>
            {
                // After "six wrong passwords" this address is blocked anyway; wait the block out first.
                host.Dispose();
                host = NewHost();
                Thread.Sleep(500);
                int wrong = 0, cut = 0;
                Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 20 }, i =>
                {
                    try { using var c = Connect("parallel-wrong-" + i); }
                    catch (Exception e) when (e.Message.StartsWith("Wrong password")) { Interlocked.Increment(ref wrong); }
                    catch { Interlocked.Increment(ref cut); }
                });
                return wrong <= 5 ? wrong + " were checked and refused, " + cut + " were hung up on unchecked" : "FAIL: " + wrong + " wrong passwords were checked (at most 5 a minute are allowed)";
            });

            // Leaks: memory and threads back where they started.
            Thread.Sleep(3000);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memAfter = GC.GetTotalMemory(true);
            int threadsAfter = Process.GetCurrentProcess().Threads.Count - ThreadPool.ThreadCount;
            // Memory is checked by the three-round clipboard scenario; here only threads (the test itself holds its own data).
            Add("leaks", threadsAfter - threadsBefore < 30,
                "memory " + (memBefore >> 20) + " MB before, " + (memAfter >> 20) + " MB after; threads of its own " + threadsBefore + " before, " + threadsAfter + " after (and " + ThreadPool.ThreadCount + " in .NET's worker pool)");
            host.Dispose();

            string text = (_failures == 0 ? "ALL PASSED" : _failures + " FAILED") + Environment.NewLine + string.Join(Environment.NewLine, Report);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "tailremote-chaostest.txt"), text);
            Console.WriteLine(text);
            try { Directory.Delete(Root, true); } catch { }
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>Runs a transfer and returns how each side said it ended (waits up to 60 s for both).</summary>
        private static (FileChannel.Transfer? Host, FileChannel.Transfer? Client) BothSay(Host host, Client ctrl, string what, Action run)
        {
            FileChannel.Transfer? h = null, c = null;
            Action<FileChannel.Transfer> onHost = t => { if (t.Finished && t.What == what) h = t; };
            Action<FileChannel.Transfer> onClient = t => { if (t.Finished && t.What == what) c = t; };
            host.TransferProgress += onHost;
            ctrl.TransferProgress += onClient;
            run();
            for (int i = 0; i < 3000 && (h == null || c == null); i++) Thread.Sleep(20);
            host.TransferProgress -= onHost;
            ctrl.TransferProgress -= onClient;
            return (h, c);
        }

        /// <summary>MB a second for a transfer, from start until the receiving side says it is done.</summary>
        private static double Timed(Host host, Client ctrl, string what, int mb, Action run)
        {
            var clock = Stopwatch.StartNew();
            var (h, c) = BothSay(host, ctrl, what, run);
            return h?.Failed == false && c?.Failed == false ? mb / clock.Elapsed.TotalSeconds : 0;
        }

        private static Host NewHost() => new(Port, Password, ListenPassword, m => { lock (Log) Log.Add(m); });

        private static Client Connect(string password)
        {
            var p = new Player(Player.NoDevice, _ => { });
            return Client.Connect("127.0.0.1", Port, password, p, _ => { });
        }

        private static bool WaitFor(Func<bool> done, int ms)
        {
            for (int i = 0; i < ms / 25 && !done(); i++) Thread.Sleep(25);
            return done();
        }

        private static bool WaitClosed(Client c, int ms)
        {
            for (int i = 0; i < ms / 25 && !c.TestClosed; i++) Thread.Sleep(25);
            return c.TestClosed;
        }

        private static string Healthy(Host host, string when)
        {
            try { using var c = Connect(Password); return "a real login still works " + when; }
            catch (Exception e) { return "FAIL: no login " + when + ": " + e.Message; }
        }

        private static string Big(string name, int mb)
        {
            string path = Path.Combine(Root, "src", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = new byte[1 << 20];
            new Random(name.GetHashCode()).NextBytes(data);
            using var f = File.Create(path);
            for (int i = 0; i < mb; i++) f.Write(data);
            return path;
        }

        private static bool Same(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);
            return System.Security.Cryptography.SHA256.HashData(sa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(sb));
        }

        private static readonly string? Only = Environment.GetEnvironmentVariable("CHAOS_ONLY");

        // task.Wait(timeout) does not cancel a timed-out scenario's body: it keeps running in the
        // background. A couple of scenarios reset shared test-only statics (Native.KeySink, for one)
        // in a finally once they finish; if that finally fires late, after a later scenario has
        // already started and set up its own state, it can wipe that out. CurrentGeneration lets such
        // a finally tell whether it is still the scenario running "now" before it resets anything.
        private static long _generation;
        public static long CurrentGeneration => Interlocked.Read(ref _generation);

        private static void Scenario(string name, int seconds, Func<string> body)
        {
            if (Only != null && !name.Contains(Only, StringComparison.OrdinalIgnoreCase)) return;
            Interlocked.Increment(ref _generation);
            var clock = Stopwatch.StartNew();
            var task = Task.Run(() => { try { return body(); } catch (Exception e) { return "FAIL: " + e.GetType().Name + ": " + e.Message; } });
            string result = task.Wait(TimeSpan.FromSeconds(seconds)) ? task.Result : "FAIL: FROZE (no answer in " + seconds + " seconds)";
            Add(name, !result.Contains("FAIL"), result + " (" + clock.Elapsed.TotalSeconds.ToString("0.0") + " s)");
        }

        private static void Add(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            string line = (ok ? "ok    " : "FAIL  ") + name + ": " + detail;
            lock (Report) Report.Add(line);
            Console.WriteLine(line);
        }
    }
}
