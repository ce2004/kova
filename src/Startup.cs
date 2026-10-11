using System;
using System.Diagnostics;
using System.Security.Principal;

namespace TailRemote
{
    /// <summary>
    /// Start hosting at sign-in through a scheduled task that runs with highest
    /// privileges (so keys reach administrator windows), plus a firewall rule.
    /// Changing either needs administrator rights, so the change is made by a
    /// second, elevated copy of Kova run with --startup on or off.
    /// </summary>
    internal static class Startup
    {
        /// <summary>
        /// Text for inside a PowerShell single-quoted string. PowerShell also ends one at the curly quotes
        /// (an account called O’Neil), so each kind of quote is doubled, not only the plain one.
        /// </summary>
        private static string Quoted(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text, "['\u2018\u2019\u201A\u201B]", "$0$0");

        private const string TaskName = Names.Task;
        // The catch-all rule older versions made. Removed here; Port editor manages ports now.
        private const string RuleName = "TailRemote";

        public static bool IsElevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public static bool IsEnabled() => Run(Schtasks, "/Query /TN \"" + TaskName + "\"") == 0;

        // Always Windows' own, from System32: the elevated copy must never run a schtasks.exe or
        // powershell.exe that sits next to Kova.exe (in Downloads, say).
        private static string Schtasks => Native.SystemTool("schtasks.exe");
        private static string Netsh => Native.SystemTool("netsh.exe");
        private static string PowerShell => System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        /// <summary>Asks for administrator rights and makes the change. False if refused or failed.</summary>
        public static async System.Threading.Tasks.Task<bool> SetAsync(bool on) => await ChangeAsync(on) == ChangeResult.Done;

        /// <summary>
        /// Asks for administrator rights and makes the change, saying whether permission was refused
        /// or the change failed. The task is made for the account signed in here, passed along:
        /// when a standard user types an administrator's password, the elevated copy runs as that
        /// administrator, and the task used to be made for them instead.
        /// </summary>
        public static System.Threading.Tasks.Task<ChangeResult> ChangeAsync(bool on)
        {
            string user = WindowsIdentity.GetCurrent().Name;
            return System.Threading.Tasks.Task.Run(() =>
                Firewall.RunElevated("--startup " + (on ? "on \"" + user + "\"" : "off"), () => IsEnabled() == on));
        }

        /// <summary>
        /// The account's name as Windows spells it, or null if there is no such account. Going
        /// through its SID also means nothing odd from the command line reaches PowerShell.
        /// </summary>
        internal static string? Account(string name)
        {
            try
            {
                var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
                string real = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
                return real.IndexOfAny(new[] { '"', '\r', '\n' }) < 0 ? real : null;
            }
            catch { return null; }
        }

        /// <summary>The elevated half for the account running it (the service's own calls).</summary>
        public static int Apply(bool on) => Apply(on, null);

        /// <summary>The elevated half: runs in the copy started by ChangeAsync. user: the account to start for, or null for this one.</summary>
        public static int Apply(bool on, string? user)
        {
            string exe = Environment.ProcessPath!;
            string? account = null;
            if (on && user != null && (account = Account(user)) == null) return 1; // never a task for a mistyped or unknown name
            Run(Netsh, "advfirewall firewall delete rule name=\"" + RuleName + "\"");
            if (!on)
            {
                Run(Schtasks, "/Delete /TN \"" + TaskName + "\" /F");
                return 0;
            }
            // Not schtasks /Create: its tasks stop after 3 days and refuse to start on battery.
            string ps = (account != null ? "$u = '" + Quoted(account) + "'; " : "$u = [Security.Principal.WindowsIdentity]::GetCurrent().Name; ") +
                "$a = New-ScheduledTaskAction -Execute '" + Quoted(exe) + "' -Argument '--host'; " +
                "$t = New-ScheduledTaskTrigger -AtLogOn -User $u; " +
                "$p = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Highest; " +
                "$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew; " +
                "Register-ScheduledTask -TaskName '" + TaskName + "' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null";
            return Run(PowerShell, "-NoProfile -NonInteractive -Command \"" + ps + "\"");
        }

        private static int Run(string file, string args) => Native.RunHidden(file, args);
    }
}
