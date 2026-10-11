using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TailRemote
{
    /// <summary>
    /// Keeps Wi-Fi steady while TailRemote is connected or hosting. Windows
    /// regularly makes the Wi-Fi card scan for other networks, and lets it doze
    /// between packets; both hold packets back and then release them in a
    /// clump, which with a fixed audio delay is heard as chopping. Windows'
    /// own answer, used by streaming and game apps, is media streaming mode
    /// plus switching background scans off. Both last only while this handle
    /// is open: Windows puts them back when it is disposed, or if TailRemote
    /// closes or crashes. Wired connections are untouched.
    /// </summary>
    internal sealed class WlanStreaming : IDisposable
    {
        private const int BackgroundScanEnabled = 2, MediaStreamingMode = 3; // WLAN_INTF_OPCODE
        private IntPtr _handle;

        /// <summary>
        /// How it went last time, said in the Streaming line and Remote PC info: null with no Wi-Fi,
        /// else whether Windows agreed to hold off the scans. Some Wi-Fi drivers refuse, and then
        /// Ride out Wi-Fi scans is what keeps the sound going.
        /// </summary>
        public static volatile string? State;
        private readonly System.Threading.Tasks.Task _opened;

        [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern int WlanSetInterface(IntPtr handle, ref Guid iface, int opcode, int size, ref int data, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);

        /// <summary>Opens in the background: the Wi-Fi service can be slow to answer, and the window must never wait on it.</summary>
        public WlanStreaming(string who) => _opened = System.Threading.Tasks.Task.Run(() => Open(who));

        private void Open(string who)
        {
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out _handle) != 0) { _handle = IntPtr.Zero; return; } // no Wi-Fi service
                if (WlanEnumInterfaces(_handle, IntPtr.Zero, out IntPtr list) != 0) return;
                var done = new List<string>();
                try
                {
                    int count = Marshal.ReadInt32(list, 0);
                    const int itemSize = 16 + 512 + 4; // GUID, 256 UTF-16 chars, state
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = list + 8 + i * itemSize;
                        var guid = Marshal.PtrToStructure<Guid>(item);
                        string name = Marshal.PtrToStringUni(item + 16) ?? "Wi-Fi";
                        int on = 1, off = 0;
                        int a = WlanSetInterface(_handle, ref guid, MediaStreamingMode, 4, ref on, IntPtr.Zero);
                        int b = WlanSetInterface(_handle, ref guid, BackgroundScanEnabled, 4, ref off, IntPtr.Zero);
                        done.Add(name + ": streaming mode " + (a == 0 ? "on" : "refused (" + a + ")") + ", background scans " + (b == 0 ? "off" : "refused (" + b + ")") +
                            (a != 0 || b != 0 ? (Startup.IsElevated() ? "" : "; this may need Kova running as administrator") : ""));
                    }
                }
                finally { WlanFreeMemory(list); }
                if (done.Count == 0) State = null; // no Wi-Fi in use now (on a cable): nothing to say about it
                else State = done.Exists(d => d.Contains("refused")) ? "Wi-Fi would not hold off its scans for networks (its driver refused)" : "Wi-Fi scans for networks held off while connected";
                DiagLog.Write(who + ": Wi-Fi " + (done.Count == 0 ? "none" : string.Join("; ", done)));
            }
            catch (Exception e) { DiagLog.Write(who + ": Wi-Fi settings not changed: " + e.Message); }
        }

        public void Dispose() => _opened.ContinueWith(_ =>
        {
            if (_handle == IntPtr.Zero) return;
            try { WlanCloseHandle(_handle, IntPtr.Zero); } catch { } // Windows restores both settings
            _handle = IntPtr.Zero;
            State = null;
        });
    }
}
