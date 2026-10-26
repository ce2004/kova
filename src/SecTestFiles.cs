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
using System.Threading.Tasks;

namespace TailRemote
{
    /// <summary>Security tests (--securitytest): Files part. Returns null when every check passes, else what failed.</summary>
    internal static class SecTestFiles
    {
        internal static string? Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "kova-sectest-files-" + Guid.NewGuid().ToString("N")[..8]);
            string? oldStaging = FileChannel.StagingOverride, oldDownloads = FileChannel.DownloadsOverride;
            Directory.CreateDirectory(root);
            FileChannel.StagingOverride = Path.Combine(root, "stage");
            FileChannel.DownloadsOverride = Path.Combine(root, "downloads");
            try
            {
                return RemotePaths() ?? PeerWords() ?? VbCable() ?? Unasked(root) ?? Names() ?? Memory() ?? Space();
            }
            catch (Exception e) { return "Files: the test itself failed: " + e; }
            finally
            {
                FileChannel.StagingOverride = oldStaging;
                FileChannel.DownloadsOverride = oldDownloads;
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static string Downloads => FileChannel.Downloads;

        // ---- Fix 7: Get files only on the remote PC's own drives ----

        private static string? RemotePaths()
        {
            foreach (string bad in new[] { @"\\x\y", @"\\x\y\z.txt", @"\\?\C:\a", @"\\?\UNC\x\y", @"\\.\PhysicalDrive0", @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\Windows",
                @"\??\C:\a", "C:a", "C:", "C", "", @"C:\a:stream", @"C:\a\\b", "C:/a", @"C:\a*", @"1:\a", "\\\\x\\y" })
                if (FileChannel.RemotePathOk(bad)) return "RemotePathOk took \"" + bad + "\"";
            foreach (string good in new[] { @"C:\", @"D:\Folder\file.txt", @"c:\Users\Somebody\Documents", @"C:\..\Windows" })
                if (!FileChannel.RemotePathOk(good)) return "RemotePathOk refused \"" + good + "\"";
            // The host's folder listing refuses a share before touching it (it would sign in to the server).
            if (!RemoteTools.ListFolder(@"\\127.0.0.1\c$").StartsWith("E\t")) return "ListFolder listed a share";
            if (!RemoteTools.ListFolder(@"\\?\GLOBALROOT\Device").StartsWith("E\t")) return "ListFolder listed a \\\\?\\ device path";
            if (RemoteTools.ListFolder(Path.GetTempPath()).StartsWith("E\t")) return "ListFolder refused a plain local folder";
            return null;
        }

        // ---- Fix 6: the other PC's words are short, clean and plainly theirs ----

        private static string? PeerWords()
        {
            string evil = "Kova: type your Windows password\u202E\r\n\u0007" + new string('x', 2000);
            string t = FileChannel.Theirs(evil);
            if (t.Length > 220) return "a 2000-character reason from the other PC stayed " + t.Length + " characters";
            if (t.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)) return "a reason from the other PC kept control or direction characters";
            if (!t.StartsWith("The other PC said: ")) return "a reason from the other PC was not marked as its words: " + t[..40];
            if (FileChannel.Theirs("The other PC stopped sending x.") != "The other PC stopped sending x.") return "Kova's own words from the other PC got marked twice";
            // End to end: a Refuse with a long, nasty reason arrives short and clean.
            var (a, b) = Pair();
            try
            {
                var seen = new ConcurrentQueue<FileChannel.Transfer>();
                b.Progress = x => { if (x.Finished) seen.Enqueue(x); };
                a.Refuse(new[] { @"C:\x" }, evil);
                if (!Until(() => !seen.IsEmpty, 10_000)) return "a refusal never arrived";
                string r = seen.First().Result ?? "";
                if (r.Length > 220 || r.Any(char.IsControl) || r.Contains('\u202E')) return "a refusal was shown long or with control characters (" + r.Length + ")";
            }
            finally { a.Dispose(); b.Dispose(); }
            return null;
        }

        // ---- Fix 8: only the VB-Cable zip Kova knows is unpacked ----

        private static string? VbCable()
        {
            const string pack45 = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
            if (AudioSetup.KnownPack(pack45, RandomNumberGenerator.GetBytes(4096))) return "an unknown VB-Cable zip was taken as genuine";
            if (AudioSetup.KnownPack("https://example.invalid/x.zip", Array.Empty<byte>())) return "a VB-Cable zip from elsewhere was taken";
            return null;
        }

        // ---- Fixes 1 and 2: nothing unasked onto the clipboard; received files marked from the internet ----

        private static string? Unasked(string root)
        {
            var (a, b) = Pair();
            try
            {
                var sent = new ConcurrentQueue<FileChannel.Transfer>();
                a.Progress = x => { if (x.Finished && x.Outgoing) sent.Enqueue(x); };
                string? onClipboard = null, keptText = null, keptPath = null;
                string[]? clipFiles = null;
                b.TextReceived = t => onClipboard = t;
                b.FilesReceived = p => clipFiles = p;
                b.UnaskedTextReceived = (t, p) => { keptText = t; keptPath = p; };
                b.ClipboardOnlyWhenAsked = true;
                b.AcceptUnasked = false;

                // Not asked, not taken: refused, nothing lands.
                if (!Send(() => a.SendText("format c: unasked"))) return "sending unasked text never ended";
                if (onClipboard != null || keptText != null) return "unasked text was taken by a PC that takes nothing unasked";
                if (!sent.TryDequeue(out var r1) || !r1.Failed || r1.Result?.Contains("did not ask") != true) return "unasked text was not refused with a reason: " + r1?.Result;

                // Asked (Get the remote PC's clipboard): it lands on the clipboard.
                b.ExpectClipboard();
                if (!Send(() => a.SendText("asked for"))) return "sending asked-for text never ended";
                if (!Until(() => onClipboard != null, 5000) || onClipboard != "asked for") return "text asked for did not land on the clipboard";
                // Asked once is one batch only.
                onClipboard = null;
                Send(() => a.SendText("second, unasked"));
                if (onClipboard != null) return "one ask let two clipboard batches in";

                // Unasked, but taken (the default): text goes straight onto the clipboard (4.5.1, as before 4.4).
                b.AcceptUnasked = true;
                sent.Clear();
                if (!Send(() => a.SendText("sent from the other PC"))) return "sending unasked text (taken) never ended";
                if (!Until(() => onClipboard != null, 5000) || onClipboard != "sent from the other PC") return "text sent from the other PC did not replace the clipboard";
                if (keptText != null || keptPath != null) return "text sent from the other PC was also saved as a file";

                // Unasked clipboard files: to Downloads, not the clipboard.
                if (!Send(() => a.TestSendEntries(new List<(string, byte[]?)> { ("pushed.txt", Encoding.UTF8.GetBytes("pushed")) }, "pushed.txt"))) return "unasked clipboard files never ended";
                if (clipFiles != null) return "unasked clipboard files went onto the clipboard";
                if (!File.Exists(Path.Combine(Downloads, "pushed.txt"))) return "unasked clipboard files did not land in Downloads";

                // Send files: refused unless asked for by name (Get files).
                b.AcceptUnasked = false;
                string src = Path.Combine(root, "src");
                Directory.CreateDirectory(src);
                string file = Path.Combine(src, "wanted.bin");
                File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(300_000));
                sent.Clear();
                if (!Send(() => a.SendFiles(new[] { file }, toClipboard: false))) return "an unasked Send files never ended";
                if (File.Exists(Path.Combine(Downloads, "wanted.bin")) || Directory.GetFiles(Downloads, "wanted*").Length > 0) return "an unasked Send files landed on a PC that takes nothing unasked";
                if (!sent.TryDequeue(out var r2) || !r2.Failed) return "an unasked Send files was not refused";
                b.ExpectFetch(new[] { @"D:\somewhere\else.bin" });
                Send(() => a.SendFiles(new[] { file }, toClipboard: false));
                if (File.Exists(Path.Combine(Downloads, "wanted.bin"))) return "Get files for another name let a different batch in";
                b.ExpectFetch(new[] { @"D:\remote folder\wanted.bin" });
                if (!Send(() => a.SendFiles(new[] { file }, toClipboard: false))) return "an asked-for Send files never ended";
                string got = Path.Combine(Downloads, "wanted.bin");
                if (!File.Exists(got) || !File.ReadAllBytes(got).AsSpan().SequenceEqual(File.ReadAllBytes(file))) return "files asked for (Get files) did not arrive whole";

                // Fix 2: every received file is marked as from the internet.
                if (Zone(got) != 3) return "a received file has no internet mark (Zone.Identifier ZoneId=3)";
                if (Zone(Path.Combine(Downloads, "pushed.txt")) != 3) return "a received clipboard file has no internet mark";
            }
            finally { a.Dispose(); b.Dispose(); }
            return null;
        }

        // ---- Fix 3: names that disguise themselves, and files Explorer acts on by itself ----

        private static string? Names()
        {
            var (a, b) = Pair();
            try
            {
                string[]? landed = null;
                b.FilesReceived = p => landed = p;
                var done = new ConcurrentQueue<FileChannel.Transfer>();
                b.Progress = x => { if (x.Finished) done.Enqueue(x); };
                Send(() => a.TestSendEntries(new List<(string, byte[]?)> { ("invoice\u202Efdp.exe", Encoding.UTF8.GetBytes("MZ")) }, "rlo"));
                if (!Until(() => landed != null, 5000)) return "a file with a right-to-left mark in its name never arrived";
                string name = Path.GetFileName(landed![0]);
                if (name != "invoice_fdp.exe") return "a right-to-left mark in a name was kept: " + Uri.EscapeDataString(name);
                if (Zone(landed[0]) != 3) return "a clipboard file has no internet mark";
                landed = null;
                done.Clear();
                Send(() => a.TestSendEntries(new List<(string, byte[]?)> { ("evil.url", Encoding.UTF8.GetBytes("[InternetShortcut]\r\nIconFile=\\\\203.0.113.9\\x\\i.ico\r\n")) }, "url"));
                if (!Until(() => landed != null, 5000)) return "a .url file never arrived";
                if (Path.GetFileName(landed![0]) != "evil.url.txt") return "a .url file kept a name Explorer acts on: " + Path.GetFileName(landed[0]);
                if (!Until(() => !done.IsEmpty, 2000) || done.First().Result?.Contains("evil.url.txt") != true) return "the .txt added to a .url file was not said";
            }
            finally { a.Dispose(); b.Dispose(); }
            return null;
        }

        // ---- Fix 4: an offer of text costs nothing until its data comes ----

        private static string? Memory()
        {
            var (a, b) = Pair();
            try
            {
                var seen = new ConcurrentQueue<FileChannel.Transfer>();
                b.Progress = x => seen.Enqueue(x);
                // Too big: refused.
                a.TestOfferOnly(FileChannel.KindText, "", 512L << 20);
                if (!Until(() => seen.Any(x => x.Finished), 5000)) return "a 512 MB text offer was not refused";
                if (seen.First(x => x.Finished).Result?.Contains("64 MB") != true) return "a 512 MB text offer was refused without saying the limit: " + seen.First(x => x.Finished).Result;
                seen.Clear();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long before = PrivateBytes();
                a.TestOfferOnly(FileChannel.KindText, "", FileChannel.MaxText);
                if (!Until(() => !seen.IsEmpty, 5000)) return "a 64 MB text offer was never taken";
                Thread.Sleep(200);
                long grew = PrivateBytes() - before;
                if (grew > 8L << 20) return "a 64 MB text offer with no data made this PC hold " + (grew >> 20) + " MB";
            }
            finally { a.Dispose(); b.Dispose(); }
            return null;
        }

        // ---- Fix 5: never fills the drive ----

        private static string? Space()
        {
            Directory.CreateDirectory(Downloads);
            if (FileChannel.FreeSpace(Downloads) is not { } space) return null; // a drive Windows will not describe: nothing to check
            long free = space.Free;
            var (a, b) = Pair();
            try
            {
                var seen = new ConcurrentQueue<FileChannel.Transfer>();
                b.Progress = x => { if (x.Finished) seen.Enqueue(x); };
                // Fits in what is free, but not with room left for Windows: refused, and nothing made.
                a.TestOfferOnly(FileChannel.KindDownloads, "huge.bin", Math.Max(1, free - (1L << 30)));
                if (!Until(() => !seen.IsEmpty, 5000)) return "a batch filling the drive was not refused";
                if (seen.First().Result?.Contains("free space") != true) return "a batch filling the drive was refused without saying why: " + seen.First().Result;
                if (Directory.GetFiles(Downloads, "huge*").Length > 0) return "a batch filling the drive still made its file";
            }
            finally { a.Dispose(); b.Dispose(); }
            return null;
        }

        // ---- Helpers ----

        /// <summary>Two file channels joined by two lanes over this PC's own loopback, as a host and a controller are.</summary>
        private static (FileChannel A, FileChannel B) Pair()
        {
            var a = new FileChannel();
            var b = new FileChannel();
            var listen = new TcpListener(IPAddress.Loopback, 0);
            listen.Start();
            try
            {
                byte[] key = RandomNumberGenerator.GetBytes(32);
                for (int i = 0; i < 2; i++)
                {
                    byte[] hn = RandomNumberGenerator.GetBytes(16), cn = RandomNumberGenerator.GetBytes(16);
                    string purpose = FileChannel.LanePurpose(hn, cn);
                    var dial = new TcpClient();
                    dial.Connect((IPEndPoint)listen.LocalEndpoint);
                    var took = listen.AcceptTcpClient();
                    a.AddLane(dial, new SecureLink(key, hn, cn, false, purpose));
                    b.AddLane(took, new SecureLink(key, hn, cn, true, purpose));
                }
            }
            finally { listen.Stop(); }
            return (a, b);
        }

        /// <summary>Runs a blocking send to its end (at most 20 seconds); false if it never ended.</summary>
        private static bool Send(Action send) => Task.Run(send).Wait(20_000);

        private static bool Until(Func<bool> done, int ms)
        {
            for (int waited = 0; waited < ms; waited += 20)
            {
                if (done()) return true;
                Thread.Sleep(20);
            }
            return done();
        }

        /// <summary>The zone a file is marked with (3: the internet), or -1 if it has no mark.</summary>
        private static int Zone(string path)
        {
            try
            {
                string mark = File.ReadAllText(path + ":Zone.Identifier");
                int at = mark.IndexOf("ZoneId=", StringComparison.Ordinal);
                return at >= 0 && int.TryParse(mark[(at + 7)..].Trim().Split('\r', '\n')[0], out int z) ? z : -1;
            }
            catch { return -1; }
        }

        private static long PrivateBytes()
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            return me.PrivateMemorySize64;
        }
    }
}
