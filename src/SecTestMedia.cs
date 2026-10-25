using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TailRemote
{
    /// <summary>Security tests (--securitytest): Media part. Returns null when every check passes, else what failed.</summary>
    internal static class SecTestMedia
    {
        internal static string? Run()
        {
            return RemoteSpeech() ?? TestSwitches() ?? ControllerDll() ?? WakeCards() ?? H264Sizes() ?? StillPictures();
        }

        // Remote NVDA text is cleaned and capped, normal text comes through unchanged, and a flood is joined, then dropped.
        private static string? RemoteSpeech()
        {
            string huge = Speech.Clean(new string('a', 1_000_000), Speech.RemoteLineMax);
            if (huge.Length != Speech.RemoteLineMax) return "a million-character line was not cut to " + Speech.RemoteLineMax + " (got " + huge.Length + ")";
            string spoof = Speech.Clean("Back on ‮this PC\u0007‏⁦x\u0085\r\n", Speech.RemoteLineMax);
            if (spoof != "Back on this PCx") return "control or direction characters were not removed: " + Escape(spoof);
            foreach (string normal in new[] { "Hello world", "Notepad  edit  blank", "café 你好 \U0001F600", "tab\there", "1 of 3, heading level 2" })
                if (Speech.Clean(normal, Speech.RemoteLineMax) != normal) return "normal NVDA text was changed: " + Escape(normal);
            if (Speech.Clean("ab\U0001F600", 3) != "ab") return "a cut left half of a two-part character";
            if (Speech.Clean(new string('b', 5000), Speech.BrailleMax).Length != Speech.BrailleMax) return "braille was not cut";

            // A fresh second on a made-up clock for each case (the clock is the caller's: far from the real one).
            long t = 1_000_000_000_000L;
            var lines200 = Enumerable.Range(0, 200).Select(i => "line " + i).ToArray();
            var said = Speech.PrepareRemote(lines200, t, out bool dropped);
            if (dropped || said.Count != Speech.RemoteLinesPerMessage || said.Any(l => l.Length > Speech.RemoteLineMax)) return "a 200-line message gave " + said.Count + " lines (dropped " + dropped + ")";
            if (said[0] != "line 0" || said[48] != "line 48" || !said[49].StartsWith("line 49 line 50 ")) return "a 200-line message was not kept in order with the rest joined";

            t += 10_000;
            var normalBurst = Enumerable.Range(0, 30).Select(i => "item " + i).ToArray();
            said = Speech.PrepareRemote(normalBurst, t, out dropped);
            if (dropped || !said.SequenceEqual(normalBurst)) return "a normal burst of 30 lines did not come through unchanged";

            // A flood: 10-line messages within one second. The first 100 lines each, then one joined line per message,
            // and past that nothing (dropped), never more than the caps.
            t += 10_000;
            int separate = 0, joined = 0, drops = 0;
            for (int k = 0; k < 40; k++)
            {
                var msg = Enumerable.Range(0, 10).Select(i => "flood " + k + "." + i).ToArray();
                var got = Speech.PrepareRemote(msg, t + k * 10, out dropped);
                if (dropped) drops++;
                foreach (var l in got) if (l.Contains(' ') && l.Split(' ').Length > 2) joined++; else separate++;
            }
            if (separate != Speech.RemoteLinesPerSecond || joined != Speech.RemoteJoinedPerSecond || drops != 40 - 10 - Speech.RemoteJoinedPerSecond)
                return "the flood limit let through " + separate + " lines and " + joined + " joined lines, dropping " + drops + " messages";
            if (Speech.PrepareRemote(new[] { "after a second" }, t + 1500, out dropped).Count != 1 || dropped) return "speech did not come back a second after a flood";

            // Time spent on the controller side per line, apart from NVDA itself (reported, and must be well under a millisecond).
            var typical = Enumerable.Range(0, 20).Select(i => "Typical NVDA line number " + i + ", button, Save").ToArray();
            var sw = Stopwatch.StartNew();
            int n = 0;
            for (int k = 0; k < 2000; k++) { n += Speech.PrepareRemote(typical, t + 100_000 + k * 1000L, out _).Count; }
            sw.Stop();
            double usPerLine = sw.Elapsed.TotalMilliseconds * 1000 / n;
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "kova-securitytest-media-timing.txt"), "remote NVDA line prepared in " + usPerLine.ToString("0.000") + " microseconds on average over " + n + " lines" + Environment.NewLine); } catch { }
            if (usPerLine > 100) return "preparing a remote NVDA line took " + usPerLine + " microseconds";
            return null;
        }

        // KOVA_SPEECH_LOG is ignored in an elevated or SYSTEM copy.
        private static string? TestSwitches()
        {
            string log = Path.Combine(Path.GetTempPath(), "kova-sectest-speechlog-" + Environment.ProcessId + ".txt");
            string? before = Environment.GetEnvironmentVariable("KOVA_SPEECH_LOG");
            try
            {
                File.Delete(log);
                Environment.SetEnvironmentVariable("KOVA_SPEECH_LOG", log);
                Speech.PrivilegedForTest = true;
                Speech.ReloadTestLogForTest();
                Speech.Speak("elevated test line", interrupt: false);
                Speech.Remote(new[] { "elevated remote line" }, false);
                Speech.Braille("elevated braille");
                if (File.Exists(log)) return "an elevated copy wrote to KOVA_SPEECH_LOG";
                // And the switch still works for an ordinary test copy, so the check above means something.
                Speech.PrivilegedForTest = false;
                Speech.ReloadTestLogForTest();
                Speech.Remote(new[] { "ordinary remote line" }, false);
                if (!File.Exists(log) || !File.ReadAllText(log).Contains("ordinary remote line")) return "an ordinary test copy no longer wrote to KOVA_SPEECH_LOG";
                return null;
            }
            finally
            {
                Environment.SetEnvironmentVariable("KOVA_SPEECH_LOG", before);
                Speech.PrivilegedForTest = null;
                Speech.ReloadTestLogForTest();
                try { File.Delete(log); } catch { }
            }
        }

        // The NVDA controller client is loaded only when it is the one Kova ships.
        private static string? ControllerDll()
        {
            bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            string name = "nvdaControllerClient" + (arm ? "Arm64" : "64") + ".dll";
            string want = arm ? Speech.ControllerSha256Arm64 : Speech.ControllerSha256X64;
            string dir = Path.Combine(Path.GetTempPath(), "kova-sectest-dll-" + Environment.ProcessId);
            try
            {
                Directory.CreateDirectory(dir);
                string fake = Path.Combine(dir, name);
                File.WriteAllBytes(fake, new byte[] { 0x4D, 0x5A, 1, 2, 3, 4, 5, 6, 7, 8 });
                using (var held = Speech.OpenTrustedController(fake, want, out _))
                    if (held != null) return "a planted controller DLL with the right name was accepted";
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            // The real one, where the runtime looks for native libraries: accepted, and loadable while held open.
            string? real = null;
            if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string search)
                foreach (var d in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    if (Path.IsPathFullyQualified(d) && File.Exists(Path.Combine(d, name))) { real = Path.Combine(d, name); break; }
            if (real == null) return "the shipped controller DLL is not in any native library folder (" + AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") + ")";
            using (var held = Speech.OpenTrustedController(real, want, out string why))
            {
                if (held == null) return "the shipped controller DLL was refused: " + why;
                if (!NativeLibrary.TryLoad(real, out IntPtr lib)) return "the shipped controller DLL would not load while held open";
                if (!NativeLibrary.TryGetExport(lib, "nvdaController_speakText", out _)) return "the shipped controller DLL has no speakText";
                // While held, nobody can replace it.
                try { using var w = new FileStream(real, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); return "the controller DLL could be opened for writing while held"; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        // A host's network cards: at most 8, and no directed broadcast to a public network.
        private static string? WakeCards()
        {
            string many = string.Join("\n", Enumerable.Range(0, 200).Select(i => "0A1B2C3D" + (i & 0xFF).ToString("X2") + "5F 8.8." + (i & 0xFF) + ".1/30"));
            var cards = Wake.Parse(many);
            if (cards.Count > Wake.MaxCards) return "200 card lines gave " + cards.Count + " cards";
            if (cards.Any(c => c.Broadcast != null)) return "a public network got a directed broadcast: " + cards.First(c => c.Broadcast != null).Broadcast;
            var home = Wake.Parse("0A1B2C3D4E5F 192.168.1.20/24\n0A1B2C3D4E60 172.20.0.5/16\n0A1B2C3D4E61 100.64.1.2/24\n0A1B2C3D4E62 10.1.2.3/8");
            if (home.Count != 4 || home[0].Broadcast?.ToString() != "192.168.1.255" || home[1].Broadcast?.ToString() != "172.20.255.255"
                || home[2].Broadcast != null || home[3].Broadcast?.ToString() != "10.255.255.255")
                return "wake broadcasts: " + string.Join(", ", home.Select(c => c.Broadcast?.ToString() ?? "none"));
            return null;
        }

        // The decoder's picture is checked against its buffer before it is read.
        private static string? H264Sizes()
        {
            int w = 1920, h = 1080;
            long need = 1920L * 1080 * 3 / 2;
            if (!Mf.Nv12Fits(need, 1920, 1080, w, h)) return "a picture that fits was refused";
            if (!Mf.Nv12Fits(2048L * 1088 * 3 / 2, 2048, 1088, w, h)) return "a padded picture that fits was refused";
            if (Mf.Nv12Fits(need - 1, 1920, 1080, w, h)) return "a buffer one byte short was taken";
            if (Mf.Nv12Fits(need * 100, 0, 1080, w, h)) return "a pitch of 0 was taken";
            if (Mf.Nv12Fits(need * 100, -1920, 1080, w, h)) return "a negative pitch was taken";
            if (Mf.Nv12Fits(need * 100, 1280, 1080, w, h)) return "a pitch under the width was taken";
            if (Mf.Nv12Fits(need * 100, 1920, 720, w, h)) return "more rows read than the buffer has were taken";
            if (Mf.Nv12Fits(int.MaxValue, int.MaxValue, 65535, w, h)) return "a huge pitch overflowed the check";
            if (Mf.Nv12Fits(1920L * 1081 * 3 / 2 - 1, 1920, 1081, w, h)) return "an odd row count was not rounded up";
            return null;
        }

        // Still pictures: a compressed-size cap, and pieces decoded one at a time.
        private static string? StillPictures()
        {
            // Over the cap: refused before anything is decoded.
            {
                int n = (int)VideoCanvas.MaxStillBytes + 1;
                byte[] m = new byte[8 + 12 + n];
                using (var w = new BinaryWriter(new MemoryStream(m)))
                {
                    w.Write(ScreenVideo.StillPicture); w.Write((ushort)8192); w.Write((ushort)8192); w.Write((byte)1); w.Write((ushort)1);
                    w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)8192); w.Write((ushort)8192); w.Write(n);
                }
                using var canvas = new VideoCanvas();
                try { canvas.Apply(m, 0); return "an update over the compressed-size cap was taken"; }
                catch (InvalidDataException e) when (e.Message.Contains("too big")) { }
            }

            // A whole 8192 x 8192 screen in 128 strips: the decoded strips were all held at once (256 MB on top of the
            // canvas); now one at a time.
            byte[] strip;
            using (var b = new Bitmap(8192, 64, PixelFormat.Format32bppRgb))
            using (var ms = new MemoryStream())
            {
                using (var g = Graphics.FromImage(b)) g.Clear(Color.SteelBlue);
                b.Save(ms, ImageFormat.Png);
                strip = ms.ToArray();
            }
            byte[] Update(bool all)
            {
                using var ms = new MemoryStream();
                using var w = new BinaryWriter(ms);
                w.Write(ScreenVideo.StillPicture); w.Write((ushort)8192); w.Write((ushort)8192); w.Write((byte)(all ? 1 : 0)); w.Write((ushort)128);
                for (int i = 0; i < 128; i++)
                {
                    w.Write((ushort)0); w.Write((ushort)(i * 64)); w.Write((ushort)8192); w.Write((ushort)64); w.Write(strip.Length);
                    w.Write(strip);
                }
                w.Flush();
                return ms.ToArray();
            }
            using (var canvas = new VideoCanvas())
            {
                if (canvas.Apply(Update(true), 0) != 128) return "a whole 8K picture was not applied";
                byte[] change = Update(false);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                using var me = Process.GetCurrentProcess();
                me.Refresh();
                long baseline = me.PrivateMemorySize64, peak = baseline;
                bool done = false;
                var watcher = new Thread(() =>
                {
                    using var p = Process.GetCurrentProcess();
                    while (!Volatile.Read(ref done)) { p.Refresh(); peak = Math.Max(peak, p.PrivateMemorySize64); Thread.Sleep(2); }
                }) { IsBackground = true };
                watcher.Start();
                int got;
                try { got = canvas.Apply(change, 0); }
                finally { Volatile.Write(ref done, true); watcher.Join(); }
                if (got != 128) return "a change to an 8K picture was not applied";
                if (canvas.At(10, 8000).ToArgb() != Color.SteelBlue.ToArgb()) return "the 8K picture was not drawn";
                long rise = peak - baseline;
                if (rise > 100L * 1024 * 1024) return "a still-picture update raised memory by " + rise / (1024 * 1024) + " MB";
            }
            return null;
        }

        private static string Escape(string s) => string.Concat(s.Select(c => c < 0x20 || c > 0x7E ? "\\u" + ((int)c).ToString("X4") : c.ToString()));
    }
}
