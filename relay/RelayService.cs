using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace KovaRelay
{
    /// <summary>
    /// Kova Relay as a Windows service.
    ///
    /// The service runs this exe where it is ("exe" --service NAME), as its own virtual account NT SERVICE\NAME (not
    /// SYSTEM), starts at boot with nobody signed in, and Windows restarts it after a crash. Its settings (settings.json,
    /// shared with the tray), hosts list, log and status live in ProgramData\NAME, which only SYSTEM, administrators
    /// and that account can change (users can read it, so the tray can show the status, the settings and the log).
    /// Without the service the folder is still there, for settings.json alone, and only administrators can change it.
    ///
    /// Everything that changes the service goes through an elevated copy of this exe (a UAC prompt): install, remove,
    /// start, stop, port, settings, forgetting a name. The elevated copy writes into the protected folder, and the
    /// service picks it up within 2 seconds (a new port restarts it). Nothing a user without administrator rights can write ever reaches the
    /// service, and installing is refused unless the exe and its folders are protected from such users
    /// (ProtectedFromUsers, the same check Kova's own service uses): otherwise anyone could swap the exe and run code
    /// as the service.
    /// </summary>
    internal static class RelayService
    {
        public const string DefaultName = "KovaRelay";

        /// <summary>The service name: KovaRelay, or KOVA_RELAY_SERVICE for tests (a word of letters and digits).</summary>
        public static string Name
        {
            get
            {
                string? v = Environment.GetEnvironmentVariable("KOVA_RELAY_SERVICE");
                return v is { Length: > 0 and <= 40 } && NameOk(v) ? v : DefaultName;
            }
        }

        public static bool NameOk(string n)
        {
            if (n.Length == 0 || n.Length > 40) return false;
            foreach (char c in n) if (!char.IsAsciiLetterOrDigit(c)) return false;
            return true;
        }

        public static string DataDir(string name) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), name);

        public static string StatusFile(string name) => Path.Combine(DataDir(name), "status.txt");
        public static string ForgetFile(string name) => Path.Combine(DataDir(name), "forget.txt");
        public static string ExeFolder => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        // ------------------------------------------------------------------ asking Windows

        public static bool IsInstalled(string name) => State(name) != 0;

        /// <summary>SERVICE_STOPPED (1), START_PENDING (2), STOP_PENDING (3), RUNNING (4)..., or 0 if it is not there.</summary>
        public static int State(string name)
        {
            IntPtr scm = OpenSCManagerW(null, null, 0x0001);
            if (scm == IntPtr.Zero) return 0;
            try
            {
                IntPtr svc = OpenServiceW(scm, name, 0x0004);
                if (svc == IntPtr.Zero) return 0;
                try { return QueryServiceStatus(svc, out SERVICE_STATUS s) ? s.CurrentState : 0; }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        public const int Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;

        // ------------------------------------------------------------------ the tray side

        public enum Result { Ok = 0, NotProtected = 10, CannotRunHere = 11, CreateFailed = 12, WouldNotStart = 13, Failed = 14, Cancelled = 15, BadArgs = 16 }

        /// <summary>Runs this exe elevated with these arguments and waits. Cancelled if the UAC prompt was turned down.</summary>
        public static Result RunElevated(string args)
        {
            try
            {
                using Process? p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args)
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                if (p is null) return Result.Failed;
                if (!p.WaitForExit(90_000)) return Result.Failed;
                return Enum.IsDefined(typeof(Result), p.ExitCode) ? (Result)p.ExitCode : Result.Failed;
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return Result.Cancelled; }
            catch (Exception) { return Result.Failed; }
        }

        public static string Say(Result r, string exe) => r switch
        {
            Result.Ok => "Done.",
            Result.NotProtected => $"The relay can only run as a service from a folder that only administrators can change, such as Program Files. Otherwise any program could replace {Path.GetFileName(exe)} and run as the service. Move KovaRelay.exe to a folder like C:\\Program Files\\Kova Relay and turn this on from there.",
            Result.CannotRunHere => "The relay cannot run as a service from here: it is on a network drive, a removable drive, OneDrive or a temporary folder, which may not be there when Windows starts. Move it to a folder like C:\\Program Files\\Kova Relay first.",
            Result.CreateFailed => "Windows would not create the service.",
            Result.WouldNotStart => "The service was installed but would not start. The relay's log in ProgramData may say why.",
            Result.Cancelled => "Nothing was changed, because the administrator prompt was turned down.",
            Result.BadArgs => "Nothing was changed.",
            _ => "That did not work, and nothing was changed.",
        };

        // ------------------------------------------------------------------ the elevated side

        /// <summary>Handles --service-* and --settings-* switches. Returns the exit code, or null if args are not one of them.</summary>
        public static int? HandleSwitch(string[] args)
        {
            if (args.Length < 2 || !(args[0].StartsWith("--service", StringComparison.OrdinalIgnoreCase) || args[0].StartsWith("--settings", StringComparison.OrdinalIgnoreCase))) return null;
            string name = args[1];
            if (!NameOk(name)) return (int)Result.BadArgs;
            bool loopback = Array.Exists(args, a => a.Equals("--loopback", StringComparison.OrdinalIgnoreCase));
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "--service": return RunService(name, loopback);
                    case "--service-install": return (int)Install(name, loopback);
                    case "--service-remove": return (int)Remove(name, args.Length > 2 && args[2] == "--delete-data");
                    case "--service-control":
                        if (args.Length < 3) return (int)Result.BadArgs;
                        return (int)(args[2] == "start" ? StartAndWait(name) : StopAndWait(name));
                    case "--settings-port":
                    {
                        // The port, for the tray and the service alike. The service, if it runs, restarts on it.
                        if (args.Length < 3 || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
                            return (int)Result.BadArgs;
                        var files = SettingsFiles(name);
                        if (!SecureDataDir(name) || !files.SaveSettings(files.LoadSettings() with { Port = port })) return (int)Result.Failed;
                        DeleteOldSettings(files);
                        if (State(name) is Running or StartPending)
                        {
                            StopAndWait(name);
                            return (int)StartAndWait(name);
                        }
                        return 0;
                    }
                    case "--settings-allow":
                    {
                        if (args.Length < 3 || args[2] is not ("yes" or "no")) return (int)Result.BadArgs;
                        var files = SettingsFiles(name);
                        if (!SecureDataDir(name) || !files.SaveSettings(files.LoadSettings() with { AllowNewNames = args[2] == "yes" })) return (int)Result.Failed;
                        DeleteOldSettings(files);
                        return 0;
                    }
                    case "--settings-migrate":
                    {
                        // 4.4.0's port.txt and relay-settings.txt, moved into settings.json once and then deleted.
                        // hosts.txt and the log beside the exe go too, once the tray has its own copy of the host list.
                        var files = SettingsFiles(name);
                        if (!SecureDataDir(name)) return (int)Result.Failed;
                        if (!File.Exists(files.SettingsFile) && !files.SaveSettings(files.LoadSettings())) return (int)Result.Failed;
                        DeleteOldSettings(files);
                        if (args.Length > 2 && args[2] == "--hosts-moved")
                            foreach (string f in new[] { "hosts.txt", "relay-log.txt", "relay-log.old.txt" })
                                try { File.Delete(Path.Combine(ExeFolder, f)); } catch (Exception) { }
                        return 0;
                    }
                    case "--service-forget":
                        if (args.Length < 3 || !KovaRelayWire.Wire.NameOk(args[2])) return (int)Result.BadArgs;
                        File.AppendAllText(ForgetFile(name), args[2] + "\r\n");
                        return 0;
                }
            }
            catch (Exception) { return (int)Result.Failed; }
            return (int)Result.BadArgs;
        }

        /// <summary>settings.json and what 4.4.0 left beside this exe, as the elevated copy reads them.</summary>
        private static RelayFiles SettingsFiles(string name) => new(DataDir(name), DataDir(name), ExeFolder);

        /// <summary>Deletes 4.4.0's settings files once settings.json holds what they said. A file that will not go is left.</summary>
        private static void DeleteOldSettings(RelayFiles files)
        {
            foreach (string? f in new[] { files.OldPortFile, files.OldSettingsFile, files.OldDataSettingsFile })
                if (f is not null) try { File.Delete(f); } catch (Exception) { }
        }

        private static Result Install(string name, bool loopback)
        {
            string exe = Path.GetFullPath(Environment.ProcessPath!);
            if (!CanRunInPlace(exe)) return Result.CannotRunHere;
            if (!ProtectedFromUsers(exe)) return Result.NotProtected;
            string bin = "\"" + exe + "\" --service " + name + (loopback ? " --loopback" : "");
            string quoted = "\"" + bin.Replace("\"", "\\\"") + "\"";
            string display = "\"Kova Relay" + (name == DefaultName ? "" : " " + name) + "\"";
            string account = "\"NT SERVICE\\" + name + "\"";
            if (!IsInstalled(name))
            {
                if (Run("sc.exe", $"create {name} binPath= {quoted} start= auto obj= {account} DisplayName= {display}") != 0) return Result.CreateFailed;
            }
            else
            {
                StopAndWait(name);
                if (Run("sc.exe", $"config {name} binPath= {quoted} start= auto obj= {account} DisplayName= {display}") != 0) return Result.CreateFailed;
            }
            Run("sc.exe", $"description {name} \"Pairs Kova hosts and the PCs that control them, so a host with no open port can still be reached.\"");
            Run("sc.exe", $"failure {name} reset= 60 actions= restart/2000/restart/5000/restart/10000");
            if (!SecureDataDir(name)) return Result.Failed;
            // The host list a relay in the tray kept (4.4.0: beside the exe; 4.4.1: this user's LocalAppData) carries over once.
            string hosts = Path.Combine(DataDir(name), "hosts.txt");
            foreach (string from in new[] { Path.Combine(ExeFolder, "hosts.txt"), Path.Combine(TrayFolder(name), "hosts.txt") })
                try { if (File.Exists(from) && !File.Exists(hosts)) File.Copy(from, hosts); } catch (Exception) { }
            // Written by this administrator: whatever settings.json said before (or 4.4.0's files) is saved again here.
            var files = SettingsFiles(name);
            if (!files.SaveSettings(files.LoadSettings())) return Result.Failed;
            DeleteOldSettings(files);
            return StartAndWait(name);
        }

        /// <summary>The folder a relay in the tray keeps its host list and log in: this user's LocalAppData\NAME.</summary>
        public static string TrayFolder(string name) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);

        /// <summary>
        /// ProgramData\NAME, made safe: owned by administrators, SYSTEM and administrators full control, the service's own
        /// account (when it is installed) may change it, users may only read. Done by every elevated copy that writes
        /// there: a user may have made the folder first (anyone can make folders in ProgramData), and then owned it.
        /// </summary>
        internal static bool SecureDataDir(string name)
        {
            try
            {
                string dir = DataDir(name);
                SecurityIdentifier? serviceSid = null;
                if (IsInstalled(name))
                    try { serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", name).Translate(typeof(SecurityIdentifier)); } catch (Exception) { }
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);
                const InheritanceFlags All = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
                if (serviceSid is not null)
                    sec.AddAccessRule(new FileSystemAccessRule(serviceSid, FileSystemRights.Modify, All, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, All, PropagationFlags.None, AccessControlType.Allow));
                sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                var info = new DirectoryInfo(dir);
                if (!info.Exists) info.Create(sec);
                info.SetAccessControl(sec);
                // Files inside get the folder's rules again (an older copy may have made them with others).
                foreach (FileInfo f in info.GetFiles())
                {
                    var fs = f.GetAccessControl();
                    fs.SetAccessRuleProtection(false, false);
                    foreach (FileSystemAccessRule r in fs.GetAccessRules(true, false, typeof(SecurityIdentifier))) fs.RemoveAccessRuleSpecific(r);
                    fs.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                    f.SetAccessControl(fs);
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        private static Result Remove(string name, bool deleteData)
        {
            if (IsInstalled(name))
            {
                StopAndWait(name);
                if (Run("sc.exe", "delete " + name) != 0 && IsInstalled(name)) return Result.Failed;
            }
            if (deleteData) try { Directory.Delete(DataDir(name), true); } catch (Exception) { }
            return Result.Ok;
        }

        private static Result StartAndWait(string name)
        {
            Run("sc.exe", "start " + name);
            for (int i = 0; i < 60; i++)
            {
                int s = State(name);
                if (s == Running) return Result.Ok;
                if (s == Stopped && i > 8) break;
                Thread.Sleep(250);
            }
            return Result.WouldNotStart;
        }

        private static Result StopAndWait(string name)
        {
            Run("sc.exe", "stop " + name);
            for (int i = 0; i < 80; i++)
            {
                int s = State(name);
                if (s == Stopped || s == 0) return Result.Ok;
                Thread.Sleep(250);
            }
            return Result.Failed;
        }

        private static int Run(string file, string args)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), file);
                using Process p = Process.Start(new ProcessStartInfo(path, args)
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
                var err = p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                err.Wait();
                return p.ExitCode;
            }
            catch (Exception) { return -1; }
        }

        // ------------------------------------------------------------------ protection checks (from Kova's ServiceHost)

        // Rights that let someone change what runs: write or append to the file, delete or rename it, change who may
        // (permissions, owner). On the exe's folder, also add files (a DLL Windows would load from beside the exe) or
        // delete what is in it; on folders above, delete or rename the folder below or change permissions.
        private const FileSystemRights FileDanger = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
        private const FileSystemRights FolderDanger = FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        private const FileSystemRights AboveDanger = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
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
            if (sec.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !TrustedSid(owner) && !root) return false;
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid || TrustedSid(sid)) continue;
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                if (sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) continue;
                if ((rule.FileSystemRights & danger) != 0) return false;
            }
            return true;
        }

        private static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

        private static bool TrustedSid(SecurityIdentifier sid) =>
            sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid == TrustedInstallerSid;

        /// <summary>Not a network drive, removable drive, temp folder or OneDrive folder (from Kova's ServiceHost.CanRunInPlace).</summary>
        internal static bool CanRunInPlace(string exe)
        {
            try
            {
                if (exe.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                if (Path.GetPathRoot(exe) is not { Length: > 0 } root || new DriveInfo(root).DriveType != DriveType.Fixed) return false;
                string temp = Path.GetFullPath(Path.GetTempPath());
                if (exe.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return false;
                if (exe.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\Windows\Temp\", StringComparison.OrdinalIgnoreCase)) return false;
                foreach (string v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
                    if (Environment.GetEnvironmentVariable(v) is { Length: > 3 } od && exe.StartsWith(od.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase)) return false;
                if (exe.Contains(@"\OneDrive\", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\OneDrive - ", StringComparison.OrdinalIgnoreCase)) return false;
                const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;
                if ((File.GetAttributes(exe) & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0) return false;
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ the service itself

        private static IntPtr _statusHandle;
        private static SERVICE_STATUS _status;
        private static readonly ManualResetEvent StopNow = new(false);
        private static ServiceMainProc? _main;
        private static HandlerEx? _handler;
        private static string _name = DefaultName;
        private static bool _loopback;

        private static int RunService(string name, bool loopback)
        {
            _name = name;
            _loopback = loopback;
            _main = ServiceMain;
            _handler = Handler;
            var table = new[]
            {
                new SERVICE_TABLE_ENTRY { Name = Marshal.StringToHGlobalUni(name), Proc = _main },
                new SERVICE_TABLE_ENTRY(),
            };
            return StartServiceCtrlDispatcherW(table) ? 0 : 1;
        }

        private static void Report(int state, int exitCode = 0)
        {
            _status.ServiceType = 0x10;   // own process
            _status.CurrentState = state;
            _status.ControlsAccepted = state == Running ? 1 | 4 : 0;   // stop, shutdown
            _status.Win32ExitCode = exitCode;
            _status.WaitHint = state is Running or Stopped ? 0 : 5000;
            SetServiceStatus(_statusHandle, ref _status);
        }

        private static int Handler(int control, int eventType, IntPtr data, IntPtr context)
        {
            if (control is 1 or 5) { Report(StopPending); StopNow.Set(); }   // stop, shutdown
            return 0;
        }

        private static void ServiceMain(int argc, IntPtr argv)
        {
            _statusHandle = RegisterServiceCtrlHandlerExW(_name, _handler!, IntPtr.Zero);
            Report(StartPending);
            string data = DataDir(_name);
            try { Directory.CreateDirectory(data); } catch (Exception) { }
            // settings.json (an administrator writes it), and everything the relay writes, in ProgramData. Until the
            // tray has moved 4.4.0's port.txt into settings.json, the port in it is still used.
            var files = new RelayFiles(data, data, ExeFolder);
            var log = new RelayLog(data);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => { log.Write("The relay service stopped because of an unexpected error: " + e.ExceptionObject); log.Flush(); };
            var server = new RelayServer(files, log) { LoopbackOnly = _loopback };
            log.Write($"Kova Relay {Program.Version} is starting as the service {_name}, from {ExeFolder}.");
            server.Start(files.LoadSettings().Port);
            Report(Running);
            DateTime settingsStamp = Stamp(files.SettingsFile);
            while (!StopNow.WaitOne(2000))
            {
                try
                {
                    // An administrator changed settings.json (through --settings-allow): take it. A new port comes with a restart.
                    DateTime now = Stamp(files.SettingsFile);
                    if (now != settingsStamp) { settingsStamp = now; server.SetAllowNewNamesFromFile(files.LoadSettings().AllowNewNames); }
                    // Names an administrator asked to forget (--service-forget).
                    string forget = ForgetFile(_name);
                    if (File.Exists(forget))
                    {
                        string[] names = File.ReadAllLines(forget);
                        File.Delete(forget);
                        foreach (string n in names)
                            if (KovaRelayWire.Wire.NameOk(n.Trim())) server.Forget(n.Trim());
                    }
                    WriteStatus(_name, server, files);
                }
                catch (Exception e) { log.Write("The service's housekeeping hit a problem: " + e.Message); }
            }
            server.Dispose();
            try { File.Delete(StatusFile(_name)); } catch (Exception) { }
            log.Write("The relay service stopped.");
            log.Dispose();
            Report(Stopped);
        }

        private static DateTime Stamp(string path) { try { return File.GetLastWriteTimeUtc(path); } catch (Exception) { return default; } }

        // ------------------------------------------------------------------ status file (service writes, tray reads)

        private static void WriteStatus(string name, RelayServer server, RelayFiles files)
        {
            RelaySnapshot s = server.Snapshot();
            var sb = new StringBuilder();
            sb.Append("running=").Append(s.Running ? "yes" : "no").Append("\r\n");
            sb.Append("port=").Append(s.Port).Append("\r\n");
            sb.Append("problem=").Append((s.Problem ?? "").Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
            sb.Append("files=").Append((files.Problem ?? "").Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
            sb.Append("allow-new-host-names=").Append(server.AllowNewNames ? "yes" : "no").Append("\r\n");
            sb.Append("hosts=").Append(s.Hosts).Append("\r\n");
            sb.Append("pcs=").Append(s.ControllingPcs).Append("\r\n");
            sb.Append("connections=").Append(s.Connections).Append("\r\n");
            foreach (HostRow r in s.Rows)
                sb.Append("host=").Append(r.Name).Append('|').Append(r.Connected ? "1" : "0").Append('|').Append(r.ControllingPcs)
                  .Append('|').Append(r.LastSeenUtc.Ticks).Append("\r\n");
            string path = StatusFile(name), tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>What the service last wrote, or null if it has not written in the last 10 seconds.</summary>
        public static (RelaySnapshot Snap, bool AllowNew, string? FilesProblem)? ReadStatus(string name)
        {
            try
            {
                var info = new FileInfo(StatusFile(name));
                if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromSeconds(10)) return null;
                bool running = false, allow = true; int port = 0, hosts = 0, pcs = 0, conns = 0; string? problem = null, filesProblem = null;
                var rows = new List<HostRow>();
                foreach (string line in File.ReadAllLines(info.FullName))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line[..eq], v = line[(eq + 1)..];
                    switch (k)
                    {
                        case "running": running = v == "yes"; break;
                        case "port": int.TryParse(v, out port); break;
                        case "problem": problem = v.Length > 0 ? v : null; break;
                        case "files": filesProblem = v.Length > 0 ? v : null; break;
                        case "allow-new-host-names": allow = v != "no"; break;
                        case "hosts": int.TryParse(v, out hosts); break;
                        case "pcs": int.TryParse(v, out pcs); break;
                        case "connections": int.TryParse(v, out conns); break;
                        case "host":
                            string[] f = v.Split('|');
                            if (f.Length == 4 && int.TryParse(f[2], out int n) && long.TryParse(f[3], out long ticks) && ticks >= 0 && ticks <= DateTime.MaxValue.Ticks)
                                rows.Add(new HostRow(f[0], f[1] == "1", n, new DateTime(ticks, DateTimeKind.Utc)));
                            break;
                    }
                }
                return (new RelaySnapshot(running, port, problem, hosts, pcs, conns, rows), allow, filesProblem);
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------ Windows declarations

        private delegate void ServiceMainProc(int argc, IntPtr argv);
        private delegate int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_TABLE_ENTRY { public IntPtr Name; public ServiceMainProc? Proc; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS { public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRY[] table);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr RegisterServiceCtrlHandlerExW(string name, HandlerEx handler, IntPtr context);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManagerW(string? machine, string? database, int access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenServiceW(IntPtr manager, string name, int access);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);
        [DllImport("advapi32.dll")]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
