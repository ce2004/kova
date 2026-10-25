using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace TailRemote
{
    /// <summary>
    /// The host as a Windows service, so the remote PC can be used at the lock
    /// screen, the sign-in screen and UAC prompts, and can be sent Ctrl+Alt+Del.
    ///
    /// The service (SYSTEM, session 0) hosts, from the moment Windows starts it, before
    /// the sign-in screen (Agent.StartHosting): connections, the sound, files, and Ctrl+Alt+Del,
    /// which only a service may send. It cannot reach the screen, so it keeps an agent in
    /// whichever session is on the console, also as SYSTEM: "TailRemote.exe --agent", which
    /// types the keys it is handed (AgentLink) on whichever desktop has the keyboard.
    ///
    /// The service runs the copy of Kova it was installed from, where it is (PlannedExe;
    /// Program Files only for older installs and copies it could not keep running); its settings live in ProgramData, readable only by SYSTEM and
    /// administrators, with the passwords DPAPI-encrypted for the machine.
    /// </summary>
    internal static class ServiceHost
    {
        public static readonly string Name = Names.Service;
        public static string InstallDir => Names.InstallDir;
        public static string InstalledExe => Names.InstalledExe;
        public static string DataDir => Names.DataDir;
        private static string ConfigPath => Path.Combine(DataDir, "service.json");
        public static string LogPath => Path.Combine(DataDir, "service.log");

        public sealed class Config
        {
            public int Port { get; set; } = Protocol.DefaultPort;
            public string PasswordEnc { get; set; } = "";
            public string ListenPasswordEnc { get; set; } = "";
            public string CaptureDevice { get; set; } = "";
            /// <summary>Where the controlling PC's microphone plays ("": Kova picks a virtual cable).</summary>
            public string MicDevice { get; set; } = "";
            public bool Logging { get; set; }
            public bool WeSetSasPolicy { get; set; }
            /// <summary>Hosting at sign-in was on before the service replaced it: Remove turns it back on unless this is false.</summary>
            public bool? HadStartupTask { get; set; }
            /// <summary>The host's lasting identity (HostIdentity, PKCS#8 as base64), sealed for this machine: the window's, handed over at install.</summary>
            public string HostIdentityEnc { get; set; } = "";
            public bool RelayHosting { get; set; }
            public string RelayAddress { get; set; } = "";
            public int RelayPort { get; set; } = 47200;
            public string RelayName { get; set; } = "";

            // Null-safe: a "null" in the file (edited by hand, or damaged) used to stop hosting with a crash.
            public static string Seal(string? s) => string.IsNullOrEmpty(s) ? "" : Convert.ToBase64String(Native.Protect(System.Text.Encoding.UTF8.GetBytes(s), true, machine: true));
            public static string Open(string? s) => string.IsNullOrEmpty(s) ? "" : System.Text.Encoding.UTF8.GetString(Native.Protect(Convert.FromBase64String(s), false, machine: true));
        }

        /// <summary>The service's settings, or the copy kept before the last change if they are damaged; null if neither can be read.</summary>
        public static Config? LoadConfig()
        {
            if (LoadConfigFrom(ConfigPath) is { } cfg) return cfg;
            cfg = LoadConfigFrom(ConfigPath + ".bak");
            if (cfg != null) Log("The service's settings could not be read, so the copy kept before the last change is used.");
            return cfg;
        }

        private static readonly object LogGate = new();

        public static void Log(string line)
        {
            // One at a time: the service logs from many threads (hosting, the agent link, the window link,
            // updates), and a line written while another had the file open was lost without a word.
            lock (LogGate)
            try
            {
                // Kept, not wiped: the previous half megabyte moves to service.log.old.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024) File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
            catch { }
        }

        // ================= Installing (elevated: --service install / remove) =================

        public static bool IsInstalled() => RunHidden("sc.exe", "query " + Name) == 0;

        /// <summary>The version of the copy the service runs, or null.</summary>
        public static Version? InstalledVersion() => ServiceExe() is string exe ? VersionOf(exe) : null;

        /// <summary>The service's own copy, as the service itself sees it (it always runs the file it was registered with).</summary>
        private static string Self => Names.OwnExe;

        /// <summary>
        /// The service's own file, inside the service: the path it is registered with. Not the process's own path: once the
        /// window has updated that same file, the running copy has been renamed to .old, and the process then reports
        /// that name, so the new version put in place was never seen and the service did not restart into it.
        /// </summary>
        private static string ServiceSelf => ServiceExe() ?? Self;

        internal static bool SameFile(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>The exe the installed service runs, read from its registration (anyone may read it), or null when there is no service.</summary>
        public static string? ServiceExe()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name);
                if (k?.GetValue("ImagePath") is not string image) return null;
                image = Environment.ExpandEnvironmentVariables(image).Trim();
                string path = image.StartsWith('"') ? image.Substring(1, Math.Max(0, image.IndexOf('"', 1) - 1)) : image.Split(' ')[0];
                return path.Length == 0 ? null : Path.GetFullPath(path);
            }
            catch { return null; }
        }

        /// <summary>
        /// The copy the service will run if it is installed, or given settings, from this one. 4.2 ran the very file
        /// Kova was started from, wherever it was; 4.4 does that only when ordinary users cannot change that file or its
        /// folders (ProtectedFromUsers), like a copy in Program Files or another folder only administrators can write.
        /// The service runs as SYSTEM: a copy in Documents, Downloads or an apps folder of the user's own let any program
        /// the user ran, with no administrator prompt, replace it (or put a DLL beside it) and so become SYSTEM. Such a
        /// copy is copied into Program Files\Kova, which the service then keeps up to date by itself (it follows the
        /// window's updates: TryUpdate). A service in Program Files keeps that copy.
        /// </summary>
        public static string PlannedExe()
        {
            string me = Self;
            if (SameFile(ServiceExe(), InstalledExe)) return InstalledExe;
            return CanRunInPlace(me) && ProtectedFromUsers(me) ? me : InstalledExe;
        }

        // Rights that let someone change what runs: write or append to the file, delete or rename it, change who may
        // (permissions, owner). On the exe's folder, also add files (a DLL Windows would load from beside the exe) or
        // delete what is in it; on folders above, delete or rename the folder below or change permissions.
        // Generic write and all (0x40000000, 0x10000000) count too: an entry can hold them unmapped.
        private const FileSystemRights Generic = (FileSystemRights)0x50000000;
        private const FileSystemRights FileDanger = Generic | FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
        private const FileSystemRights FolderDanger = Generic | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        private const FileSystemRights AboveDanger = Generic | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        /// <summary>
        /// Whether only SYSTEM, administrators and TrustedInstaller can change this exe, add files beside it, or rename
        /// or re-permission the folders it is in (and they own them all). False when anything cannot be read.
        /// </summary>
        internal static bool ProtectedFromUsers(string exe)
        {
            try
            {
                exe = Path.GetFullPath(exe);
                if (!Trusted(new FileInfo(exe).GetAccessControl(), FileDanger)) return false;
                var dir = new DirectoryInfo(Path.GetDirectoryName(exe)!);
                if (!Trusted(dir.GetAccessControl(), FolderDanger)) return false;
                for (var up = dir.Parent; up != null; up = up.Parent)
                    if (!Trusted(up.GetAccessControl(), AboveDanger, root: up.Parent == null)) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool Trusted(FileSystemSecurity sec, FileSystemRights danger, bool root = false)
        {
            // The drive's root is owned by SYSTEM or TrustedInstaller on Windows; anything else is checked as owner too.
            if (sec.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !TrustedSid(owner) && !root) return false;
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid || TrustedSid(sid)) continue;
                // Rules only for what is made inside later (inherit only) say nothing about this one.
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                // CREATOR OWNER on this very object means nothing (it applies to things made inside).
                if (sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) continue;
                if ((rule.FileSystemRights & danger) != 0) return false;
            }
            return true;
        }

        private static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

        private static bool TrustedSid(SecurityIdentifier sid) =>
            sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid == TrustedInstallerSid;

        /// <summary>
        /// Whether the service can keep running this file where it is. Not on a network drive (it is not there for
        /// the service before anyone signs in), a USB stick (it may be pulled out), nor in a temp folder (a copy
        /// opened straight from a zip, which Windows clears away).
        /// </summary>
        internal static bool CanRunInPlace(string exe)
        {
            try
            {
                if (exe.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                if (Path.GetPathRoot(exe) is not { Length: > 0 } root || new DriveInfo(root).DriveType != DriveType.Fixed) return false;
                string temp = Path.GetFullPath(Path.GetTempPath());
                if (exe.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return false;
                // Another account's temp folder too: the elevated half may run as an administrator who is not the user.
                if (exe.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\Windows\Temp\", StringComparison.OrdinalIgnoreCase)) return false;
                // Nor a OneDrive folder (Documents often is one): OneDrive can leave only a placeholder of the file,
                // which the service cannot fetch back before anyone signs in, and the PC would be lost.
                foreach (string v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
                    if (Environment.GetEnvironmentVariable(v) is { Length: > 3 } od && exe.StartsWith(od.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase)) return false;
                if (exe.Contains(@"\OneDrive\", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\OneDrive - ", StringComparison.OrdinalIgnoreCase)) return false;
                const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;
                if ((File.GetAttributes(exe) & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0) return false;
                return true;
            }
            catch { return false; }
        }

        private static Version? VersionOf(string path)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
            }
            catch { return null; }
        }

        /// <summary>
        /// What the window hands the elevated copy that installs or removes the service. When a standard user types
        /// an administrator's password, that copy runs as the administrator: it read the administrator's settings
        /// (none, so "set a password first" every time, and it could never have opened the user's passwords), wrote
        /// why it failed where the window never looked, and on removal made the sign-in task for the administrator.
        /// So the window says it all, over a pipe only an elevated administrator can open.
        /// </summary>
        internal sealed class Handover
        {
            public int Port { get; set; }
            public string Password { get; set; } = "";
            public string ListenPassword { get; set; } = "";
            public string CaptureDevice { get; set; } = "";
            public string MicDevice { get; set; } = "";
            public bool Logging { get; set; }
            public string User { get; set; } = "";
            /// <summary>The window's lasting identity as a host (PKCS#8, base64): the service hosts as the same PC, so controlling PCs know it.</summary>
            public string Identity { get; set; } = "";
            public bool RelayHosting { get; set; }
            public string RelayAddress { get; set; } = "";
            public int RelayPort { get; set; } = 47200;
            public string RelayName { get; set; } = "";
        }

        private const string HandoverPrefix = "Kova-setup-";
        private static Handover? _handover;
        private static bool _handoverWanted;
        private static NamedPipeClientStream? _answerTo;
        private static string? _failWhy, _lastError;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

        private static void WriteBlock(Stream s, byte[] b)
        {
            s.Write(BitConverter.GetBytes(b.Length));
            s.Write(b);
            s.Flush();
        }

        private static byte[] ReadBlock(Stream s)
        {
            byte[] head = new byte[4];
            Protocol.ReadExactly(s, head);
            int n = BitConverter.ToInt32(head);
            if (n < 0 || n > 1 << 20) throw new InvalidDataException("a message too big");
            byte[] b = new byte[n];
            Protocol.ReadExactly(s, b);
            return b;
        }

        /// <summary>Asks for administrator rights and runs --service install or remove with these settings. False if refused or failed.</summary>
        public static System.Threading.Tasks.Task<bool> SetAsync(bool install, Settings s) => System.Threading.Tasks.Task.Run(() =>
        {
            _lastError = null;
            try
            {
                // An old failure's words must not be read out when this time the prompt was turned down.
                try { File.Delete(Path.Combine(Path.GetTempPath(), "tailremote-service.txt")); } catch { }
                string name = HandoverPrefix + Guid.NewGuid().ToString("N");
                var sec = new PipeSecurity();
                sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, sec);
                Process? p;
                try
                {
                    p = Process.Start(new ProcessStartInfo(Names.OwnExe, Names.InstanceArgs + "--service " + (install ? "install" : "remove") + " " + Environment.ProcessId + " " + name)
                    { UseShellExecute = true, Verb = "runas" });
                }
                catch { return false; } // the prompt was turned down
                if (p == null) return false;
                using (p)
                {
                    var connected = pipe.WaitForConnectionAsync();
                    try { System.Threading.Tasks.Task.WaitAny(connected, p.WaitForExitAsync()); } catch { }
                    if (connected.IsCompletedSuccessfully)
                    {
                        // Only the copy just started gets the passwords: anything else that got in first is told nothing.
                        if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint by) && by == (uint)p.Id)
                        {
                            using (var me = WindowsIdentity.GetCurrent())
                                WriteBlock(pipe, JsonSerializer.SerializeToUtf8Bytes(new Handover
                                {
                                    Port = s.Port, Password = s.Password, ListenPassword = s.ListenPassword,
                                    CaptureDevice = s.CaptureDevice, MicDevice = s.MicOutput, Logging = s.Logging, User = me.Name,
                                    Identity = Settings.Unprotect(s.HostIdentityEnc),
                                    RelayHosting = s.RelayHosting, RelayAddress = s.RelayAddress, RelayPort = s.RelayPort, RelayName = s.RelayName,
                                }));
                            try { _lastError = System.Text.Encoding.UTF8.GetString(ReadBlock(pipe)); } catch { }
                            if (_lastError?.StartsWith('\u0001') == true) { ServiceIdentity = _lastError[1..]; _lastError = null; }
                            if (_lastError?.Length == 0) _lastError = null;
                        }
                        else
                        {
                            _lastError = "another program answered in place of Kova's setup.";
                            pipe.Dispose(); // let go: the real setup then finds no window and ends, rather than wait for ever
                        }
                    }
                    p.WaitForExit();
                    // Ended without a word (it never got this window's settings): not "permission was not given".
                    if (p.ExitCode != 0 && _lastError == null) _lastError = "Kova's setup could not get this window's settings. Try again.";
                    return p.ExitCode == 0;
                }
            }
            catch (Exception e) { _lastError ??= e.Message; return false; }
        });

        /// <summary>The elevated half: takes the settings the window hands over (--service install|remove PID PIPE).</summary>
        internal static void ReceiveHandover(string name)
        {
            _handoverWanted = true;
            if (!name.StartsWith(HandoverPrefix, StringComparison.Ordinal) || name.Length > 64) return;
            try
            {
                var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Identification);
                c.Connect(10_000);
                // The window that asked made it, and nothing else.
                if (!GetNamedPipeServerProcessId(c.SafePipeHandle, out uint by) || by != (uint)WindowPid) { c.Dispose(); return; }
                try { _handover = JsonSerializer.Deserialize<Handover>(ReadBlock(c)); }
                catch { c.Dispose(); throw; }
                _answerTo = c;
            }
            catch { _handover = null; }
        }

        /// <summary>The elevated half's last word to the window: nothing when it worked, else why not.</summary>
        internal static void Answer(int result)
        {
            if (_answerTo is not NamedPipeClientStream c) return;
            // Done: the identity the service hosts as goes back with it (after \u0001), so the window hosts as the same PC.
            string word = result == 0 ? (_identityBack != null ? "\u0001" + _identityBack : "") : _failWhy ?? "it did not work.";
            try { WriteBlock(c, System.Text.Encoding.UTF8.GetBytes(word)); } catch { }
            try { c.Dispose(); } catch { }
        }

        /// <summary>The settings to install with: the window's, or this account's own when run by hand.</summary>
        private static Handover? SettingsToInstall()
        {
            if (_handoverWanted) return _handover;
            var s = Settings.Load();
            return new Handover
            {
                Port = s.Port, Password = s.Password, ListenPassword = s.ListenPassword, CaptureDevice = s.CaptureDevice, MicDevice = s.MicOutput, Logging = s.Logging,
                Identity = Settings.Unprotect(s.HostIdentityEnc),
                RelayHosting = s.RelayHosting, RelayAddress = s.RelayAddress, RelayPort = s.RelayPort, RelayName = s.RelayName,
            };
        }

        /// <summary>
        /// Installs, or updates the copy and settings of, the service from this
        /// user's TailRemote settings, then (re)starts it. Elevated.
        /// </summary>
        public static int Install()
        {
            // Once the service is stopped, any failure must not leave it stopped: on a PC reached only
            // through Kova that would lose the PC. Started again in finally unless the install did it.
            bool stopped = false, started = false;
            // Usually this very file (PlannedExe): then nothing is copied, and the service is pointed at it.
            string target = PlannedExe();
            // What the service runs now, which may be another file: that is the one to wait for as it stops.
            string? running = ServiceExe();
            // Not ".new": the service's own update downloads to that name, and one starting between this copy and
            // the stop below cut the copy short as the stop killed it, and then the cut-off file was put in place.
            string fresh = target + ".install";
            try
            {
                if (SettingsToInstall() is not Handover s) return Fail("Kova could not get this window's settings. Try again.");
                if (MainForm.Characters(s.Password) < MainForm.MinPasswordLength) return Fail("Set a password of at least " + MainForm.MinPasswordLength + " characters first.");

                // The new copy goes in next to the old one first, while the service still runs: a full
                // disk or a refused copy then fails here, with the service still up.
                bool copy = !SameFile(Self, target);
                if (copy)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Self, fresh, true);
                    // On the disk before it is renamed into place: after a power cut, a rename that landed
                    // before the contents left a Kova.exe of zeros, and nothing could start to roll it back.
                    // Shared: an antivirus still looking at the new copy must not make the install fail.
                    using (var f = new FileStream(fresh, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) f.Flush(flushToDisk: true);
                }

                // 1060: there is no service yet (the first install), so nothing was stopped. Counted as stopped,
                // any failure after this spent a minute trying to start a service that did not exist before
                // saying what went wrong.
                stopped = RunHidden("sc.exe", "stop " + Name) != 1060;
                WaitForStopped(running ?? target);

                if (copy)
                {
                    for (int i = 0; ; i++)
                    {
                        try { File.Move(fresh, target, overwrite: true); break; }
                        catch when (i < 20) { Thread.Sleep(250); } // the old agent is still letting go
                    }
                }

                var old = LoadConfig();
                var cfg = new Config
                {
                    Port = s.Port,
                    PasswordEnc = Config.Seal(s.Password),
                    ListenPasswordEnc = Config.Seal(s.ListenPassword),
                    CaptureDevice = s.CaptureDevice,
                    MicDevice = s.MicDevice ?? "",
                    Logging = s.Logging,
                    WeSetSasPolicy = old?.WeSetSasPolicy ?? false,
                    // Asked before the task goes (below); a reinstall keeps what the first install found.
                    HadStartupTask = old != null ? old.HadStartupTask : Startup.IsEnabled(),
                    // The window's identity, so the service is the same PC to the controlling PCs; one made by the
                    // service itself (before any window handed one over) is kept only if the window has none.
                    HostIdentityEnc = ChooseIdentity(old, s.Identity),
                    RelayHosting = s.RelayHosting, RelayAddress = s.RelayAddress ?? "", RelayPort = s.RelayPort, RelayName = s.RelayName ?? "",
                };
                // Ctrl+Alt+Del from a service needs this policy (1 = services may send it).
                using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                {
                    if (k.GetValue("SoftwareSASGeneration") is not int v || (v & 1) == 0)
                    {
                        k.SetValue("SoftwareSASGeneration", 1, RegistryValueKind.DWord);
                        cfg.WeSetSasPolicy = true;
                    }
                }
                WriteConfig(cfg);
                if (!Register(target)) return Fail("Windows would not create the service.");

                // The service replaces the at-sign-in task, and opens its port (closing the one it
                // used before, which Remove would otherwise never close).
                Startup.Apply(false);
                if (old != null && old.Port != s.Port) Firewall.Apply(false, old.Port);
                Firewall.Apply(true, s.Port);
                Firewall.Clean(new[] { s.Port }, thorough: false); // already administrator: rules for copies that are gone go too

                if (RunHidden("sc.exe", "start " + Name) != 0) return Fail("The service was installed but would not start.");
                started = true;
                return 0;
            }
            catch (Exception e) { return Fail(e.Message); }
            finally
            {
                // Whatever failed, the service (the old copy, if the new one never got in) runs again.
                // A start sent while the old one was still stopping fails, and it then stayed stopped: wait for
                // it to really stop, and keep trying for a minute (1056: it is already running, which is fine).
                if (stopped && !started)
                    for (int i = 0; i < 12; i++)
                    {
                        WaitForStopped(running ?? target);
                        int rc = RunHidden("sc.exe", "start " + Name);
                        if (rc == 0 || rc == 1056) break;
                        Thread.Sleep(5000);
                    }
                try { File.Delete(fresh); } catch { } // a copy that never got in
            }
        }

        public static int Remove()
        {
            // Asked by a window that could not hand over who it is: removing anyway would make the sign-in task
            // for the administrator whose password let this run.
            if (_handoverWanted && _handover == null) return Fail("Kova could not get this window's settings. Try again.");
            try
            {
                string? exe = ServiceExe(); // asked before the service is gone
                RunHidden("sc.exe", "stop " + Name);
                WaitForStopped(exe ?? InstalledExe);
                RunHidden("sc.exe", "delete " + Name);
                var cfg = LoadConfig();
                if (cfg?.WeSetSasPolicy == true)
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", true);
                    k?.DeleteValue("SoftwareSASGeneration", false);
                }

                // Undo what Install() did: close the port it opened, and bring back the at-sign-in task
                // the service had replaced, only if there was one ("this PC stops hosting" means just that).
                Firewall.Apply(false, cfg?.Port ?? Protocol.DefaultPort);
                // For the person whose window asked, not the administrator whose password let it run.
                if (cfg?.HadStartupTask != false) Startup.Apply(true, _handover?.User is { Length: > 0 } user ? user : null); // not known (installed before 4.1.1): as it always did
                // Each on its own: the window often runs from InstallDir, which then cannot go, and
                // that must not leave the service's saved passwords behind in DataDir. Program Files\Kova
                // only when the service ran from there: a service run from the user's own folder leaves
                // that folder and the copy in it alone, with only its roll back note cleared away.
                bool fromInstallDir = exe == null || SameFile(Path.GetDirectoryName(exe), InstallDir);
                if (exe != null && !fromInstallDir) try { File.Delete(exe + ".rollback"); } catch { }
                foreach (string dir in fromInstallDir ? new[] { DataDir, InstallDir } : new[] { DataDir })
                {
                    for (int i = 0; i < 20; i++)
                    {
                        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); break; }
                        catch { Thread.Sleep(250); }
                    }
                }
                return 0;
            }
            catch (Exception e) { return Fail(e.Message); }
        }

        /// <summary>The identity the service hosts as, from the elevated half's last install (PKCS#8, base64): the window takes it on. Null if none came back.</summary>
        internal static string? ServiceIdentity;
        private static string? _identityBack;

        /// <summary>
        /// One identity for this PC, whether the window or the service hosts: the service's own, if it already has one
        /// (controlling PCs know it by it; it then goes back to the window), else the window's.
        /// </summary>
        private static string ChooseIdentity(Config? old, string? fromWindow)
        {
            string? kept = null;
            try { if (old != null && HostIdentity.FromPkcs8(TryBase64(Config.Open(old.HostIdentityEnc))) is { } k) { k.Dispose(); kept = Config.Open(old.HostIdentityEnc); } } catch { }
            if (kept == null && HostIdentity.FromPkcs8(TryBase64(fromWindow)) is { } w) { w.Dispose(); kept = fromWindow; }
            _identityBack = kept;
            return kept == null ? "" : Config.Seal(kept);
        }

        private static byte[]? TryBase64(string? s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return Convert.FromBase64String(s); } catch { return null; }
        }

        /// <summary>The service's identity as a host: from its settings, or made now and kept there (a service set up before 4.4).</summary>
        internal static HostIdentity LoadOrMakeIdentity(Config cfg)
        {
            try { if (HostIdentity.FromPkcs8(TryBase64(Config.Open(cfg.HostIdentityEnc))) is { } kept) return kept; } catch { }
            var made = HostIdentity.Create();
            try
            {
                cfg.HostIdentityEnc = Config.Seal(Convert.ToBase64String(made.ExportPkcs8()));
                WriteConfig(cfg);
            }
            catch (Exception e) { Log("Could not keep the service's identity: " + e.Message + " It is made again at the next start."); }
            return made;
        }

        private static int Fail(string why)
        {
            _failWhy = why;
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "tailremote-service.txt"), why); } catch { }
            return 1;
        }

        public static string LastError()
        {
            if (_lastError != null) return _lastError;
            try { return File.ReadAllText(Path.Combine(Path.GetTempPath(), "tailremote-service.txt")); }
            catch { return "administrator permission was not given"; }
        }

        private static void WriteConfig(Config cfg)
        {
            var dir = Directory.CreateDirectory(DataDir);
            // A link made by someone else would send the passwords wherever it points.
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException(DataDir + " is a link to somewhere else, so the service's settings were not saved there.");
            // Only SYSTEM and administrators, nothing inherited. Owned by administrators too: an ordinary
            // user who made the folder first would otherwise still own it, and an owner may always change
            // who can read it.
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var sec = new DirectorySecurity();
            sec.SetOwner(admins);
            sec.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            dir.SetAccessControl(sec);
            // Never written in place: a crash or power cut half way left settings the service could not
            // read. Written whole beside it, then put in place in one step, with the last good copy kept
            // as .bak (LoadConfig falls back to it). Old files are deleted, not overwritten, so each new
            // one takes this folder's rules rather than any rules an old one was given.
            string tmp = ConfigPath + ".tmp", bak = ConfigPath + ".bak";
            File.Delete(tmp);
            // Flushed to the disk before the rename, as Settings.Save does: otherwise a power cut can keep the
            // rename and lose the contents, and on a first install there is no .bak to fall back to.
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                f.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true })));
                f.Flush(flushToDisk: true);
            }
            if (LoadConfigFrom(ConfigPath) != null)
            {
                File.Delete(bak);
                File.Copy(ConfigPath, bak);
            }
            File.Move(tmp, ConfigPath, overwrite: true);
        }

        private static Config? LoadConfigFrom(string path)
        {
            try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(path)); }
            catch { return null; }
        }

        /// <summary>Creates the Kova service, or brings its settings up to date. False if Windows would not create it.</summary>
        private static bool Register(string exe)
        {
            string bin = "\"" + exe + "\" --service";
            string quoted = "\"" + bin.Replace("\"", "\\\"") + "\"";
            string display = "\"Kova host" + (Names.Instance.Length > 0 ? " " + Names.Instance : "") + "\"";
            if (!IsInstalled())
            {
                if (RunHidden("sc.exe", "create " + Name + " binPath= " + quoted + " start= auto DisplayName= " + display) != 0) return false;
            }
            else if (RunHidden("sc.exe", "config " + Name + " binPath= " + quoted + " start= auto DisplayName= " + display) != 0) return false;
            // A test service (KOVA_INSTANCE) must know it is one before it reads a single setting: Windows
            // gives a service only the environment written here, and its agent inherits it.
            if (Names.Instance.Length > 0)
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name, true))
                    k?.SetValue("Environment", new[] { "KOVA_INSTANCE=" + Names.Instance }, RegistryValueKind.MultiString);
            RunHidden("sc.exe", "description " + Name + " \"Lets another PC control this one through Kova, including the lock screen and UAC prompts.\"");
            RunHidden("sc.exe", "failure " + Name + " reset= 60 actions= restart/2000/restart/5000/restart/10000");
            return true;
        }

        /// <summary>The window that started this install or remove (passed on its command line), or 0.</summary>
        public static int WindowPid;

        private static void WaitForStopped(string exe)
        {
            for (int i = 0; i < 80; i++) // up to 20 seconds
            {
                bool any = false;
                // By the exe's own name: a copy called Kova-x64.exe is not a process called Kova.
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                {
                    // Not this installer itself, nor the window that started it: both often run the
                    // service's file too, and never stop, so every Apply used to wait the full 10 seconds.
                    // Its file or the same file renamed aside by an update (name.old...): a copy running from there still holds on.
                    using (p) try { if (p.Id != Environment.ProcessId && p.Id != WindowPid && p.MainModule?.FileName is string f && (SameFile(f, exe) || f.StartsWith(exe + ".old", StringComparison.OrdinalIgnoreCase))) any = true; } catch { }
                }
                // And Windows itself must say it has stopped: a start sent while it was still stopping failed with
                // "already running", and the service then finished stopping and stayed stopped.
                int state = NativeService.ServiceState(Name);
                if (!any && state != NativeService.SERVICE_STOP_PENDING && state != NativeService.SERVICE_RUNNING) return;
                Thread.Sleep(250);
            }
        }

        private static int RunHidden(string file, string args) => Native.RunHidden(file, args);

        // ================= The service itself (--service) =================

        private static IntPtr _statusHandle;
        private static NativeService.SERVICE_STATUS _status;
        private static readonly ManualResetEvent Stop = new(false);
        private static readonly AutoResetEvent Wake = new(false);
        private static NativeService.ServiceMainProc? _main;
        private static NativeService.HandlerEx? _handler;

        public static int RunService()
        {
            _main = ServiceMain;
            _handler = Handler;
            var table = new[]
            {
                new NativeService.SERVICE_TABLE_ENTRY { Name = Marshal.StringToHGlobalUni(Name), Proc = _main },
                new NativeService.SERVICE_TABLE_ENTRY(),
            };
            return NativeService.StartServiceCtrlDispatcherW(table) ? 0 : 1;
        }

        private static void Report(int state)
        {
            _status.ServiceType = NativeService.SERVICE_WIN32_OWN_PROCESS;
            _status.CurrentState = state;
            _status.ControlsAccepted = state == NativeService.SERVICE_RUNNING
                ? NativeService.SERVICE_ACCEPT_STOP | NativeService.SERVICE_ACCEPT_SHUTDOWN | NativeService.SERVICE_ACCEPT_SESSIONCHANGE : 0;
            _status.WaitHint = state == NativeService.SERVICE_RUNNING || state == NativeService.SERVICE_STOPPED ? 0 : 5000;
            NativeService.SetServiceStatus(_statusHandle, ref _status);
        }

        private static int Handler(int control, int eventType, IntPtr data, IntPtr context)
        {
            switch (control)
            {
                case NativeService.SERVICE_CONTROL_STOP:
                case NativeService.SERVICE_CONTROL_SHUTDOWN:
                    if (control == NativeService.SERVICE_CONTROL_SHUTDOWN) _shuttingDown = true;
                    Report(NativeService.SERVICE_STOP_PENDING);
                    Stop.Set();
                    break;
                case NativeService.SERVICE_CONTROL_SESSIONCHANGE:
                    Wake.Set(); // someone signed in or out, or switched user: move the agent
                    break;
            }
            return 0;
        }

        private static void ServiceMain(int argc, IntPtr argv)
        {
            _statusHandle = NativeService.RegisterServiceCtrlHandlerExW(Name, _handler!, IntPtr.Zero);
            Report(NativeService.SERVICE_START_PENDING);
            if (_rolledBack) Environment.Exit(1); // a failed start: Windows' recovery setting starts the restored copy
            _startedFrom = FileNow();
            // Hosting starts at once, from the service itself: before the sign-in screen exists.
            new Thread(Agent.StartHosting) { IsBackground = true, Name = "Kova service hosting" }.Start();
            new Thread(UpdateLoop) { IsBackground = true, Name = "Kova service updates" }.Start();
            new Thread(UpdatePipe) { IsBackground = true, Name = "Kova update nudge" }.Start();
            new Thread(MoveToProtected) { IsBackground = true, Name = "Kova service location", Priority = ThreadPriority.BelowNormal }.Start();
            Report(NativeService.SERVICE_RUNNING);
            if (!File.Exists(RollbackNote)) { try { File.Delete(ServiceSelf + ".old"); } catch { } } // left by the last self-update
            Log("Service started, version " + Updater.Current + ".");

            Process? agent = null;
            uint agentSession = uint.MaxValue;
            long lastStart = 0, nextStart = 0, wait = 2000;
            bool counted = true; // whether the agent that ended (or never started) has been counted in wait yet
            while (!Stop.WaitOne(0))
            {
                if (Restarting)
                {
                    // A new copy is in place: close the agent and leave without saying
                    // "stopped", so Windows' recovery setting starts the new copy.
                    Agent.LeaveForUpdate(_restartingTo);
                    try { if (agent != null && !agent.HasExited) agent.Kill(); } catch { }
                    Log("Restarting into the new version.");
                    Environment.Exit(1);
                }
                uint session = NativeService.WTSGetActiveConsoleSessionId();
                bool alive = agent != null && !agent.HasExited;
                // Counted as soon as it is seen to have ended (within a second), by how long it really ran.
                // It used to be measured only at the next start, by which time it always looked long-lived,
                // so the wait went 2, 4, 8, 16 seconds and back to 2, never reaching a minute.
                if (!alive && !counted)
                {
                    wait = NextWait(wait, Environment.TickCount64 - lastStart, session == agentSession);
                    nextStart = lastStart + wait;
                    counted = true;
                }
                bool moved = session != agentSession;
                if (session != 0xFFFFFFFF && (!alive || moved) && Environment.TickCount64 >= (moved ? lastStart + 2000 : nextStart))
                {
                    try { if (alive) agent!.Kill(); } catch { }
                    if (moved) wait = 2000; // someone else at the screen: a fresh start
                    agent?.Dispose();
                    lastStart = Environment.TickCount64;
                    agent = StartAgent(session);
                    agentSession = session;
                    counted = false;
                }
                WaitHandle.WaitAny(new WaitHandle[] { Stop, Wake }, 1000);
            }
            try { if (agent != null && !agent.HasExited) agent.Kill(); } catch { }
            agent?.Dispose();
            Agent.StopHosting(_shuttingDown ? Protocol.LeavingShutdown : Protocol.LeavingStopped);
            Log("Service stopped.");
            Report(NativeService.SERVICE_STOPPED);
        }

        /// <summary>
        /// How long to wait before starting the agent again, after one that ran livedMs. One that keeps
        /// failing straight away (under 10 seconds, in the same session) is retried less and less often,
        /// up to once a minute, so it cannot fill the log or keep the CPU busy; otherwise 2 seconds.
        /// </summary>
        internal static long NextWait(long wait, long livedMs, bool sameSession) =>
            sameSession && livedMs < 10_000 ? Math.Min(wait * 2, 60_000) : 2000;

        private static Process? StartAgent(uint session)
        {
            IntPtr own = IntPtr.Zero, token = IntPtr.Zero;
            try
            {
                if (!NativeService.OpenProcessToken(NativeService.GetCurrentProcess(), NativeService.TOKEN_ALL_ACCESS, out own)) throw new InvalidOperationException("OpenProcessToken " + Marshal.GetLastWin32Error());
                if (!NativeService.DuplicateTokenEx(own, NativeService.TOKEN_ALL_ACCESS, IntPtr.Zero, NativeService.SecurityImpersonation, NativeService.TokenPrimary, out token))
                    throw new InvalidOperationException("DuplicateTokenEx " + Marshal.GetLastWin32Error());
                uint sid = session;
                if (!NativeService.SetTokenInformation(token, NativeService.TokenSessionId, ref sid, 4)) throw new InvalidOperationException("SetTokenInformation " + Marshal.GetLastWin32Error());
                var si = new NativeService.STARTUPINFO { cb = Marshal.SizeOf<NativeService.STARTUPINFO>(), lpDesktop = "winsta0\\default" };
                string exe = Environment.ProcessPath!;
                // This service's number goes with it: the agent ends when this service does, so one left
                // behind by a crash never fights the next service's agent for the pipe.
                if (!NativeService.CreateProcessAsUserW(token, null, "\"" + exe + "\" --agent " + Environment.ProcessId, IntPtr.Zero, IntPtr.Zero, false,
                        NativeService.CREATE_NO_WINDOW | NativeService.CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi))
                    throw new InvalidOperationException("CreateProcessAsUser " + Marshal.GetLastWin32Error());
                NativeService.CloseHandle(pi.hThread);
                // Looked up while its handle is still held, so its number cannot have gone to another
                // process yet. One that already ended counts as a failed start.
                Process? agent;
                try
                {
                    agent = Process.GetProcessById(pi.dwProcessId);
                    _ = agent.Handle; // its own handle now, so later checks see this process and no other
                }
                catch (Exception) { agent = null; }
                finally { NativeService.CloseHandle(pi.hProcess); }
                Log(agent != null ? "Agent started in session " + session + "." : "The agent in session " + session + " ended as soon as it started.");
                return agent;
            }
            catch (Exception e)
            {
                Log("Could not start the agent: " + e.Message);
                return null;
            }
            finally
            {
                if (own != IntPtr.Zero) NativeService.CloseHandle(own);
                if (token != IntPtr.Zero) NativeService.CloseHandle(token);
            }
        }

        // ================= The service keeps itself up to date =================

        private static readonly AutoResetEvent CheckNow = new(false);
        private static volatile bool Restarting;
        private static volatile string _restartingTo = "";
        private static volatile bool _shuttingDown;

        /// <summary>
        /// Never on its own: Conner chooses when to update. When TailRemote on
        /// this PC has been updated (Check for updates), it nudges the service,
        /// which then downloads the same version for itself, checks it against
        /// GitHub's fingerprint, puts it in place of the running one and restarts
        /// into it. No questions, no administrator prompt: it runs as SYSTEM.
        /// </summary>
        private static void UpdateLoop()
        {
            while (WaitHandle.WaitAny(new WaitHandle[] { Stop, CheckNow }) == 1)
            {
                // A controlling PC's request (File, Update the remote PC) is answered either way.
                // Taken as one: the version and who to answer always belong together.
                var request = Interlocked.Exchange(ref _request, null);
                var reply = request?.Reply;
                // One at a time with MoveToProtected: both put files where the service runs and restart it.
                try { lock (SwapGate) TryUpdate(request?.Want, reply); }
                catch (Exception e) { Log("Update failed: " + e.Message); reply?.Invoke("The remote PC could not update: " + e.Message); }
                if (Restarting) { Wake.Set(); return; }
            }
        }

        private sealed record UpdateRequest(Version Want, Action<string> Reply);
        private static UpdateRequest? _request;

        /// <summary>
        /// A controlling PC chose File, Update the remote PC: update to that version, which must be
        /// GitHub's newest. That is someone choosing to update, so the window's version is no limit.
        /// </summary>
        public static void UpdateFor(string version, Action<string> reply)
        {
            if (!Version.TryParse(version, out var want)) { reply("Kova on the remote PC did not understand the version " + version + "."); return; }
            if (want <= Updater.Current) { reply("The remote PC already has Kova " + Updater.Current + "."); return; }
            if (Restarting) { reply("Kova on the remote PC is already updating."); return; }
            Interlocked.Exchange(ref _request, new UpdateRequest(want, reply))?.Reply("Another controlling PC asked for an update at the same time; it is going ahead.");
            CheckNow.Set();
        }

        private static void TryUpdate(Version? requested, Action<string>? reply)
        {
            // The window runs this same file (the service runs the copy it was installed from) and has just
            // updated it: the new version is already in place, so restart into it straight away, without
            // asking GitHub (which may be limiting questions, and then the service stayed behind for an hour).
            // Its version cannot be read from here: Windows answers with the copy this process already runs from that
            // path. So: the file is not the one this service started from, and the window says it runs a newer version.
            if (requested == null && WindowVersion() is Version told && told > Updater.Current && FileChangedSinceStart())
            {
                RestartIntoPlaced(told, reply);
                return;
            }
            var r = Updater.CheckAsync().GetAwaiter().GetResult();
            if (requested != null && r?.Version != requested)
            {
                reply?.Invoke("The remote PC did not update: the newest Kova on GitHub is " + (r?.Version ?? Updater.Current) + ", not " + requested + ".");
                return;
            }
            if (r == null)
            {
                Log("No update: GitHub has nothing newer than " + Updater.Current + ".");
                return;
            }
            var chosen = WindowVersion();
            if (requested == null && chosen != null && r.Version > chosen)
            {
                Log("GitHub has " + r.Version + ", but Kova on this PC is " + chosen + "; staying in step with it.");
                return;
            }
            if (FileChangedSinceStart() && WindowVersion() == r.Version)
            {
                RestartIntoPlaced(r.Version, reply);
                return;
            }
            Log("Updating the service to version " + r.Version + ".");
            reply?.Invoke("The remote PC's Kova service is downloading " + r.Version + ". Everything carries on until it is ready.");
            if (r.Sha256 == null) throw new InvalidOperationException("GitHub gave no fingerprint for the download, so it was not used");
            string exe = ServiceSelf, fresh = exe + ".new", old = exe + ".old";
            // Streamed and checked as it comes, for as long as data keeps arriving: a whole-download time limit
            // meant a slow line could never update the service. A damaged download throws and changes nothing.
            try { Updater.DownloadAsync(r.Url, fresh, r.Sha256).GetAwaiter().GetResult(); }
            catch { try { File.Delete(fresh); } catch { } throw; }
            // This copy is itself still on trial (updated less than 2 minutes of hosting ago): the copy kept
            // from before it is the last one known to work, and stays the one to go back to.
            string[]? trial = null;
            try { if (File.Exists(RollbackNote)) trial = File.ReadAllLines(RollbackNote); } catch { }
            bool keepPrevious = trial is { Length: >= 2 } && File.Exists(trial[0]);
            if (!keepPrevious || !string.Equals(Path.GetFullPath(trial![0]), Path.GetFullPath(old), StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old); } catch { }
            // Still there (antivirus, a lingering handle): a name of its own, cleared up later
            // with the other leftovers, rather than every update failing until it is freed.
            if (File.Exists(old)) old = exe + ".old-" + DateTime.UtcNow.Ticks;
            File.Move(exe, old);       // a running exe can be renamed, not overwritten
            try { File.Move(fresh, exe); }
            catch { File.Move(old, exe); throw; }
            // Until the new copy has run for 2 minutes, the old one is kept and can come back (or, if this
            // copy was still on trial, the one before it that is known to work).
            try { File.WriteAllLines(RollbackNote, keepPrevious ? new[] { trial![0], trial[1], "0" } : new[] { old, Updater.Current.ToString(), "0" }); } catch { }
            _restartingTo = r.Version.ToString();
            Restarting = true;
        }


        /// <summary>
        /// The window runs this same file and has already updated it: the new version is in place and this
        /// service runs the previous one, renamed to name.old or name.old-.... Swapping again would move the
        /// window's new copy aside and record it as the copy to roll back to, so a bad update could only ever
        /// be rolled back to itself. Point the roll back at the real previous copy instead, and restart into the new one.
        /// </summary>
        // The service's file as it started: its size and time, to see later whether something else replaced it.
        private static (long Length, DateTime Written)? _startedFrom;

        private static (long, DateTime)? FileNow()
        {
            try { var f = new FileInfo(ServiceSelf); return f.Exists ? (f.Length, f.LastWriteTimeUtc) : null; }
            catch { return null; }
        }

        private static bool FileChangedSinceStart() => _startedFrom != null && FileNow() is { } now && now != _startedFrom.Value;

        private static void RestartIntoPlaced(Version version, Action<string>? reply)
        {
            string? previous = null;
            try
            {
                previous = Directory.GetFiles(Path.GetDirectoryName(ServiceSelf)!, Path.GetFileName(ServiceSelf) + ".old*")
                    .Where(f => !f.Contains(".old-failed-", StringComparison.OrdinalIgnoreCase)) // a copy that already failed is never one to go back to
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault(f => VersionOf(f) == Updater.Current);
            }
            catch { }
            if (previous != null) try { File.WriteAllLines(RollbackNote, new[] { previous, Updater.Current.ToString(), "0" }); } catch { }
            Log("Kova on this PC already put version " + version + " in place; the service restarts into it.");
            reply?.Invoke("The remote PC's Kova service is restarting into " + version + ".");
            _restartingTo = version.ToString();
            Restarting = true;
        }

        /// <summary>
        /// Written when the service swaps in a new version: where the old copy is, its version, and
        /// how many times the new one has started. While it is there, the old copy is never cleared.
        /// </summary>
        internal static string RollbackNote => ServiceSelf + ".rollback";

        /// <summary>
        /// Automatic roll back. A PC reached only through TailRemote must never be lost to an update
        /// that will not run: Windows restarts a failing service again and again, but the same broken
        /// copy each time. So each start after an update is counted, and the fourth start without the
        /// new version having stayed up for 2 minutes puts the previous version back and restarts into
        /// it. After 2 minutes really hosting, the update counts as good and the note goes.
        ///
        /// Counted first thing in Main (Program), before anything else the new copy does can fail: a copy
        /// that crashed before reaching the service itself used to never be counted, so never rolled back.
        /// </summary>
        public static void CountStart()
        {
            CountRelocatedStart();
            try
            {
                if (!File.Exists(RollbackNote)) return;
                var note = File.ReadAllLines(RollbackNote);
                string old = note.Length > 0 ? note[0] : "";
                string oldVersion = note.Length > 1 ? note[1] : "the previous version";
                int starts = (note.Length > 2 && int.TryParse(note[2], out int n) ? n : 0) + 1;
                // Only ever a copy kept beside the service's own file (name.old...): the note may sit in a folder others
                // can write, and the service must never be steered into moving some other file as SYSTEM.
                string self = ServiceSelf;
                bool beside = old.Length > 0 && SameFile(Path.GetDirectoryName(old), Path.GetDirectoryName(self))
                    && Path.GetFileName(old).StartsWith(Path.GetFileName(self) + ".old", StringComparison.OrdinalIgnoreCase);
                if (!beside) { File.Delete(RollbackNote); return; }
                if (starts >= 4 && File.Exists(old))
                {
                    string exe = ServiceSelf;
                    string bad = exe + ".old-failed-" + DateTime.UtcNow.Ticks;
                    File.Move(exe, bad);
                    // Never no copy at all: if the old one cannot go back, the failing one does, and is
                    // tried again (and counted again) rather than leaving Windows nothing to start.
                    try { File.Move(old, exe); }
                    catch { File.Move(bad, exe); throw; }
                    File.Delete(RollbackNote);
                    Log("Version " + Updater.Current + " kept failing to start, so the service went back to " + oldVersion + ".");
                    // Not yet: a process that ends before it has reached the service manager may not count
                    // as a failed start, and nothing would start the restored copy. ServiceMain exits.
                    _rolledBack = true;
                    return;
                }
                File.WriteAllLines(RollbackNote, new[] { old, oldVersion, starts.ToString() });
            }
            catch (Exception e) { Log("Roll back check failed: " + e.Message); }
        }

        private static int _hostingStarted;
        private static bool _rolledBack; // CountStart put the previous version back: this copy ends as soon as Windows knows it started

        /// <summary>
        /// Hosting really started (Agent.StartHosting). Only from now does the 2 minutes count: a new copy
        /// that ran but never managed to host used to count as good, and was never rolled back.
        /// </summary>
        private static int _hostingNow, _relocatedWatch; // Agent.StartHosting got as far as hosting (MoveToProtected waits for it)

        public static void HostingStarted()
        {
            Volatile.Write(ref _hostingNow, 1);
            if (File.Exists(RelocatedNote) && Interlocked.Exchange(ref _relocatedWatch, 1) == 0)
                new Thread(() =>
                {
                    Thread.Sleep(TimeSpan.FromMinutes(2));
                    try { File.Delete(RelocatedNote); Log("The service runs well from " + InstallDir + "."); } catch { }
                }) { IsBackground = true, Name = "Kova move check" }.Start();
            if (!File.Exists(RollbackNote) || Interlocked.Exchange(ref _hostingStarted, 1) == 1) return;
            new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromMinutes(2));
                try { File.Delete(RollbackNote); Log("Version " + Updater.Current + " is running well; the previous copy is no longer kept."); } catch { }
            }) { IsBackground = true, Name = "Kova update check" }.Start();
        }

        /// <summary>
        /// A service set up by 4.2 or 4.3 may run a copy that ordinary users can change (Documents, an apps folder):
        /// any program the user runs could swap it and become SYSTEM. Once this version has proven itself (hosting
        /// for two minutes, no roll back pending), it copies itself into Program Files\Kova, points the service there
        /// and restarts into it: controlling PCs reconnect by themselves within seconds. The copy it came from is the
        /// user's own and is left alone. Tried once per start; a failure is logged and tried again at the next start.
        /// </summary>
        private static void MoveToProtected()
        {
            try
            {
                string self = ServiceSelf;
                if (SameFile(self, InstalledExe) || ProtectedFromUsers(self)) return;
                // Not while a new version is still on trial, and only after it has really hosted for a while.
                for (int waited = 0; !Stop.WaitOne(10_000); waited += 10)
                    if (waited >= 120 && Volatile.Read(ref _hostingNow) == 1 && !File.Exists(RollbackNote)) break;
                lock (SwapGate)
                {
                    if (Stop.WaitOne(0) || Restarting) return;
                    Log("This service runs " + self + ", which programs without administrator rights can change. Moving it to " + InstalledExe + ".");
                    Directory.CreateDirectory(InstallDir);
                    string fresh = InstalledExe + ".install";
                    try
                    {
                        File.Copy(self, fresh, true);
                        using (var f = new FileStream(fresh, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) f.Flush(flushToDisk: true);
                        File.Move(fresh, InstalledExe, overwrite: true);
                    }
                    finally { try { File.Delete(fresh); } catch { } }
                    // The way back, written first: if the new copy keeps failing to start, CountStart points the service
                    // at the old one again (a PC reached only through Kova must never be lost to this move).
                    File.WriteAllLines(RelocatedNote, new[] { self, "0" });
                    if (!Register(InstalledExe)) { File.Delete(RelocatedNote); Log("Windows would not point the service at " + InstalledExe + "; it carries on from where it was."); return; }
                    Log("The service now runs " + InstalledExe + " and restarts into it.");
                    _restartingTo = Updater.Current.ToString();
                    Restarting = true;
                    Wake.Set();
                }
            }
            catch (Exception e) { Log("Could not move the service to Program Files: " + e.Message + " Trying again at the next start."); }
        }

        private static readonly object SwapGate = new();

        /// <summary>Where the service ran before MoveToProtected, and how many times the new copy has started; in the service's own folder in ProgramData (administrators only).</summary>
        private static string RelocatedNote => Path.Combine(DataDir, "relocated.txt");

        /// <summary>
        /// Counted first thing at each start, like CountStart: a service just moved to Program Files that fails to start
        /// three times before it hosts goes back to the copy it ran before. Hosting for two minutes ends the trial.
        /// </summary>
        private static void CountRelocatedStart()
        {
            try
            {
                if (!File.Exists(RelocatedNote) || !SameFile(ServiceSelf, InstalledExe)) return;
                var note = File.ReadAllLines(RelocatedNote);
                string old = note.Length > 0 ? note[0] : "";
                int starts = (note.Length > 1 && int.TryParse(note[1], out int n) ? n : 0) + 1;
                if (starts >= 4 && File.Exists(old) && Register(old))
                {
                    File.Delete(RelocatedNote);
                    Log("The copy in " + InstallDir + " kept failing to start, so the service went back to " + old + ".");
                    _rolledBack = true; // ServiceMain ends at once: Windows' recovery starts the old copy
                    return;
                }
                File.WriteAllLines(RelocatedNote, new[] { old, starts.ToString() });
            }
            catch (Exception e) { Log("Move check failed: " + e.Message); }
        }

        /// <summary>A nudge from TailRemote after it updated itself: check now. At most once a minute.</summary>
        private static void UpdatePipe()
        {
            var sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            // Whoever is signed in at the PC, not only an elevated window: the window usually runs without
            // administrator rights, and its nudge was refused, so the service never followed its update.
            // Safe to allow: a nudge only makes the service fetch GitHub's newest release, checked against
            // GitHub's fingerprint, at most once a minute. Nobody over the network.
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            long last = -60_000;
            System.Threading.Timer? later = null;
            while (!Stop.WaitOne(0))
            {
                try
                {
                    using var pipe = NamedPipeServerStreamAcl.Create(Names.UpdatePipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
                    pipe.WaitForConnection();
                    // Four bytes within 2 seconds: anyone signed in may open this pipe, and one that opened it
                    // and never wrote used to block every nudge after it.
                    byte[] nudge = new byte[4];
                    int got = 0;
                    using (var cut = new CancellationTokenSource(2000))
                        try { while (got < 4) { int r = pipe.ReadAsync(nudge, got, 4 - got, cut.Token).GetAwaiter().GetResult(); if (r == 0) break; got += r; } }
                        catch (OperationCanceledException) { }
                    if (got < 4 || nudge[0] != 1) continue;
                    // The version is always taken, even inside the minute: the newest nudge says what the
                    // window runs now, and the check it asks for still happens, when the minute is up. One
                    // older than this service is nobody's real window (it would hold updates back): ignored.
                    var told = new Version(nudge[1], nudge[2], nudge[3]);
                    if (told < Updater.Current) continue;
                    _windowVersion = told;
                    Log("Kova " + told + " on this PC asked the service to check for an update.");
                    long since = Environment.TickCount64 - last;
                    if (since >= 60_000)
                    {
                        last = Environment.TickCount64;
                        CheckNow.Set();
                    }
                    else if (since >= 0) // below 0: a check is already set for when the minute is up
                    {
                        // One check, when the minute is up, however many nudges come before then.
                        long due = 60_000 - since;
                        last = Environment.TickCount64 + due;
                        later ??= new System.Threading.Timer(_ => CheckNow.Set());
                        later.Change(due, Timeout.Infinite);
                    }
                }
                catch { Thread.Sleep(1000); }
            }
        }

        private static Version? _windowVersion;

        /// <summary>The version the TailRemote window that nudged us runs, sent with the nudge.</summary>
        private static Version? WindowVersion() => _windowVersion;

        /// <summary>From the TailRemote window, after it updated: ask the service to check for its update now.</summary>
        public static bool NudgeUpdate()
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Names.UpdatePipe, PipeDirection.Out);
                pipe.Connect(2000);
                var v = Updater.Current;
                pipe.Write(new byte[] { 1, (byte)v.Major, (byte)v.Minor, (byte)Math.Max(0, v.Build) });
                return true;
            }
            catch { return false; }
        }
    }
}
