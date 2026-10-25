using System;
using System.IO;

namespace TailRemote
{
    /// <summary>
    /// What Kova is called in Windows: its service, pipes, folders, firewall rules and sign-in task.
    /// Up to 2.2 the app was TailRemote, under those names; none of them is looked for any more, so a
    /// PC is moved from TailRemote to Kova by hand (see CHANGES.txt for 2.3.0).
    /// </summary>
    internal static class Names
    {
        /// <summary>
        /// KOVA_INSTANCE (tests only): a word added to the service, its folders, pipes and sign-in task, so a
        /// test service can be installed and removed on a PC whose real Kova service must never be touched.
        /// The test service gets the same word in its own environment (ServiceHost.Register), and its agent
        /// inherits it. Letters and digits only, up to 16; empty for the real thing.
        /// </summary>
        public static readonly string Instance = CleanInstance(Environment.GetEnvironmentVariable("KOVA_INSTANCE"));

        /// <summary>
        /// A test switch from the environment, or null when this copy runs as administrator or SYSTEM (4.4): an elevated
        /// process's environment comes partly from HKCU\Environment, which any program the user runs can write, so a
        /// switch there must never steer an elevated copy (where its settings come from, what it would install).
        /// </summary>
        public static string? TestSwitch(string name)
        {
            try
            {
                using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (me.IsSystem || new System.Security.Principal.WindowsPrincipal(me).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return null;
            }
            catch { return null; }
            return Environment.GetEnvironmentVariable(name);
        }

        private static string CleanInstance(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // Never quietly the real thing: a word that is set but not usable (a dash, a space, too long) still
            // makes a test instance, of its letters and digits, so nothing can act on the real service by mistake.
            var kept = new System.Text.StringBuilder();
            foreach (char c in s) if (char.IsAsciiLetterOrDigit(c) && kept.Length < 16) kept.Append(c);
            return kept.Length > 0 ? kept.ToString() : "Test";
        }

        /// <summary>
        /// Put in front of the arguments of every elevated copy: Windows starts those with a fresh environment,
        /// so a test copy's elevated half would otherwise have changed the real service. Program.Main takes it.
        /// </summary>
        public static string InstanceArgs => Instance.Length > 0 ? "--instance " + Instance + " " : "";

        public static readonly string Service = "KovaHost" + Instance;

        /// <summary>
        /// This copy's own file. When something else (the service, or another copy) has updated the same file while
        /// this one runs, the running file was renamed to name.old or name.old-..., and Windows may then report that
        /// name. Kova's file is the one without it: that is what gets updated, registered and started.
        /// </summary>
        public static string OwnExe
        {
            get
            {
                string p = Path.GetFullPath(Environment.ProcessPath!);
                int at = p.LastIndexOf(".exe.old", StringComparison.OrdinalIgnoreCase);
                if (at > 0 && p.IndexOf('\\', at) < 0) { string real = p[..(at + 4)]; if (File.Exists(real)) return real; }
                return p;
            }
        }

        private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        /// <summary>Where the service's copy went up to 4.1.5. Now only used when Kova was run from somewhere a service cannot keep using (a network drive, a USB stick, the temp folder), and for services installed there before.</summary>
        public static string InstallDir => Path.Combine(ProgramFiles, "Kova" + Instance);
        public static string InstalledExe => Path.Combine(InstallDir, "Kova.exe");
        public static string DataDir => Path.Combine(ProgramData, "Kova" + Instance);

        public static string FilesPipe => "KovaFiles" + TestPipes;   // the window's link to the service
        public static string AgentPipe => "KovaAgent" + TestPipes;   // the service's link to its agent
        public static string UpdatePipe => "KovaUpdate" + TestPipes; // the window's nudge to update the service

        /// <summary>The chaos test only: its pipes get this added, so on a PC with a real Kova service it never meets that one.</summary>
        internal static string TestPipes = Instance;

        // A test instance's rules carry its word too, so a test can never open, close or clean up a real rule.
        private static string RuleWord => Instance.Length > 0 ? " " + Instance : "";
        public static string FirewallRule(int port) => "Kova port " + port + RuleWord;

        /// <summary>What TailRemote (Kova before 2.3.0) called its port rules. Only ever removed, never made.</summary>
        public static string OldFirewallRule(int port) => "TailRemote port " + port + RuleWord;

        public static readonly string Task = "Kova Host" + Instance;

        /// <summary>This user's settings.</summary>
        public static string UserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kova" + Instance);

        /// <summary>This user's local, throwaway files (the clipboard's holding folder, build leftovers).</summary>
        public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kova" + Instance);
    }
}
