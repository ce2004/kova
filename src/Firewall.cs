using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace TailRemote
{
    /// <summary>
    /// Opens and closes ports in Windows Firewall for Kova. Every rule it
    /// makes is named "Kova port N" (one for TCP, one for UDP), and it only
    /// ever touches rules with that name, so nothing another program set up is
    /// changed. The ports are remembered in the settings, so a port used once
    /// can still be closed after the port setting has moved on.
    /// </summary>
    internal static class Firewall
    {
        public static string RuleName(int port) => Names.FirewallRule(port);

        /// <summary>Whether Kova's rule for this port exists. Needs no administrator rights.</summary>
        public static bool IsOpen(int port) => Run(Netsh, "advfirewall firewall show rule name=\"" + RuleName(port) + "\"") == 0;

        // Always the real one in System32: the elevated copy must never run a netsh.exe that sits next to Kova.exe.
        private static string Netsh => Native.SystemTool("netsh.exe");

        /// <summary>Opens or closes a port through an elevated copy of Kova. False if refused or failed.</summary>
        public static async Task<bool> SetAsync(int port, bool open) => await ChangeAsync(port, open) == ChangeResult.Done;

        /// <summary>Opens or closes a port through an elevated copy of Kova, saying whether permission was refused or the change failed.</summary>
        public static Task<ChangeResult> ChangeAsync(int port, bool open) =>
            Task.Run(() => RunElevated("--firewall " + (open ? "open " : "close ") + port, () => IsOpen(port) == open));

        /// <summary>
        /// Runs an elevated copy of Kova with these arguments and waits for it. Declined: the
        /// administrator prompt was refused, so nothing was tried. Failed: the copy ran but its exit
        /// code or the check afterwards says the change did not happen.
        /// </summary>
        internal static ChangeResult RunElevated(string args, Func<bool> changed)
        {
            int exit;
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
                if (p == null) return ChangeResult.Failed;
                p.WaitForExit();
                exit = p.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { return ChangeResult.Declined; } // ERROR_CANCELLED: No was pressed
            catch { return ChangeResult.Failed; }
            return exit == 0 && changed() ? ChangeResult.Done : ChangeResult.Failed;
        }

        /// <summary>The elevated half.</summary>
        public static int Apply(bool open, int port)
        {
            if (Protocol.PortProblem(port.ToString(), out _) != null) return 1;
            string name = RuleName(port);
            Run(Netsh, "advfirewall firewall delete rule name=\"" + name + "\"");
            if (!open) return 0;
            int tcp = Run(Netsh, "advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow enable=yes profile=any protocol=TCP localport=" + port);
            int udp = Run(Netsh, "advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow enable=yes profile=any protocol=UDP localport=" + port);
            return tcp == 0 && udp == 0 ? 0 : 1;
        }

        private static int Run(string file, string args) => Native.RunHidden(file, args);
    }

    /// <summary>How a change made through an elevated copy of Kova went.</summary>
    internal enum ChangeResult { Done, Declined, Failed }
}
