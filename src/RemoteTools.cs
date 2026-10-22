using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TailRemote
{
    /// <summary>
    /// What a host answers for the controlling PC's Get files from the remote PC and Remote
    /// PC info. Only controllers get here (never listeners); whoever has the main password can
    /// type anything on this PC anyway, so this adds no reach, only convenience.
    /// </summary>
    internal static class RemoteTools
    {
        private static readonly Guid FolderDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
        private static readonly Guid FolderDocuments = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");

        public const int MaxEntries = 5000;
        private static readonly HashSet<string> Asking = new(StringComparer.OrdinalIgnoreCase); // drives whose last look has not come back yet

        /// <summary>
        /// A folder's contents, one line per entry, tab-separated: kind (D folder, F file),
        /// full path, name to show, bytes, last-modified ticks (UTC). "" lists the usual
        /// folders and the drives. A problem is one line: E, then what went wrong.
        /// </summary>
        public static string ListFolder(string path)
        {
            var sb = new StringBuilder();
            void Line(char kind, string full, string name, long bytes = 0, long ticks = 0) =>
                sb.Append(kind).Append('\t').Append(full).Append('\t').Append(name).Append('\t').Append(bytes).Append('\t').Append(ticks).Append('\n');
            try
            {
                if (path.Length == 0)
                {
                    // The usual folders of whoever is signed in here (the service runs as the system, which has none worth showing).
                    bool isSystem;
                    using (var me = System.Security.Principal.WindowsIdentity.GetCurrent()) isSystem = me.IsSystem;
                    if (!isSystem)
                    {
                        // Asked with this thread's own user: when the service lists as the signed-in user,
                        // Environment.GetFolderPath can still answer with the system's profile.
                        foreach (var (folder, name) in new[]
                        {
                            (FolderDesktop, "Desktop"),
                            (FolderDocuments, "Documents"),
                            (NativeService.FolderDownloads, "Downloads"),
                        })
                        {
                            string? f = NativeService.ThreadUserFolder(folder);
                            if (!string.IsNullOrEmpty(f) && Directory.Exists(f)) Line('D', f, name);
                        }
                    }
                    else if (Directory.Exists(@"C:\Users")) Line('D', @"C:\Users", "Users (everyone's folders)");
                    // A network drive whose server is gone, an empty CD drive or a sleeping USB disk can
                    // take tens of seconds to say whether it is ready, and the controlling PC gives up
                    // after 15: the whole list of drives failed because of one. So every drive is asked
                    // at once, each on a thread of its own, and one that has not answered within about a
                    // second is listed without its details, as not answering.
                    var drives = DriveInfo.GetDrives();
                    var details = drives.Select(d =>
                    {
                        // Still stuck from the last look: not asked again, so stuck threads never pile up.
                        lock (Asking) if (!Asking.Add(d.Name)) return System.Threading.Tasks.Task.FromException<string>(new TimeoutException());
                        return System.Threading.Tasks.Task.Factory.StartNew(() => { try { return DriveDetails(d); } finally { lock (Asking) Asking.Remove(d.Name); } },
                            System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
                    }).ToArray();
                    try { System.Threading.Tasks.Task.WaitAll(details, 1200); } catch { }
                    for (int i = 0; i < drives.Length; i++)
                    {
                        var t = details[i];
                        string name = drives[i].Name.TrimEnd('\\') + (t.IsCompletedSuccessfully ? t.Result : ", not answering");
                        Line('D', drives[i].Name, name);
                    }
                    return sb.ToString();
                }
                // Opened by its \\?\ form: plain, Windows cuts a dot or space off the end of a name, so a folder a
                // Mac, Linux or WSL made ("notes.") was listed but opening it opened "notes" (or said it was gone).
                var dir = new DirectoryInfo(FileChannel.Long(path));
                if (!dir.Exists)
                {
                    // An empty CD drive or a disk that is asleep or locked is not "a folder not there any more".
                    string? root = Path.GetPathRoot(FileChannel.Plain(dir.FullName));
                    if (root != null && root.Length <= 3 && !Directory.Exists(root) &&
                        DriveInfo.GetDrives().Any(d => string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase)))
                        return "E\tThat drive is not ready.\n";
                    return "E\tThat folder is not there any more.\n";
                }
                const FileAttributes hiddenSystem = FileAttributes.Hidden | FileAttributes.System;
                // At most MaxEntries + 1 in all are read, then sorted: a folder of a million files must
                // not be read whole. The one extra says whether there were more than MaxEntries, so
                // "only the first" is said only when something really was left out (a folder of exactly
                // MaxEntries used to say it too).
                var dirs = dir.EnumerateDirectories().Where(d => (d.Attributes & hiddenSystem) != hiddenSystem).Take(MaxEntries + 1).ToList();
                var files = dirs.Count > MaxEntries ? new List<FileInfo>()
                    : dir.EnumerateFiles().Where(f => (f.Attributes & hiddenSystem) != hiddenSystem).Take(MaxEntries + 1 - dirs.Count).ToList();
                bool more = dirs.Count + files.Count > MaxEntries;
                int n = 0;
                // The answer must fit in one message (SecureLink: 8 MB): 5000 entries with very long paths
                // can be more, and an answer too big to send never arrived ("did not answer"). So the list
                // stops short of 7 MB and says how many it shows, like a folder of too many entries.
                long bytes = 0;
                bool Fits(string full, string name)
                {
                    bytes += Encoding.UTF8.GetByteCount(full) + Encoding.UTF8.GetByteCount(name) + 64;
                    if (bytes <= MaxListingBytes) return true;
                    more = true;
                    return false;
                }
                // Sorted as Explorer sorts (file2 before file10, letters with accents among their own
                // letter), whatever the culture settings of the process.
                foreach (var d in dirs.OrderBy(d => d.Name, Natural))
                {
                    if (n >= MaxEntries || !Fits(FileChannel.Plain(d.FullName), d.Name)) break;
                    n++;
                    Line('D', FileChannel.Plain(d.FullName), d.Name, 0, Ticks(d));
                }
                foreach (var f in files.OrderBy(f => f.Name, Natural))
                {
                    if (n >= MaxEntries || !Fits(FileChannel.Plain(f.FullName), f.Name)) break;
                    n++;
                    long length;
                    try { length = f.Length; } catch { length = 0; }
                    Line('F', FileChannel.Plain(f.FullName), f.Name, length, Ticks(f));
                }
                // T: there were more than were listed, and how many were. An older controlling PC skips lines it does not know.
                if (more) sb.Append("T\t").Append(n).Append('\n');
                return sb.ToString();
            }
            catch (UnauthorizedAccessException) { return "E\tKova on the remote PC is not allowed into that folder.\n"; }
            catch (Exception e) { return "E\t" + FileChannel.Plain(e.Message).Replace('\n', ' ').Replace('\t', ' ') + "\n"; } // the folder is opened by its \\?\ form: not said aloud
        }

        /// <summary>How big a folder listing may grow: under SecureLink's 8 MB message, with room to spare.</summary>
        public const int MaxListingBytes = 7 << 20;

        /// <summary>
        /// When an entry was last changed, or 0 if it cannot say: a file stamped beyond the year 9999
        /// (a broken copy tool, a damaged disk) threw for the whole folder, which then could not be listed.
        /// </summary>
        private static long Ticks(FileSystemInfo i)
        {
            try { return i.LastWriteTimeUtc.Ticks; } catch { return 0; }
        }

        /// <summary>Explorer's order: numbers in names by value, letters as the user's language orders them.</summary>
        private static readonly IComparer<string> Natural = Comparer<string>.Create((a, b) => StrCmpLogicalW(a, b));

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int StrCmpLogicalW(string a, string b);

        /// <summary>A drive's label and space (", not ready" if it has no disk), as shown after its letter.</summary>
        private static string DriveDetails(DriveInfo d)
        {
            try
            {
                if (!d.IsReady) return ", not ready";
                return (d.VolumeLabel.Length > 0 ? " " + d.VolumeLabel : "") + ", " + FileChannel.Size(d.AvailableFreeSpace) + " free of " + FileChannel.Size(d.TotalSize);
            }
            catch { return ""; }
        }

        /// <summary>About this PC, one "Name: value" line each, for Remote PC info. recentCpu: the host's own processor reading, or -1.</summary>
        public static string Info(string tailRemote, int recentCpu = -1)
        {
            int cpu = CpuPercent(recentCpu);
            var lines = new List<string> { "Name: " + Environment.MachineName };
            lines.Add("Windows: " + WindowsName());
            string user = SignedInUser();
            lines.Add("Signed in: " + (user.Length == 0 ? "nobody (the sign-in screen)" : user));
            lines.Add("On for: " + MainForm.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64)));
            lines.Add("Processor busy: " + (cpu >= 0 ? cpu + " percent" : "not measured yet, ask again in a moment") + ", " + Environment.ProcessorCount + " cores");
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
                lines.Add("Memory: " + mem.dwMemoryLoad + " percent used, " + FileChannel.Size((long)mem.ullAvailPhys) + " free of " + FileChannel.Size((long)mem.ullTotalPhys));
            if (GetSystemPowerStatus(out var power))
            {
                if (WlanStreaming.State is string wifi) lines.Add(wifi);
                if (power.BatteryFlag == 255) lines.Add("Battery: unknown"); // Windows cannot tell
                else if ((power.BatteryFlag & 128) != 0) lines.Add("Battery: none (plugged in)");
                else lines.Add("Battery: " + (power.BatteryLifePercent <= 100 ? power.BatteryLifePercent + " percent" : "unknown") +
                    (power.ACLineStatus == 1 ? ", plugged in" + ((power.BatteryFlag & 8) != 0 ? " and charging" : "") : ", on battery") +
                    (power.ACLineStatus != 1 && power.BatteryLifeTime != uint.MaxValue ? ", about " + MainForm.Duration(TimeSpan.FromSeconds(power.BatteryLifeTime)) + " left" : ""));
            }
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == DriveType.Fixed && d.IsReady)
                        lines.Add("Drive " + d.Name.TrimEnd('\\') + ": " + FileChannel.Size(d.AvailableFreeSpace) + " free of " + FileChannel.Size(d.TotalSize));
                }
                catch { }
            }
            lines.Add("Kova: " + tailRemote);
            return string.Join("\n", lines);
        }

        private static string WindowsName()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                int build = Environment.OSVersion.Version.Build;
                // ProductName still says Windows 10 on Windows 11, so the build decides.
                string product = (k?.GetValue("ProductName") as string ?? "Windows").Replace("Windows 10", build >= 22000 ? "Windows 11" : "Windows 10");
                string release = k?.GetValue("DisplayVersion") as string ?? "";
                return product + (release.Length > 0 ? " " + release : "") + ", build " + build + ", " + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
            }
            catch { return RuntimeInformation.OSDescription; }
        }

        /// <summary>Who is signed in at the screen (the service runs as the system, so it asks Windows).</summary>
        private static string SignedInUser()
        {
            try
            {
                uint session = NativeService.WTSGetActiveConsoleSessionId();
                if (session == 0xFFFFFFFF) return "";
                if (!WTSQuerySessionInformationW(IntPtr.Zero, session, 5 /* WTSUserName */, out IntPtr buf, out _)) return "";
                try { return Marshal.PtrToStringUni(buf) ?? ""; }
                finally { WTSFreeMemory(buf); }
            }
            catch { return ""; }
        }

        private static readonly object CpuGate = new();
        private static long _cpuAt, _cpuIdle, _cpuKernel, _cpuUser; // the last reading (_cpuAt 0: none yet)
        private static int _cpuLast = -1;

        /// <summary>
        /// How busy the processor is, since the last time this was asked (or, if that was too long ago or
        /// never, the host's own reading of the last few seconds: recent, -1 if it has none), or -1 if
        /// nothing is known yet. Never waits: it used to sleep half a second on a pool thread for every
        /// request, and a flood of them starved everything else on the pool.
        /// </summary>
        private static int CpuPercent(int recent)
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user)) return recent;
            long now = Environment.TickCount64;
            lock (CpuGate)
            {
                long age = now - _cpuAt;
                if (_cpuAt != 0 && age < 250) return _cpuLast >= 0 ? _cpuLast : recent; // asked again at once: too short to measure
                int result;
                long total = (kernel - _cpuKernel) + (user - _cpuUser); // kernel time includes idle
                if (_cpuAt != 0 && age <= 30_000 && total > 0) result = (int)Math.Clamp(100 - (idle - _cpuIdle) * 100 / total, 0, 100);
                else if (recent >= 0) result = recent;
                else if (_cpuAt != 0 && total > 0) result = (int)Math.Clamp(100 - (idle - _cpuIdle) * 100 / total, 0, 100); // a long while, but still a measure
                else result = -1;
                _cpuAt = now; _cpuIdle = idle; _cpuKernel = kernel; _cpuUser = user;
                _cpuLast = result;
                return result;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public uint BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    }
}
