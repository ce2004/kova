using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TailRemote
{
    /// <summary>
    /// Gives a host with no sound card an output called TailRemote, made from
    /// VB-Cable (free, donationware, signed by Microsoft for x64 and ARM64).
    ///
    /// VB-Cable's licence does not allow building it into another program's
    /// installer, so TailRemote downloads the official package from vb-audio.com,
    /// checks it is really signed by VB-Audio, and opens VB-Audio's own installer
    /// for the one Install Driver press. Everything after that is configuration:
    /// the "CABLE Input" output is renamed TailRemote and made the default.
    ///
    /// All of it runs in an elevated copy of TailRemote (--setup-audio), so one
    /// administrator prompt covers the installer and the renaming.
    /// </summary>
    internal static class AudioSetup
    {
        public const string DeviceName = "Kova";

        // Newest first; a missing pack answers 404 at once and the next is tried.
        private static readonly string[] PackUrls =
        {
            "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack47.zip",
            "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip",
            "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip",
            "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip",
        };

        private static readonly Guid PkeyDevice = new("a45c254e-df1c-4efd-8020-67d146a850e0");    // pid 2: the endpoint's own name
        private static readonly Guid PkeyInterface = new("026e516e-b814-414b-83cd-856d6fef4822"); // pid 2: the driver's name

        private const uint StateDisabled = 2; // DEVICE_STATE_DISABLED: turned off in the Sound settings
        private static readonly Guid PkeyFormat = new("f19f064d-082c-4e27-bc73-6882a1bb8e4c"); // pid 0: the device format (a WAVEFORMATEX)

        /// <summary>
        /// VB-Cable's output (named Kova, CABLE Input, or anything else), if it is installed, working
        /// or turned off. Never the 16-channel "CABLE In 16 Ch" the driver also adds. Only seeing
        /// working outputs named exactly Kova or CABLE Input meant a cable turned off or renamed in
        /// the Sound settings looked missing, and setup reinstalled it and asked for a restart forever.
        /// </summary>
        public static Wasapi.IMMDevice? FindCable()
        {
            try
            {
                Wasapi.Enumerator().EnumAudioEndpoints(Wasapi.eRender, Wasapi.DEVICE_STATE_ACTIVE | StateDisabled, out var coll);
                coll.GetCount(out uint n);
                Wasapi.IMMDevice? best = null;
                int bestRank = int.MaxValue;
                for (uint i = 0; i < n; i++)
                {
                    coll.Item(i, out var dev);
                    if (Wasapi.ReadString(dev, PkeyInterface, 2) != "VB-Audio Virtual Cable") continue;
                    string? name = Wasapi.ReadString(dev, PkeyDevice, 2);
                    if (name == "CABLE In 16 Ch" || Channels(dev) == 16) continue;
                    dev.GetState(out uint state);
                    // Kova first, then the name it came with, then a name someone gave it; working before off.
                    int rank = (name == DeviceName ? 0 : name == "CABLE Input" ? 2 : 4) + (state == Wasapi.DEVICE_STATE_ACTIVE ? 0 : 1);
                    if (rank < bestRank) { best = dev; bestRank = rank; }
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>The device format's channel count, or 0 if Windows does not say.</summary>
        private static int Channels(Wasapi.IMMDevice dev)
        {
            try
            {
                dev.OpenPropertyStore(0, out var store);
                var key = new Wasapi.PropertyKey { fmtid = PkeyFormat, pid = 0 };
                store.GetValue(ref key, out var pv);
                try
                {
                    // A VT_BLOB: its size at 8 and its data pointer at 16 (64-bit Windows only, as Kova is).
                    var raw = System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref pv, 1));
                    if (pv.vt != 65 || BitConverter.ToUInt32(raw[8..]) < 4) return 0;
                    IntPtr data = (IntPtr)BitConverter.ToInt64(raw[16..]);
                    return data == IntPtr.Zero ? 0 : Marshal.ReadInt16(data, 2); // WAVEFORMATEX.nChannels
                }
                finally { PropVariantClear(ref pv); }
            }
            catch { return 0; }
        }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Wasapi.PropVariant pv);

        /// <summary>Set up: VB-Cable is named Kova and is the default output.</summary>
        public static bool IsReady()
        {
            var d = FindCable();
            if (d == null || Wasapi.ReadString(d, PkeyDevice, 2) != DeviceName) return false;
            try
            {
                Wasapi.Enumerator().GetDefaultAudioEndpoint(Wasapi.eRender, Wasapi.eConsole, out var def);
                def.GetId(out string defId);
                d.GetId(out string id);
                return id == defId && ExtraOutputs(id).Count == 0;
            }
            catch { return false; }
        }

        /// <summary>Starts the elevated setup window and waits for it. False if refused or it failed.</summary>
        public static async Task<bool> RunElevatedAsync()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--setup-audio") { UseShellExecute = true, Verb = "runas" });
                if (p == null) return false;
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch { return false; } // the administrator prompt was declined
        }

        /// <summary>The whole setup, reporting (text, percent or -1) as it goes.</summary>
        public static async Task RunAsync(Action<string, int> report, CancellationToken ct)
        {
            if (FindCable() == null)
            {
                // Program Files: only administrators can change files there between
                // the signature check and running the installer. Named Kova, as every folder Kova makes is.
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kova-vbcable");
                byte[] zip = await DownloadAsync(report, ct);
                Process? installer = null;
                try
                {
                    report("Checking the download.", 100);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    using (var ms = new MemoryStream(zip)) ZipFile.ExtractToDirectory(ms, dir);
                    string setup = Path.Combine(dir, "VBCABLE_Setup_x64.exe"); // VB-Audio's installer on ARM64 too
                    if (!File.Exists(setup) || !SignedByVbAudio(setup))
                        throw new InvalidOperationException("The VB-Cable download did not check out as genuine, so it was not run. Try again later.");

                    report("VB-Cable's installer is open. Press Install Driver, then wait for it to finish.", -1);
                    installer = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, WorkingDirectory = dir })
                        ?? throw new InvalidOperationException("VB-Cable's installer did not start.");
                    try { await installer.WaitForExitAsync(ct); }
                    catch (OperationCanceledException) when (!installer.HasExited)
                    {
                        // Cancel cannot stop VB-Audio's own window, and saying "Cancelled" while it
                        // could still install the driver was untrue.
                        throw new InvalidOperationException("Kova stopped waiting, but VB-Cable's installer is still open and can still install the driver. Close it, then press Set up audio device again if you want to finish.");
                    }
                }
                finally
                {
                    // The installer has copied what it needs into Windows; the download goes. Not
                    // while the installer still runs from it: the next setup clears it instead.
                    if (installer == null || installer.HasExited)
                        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                    installer?.Dispose();
                }

                report("Waiting for Windows to add the device.", -1);
                for (int i = 0; i < 60 && FindCable() == null; i++) await Task.Delay(500, ct);
                if (FindCable() == null)
                {
                    if (DriverInstalled())
                        throw new InvalidOperationException("VB-Cable is installed, but Windows needs a restart to finish. Restart, then press Set up audio device again to finish.");
                    throw new InvalidOperationException("VB-Cable was not installed. Press Set up audio device to try again, and press Install Driver in its window.");
                }
            }

            report("Naming the device " + DeviceName + " and making it the default output.", 100);
            await Configure(ct);
        }

        private static async Task Configure(CancellationToken ct)
        {
            var dev = FindCable() ?? throw new InvalidOperationException("VB-Cable's output was not found.");
            dev.GetId(out string id);
            dev.GetState(out uint state);
            if (state != Wasapi.DEVICE_STATE_ACTIVE)
            {
                // Turned off in the Sound settings: on again, as Enable there does (only ever from this button).
                ((IPolicyConfig)new PolicyConfigCo()).SetEndpointVisibility(id, 1);
                for (int i = 0; i < 20; i++)
                {
                    Wasapi.Enumerator().GetDevice(id, out dev);
                    dev.GetState(out state);
                    if (state == Wasapi.DEVICE_STATE_ACTIVE) break;
                    await Task.Delay(250, ct);
                }
                if (state != Wasapi.DEVICE_STATE_ACTIVE)
                    throw new InvalidOperationException("VB-Cable's output is turned off and Windows would not turn it on. Turn it on in the Sound settings, then press Set up audio device again.");
            }
            RememberPreviousOutput();
            Wasapi.WriteString(dev, PkeyDevice, 2, DeviceName);
            SetDefault(id);
            DisableExtraOutputs(id);
        }

        // Where setup notes the output that was the default before it, so removing VB-Cable can go back to it.
        private const string RecordKey = @"SOFTWARE\Kova";
        private const string RecordValue = "OutputBeforeAudioSetup";

        private static void RememberPreviousOutput()
        {
            try
            {
                Wasapi.Enumerator().GetDefaultAudioEndpoint(Wasapi.eRender, Wasapi.eConsole, out var def);
                if (Wasapi.ReadString(def, PkeyInterface, 2) == "VB-Audio Virtual Cable") return; // already the cable: keep the one noted before
                def.GetId(out string id);
                using var k = Registry.LocalMachine.CreateSubKey(RecordKey);
                k.SetValue(RecordValue, id);
            }
            catch { } // no default output at all, or nowhere to note it: removal picks another
        }

        /// <summary>The output noted at setup, if it is still here and working.</summary>
        private static (string Id, string Name)? PreviousOutput()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(RecordKey);
                if (k?.GetValue(RecordValue) is not string id) return null;
                Wasapi.Enumerator().GetDevice(id, out var dev);
                dev.GetState(out uint state);
                if (state != Wasapi.DEVICE_STATE_ACTIVE || Wasapi.ReadString(dev, PkeyInterface, 2) == "VB-Audio Virtual Cable") return null;
                return (id, Wasapi.FriendlyName(dev) ?? id);
            }
            catch { return null; }
        }

        private static bool DriverInstalled() =>
            Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\VBAudioVACMME") != null;

        /// <summary>Downloads the newest pack, retrying through network trouble for up to 3 minutes.</summary>
        private static async Task<byte[]> DownloadAsync(Action<string, int> report, CancellationToken ct)
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Kova/" + Updater.Current);
            var giveUp = DateTime.UtcNow.AddMinutes(3);
            bool retrying = false;
            while (true)
            {
                // Spoken the first time only; after that the retry line already said what is going on.
                report("Downloading VB-Cable from vb-audio.com.", retrying ? SetupForm.ShowOnly : 0);
                try
                {
                    foreach (string url in PackUrls)
                    {
                        using var resp = await h.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (resp.StatusCode == HttpStatusCode.NotFound) continue;
                        resp.EnsureSuccessStatusCode();
                        long total = resp.Content.Headers.ContentLength ?? 0;
                        using var s = await resp.Content.ReadAsStreamAsync(ct);
                        var ms = new MemoryStream();
                        byte[] buf = new byte[65536];
                        int n;
                        // HttpClient's own timeout stops at the headers: a line that went quiet in the
                        // middle of the body waited forever. Each read gets 30 seconds.
                        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        while (true)
                        {
                            stalled.CancelAfter(TimeSpan.FromSeconds(30));
                            try { n = await s.ReadAsync(buf, stalled.Token); }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                throw new IOException("vb-audio.com stopped sending.");
                            }
                            if (n <= 0) break;
                            ms.Write(buf, 0, n);
                            if (total > 0) report("Downloading VB-Cable from vb-audio.com.", (int)(Math.Min(ms.Length, total) * 100 / total));
                        }
                        // A connection that closed early looks like the end: never hand on half a download.
                        if (total > 0 && ms.Length != total) throw new IOException("The download from vb-audio.com was cut short.");
                        return ms.ToArray();
                    }
                    throw new InvalidOperationException("vb-audio.com no longer has the VB-Cable download where Kova expects it. Check for a Kova update.");
                }
                catch (Exception e) when (e is HttpRequestException || e is IOException || (e is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    if (DateTime.UtcNow > giveUp)
                        throw new InvalidOperationException("Could not reach vb-audio.com for 3 minutes. Check this PC's internet connection and try again.");
                    // Said once; the countdown only changes the text on screen, not every second aloud.
                    if (!retrying) report("Can't reach vb-audio.com. Trying again every few seconds.", -1);
                    retrying = true;
                    for (int left = 5; left > 0; left--)
                    {
                        report("Can't reach vb-audio.com. Trying again in " + left + " seconds.", SetupForm.ShowOnly);
                        await Task.Delay(1000, ct);
                    }
                }
            }
        }

        // ---- Removal: default elsewhere, then force VB-Cable off the PC ----

        /// <summary>Whether any VB-Cable device is installed, working or not.</summary>
        public static bool Installed() => DriverInstalled() || FindCable() != null;

        /// <summary>The output that will become the default when VB-Cable goes, or null if there is none.</summary>
        public static (string Id, string Name)? OtherOutput()
        {
            // The one that was the default before setup, if it is still here; otherwise the first other output.
            if (PreviousOutput() is { } before) return before;
            foreach (var d in Wasapi.OutputDevices())
            {
                try
                {
                    Wasapi.Enumerator().GetDevice(d.Id, out var dev);
                    if (Wasapi.ReadString(dev, PkeyInterface, 2) != "VB-Audio Virtual Cable") return d;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Starts the elevated removal window and waits for it. False if refused or it failed.</summary>
        public static async Task<bool> RemoveElevatedAsync()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--remove-audio") { UseShellExecute = true, Verb = "runas" });
                if (p == null) return false;
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>The whole removal. Returns the sentence to finish with.</summary>
        public static async Task<string> RemoveAsync(Action<string, int> report, CancellationToken ct)
        {
            report("Making another output the default.", 10);
            var other = OtherOutput();
            if (other != null) SetDefault(other.Value.Id);

            // Programs following the default (TailRemote's own capture among
            // them) move off the cable within a second; the rest are cut off.
            report("Letting programs move off it.", 25);
            await Task.Delay(1500, ct);

            report("Removing VB-Cable's devices.", 45);
            bool reboot = false;
            var infs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RemoveDevices(infs, ref reboot);

            report("Removing VB-Cable's driver.", 75);
            foreach (string inf in infs) SetupUninstallOEMInfW(inf, 1 /* SUOI_FORCEDELETE */, IntPtr.Zero);
            RunHidden(Native.SystemTool("sc.exe"), "delete VBAudioVACMME"); // System32's, never one beside Kova.exe
            try { Registry.LocalMachine.OpenSubKey(RecordKey, true)?.DeleteValue(RecordValue, false); } catch { } // done with the note
            string drivers = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers");
            foreach (string sys in new[] { "vbaudio_cable64_win10.sys", "vbaudio_cable64arm_win10.sys" })
            {
                string f = Path.Combine(drivers, sys);
                if (!File.Exists(f)) continue;
                try { File.Delete(f); }
                catch { MoveFileExW(f, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */); reboot = true; }
            }

            report("Checking.", 95);
            for (int i = 0; i < 10 && FindCable() != null; i++) await Task.Delay(300, ct);
            if (FindCable() != null) reboot = true;
            string now = other != null ? " The default output is now " + other.Value.Name + "." : " This PC has no other sound output now.";
            return reboot
                ? "VB-Cable is removed. Restart Windows to finish." + now
                : "VB-Cable is removed." + now;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint regType, byte[]? buffer, uint size, out uint required);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("newdev.dll", SetLastError = true)]
        private static extern bool DiUninstallDevice(IntPtr hwnd, IntPtr set, ref SP_DEVINFO_DATA data, uint flags, out bool reboot);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupUninstallOEMInfW(string inf, uint flags, IntPtr reserved);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileExW(string from, string? to, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        /// <summary>Uninstalls every VB-Cable device, present or not, noting their driver packages.</summary>
        private static void RemoveDevices(System.Collections.Generic.HashSet<string> infs, ref bool reboot)
        {
            var media = new Guid("4d36e96c-e325-11ce-bfc1-08002be10318");
            IntPtr set = SetupDiGetClassDevsW(ref media, IntPtr.Zero, IntPtr.Zero, 0); // 0: not only present ones
            if (set == new IntPtr(-1)) return;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    if (!(Property(set, ref data, 1 /* SPDRP_HARDWAREID */) ?? "").Contains("VBAudioVACWDM", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Property(set, ref data, 9 /* SPDRP_DRIVER */) is string key)
                    {
                        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + key);
                        if (k?.GetValue("InfPath") is string inf && inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase)) infs.Add(inf);
                    }
                    if (DiUninstallDevice(IntPtr.Zero, set, ref data, 0, out bool r) && r) reboot = true;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        private static string? Property(IntPtr set, ref SP_DEVINFO_DATA data, uint prop)
        {
            SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out _, null, 0, out uint need);
            if (need == 0) return null;
            byte[] buf = new byte[need];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out _, buf, need, out _)) return null;
            return System.Text.Encoding.Unicode.GetString(buf).Replace('\0', ' ').Trim();
        }

        private static void RunHidden(string file, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit(10000);
            }
            catch { }
        }

        // ---- Is the installer really VB-Audio's? Windows' own signature check. ----

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO { public uint cbStruct; public IntPtr pcwszFilePath, hFile, pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData, pSIPClientData;
            public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData, pwszURLReference;
            public uint dwProvFlags, dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll")]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

        internal static bool SignedByVbAudio(string path)
        {
            IntPtr pathPtr = Marshal.StringToCoTaskMemUni(path);
            var info = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = pathPtr };
            IntPtr infoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = 2,    // no UI
                    dwUnionChoice = 1, // a file
                    pFile = infoPtr,
                };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
                if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) return false;
#pragma warning disable SYSLIB0057
                var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                // The name as the certificate holds it: parsing its text back could fail on parts it carries
                // (an EV certificate's extra fields) and refuse the genuine installer.
                using var full = new X509Certificate2(cert);
                return SignerIsVbAudio(full.SubjectName);
            }
            catch { return false; }
            finally
            {
                Marshal.FreeCoTaskMem(infoPtr);
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }

        /// <summary>
        /// The certificate is VB-Audio's (Vincent Burel's): its name or organisation is exactly that.
        /// "Contains" also let through any signed certificate whose name merely held those words.
        /// </summary>
        // The names VB-Audio signs with, exactly: the installer of pack 45 is signed by
        // "BUREL VINCENT Entrepreneur individuel" (checked against the real download, 2026-10-11).
        private static readonly string[] VbAudioNames = { "BUREL VINCENT", "BUREL VINCENT Entrepreneur individuel" };

        internal static bool SignerIsVbAudio(string subject)
        {
            try { return SignerIsVbAudio(new X500DistinguishedName(subject)); }
            catch { return false; }
        }

        internal static bool SignerIsVbAudio(X500DistinguishedName subject)
        {
            try
            {
                foreach (var rdn in subject.EnumerateRelativeDistinguishedNames())
                {
                    if (rdn.HasMultipleElements) continue;
                    string? oid = rdn.GetSingleElementType().Value;
                    if (oid is "2.5.4.3" or "2.5.4.10" // CN, O
                        && System.Linq.Enumerable.Contains(VbAudioNames, rdn.GetSingleElementValue()?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        // ---- Default device: IPolicyConfig, what the Sound control panel uses ----

        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyConfigCo { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            void GetMixFormat(); void GetDeviceFormat(); void ResetDeviceFormat(); void SetDeviceFormat();
            void GetProcessingPeriod(); void SetProcessingPeriod(); void GetShareMode(); void SetShareMode();
            void GetPropertyValue(); void SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
        }

        /// <summary>
        /// VB-Cable's driver always adds a second output, "CABLE In 16 Ch". Only
        /// the first cable is wanted, so the extra one is disabled, the same as
        /// Disable in the Sound settings.
        /// </summary>
        private static void DisableExtraOutputs(string cable)
        {
            var pc = (IPolicyConfig)new PolicyConfigCo();
            foreach (string id in ExtraOutputs(cable)) pc.SetEndpointVisibility(id, 0);
        }

        /// <summary>Every working VB-Cable output but the one that is Kova's (cable: its id).</summary>
        private static System.Collections.Generic.List<string> ExtraOutputs(string cable)
        {
            var ids = new System.Collections.Generic.List<string>();
            try
            {
                Wasapi.Enumerator().EnumAudioEndpoints(Wasapi.eRender, Wasapi.DEVICE_STATE_ACTIVE, out var coll);
                coll.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    coll.Item(i, out var dev);
                    if (Wasapi.ReadString(dev, PkeyInterface, 2) != "VB-Audio Virtual Cable") continue;
                    dev.GetId(out string id);
                    if (id != cable) ids.Add(id);
                }
            }
            catch { }
            return ids;
        }

        public static void SetDefault(string id)
        {
            var pc = (IPolicyConfig)new PolicyConfigCo();
            for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role);
        }
    }
}
