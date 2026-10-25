using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace TailRemote
{
    internal enum NvdaHelperAction { None, Install, Update, Remove }

    /// <summary>
    /// Where the Kova Helper stands on this PC. ConfigDir: NVDA's settings folder for the person at the screen (null when
    /// it cannot be found: Problem says why). Installed: the version in NVDA's add-ons folder, or null. Carried: the
    /// version inside this Kova. Connected: the add-on is talking to this Kova now.
    /// </summary>
    internal sealed record NvdaHelperState(string? ConfigDir, string? Installed, string? Carried, bool NvdaRunning, bool Connected, string? Problem)
    {
        public bool IsInstalled => Installed != null;
        public bool UpdateAvailable => Installed != null && Carried != null && NvdaHelper.CompareVersions(Carried, Installed) > 0;
        /// <summary>What the menu offers: install when it is not there, update when Kova carries a newer one, else remove.</summary>
        public NvdaHelperAction Offer => Carried == null ? NvdaHelperAction.None
            : !IsInstalled ? NvdaHelperAction.Install : UpdateAvailable ? NvdaHelperAction.Update : NvdaHelperAction.Remove;
    }

    /// <summary>
    /// The Kova Helper: a small NVDA add-on (nvda-addon\ in the source, carried inside Kova.exe as KovaHelper.nvda-addon)
    /// installed on a HOST only. It hands everything NVDA speaks, its cut-offs, beeps, sounds and braille straight to
    /// Kova on the same PC, so no Speech Viewer is needed and pitch and sounds come through too. The controlling PC needs
    /// nothing.
    ///
    /// Two halves here:
    ///  - Server: a named pipe in Kova that the add-on connects to. No network listener at all: the pipe refuses network
    ///    clients, only the person at the screen (and SYSTEM) may open it, nothing else can take its name first while Kova
    ///    runs, and every connection must come from a real NVDA (Program Files, or signed by NV Access) in that session.
    ///    The add-on, for its part, only sends to a pipe owned by its own user, SYSTEM or Administrators, served by a
    ///    program called Kova. It never does anything Kova says: nothing comes back on the pipe.
    ///  - Install, update and removal into NVDA's own add-ons folder, done the way NVDA would on a restart (so NVDA never
    ///    asks), then NVDA restarted as the person who runs it.
    /// </summary>
    internal static class NvdaHelper
    {
        public const string AddonName = "kovaHelper";
        public const string ResourceName = "KovaHelper.nvda-addon";
        /// <summary>The pipe's name ends in the Windows session number: each signed-in person's NVDA finds their own Kova.</summary>
        public const string PipePrefix = "KovaNvda-";
        public static string PipeName(uint session) => PipePrefix + session + Names.TestPipes;

        // ---- Menu text and sentences (read aloud by NVDA: plain words) ----

        public static string MenuText(NvdaHelperState s) => s.Offer switch
        {
            NvdaHelperAction.Update => "Update the Kova Helper NVDA add-on...",
            NvdaHelperAction.Remove => "Remove the Kova Helper NVDA add-on...",
            _ => "Install the Kova Helper NVDA add-on...",
        };
        public const string RemoteMenuText = "Install the Kova Helper on the remote PC...";

        // ---- The add-on inside Kova ----

        private static byte[]? _package;
        private static bool _packageLooked;

        /// <summary>The .nvda-addon (a zip) built into this exe, or null if this build has none.</summary>
        public static byte[]? Package()
        {
            if (_packageLooked) return _package;
            try
            {
                using var s = typeof(NvdaHelper).Assembly.GetManifestResourceStream(ResourceName);
                if (s != null && s.Length < 4 << 20) { var ms = new MemoryStream(); s.CopyTo(ms); _package = ms.ToArray(); }
            }
            catch { }
            _packageLooked = true;
            return _package;
        }

        /// <summary>The version of the add-on this Kova carries, from its manifest.</summary>
        public static string? CarriedVersion => Package() is byte[] p && ReadManifest(p) is { } m && m.TryGetValue("version", out var v) ? v : null;

        /// <summary>manifest.ini from an add-on package, parsed; null if it has none or it is not a zip.</summary>
        internal static Dictionary<string, string>? ReadManifest(byte[] package)
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.Replace('\\', '/') != "manifest.ini" || e.Length > 64 << 10) continue;
                    using var r = new StreamReader(e.Open(), Encoding.UTF8);
                    return ParseManifest(r.ReadToEnd());
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// An NVDA manifest.ini: "key = value" lines, values plain, in "quotes", or in """three quotes""" over several lines.
        /// Keys are lower case; comments (#) and anything else are skipped.
        /// </summary>
        internal static Dictionary<string, string> ParseManifest(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim().ToLowerInvariant(), val = line[(eq + 1)..].Trim();
                if (val.StartsWith("\"\"\"", StringComparison.Ordinal))
                {
                    var sb = new StringBuilder(val[3..]);
                    while (!sb.ToString().Contains("\"\"\"", StringComparison.Ordinal) && i + 1 < lines.Length) sb.Append('\n').Append(lines[++i]);
                    string all = sb.ToString();
                    int end = all.IndexOf("\"\"\"", StringComparison.Ordinal);
                    val = (end >= 0 ? all[..end] : all).Trim();
                }
                else if (val.Length >= 2 && val[0] == '"' && val.IndexOf('"', 1) > 0) val = val[1..val.IndexOf('"', 1)];
                else { int hash = val.IndexOf(" #", StringComparison.Ordinal); if (hash >= 0) val = val[..hash].Trim(); }
                d[key] = val;
            }
            return d;
        }

        /// <summary>Compares dotted versions ("1.0.10" is newer than "1.0.9"); anything that is not a number counts as 0.</summary>
        public static int CompareVersions(string a, string b)
        {
            var x = a.Split('.'); var y = b.Split('.');
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int.TryParse(i < x.Length ? x[i] : "0", out int p);
                int.TryParse(i < y.Length ? y[i] : "0", out int q);
                if (p != q) return p.CompareTo(q);
            }
            return 0;
        }

        // ---- Installing into an NVDA settings folder (plain file work, testable on any folder) ----

        // NVDA ignores folders in its add-ons folder whose names end in ".delete" (it uses them itself for add-ons it is
        // removing), so the new copy is unpacked under such a name and renamed into place in one step: a crash halfway
        // never leaves NVDA a half-written add-on to load. The old copy goes aside the same way and is deleted.
        private const string TempMark = ".kova-";
        private const string DeleteSuffix = ".delete";
        private const int MaxEntries = 64, MaxEntryBytes = 1 << 20, MaxTotalBytes = 4 << 20;

        /// <summary>The version of the add-on in this NVDA settings folder, or null if it is not there.</summary>
        internal static string? InstalledVersion(string configDir)
        {
            try
            {
                string m = Path.Combine(configDir, "addons", AddonName, "manifest.ini");
                var fi = new FileInfo(m);
                if (!fi.Exists || fi.Length > 64 << 10) return null;
                return ParseManifest(File.ReadAllText(m)).TryGetValue("version", out var v) && v.Length > 0 ? v : "0";
            }
            catch { return null; }
        }

        /// <summary>Puts the add-on into configDir\addons\kovaHelper (a new copy, or over an older one). Null when done, else why not.</summary>
        internal static string? InstallInto(string configDir, byte[] package)
        {
            string addons = Path.Combine(configDir, "addons");
            string final = Path.Combine(addons, AddonName);
            string? temp = null, old = null;
            try
            {
                Directory.CreateDirectory(addons);
                CleanLeftovers(addons);
                temp = Path.Combine(addons, AddonName + TempMark + Hex() + DeleteSuffix);
                string? bad = Extract(package, temp);
                if (bad != null) { TryDelete(temp); return bad; }
                var m = File.Exists(Path.Combine(temp, "manifest.ini")) ? ParseManifest(File.ReadAllText(Path.Combine(temp, "manifest.ini"))) : null;
                if (m == null || !m.TryGetValue("name", out var name) || name != AddonName) { TryDelete(temp); return "the add-on inside Kova is damaged"; }
                if (Directory.Exists(final))
                {
                    old = Path.Combine(addons, AddonName + TempMark + "old-" + Hex() + DeleteSuffix);
                    Directory.Move(final, old);
                }
                try { Directory.Move(temp, final); }
                catch
                {
                    if (old != null) try { Directory.Move(old, final); } catch { }
                    throw;
                }
                temp = null;
                if (old != null) TryDelete(old);
                return null;
            }
            catch (Exception e)
            {
                if (temp != null) TryDelete(temp);
                return e is UnauthorizedAccessException ? "Windows did not let Kova write to NVDA's add-ons folder" : e.Message;
            }
        }

        /// <summary>Takes the add-on out of configDir. Null when done (or it was not there), else why not.</summary>
        internal static string? RemoveFrom(string configDir)
        {
            string addons = Path.Combine(configDir, "addons");
            string final = Path.Combine(addons, AddonName);
            try
            {
                if (!Directory.Exists(final)) return null;
                // Renamed first: NVDA skips it from then on even if a file in it cannot be deleted right now.
                string old = Path.Combine(addons, AddonName + TempMark + "old-" + Hex() + DeleteSuffix);
                Directory.Move(final, old);
                TryDelete(old);
                return null;
            }
            catch (Exception e) { return e is UnauthorizedAccessException ? "Windows did not let Kova change NVDA's add-ons folder" : e.Message; }
        }

        /// <summary>Kova's own leftovers in the add-ons folder (from an earlier try that could not delete them).</summary>
        private static void CleanLeftovers(string addons)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(addons, AddonName + TempMark + "*" + DeleteSuffix)) TryDelete(d);
            }
            catch { }
        }

        private static void TryDelete(string dir)
        {
            try
            {
                // Never follows a link out of the folder: a link inside is removed, not what it points to.
                var di = new DirectoryInfo(dir);
                if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) { di.Delete(false); return; }
                di.Delete(true);
            }
            catch { }
        }

        private static string Hex() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        /// <summary>
        /// Unpacks a package into a new folder. Every name is checked (no climbing out, no drive letters or odd characters)
        /// and sizes are capped, though the package is Kova's own. Null when done, else why not.
        /// </summary>
        internal static string? Extract(byte[] package, string dir)
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
                if (zip.Entries.Count > MaxEntries) return "the add-on inside Kova has too many files";
                string root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
                Directory.CreateDirectory(dir);
                long total = 0;
                foreach (var e in zip.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    bool folder = name.EndsWith('/');
                    string trimmed = name.TrimEnd('/');
                    if (trimmed.Length == 0 || name.StartsWith('/') || !SafeEntry(trimmed)) return "the add-on inside Kova has a file name Kova will not unpack: " + name;
                    string path = Path.GetFullPath(Path.Combine(dir, trimmed.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "the add-on inside Kova has a file name Kova will not unpack: " + name;
                    if (folder) { Directory.CreateDirectory(path); continue; }
                    if (e.Length > MaxEntryBytes || (total += e.Length) > MaxTotalBytes) return "the add-on inside Kova is too big";
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var src = e.Open();
                    using var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                    var buf = new byte[81920];
                    long wrote = 0;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        if ((wrote += n) > MaxEntryBytes) return "the add-on inside Kova is too big";
                        dst.Write(buf, 0, n);
                    }
                }
                return null;
            }
            catch (InvalidDataException) { return "the add-on inside Kova is damaged"; }
        }

        private static bool SafeEntry(string name)
        {
            foreach (string part in name.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith('.') || part.EndsWith(' ')) return false;
                foreach (char c in part) if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ' ')) return false;
            }
            return true;
        }

        // ---- Who and where: the person at the screen, their NVDA and its settings folder ----

        /// <summary>The NVDA the person in a session runs (never the copy on the sign-in screen, which runs as SYSTEM).</summary>
        internal sealed class NvdaProc { public int Pid; public string Exe = ""; }

        public static uint MySession() { ProcessIdToSessionId((uint)Environment.ProcessId, out uint s); return s; }

        /// <summary>The session whose NVDA this Kova serves: its own, or for the service (session 0) the one at the screen.</summary>
        public static uint TargetSession()
        {
            uint mine = MySession();
            return mine != 0 ? mine : WTSGetActiveConsoleSessionId();
        }

        internal static NvdaProc? FindNvda(uint session)
        {
            foreach (var p in Process.GetProcessesByName("nvda"))
            {
                using (p)
                {
                    try
                    {
                        if ((uint)p.SessionId != session) continue;
                        string? exe = ImagePath(p.Id);
                        if (exe == null || !NvdaTrust.IsTrustedNvda(exe) || RunsAsSystem(p.Id)) continue;
                        return new NvdaProc { Pid = p.Id, Exe = exe };
                    }
                    catch { }
                }
            }
            return null;
        }

        internal static string? ImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* query limited information */, false, (uint)pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        private static bool RunsAsSystem(int pid)
        {
            using var t = ProcessToken(pid, TOKEN_QUERY);
            if (t == null) return false;
            try { using var id = new WindowsIdentity(t.DangerousGetHandle()); return id.IsSystem; } catch { return false; }
        }

        private static SafeAccessTokenHandle? ProcessToken(int pid, uint access)
        {
            IntPtr h = OpenProcess(0x1000, false, (uint)pid);
            if (h == IntPtr.Zero) return null;
            try { return OpenProcessToken(h, access, out IntPtr tok) ? new SafeAccessTokenHandle(tok) : null; }
            finally { CloseHandle(h); }
        }

        /// <summary>True for SYSTEM (the service and its agent) and for a Kova run as administrator.</summary>
        internal static bool Privileged()
        {
            try { using var me = WindowsIdentity.GetCurrent(); return me.IsSystem || new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return true; }
        }

        private static bool IAmSystem() { try { using var me = WindowsIdentity.GetCurrent(); return me.IsSystem; } catch { return false; } }

        /// <summary>
        /// What Apply works on. User: the token Kova acts with when it is privileged (the token of the person's own NVDA,
        /// or for the service with NVDA not running, the person signed in at the screen), so files in their profile are
        /// written with their rights, never SYSTEM's or an administrator's. Null when Kova already runs as that person.
        /// </summary>
        private sealed class Target : IDisposable
        {
            public NvdaProc? Nvda;
            public string? ConfigDir;
            public string? Problem;
            public SafeAccessTokenHandle? User;
            public bool Privileged;
            public void Dispose() => User?.Dispose();
        }

        private static Target Locate()
        {
            var t = new Target { Privileged = Privileged() };
            uint session = TargetSession();
            if (session == 0xFFFFFFFF) { t.Problem = "Nobody is signed in on this PC."; return t; }
            t.Nvda = FindNvda(session);
            if (t.Privileged)
            {
                if (t.Nvda != null) t.User = ProcessToken(t.Nvda.Pid, TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE | TOKEN_ASSIGN_PRIMARY);
                else if (IAmSystem() && WTSQueryUserToken(session, out IntPtr tok)) t.User = new SafeAccessTokenHandle(tok);
                if (t.User == null)
                {
                    t.Problem = t.Nvda == null ? "NVDA is not running on this PC. Start NVDA, then try again." : "Windows did not let Kova see who runs NVDA.";
                    return t;
                }
            }
            string? nvdaDir = t.Nvda != null ? Path.GetDirectoryName(t.Nvda.Exe) : null;
            if (nvdaDir != null && !IsInstalledCopy(nvdaDir)) t.ConfigDir = Path.Combine(nvdaDir, "userConfig"); // a portable copy keeps its settings beside it
            else
            {
                string? data = UserFolder(t.User, LocalConfig() ? FolderLocalAppData : FolderRoamingAppData);
                if (data == null) t.Problem = "Windows did not say where NVDA keeps its settings.";
                else t.ConfigDir = Path.Combine(data, "nvda");
            }
            return t;
        }

        /// <summary>An installed NVDA: in Program Files, or wherever NVDA's uninstall entry says.</summary>
        private static bool IsInstalledCopy(string dir)
        {
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string pf = Environment.GetFolderPath(root);
                if (pf.Length > 0 && dir.Equals(Path.Combine(pf, "NVDA"), StringComparison.OrdinalIgnoreCase)) return true;
            }
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
                    using var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NVDA");
                    if (k?.GetValue("UninstallDirectory") is string u && u.Trim().TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>NVDA's installer can keep settings in Local AppData instead of Roaming (HKLM\SOFTWARE\NVDA, configInLocalAppData).</summary>
        private static bool LocalConfig()
        {
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
                    using var k = hklm.OpenSubKey(@"SOFTWARE\NVDA");
                    if (k?.GetValue("configInLocalAppData") is int i && i != 0) return true;
                }
                catch { }
            }
            return false;
        }

        private static readonly Guid FolderRoamingAppData = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");
        private static readonly Guid FolderLocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

        private static string? UserFolder(SafeAccessTokenHandle? user, Guid folder)
        {
            Guid f = folder;
            if (SHGetKnownFolderPath(ref f, 0, user?.DangerousGetHandle() ?? IntPtr.Zero, out IntPtr p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        }

        /// <summary>Runs work as the person (impersonating their token) when Kova is privileged; as itself otherwise.</summary>
        private static T AsUser<T>(Target t, Func<T> work) => t.User == null ? work() : WindowsIdentity.RunImpersonated(t.User, work);

        // ---- The state, and doing what the person chose ----

        /// <summary>Where the Kova Helper stands now. server: Kova's pipe, for whether the add-on is talking to it.</summary>
        public static NvdaHelperState GetState(Server? server = null)
        {
            try
            {
                using var t = Locate();
                string? installed = t.ConfigDir is string dir ? AsUser(t, () => InstalledVersion(dir)) : null;
                return new NvdaHelperState(t.ConfigDir, installed, CarriedVersion, t.Nvda != null, server?.Connected == true, t.ConfigDir == null ? t.Problem : null);
            }
            catch (Exception e) { return new NvdaHelperState(null, null, CarriedVersion, false, server?.Connected == true, e.Message); }
        }

        /// <summary>
        /// Installs, updates or removes the Kova Helper for the person at the screen, then restarts their NVDA a few seconds
        /// later (time to say the sentence first). Returns a sentence saying what happened, in plain words. remote: the
        /// sentence is for a controlling PC ("the remote PC's NVDA"). Blocks for a moment (file work only).
        /// </summary>
        public static string Apply(NvdaHelperAction action, bool remote = false, Action<string>? log = null)
        {
            string pc = remote ? "the remote PC" : "this PC";
            string nvda = remote ? "The remote PC's NVDA" : "NVDA";
            if (action == NvdaHelperAction.None) return "";
            byte[]? package = Package();
            string? carried = CarriedVersion;
            if (action != NvdaHelperAction.Remove && (package == null || carried == null)) return "This copy of Kova does not carry the Kova Helper.";
            Target t;
            try { t = Locate(); }
            catch (Exception e) { return "Kova could not find NVDA's settings on " + pc + ": " + e.Message; }
            bool restartOwned = false;
            try
            {
                if (t.ConfigDir == null) return remote ? "On the remote PC: " + LowerFirst(t.Problem ?? "NVDA's settings could not be found.") : t.Problem ?? "Kova could not find NVDA's settings.";
                string dir = t.ConfigDir;
                string? why = AsUser(t, () => action == NvdaHelperAction.Remove ? RemoveFrom(dir) : InstallInto(dir, package!));
                log?.Invoke("NVDA helper: " + action + " in " + dir + ": " + (why ?? "done"));
                if (why != null)
                    return action == NvdaHelperAction.Remove ? "Kova could not remove the Kova Helper from " + pc + ": " + why + "."
                        : "Kova could not install the Kova Helper on " + pc + ": " + why + ".";
                bool running = t.Nvda != null;
                if (running) { RestartSoon(t, log); restartOwned = true; }
                return action switch
                {
                    NvdaHelperAction.Remove => running ? "The Kova Helper is removed. " + nvda + " restarts in a moment without it."
                        : "The Kova Helper is removed. NVDA starts without it next time.",
                    NvdaHelperAction.Update => running ? "The Kova Helper is updated to version " + carried + ". " + nvda + " restarts in a moment to start the new one."
                        : "The Kova Helper is updated to version " + carried + ". NVDA starts the new one next time.",
                    _ => running ? "The Kova Helper is installed. " + nvda + " restarts in a moment to start it."
                        : "The Kova Helper is installed. NVDA starts it the next time NVDA starts.",
                };
            }
            finally { if (!restartOwned) t.Dispose(); }
        }

        private static string LowerFirst(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;

        /// <summary>Tests: set to see the restart instead of doing it.</summary>
        internal static Action<string>? RestartForTest;

        /// <summary>NVDA restarted (nvda.exe -r from the running copy's folder) after 3 seconds, as the person who runs it.</summary>
        private static void RestartSoon(Target t, Action<string>? log)
        {
            var nvda = t.Nvda!;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Thread.Sleep(3000);
                    if (RestartForTest != null) { RestartForTest(nvda.Exe); return; }
                    string args = "-r";
                    if (!t.Privileged)
                    {
                        // NVDA asks Windows for uiAccess, which only the shell's way of starting programs grants.
                        using var p = Process.Start(new ProcessStartInfo(nvda.Exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(nvda.Exe)! });
                    }
                    else StartAs(t.User!, nvda.Exe, args, IAmSystem());
                    log?.Invoke("NVDA helper: restarted NVDA");
                }
                catch (Exception e) { log?.Invoke("NVDA helper: could not restart NVDA: " + e.Message); }
                finally { t.Dispose(); }
            });
        }

        /// <summary>
        /// Starts NVDA with the token of the NVDA already running (it keeps its uiAccess, and never runs as SYSTEM or as
        /// administrator): SYSTEM may hand a token to a new process itself; an administrator goes through Windows' logon service.
        /// </summary>
        private static void StartAs(SafeAccessTokenHandle user, string exe, string args, bool system)
        {
            IntPtr dup = IntPtr.Zero, env = IntPtr.Zero;
            try
            {
                if (!DuplicateTokenEx(user.DangerousGetHandle(), 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2 /* impersonation */, 1 /* primary */, out dup)) throw new Win32Exception();
                CreateEnvironmentBlock(out env, dup, false);
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
                string cmd = "\"" + exe + "\" " + args;
                PROCESS_INFORMATION pi;
                bool ok = system
                    ? CreateProcessAsUserW(dup, exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x00000400 /* unicode environment */, env, Path.GetDirectoryName(exe), ref si, out pi)
                    : CreateProcessWithTokenW(dup, 0, exe, cmd, 0x00000400, env, Path.GetDirectoryName(exe), ref si, out pi);
                if (!ok) throw new Win32Exception();
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
            finally
            {
                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                if (dup != IntPtr.Zero) CloseHandle(dup);
            }
        }

        private static int _updateTried;

        /// <summary>
        /// Call once at start (the window, the service): if NVDA has an older Kova Helper than this Kova carries, it is
        /// updated and NVDA restarted, and say is told in plain words. The pipe calls it too when an older add-on connects.
        /// Once per run; works in the background, never blocks.
        /// </summary>
        public static void UpdateIfNeeded(Action<string>? say, Action<string>? log = null)
        {
            if (Interlocked.Exchange(ref _updateTried, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var s = GetState();
                    if (!s.UpdateAvailable) { Interlocked.Exchange(ref _updateTried, 0); return; } // not installed or up to date: an older add-on connecting later may still ask
                    // The window and the service may both start at once: one does it.
                    using var m = new Mutex(false, @"Global\KovaNvdaHelperUpdate" + Names.Instance);
                    if (!m.WaitOne(0)) return;
                    try
                    {
                        if (!GetState().UpdateAvailable) return;
                        string said = Apply(NvdaHelperAction.Update, false, log);
                        log?.Invoke("NVDA helper: " + said);
                        if (said.StartsWith("The Kova Helper is updated", StringComparison.Ordinal))
                            say?.Invoke("Kova updated its NVDA add-on, the Kova Helper, to version " + CarriedVersion + ". NVDA restarts in a moment.");
                    }
                    finally { m.ReleaseMutex(); }
                }
                catch (Exception e) { log?.Invoke("NVDA helper: update check failed: " + e.Message); }
            });
        }

        // ================= The pipe the add-on talks to =================

        /// <summary>
        /// Kova's end of the Kova Helper's pipe. Raises Event for each thing NVDA did (on the pipe's reading thread, in order)
        /// and ConnectedChanged when the add-on comes or goes, so a controlling PC can be told when it is not there (it then hears NVDA through the sound).
        ///
        /// Frames: u32 length (1-16384), then a body. The first body is a hello (0, u8 length, version in digits and dots);
        /// every later one an NvdaEvent, or a keepalive (the single byte 0). Anything else ends that connection (the add-on reconnects); the pipe itself carries on.
        /// </summary>
        internal sealed class Server : IDisposable
        {
            public const int MaxFrame = 16384;
            private const int MaxConnections = 2;

            private readonly Action<string>? _log;
            private readonly string? _fixedName;
            private readonly Func<int, bool> _trust;
            private readonly Thread _thread;
            private readonly CancellationTokenSource _stop = new();
            private readonly List<NamedPipeServerStream> _active = new();
            private int _helloed;

            public event Action<NvdaEvent>? Event;
            public event Action<bool>? ConnectedChanged;
            /// <summary>The add-on's version, as it said when it connected (null when none is connected).</summary>
            public volatile string? AddonVersion;
            /// <summary>Tests and diagnostics: connections refused (not NVDA), connections ended for bad data, and whether the pipe is up.</summary>
            public int Refused, Dropped;
            public volatile bool Listening;
            public volatile string? ListeningName;

            /// <summary>Whether the add-on is talking to Kova right now.</summary>
            public bool Connected => Volatile.Read(ref _helloed) > 0;

            /// <summary>
            /// pipeName and trustClient are for tests only (a fixed name, and who counts as NVDA); normally the name follows
            /// the session at the screen and only a real NVDA may connect.
            /// </summary>
            public Server(Action<string>? log = null, string? pipeName = null, Func<int, bool>? trustClient = null)
            {
                _log = log;
                _fixedName = pipeName;
                _trust = trustClient ?? IsTrustedNvdaProcess;
                _thread = new Thread(Run) { IsBackground = true, Name = "Kova NVDA helper pipe" };
                _thread.Start();
            }

            public void Dispose()
            {
                _stop.Cancel();
                lock (_active) { foreach (var c in _active) try { c.Dispose(); } catch { } _active.Clear(); }
            }

            private static bool IsTrustedNvdaProcess(int pid) => ImagePath(pid) is string exe && NvdaTrust.IsTrustedNvda(exe);

            private readonly HashSet<string> _said = new();
            private void LogOnce(string key, string message)
            {
                lock (_said) { if (_said.Count > 100 || !_said.Add(key)) return; }
                _log?.Invoke("NVDA helper: " + message);
            }

            private void Run()
            {
                NamedPipeServerStream? listener = null;
                Task? waiting = null;
                string? name = null, sid = null;
                uint session = 0;
                var ct = _stop.Token;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Who may connect: the person signed in at the screen (the service, as SYSTEM, serves them), else this
                        // Kova's own user. Looked at again every few seconds, as the person at the screen can change.
                        session = TargetSession();
                        string? wantSid = PipeUser(session);
                        string? wantName = session == 0xFFFFFFFF ? null : _fixedName ?? PipeName(session);
                        if (wantName != name || wantSid != sid)
                        {
                            Close(ref listener); waiting = null; Listening = false; ListeningName = null;
                            name = wantName; sid = wantSid;
                        }
                        if (name == null || sid == null) { ct.WaitHandle.WaitOne(5000); continue; }
                        if (listener == null)
                        {
                            listener = Create(name, sid, first: true);
                            if (listener == null)
                            {
                                Listening = false;
                                LogOnce("taken " + name, "the pipe " + name + " is already taken by another program, so the Kova Helper cannot reach Kova; trying again every few seconds");
                                ct.WaitHandle.WaitOne(5000);
                                continue;
                            }
                            Listening = true; ListeningName = name;
                        }
                        waiting ??= listener.WaitForConnectionAsync(ct);
                        try { if (!waiting.Wait(5000, ct)) continue; }
                        catch (OperationCanceledException) { break; }
                        catch { Close(ref listener); waiting = null; continue; }
                        var conn = listener;
                        waiting = null;
                        // The next listener first, so the name is never free for another program between connections.
                        listener = Create(name, sid, first: false);
                        uint s = session;
                        ThreadPool.QueueUserWorkItem(_ => Serve(conn, s));
                    }
                    catch (Exception e)
                    {
                        LogOnce("loop " + e.GetType().Name, "the pipe had a problem: " + e.Message);
                        Close(ref listener); waiting = null;
                        ct.WaitHandle.WaitOne(1000);
                    }
                }
                Close(ref listener);
                Listening = false;
            }

            private static void Close(ref NamedPipeServerStream? p) { try { p?.Dispose(); } catch { } p = null; }

            /// <summary>The user allowed on the pipe (as a SID string), or null when there is nobody (nobody signed in).</summary>
            private static string? PipeUser(uint session)
            {
                try
                {
                    if (!IAmSystem()) { using var me = WindowsIdentity.GetCurrent(); return me.User?.Value; }
                    if (session == 0xFFFFFFFF || !WTSQueryUserToken(session, out IntPtr tok)) return null;
                    using var t = new SafeAccessTokenHandle(tok);
                    using var id = new WindowsIdentity(t.DangerousGetHandle());
                    return id.User?.Value;
                }
                catch { return null; }
            }

            /// <summary>
            /// The pipe, open to that user only (and SYSTEM when the service serves them), never over the network. first:
            /// Windows refuses if any program has already made a pipe of this name, so nothing can sit in front of Kova.
            /// </summary>
            private NamedPipeServerStream? Create(string name, string sid, bool first)
            {
                var ps = new PipeSecurity();
                ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                var user = new SecurityIdentifier(sid);
                if (IAmSystem())
                {
                    ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                    // The person may connect and write, not make pipes of this name themselves.
                    ps.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.Read | PipeAccessRights.WriteData | PipeAccessRights.WriteAttributes | PipeAccessRights.Synchronize, AccessControlType.Allow));
                }
                else ps.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow)); // Kova is that user: it makes the next pipe with this right
                try
                {
                    return NamedPipeServerStreamAcl.Create(name, PipeDirection.In, MaxConnections + 2, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0), MaxFrame * 4, 0, ps);
                }
                catch (Exception e)
                {
                    if (!first) LogOnce("next " + e.GetType().Name, "could not make the next pipe: " + e.Message);
                    return null;
                }
            }

            /// <summary>One connection: checked to be NVDA before a single byte is read, then read until it ends or misbehaves.</summary>
            private void Serve(NamedPipeServerStream conn, uint session)
            {
                bool counted = false, added = false;
                try
                {
                    var h = conn.SafePipeHandle;
                    if (!GetNamedPipeClientProcessId(h, out uint pid) || !GetNamedPipeClientSessionId(h, out uint clientSession))
                    { Interlocked.Increment(ref Refused); return; }
                    if ((_fixedName == null && clientSession != session) || !_trust((int)pid))
                    {
                        Interlocked.Increment(ref Refused);
                        LogOnce("refused " + (ImagePath((int)pid) ?? pid.ToString()), "refused a connection from " + (ImagePath((int)pid) ?? "process " + pid) + ", which is not NVDA");
                        return;
                    }
                    lock (_active)
                    {
                        // NVDA itself only ever has one: an older one (NVDA restarting) makes way for the newest.
                        while (_active.Count >= MaxConnections) { try { _active[0].Dispose(); } catch { } _active.RemoveAt(0); }
                        _active.Add(conn);
                        added = true;
                    }
                    var head = new byte[4];
                    var body = new byte[MaxFrame];
                    bool hello = false;
                    while (true)
                    {
                        if (!ReadAll(conn, head, 4)) return;
                        int len = BitConverter.ToInt32(head, 0);
                        if (len < 1 || len > MaxFrame) { Bad("a frame of " + len + " bytes"); return; }
                        if (!ReadAll(conn, body, len)) return;
                        if (!hello)
                        {
                            string? v = Hello(body.AsSpan(0, len));
                            if (v == null) { Bad("no hello"); return; }
                            hello = true;
                            AddonVersion = v;
                            counted = true;
                            if (Interlocked.Increment(ref _helloed) == 1) Raise(true);
                            _log?.Invoke("NVDA helper: the Kova Helper " + v + " is connected");
                            // An older add-on: updated (only from the real pipe; a test pipe must never touch the real NVDA).
                            if (_fixedName == null && CarriedVersion is string carried && CompareVersions(carried, v) > 0) UpdateIfNeeded(null, _log);
                            continue;
                        }
                        if (len == 1 && body[0] == 0) continue; // the add-on's keepalive (sent every 2 seconds while NVDA is quiet)
                        var ev = NvdaEvent.Decode(body.AsSpan(0, len));
                        if (ev == null) { Bad("something it could not read"); return; }
                        try { Event?.Invoke(ev); } catch (Exception e) { LogOnce("handler", "an event handler failed: " + e.Message); }
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
                catch (Exception e) { LogOnce("serve " + e.GetType().Name, "a connection failed: " + e.Message); }
                finally
                {
                    if (added) lock (_active) _active.Remove(conn);
                    try { conn.Dispose(); } catch { }
                    if (counted && Interlocked.Decrement(ref _helloed) == 0) { AddonVersion = null; Raise(false); }
                }
            }

            private void Bad(string what)
            {
                Interlocked.Increment(ref Dropped);
                LogOnce("bad " + what, "ended a connection that sent " + what);
            }

            private void Raise(bool on) { try { ConnectedChanged?.Invoke(on); } catch { } }

            private bool ReadAll(Stream s, byte[] buf, int n)
            {
                int got = 0;
                while (got < n)
                {
                    int r = s.Read(buf, got, n - got);
                    if (r <= 0) return false;
                    got += r;
                }
                return true;
            }

            /// <summary>The version in a hello (0, u8 length, 1-20 digits and dots), or null.</summary>
            internal static string? Hello(ReadOnlySpan<byte> b)
            {
                if (b.Length < 3 || b[0] != 0 || b[1] < 1 || b[1] > 20 || b.Length != 2 + b[1]) return null;
                var sb = new StringBuilder();
                foreach (byte c in b[2..]) { if (!(c is >= (byte)'0' and <= (byte)'9' || c == '.')) return null; sb.Append((char)c); }
                return sb.ToString();
            }
        }

        // ---- Windows ----

        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001, TOKEN_DUPLICATE = 0x0002, TOKEN_IMPERSONATE = 0x0004, TOKEN_QUERY = 0x0008;
        [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
        [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr p, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonation, int type, out IntPtr newToken);
        [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);
        [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
        [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr env);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb; public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithTokenW(IntPtr token, int logonFlags, string app, string cmd, int flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUserW(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, int flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    }
}
