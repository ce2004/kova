using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// --soaktest [minutes]: Kova has to run for years, so this does what months of use do, round after
    /// round on this PC (a host and its clients, as in --chaostest; no keys, never the real clipboard):
    /// log in as a controller and a listener, send files both ways, watch the screen, rebuild a big menu
    /// the way the history and saved PC menus are rebuilt, and disconnect. After a few rounds to settle,
    /// handles, GDI and USER objects, threads and memory must stay where they were. Results:
    /// %TEMP%\kova-soaktest.txt.
    /// </summary>
    internal static class SoakTest
    {
        private const int Port = 48200;
        private const string Password = "soak-pass", ListenPassword = "soak-listen";
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "kova-soak");

        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);

        private readonly record struct Sample(int Round, long Managed, long Private, int Handles, int Gdi, int User, int Threads);

        public static int Run(int minutes)
        {
            Host.WrongPasswordDelayMs = 0;
            Names.TestPipes = "Soak";
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            Directory.CreateDirectory(Root);
            FileChannel.StagingOverride = Path.Combine(Root, "stage");
            FileChannel.DownloadsOverride = Path.Combine(Root, "downloads");
            var lines = new List<string>();
            void Say(string s) { lines.Add(s); Console.WriteLine(s); }

            string up = Path.Combine(Root, "src", "up.bin"), down = Path.Combine(Root, "src", "down.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(up)!);
            var data = new byte[256 << 10];
            new Random(5).NextBytes(data);
            File.WriteAllBytes(up, data);
            File.WriteAllBytes(down, data);

            // The menu as the window builds it: a bar with stay-open submenus, one rebuilt over and over.
            using var bar = new MenuStrip();
            var history = new ToolStripMenuItem("History");
            history.DropDownItems.Add(new ToolStripMenuItem("placeholder"));
            bar.Items.Add(history);
            Menus.Attach(bar);

            var samples = new List<Sample>();
            int failures = 0;
            using var host = new Host(Port, Password, ListenPassword, _ => { });
            Thread.Sleep(500);
            var clock = Stopwatch.StartNew();
            int round = 0;
            while (clock.Elapsed.TotalMinutes < minutes || round < 12)
            {
                round++;
                string? problem = Round(host, up, down, history);
                if (problem != null) { failures++; Say("round " + round + ": " + problem); }
                var s = Measure(round);
                samples.Add(s);
                if (round % 5 == 0 || round <= 3)
                    Say("round " + round + ": managed " + (s.Managed >> 20) + " MB, private " + (s.Private >> 20) + " MB, handles " + s.Handles + ", GDI " + s.Gdi + ", USER " + s.User + ", threads " + s.Threads);
            }

            // Settled after the first few rounds (caches, .NET's own warm-up): judged from there.
            var from = samples[Math.Min(4, samples.Count - 1)];
            var to = samples[^1];
            // Growth over the second half too, so a slow, steady climb is seen even when the start was noisy.
            var mid = samples[samples.Count / 2];
            string Grew(string what, Sample start, long a, long b, long allowed, string unit = "")
            {
                bool ok = b - a <= allowed;
                if (!ok) failures++;
                return (ok ? "ok    " : "FAIL  ") + what + ": " + a + unit + " after round " + start.Round + ", " + b + unit + " after round " + to.Round + " (allowed growth " + allowed + unit + ")";
            }
            Say("");
            Say(Grew("handles", from, from.Handles, to.Handles, 60));
            Say(Grew("handles, second half", mid, mid.Handles, to.Handles, 30));
            Say(Grew("GDI objects", from, from.Gdi, to.Gdi, 10));
            Say(Grew("USER objects", from, from.User, to.User, 10));
            Say(Grew("threads of its own", from, from.Threads, to.Threads, 8));
            Say(Grew("managed memory", from, from.Managed >> 20, to.Managed >> 20, 8, " MB"));
            Say(Grew("managed memory, second half", mid, mid.Managed >> 20, to.Managed >> 20, 4, " MB"));
            Say(Grew("private memory", from, from.Private >> 20, to.Private >> 20, 40, " MB"));
            string text = (failures == 0 ? "ALL PASSED" : failures + " FAILED") + ", " + round + " rounds in " + clock.Elapsed.TotalMinutes.ToString("0.0") + " minutes" + Environment.NewLine + string.Join(Environment.NewLine, lines);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "kova-soaktest.txt"), text);
            try { Directory.Delete(Root, true); } catch { }
            return failures == 0 ? 0 : 1;
        }

        /// <summary>One round of everyday use. Null when it all worked, else what did not.</summary>
        private static string? Round(Host host, string up, string down, ToolStripMenuItem history)
        {
            var player = new Player(Player.NoDevice, _ => { });
            var listenPlayer = new Player(Player.NoDevice, _ => { });
            try
            {
                using var ctrl = Client.Connect("127.0.0.1", Port, Password, player, _ => { });
                using var listener = Client.Connect("127.0.0.1", Port, ListenPassword, listenPlayer, _ => { });

                // Files both ways, through the clipboard path (the one with a holding folder to clean up).
                string[]? atHost = null, atClient = null;
                Action<string[]> gotHost = p => atHost = p;
                Action<string[]> gotClient = p => atClient = p;
                host.ClipboardFilesReceived += gotHost;
                ctrl.ClipboardFilesReceived += gotClient;
                try
                {
                    if (!Wait(() => ctrl.Files != null && host.ControllerFiles != null, 10_000)) return "the file connection never opened";
                    ctrl.Files!.SendFiles(new[] { up });
                    if (!Wait(() => atHost != null, 20_000)) return "a file sent to the host never arrived";
                    host.ControllerFiles!.SendFiles(new[] { down });
                    if (!Wait(() => atClient != null, 20_000)) return "a file sent from the host never arrived";
                }
                finally
                {
                    host.ClipboardFilesReceived -= gotHost;
                    ctrl.ClipboardFilesReceived -= gotClient;
                }

                // The remote screen for two seconds.
                using (var watch = ctrl.WatchScreen())
                {
                    if (watch == null) return "the screen could not be watched";
                    if (!Wait(() => watch.Pictures > 0, 10_000)) return "no picture of the screen came";
                    Thread.Sleep(2000);
                }

                // The history menu rebuilt as after 20 transfers, 100 lines each time.
                for (int i = 0; i < 20; i++)
                {
                    Menus.ClearAndDispose(history.DropDownItems);
                    for (int j = 0; j < 100; j++) history.DropDownItems.Add(new MenuStatus("line " + j).Item);
                }
                return null;
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
            finally
            {
                player.Dispose();
                listenPlayer.Dispose();
                Thread.Sleep(1500); // the host lets the sessions go in the background
            }
        }

        private static bool Wait(Func<bool> done, int ms)
        {
            for (int i = 0; i < ms / 25 && !done(); i++) Thread.Sleep(25);
            return done();
        }

        private static Sample Measure(int round)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            GC.GetTotalMemory(true);
            long managed = GC.GetGCMemoryInfo().HeapSizeBytes; // GetTotalMemory came out below zero here
            using var me = Process.GetCurrentProcess();
            return new Sample(round, managed, me.PrivateMemorySize64, me.HandleCount,
                GetGuiResources(me.Handle, 0), GetGuiResources(me.Handle, 1),
                // Threads of its own: .NET's worker pool grows and shrinks by itself, which is not a leak.
                me.Threads.Count - ThreadPool.ThreadCount);
        }
    }
}
