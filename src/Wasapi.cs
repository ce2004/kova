using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TailRemote
{
    /// <summary>The handful of WASAPI interfaces TailRemote needs, declared by hand.</summary>
    internal static class Wasapi
    {
        /// <summary>
        /// The device went away or changed under us (headphones or Bluetooth unplugged, its sample rate
        /// changed): reopening is all it takes, at once and without a word. Anything else is a real problem.
        /// </summary>
        public static bool DeviceReset(Exception e) => e.HResult == unchecked((int)0x88890004); // AUDCLNT_E_DEVICE_INVALIDATED

        /// <summary>Lets go of a COM object now rather than whenever the garbage collector gets to it.</summary>
        public static void Release(object? o)
        {
            if (o == null || !Marshal.IsComObject(o)) return;
            try { Marshal.ReleaseComObject(o); } catch { }
        }
        public const int eRender = 0, eConsole = 0;
        public const uint DEVICE_STATE_ACTIVE = 1;
        public const uint CLSCTX_ALL = 23;
        public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        public const uint AUDCLNT_BUFFERFLAGS_SILENT = 2;

        public static readonly Guid IID_IAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
        public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCo { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
            void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection
        {
            void GetCount(out uint count);
            void Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            void OpenPropertyStore(uint access, out IPropertyStore store);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetState(out uint state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        public static string? ReadString(IMMDevice dev, Guid fmtid, uint pid)
        {
            try
            {
                dev.OpenPropertyStore(0, out var store);
                var key = new PropertyKey { fmtid = fmtid, pid = pid };
                store.GetValue(ref key, out var pv);
                string? s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.ptr) : null;
                PropVariantClear(ref pv);
                return s;
            }
            catch { return null; }
        }

        /// <summary>Writes a string property. Needs administrator rights for endpoint names.</summary>
        public static void WriteString(IMMDevice dev, Guid fmtid, uint pid, string value)
        {
            dev.OpenPropertyStore(2 /* STGM_READWRITE */, out var store);
            var key = new PropertyKey { fmtid = fmtid, pid = pid };
            var pv = new PropVariant { vt = 31, ptr = Marshal.StringToCoTaskMemUni(value) };
            try { store.SetValue(ref key, ref pv); store.Commit(); }
            finally { PropVariantClear(ref pv); }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PropertyKey { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr ptr;
        }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pv);

        [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient3
        {
            // IAudioClient
            void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            void GetBufferSize(out uint frames);
            void GetStreamLatency(out long latency);
            void GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            void GetMixFormat(out IntPtr format);
            void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            void Start();
            void Stop();
            void Reset();
            void SetEventHandle(IntPtr handle);
            void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
            // IAudioClient2
            void IsOffloadCapable(int category, out int capable);
            void SetClientProperties(IntPtr props);
            void GetBufferSizeLimits(IntPtr format, int eventDriven, out long min, out long max);
            // IAudioClient3
            void GetSharedModeEnginePeriod(IntPtr format, out uint defaultFrames, out uint fundamentalFrames, out uint minFrames, out uint maxFrames);
            void GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint periodFrames);
            void InitializeSharedAudioStream(uint streamFlags, uint periodFrames, IntPtr format, IntPtr sessionGuid);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioRenderClient
        {
            [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
            [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
        }

        public static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();

        /// <summary>Output devices as (id, friendly name).</summary>
        public static List<(string Id, string Name)> OutputDevices()
        {
            var list = new List<(string, string)>();
            try
            {
                Enumerator().EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var coll);
                coll.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    coll.Item(i, out var dev);
                    dev.GetId(out string id);
                    list.Add((id, FriendlyName(dev) ?? id));
                }
            }
            catch { }
            return list;
        }

        internal static string? FriendlyName(IMMDevice dev)
        {
            try
            {
                dev.OpenPropertyStore(0, out var store);
                var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                store.GetValue(ref key, out var pv);
                string? s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.ptr) : null;
                PropVariantClear(ref pv);
                return s;
            }
            catch { return null; }
        }

        /// <summary>The chosen output device, or the default one when id is empty or gone.</summary>
        public static IMMDevice OutputDevice(string? id)
        {
            var e = Enumerator();
            if (!string.IsNullOrEmpty(id))
            {
                try { e.GetDevice(id, out var d); d.GetState(out uint st); if (st == DEVICE_STATE_ACTIVE) return d; }
                catch { }
            }
            e.GetDefaultAudioEndpoint(eRender, eConsole, out var def);
            return def;
        }

        /// <summary>IAudioClient3 exists on every Windows 10 1803+ and Windows 11 machine.</summary>
        public static IAudioClient3 Activate(IMMDevice dev)
        {
            var iid = IID_IAudioClient3;
            dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o);
            return (IAudioClient3)o;
        }

        /// <summary>What a mix format holds: rate, channels, and whether it is float.</summary>
        public readonly record struct Format(int Rate, int Channels, int Bits, bool IsFloat);

        public static Format ReadFormat(IntPtr wfx)
        {
            ushort tag = (ushort)Marshal.ReadInt16(wfx, 0);
            int ch = Marshal.ReadInt16(wfx, 2);
            int rate = Marshal.ReadInt32(wfx, 4);
            int bits = Marshal.ReadInt16(wfx, 14);
            bool isFloat = tag == 3;
            if (tag == 0xFFFE)
            {
                // WAVEFORMATEXTENSIBLE: the sub-format GUID's first 4 bytes are the real tag.
                isFloat = Marshal.ReadInt32(wfx, 24) == 3;
            }
            return new Format(rate, ch, bits, isFloat);
        }
    }
}
