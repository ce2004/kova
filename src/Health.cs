using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace TailRemote
{
    /// <summary>One look at how the host PC is doing: power, its Windows drive, processor and memory.</summary>
    internal readonly record struct HealthSample(bool HasBattery, bool OnBattery, int BatteryPercent,
        string Drive, long DiskFree, long DiskTotal, int CpuPercent, int MemoryPercent)
    {
        /// <summary>This PC now. The processor figure is the share used since the last call (the first call says 0).</summary>
        public static HealthSample Read()
        {
            bool hasBattery = false, onBattery = false;
            int percent = -1;
            if (GetSystemPowerStatus(out var p))
            {
                // Flag 128: no battery at all (a desktop); 255: Windows does not know.
                hasBattery = p.BatteryFlag != 128 && p.BatteryFlag != 255 && p.BatteryLifePercent <= 100;
                onBattery = hasBattery && p.ACLineStatus == 0;
                percent = hasBattery ? p.BatteryLifePercent : -1;
            }
            string drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            long free = 0, total = 0;
            try { var d = new DriveInfo(drive); free = d.AvailableFreeSpace; total = d.TotalSize; } catch { }
            int memory = 0;
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref m)) memory = (int)m.dwMemoryLoad;
            return new HealthSample(hasBattery, onBattery, percent, drive.TrimEnd('\\'), free, total, Cpu(), memory);
        }

        private static long _idle, _busy;

        private static int Cpu()
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
            long busy = kernel + user - idle; // kernel time includes the idle time
            long dIdle = idle - _idle, dBusy = busy - _busy;
            bool first = _idle == 0 && _busy == 0;
            _idle = idle; _busy = busy;
            if (first || dIdle + dBusy <= 0) return 0;
            return (int)Math.Clamp(dBusy * 100 / (dIdle + dBusy), 0, 100);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    }

    /// <summary>
    /// Decides what about the host PC is worth saying, from a look every 10 seconds: only changes (unplugged,
    /// plugged in, battery low, drive nearly full, processor busy for a minute, memory nearly gone), each once,
    /// and again only after it has got better. Nothing is said while all is well.
    /// </summary>
    internal sealed class HealthWatch
    {
        public const int EveryMs = 10_000;
        private bool _started, _wasOnBattery, _saidLow, _saidCritical, _saidCpu, _saidMemory;
        private long? _saidDiskAt; // when the drive was last said to be nearly full; null: not since it was fine
        private readonly Queue<int> _cpu = new(), _memory = new();

        /// <summary>What to say about this look, usually nothing. now: milliseconds, any steady clock.</summary>
        public List<string> Next(HealthSample s, long now)
        {
            var say = new List<string>();
            if (s.HasBattery)
            {
                if (_started && s.OnBattery != _wasOnBattery)
                    say.Add(s.OnBattery ? "The remote PC is now running on its battery, " + s.BatteryPercent + " percent."
                                        : "The remote PC is plugged in again, " + s.BatteryPercent + " percent.");
                _wasOnBattery = s.OnBattery;
                if (!s.OnBattery || s.BatteryPercent >= 30) _saidLow = _saidCritical = false;
                else if (s.BatteryPercent <= 10 && !_saidCritical)
                {
                    _saidCritical = _saidLow = true;
                    say.Add("The remote PC's battery is very low, " + s.BatteryPercent + " percent. It may shut down soon.");
                }
                else if (s.BatteryPercent <= 20 && !_saidLow)
                {
                    _saidLow = true;
                    say.Add("The remote PC's battery is low, " + s.BatteryPercent + " percent.");
                }
            }
            _started = true;

            if (s.DiskTotal > 0)
            {
                bool full = Full(s);
                bool fine = s.DiskFree > s.DiskTotal * 8 / 100 || s.DiskFree > 15L << 30;
                if (fine) _saidDiskAt = null;
                else if (full && _saidDiskAt == null) // once, until there is room again: a big drive at 5% still has room for a long time
                {
                    _saidDiskAt = now;
                    say.Add("The remote PC's " + s.Drive + " drive is nearly full: " + Size(s.DiskFree) + " free.");
                }
            }

            // The processor: busy over a whole minute (6 looks), not a moment's spike.
            Keep(_cpu, s.CpuPercent, 6);
            if (_cpu.Count == 6 && _cpu.All(c => c >= 90) && !_saidCpu)
            {
                _saidCpu = true;
                say.Add("The remote PC's processor has been busy for a minute, at " + s.CpuPercent + " percent.");
            }
            else if (_saidCpu && _cpu.Count == 6 && _cpu.All(c => c < 70)) _saidCpu = false;

            Keep(_memory, s.MemoryPercent, 3);
            if (_memory.Count == 3 && _memory.All(m => m >= 95) && !_saidMemory)
            {
                _saidMemory = true;
                say.Add("The remote PC is nearly out of memory: " + s.MemoryPercent + " percent in use.");
            }
            else if (_saidMemory && _memory.All(m => m < 85)) _saidMemory = false;
            return say;
        }

        /// <summary>
        /// What is wrong right now, for a PC that has just connected: it missed the moment it happened.
        /// The processor is not included: one look cannot say it has been busy for a minute.
        /// </summary>
        public static List<string> Standing(HealthSample s)
        {
            var say = new List<string>();
            if (s.HasBattery && s.OnBattery && s.BatteryPercent <= 20)
                say.Add("The remote PC is running on its battery, " + s.BatteryPercent + " percent" + (s.BatteryPercent <= 10 ? ", very low." : ", low."));
            if (Full(s))
                say.Add("The remote PC's " + s.Drive + " drive is nearly full: " + Size(s.DiskFree) + " free.");
            if (s.MemoryPercent >= 95) say.Add("The remote PC is nearly out of memory: " + s.MemoryPercent + " percent in use.");
            return say;
        }

        /// <summary>Under 2 gigabytes, or under 5% when that is also under 10 gigabytes (a 4 terabyte drive at 5% has 200).</summary>
        private static bool Full(HealthSample s) =>
            s.DiskTotal > 0 && (s.DiskFree < 2L << 30 || (s.DiskFree < s.DiskTotal / 20 && s.DiskFree < 10L << 30));

        private static void Keep(Queue<int> q, int v, int n)
        {
            q.Enqueue(v);
            while (q.Count > n) q.Dequeue();
        }

        public static string Size(long bytes) =>
            bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0") + " gigabytes" : (bytes >> 20) + " megabytes";
    }
}
