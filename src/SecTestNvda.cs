using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Security tests (--securitytest): Nvda part, what tells the real NVDA from a program merely called nvda.exe (the
    /// Kova Helper add-on's pipe only takes NVDA). Returns null when every check passes, else what failed.
    ///
    /// Live tools for this PC's real NVDA (never run by the plain test): --securitytest Nvda live MODE ...
    ///   speak N MS         NVDA says N lines "kova latency HH:mm:ss.fff" (the time it was handed over), one every MS ms
    ///   latency FILE       reads a kova-testcontrol.txt and works out how long each such line took to arrive
    /// Results go to %TEMP%\kova-nvdalive.txt.
    /// </summary>
    internal static class SecTestNvda
    {
        internal static string? Run()
        {
            var args = Environment.GetCommandLineArgs();
            int live = Array.IndexOf(args, "live");
            if (live >= 0) return Live(args.Skip(live + 1).ToArray());
            return Trust();
        }

        private static void Note(string s) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "kova-nvdalive.txt"), DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { } }

        private static string? Trust()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            Func<string, bool> unsigned = _ => false, signedOk = _ => true;
            if (!NvdaTrust.TrustedNvdaPath(Path.Combine(pf, "NVDA", "nvda.exe"), unsigned)) return "nvda.exe in Program Files, NVDA was not trusted";
            if (!NvdaTrust.TrustedNvdaPath(Path.Combine(pf86, "NVDA", "NVDA.EXE"), unsigned)) return "nvda.exe in Program Files (x86), NVDA was not trusted";
            if (NvdaTrust.TrustedNvdaPath(@"C:\Users\Someone\Downloads\nvda.exe", unsigned)) return "an unsigned nvda.exe in Downloads was trusted (the old check by name only)";
            if (NvdaTrust.TrustedNvdaPath(Path.Combine(pf, "NVDA", "sub", "nvda.exe"), unsigned)) return "an unsigned nvda.exe in a folder below Program Files, NVDA was trusted";
            if (NvdaTrust.TrustedNvdaPath(Path.Combine(pf, "NVDA", "..", "Evil", "nvda.exe"), unsigned)) return "a path climbing out of Program Files, NVDA was trusted";
            if (NvdaTrust.TrustedNvdaPath(Path.Combine(pf, "NVDA", "notepad.exe"), signedOk)) return "a program not called nvda.exe was trusted";
            if (!NvdaTrust.TrustedNvdaPath(@"D:\Portable\NVDA\nvda.exe", signedOk)) return "a portable NVDA signed by NV Access was not trusted";
            if (NvdaTrust.TrustedNvdaPath("nvda.exe", signedOk)) return "a bare file name was trusted";
            // The real signature check, on real files: Windows' own Notepad is signed, but not by NV Access.
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            if (File.Exists(notepad) && NvdaTrust.SignedByNvAccess(notepad)) return "Notepad was taken for signed by NV Access";
            string realNvda = Path.Combine(pf, "NVDA", "nvda.exe");
            if (File.Exists(realNvda) && !NvdaTrust.SignedByNvAccess(realNvda)) return "the installed NVDA's signature was not recognised as NV Access's";
            string copy = Path.Combine(Path.GetTempPath(), "kova-sectest-nvda", "nvda.exe");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.WriteAllText(copy, "not a program");
                if (NvdaTrust.IsTrustedNvda(copy)) return "a fake nvda.exe in the temp folder was trusted";
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(copy)!, true); } catch { } }
            return null;
        }

        // ---- What a person hears ----

        private static string? Live(string[] a)
        {
            string mode = a.Length > 0 ? a[0] : "speak";
            switch (mode)
            {
                case "speak":
                {
                    int n = a.Length > 1 ? int.Parse(a[1]) : 20, ms = a.Length > 2 ? int.Parse(a[2]) : 700;
                    Speech.Init();
                    for (int i = 0; i < n; i++)
                    {
                        Speech.Speak("kova latency " + DateTime.Now.ToString("HH:mm:ss.fff"), false);
                        Thread.Sleep(ms);
                    }
                    Thread.Sleep(500);
                    Note("spoke " + n + " lines");
                    return null;
                }
                case "latency": return Latency(a.Length > 1 ? a[1] : Path.Combine(Path.GetTempPath(), "kova-testcontrol.txt"));
                default: return "unknown live mode " + mode;
            }
        }

        /// <summary>
        /// From a --testcontrol log: each "kova latency T" line arrived at the log line's time; the difference is the whole
        /// way from NVDA being handed the line on the host to the controlling Kova handing it to its own NVDA.
        /// </summary>
        private static string? Latency(string file)
        {
            var ms = new List<double>();
            foreach (string line in File.ReadAllLines(file))
            {
                if (line.Length < 13 || !line.Contains("remote NVDA:")) continue;
                if (!TimeSpan.TryParseExact(line[..12], @"hh\:mm\:ss\.fff", null, out TimeSpan got)) continue;
                int at = 0;
                while ((at = line.IndexOf("kova latency ", at, StringComparison.Ordinal)) >= 0)
                {
                    at += 13;
                    if (at + 12 <= line.Length && TimeSpan.TryParseExact(line.Substring(at, 12), @"hh\:mm\:ss\.fff", null, out TimeSpan sent))
                        ms.Add((got - sent).TotalMilliseconds);
                }
            }
            if (ms.Count == 0) { Note("latency: no lines found in " + file); return "no latency lines in " + file; }
            ms.Sort();
            double median = ms.Count % 2 == 1 ? ms[ms.Count / 2] : (ms[ms.Count / 2 - 1] + ms[ms.Count / 2]) / 2;
            Note("latency over " + ms.Count + " lines: median " + median + " ms, best " + ms[0] + " ms, worst " + ms[^1] + " ms; all: " + string.Join(" ", ms));
            return null;
        }
    }
}
