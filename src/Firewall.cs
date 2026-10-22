using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TailRemote
{
    /// <summary>
    /// Opens and closes ports in Windows Firewall for Kova. Every rule it
    /// makes is named "Kova port N" (one for TCP, one for UDP), and it only
    /// ever touches rules with that name, so nothing another program set up is
    /// changed. The ports are remembered in the settings, so a port used once
    /// can still be closed after the port setting has moved on.
    ///
    /// It also tidies up what piles up over time: Windows makes a rule of its own for every copy of Kova
    /// (or TailRemote, its old name) that ever waited for connections, wherever that copy was, and TailRemote
    /// made "TailRemote port N" rules. Only those are ever removed: rules for a kova or tailremote exe, and
    /// rules with those two names. Never a "Kova port N" rule except by closing that port, and never the
    /// port Kova uses now.
    /// </summary>
    internal static class Firewall
    {
        public static string RuleName(int port) => Names.FirewallRule(port);

        /// <summary>Whether Kova's rule for this port exists. Needs no administrator rights.</summary>
        public static bool IsOpen(int port) => Run(Netsh, "advfirewall firewall show rule name=\"" + RuleName(port) + "\"") == 0;

        // Always the real one in System32: the elevated copy must never run a netsh.exe that sits next to Kova.exe.
        private static string Netsh => Native.SystemTool("netsh.exe");
        private static string PowerShell => Native.SystemTool(@"WindowsPowerShell\v1.0\powershell.exe");

        /// <summary>Opens or closes a port through an elevated copy of Kova. False if refused or failed.</summary>
        public static async Task<bool> SetAsync(int port, bool open) => await ChangeAsync(port, open) == ChangeResult.Done;

        /// <summary>Opens or closes a port through an elevated copy of Kova, saying whether permission was refused or the change failed.</summary>
        public static Task<ChangeResult> ChangeAsync(int port, bool open) =>
            Task.Run(() => RunElevated("--firewall " + (open ? "open " : "close ") + port, () => IsOpen(port) == open));

        /// <summary>Opens the new port and closes the old one, with one administrator prompt.</summary>
        public static Task<ChangeResult> MoveAsync(int newPort, int oldPort) =>
            Task.Run(() => RunElevated("--firewall move " + newPort + " " + oldPort, () => IsOpen(newPort) && !IsOpen(oldPort)));

        /// <summary>Removes the old rules Stale finds (thorough), keeping the ports in use. Checked by scanning again.</summary>
        public static Task<ChangeResult> CleanAsync(IReadOnlyCollection<int> inUse) =>
            Task.Run(() => RunElevated("--firewall clean" + string.Concat(inUse.Select(p => " " + p)),
                () => Scan() is { } after && Stale(after, inUse, thorough: true).Count == 0));

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
                using var p = Process.Start(new ProcessStartInfo(Names.OwnExe, Names.InstanceArgs + args) { UseShellExecute = true, Verb = "runas" });
                if (p == null) return ChangeResult.Failed;
                p.WaitForExit();
                exit = p.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { return ChangeResult.Declined; } // ERROR_CANCELLED: No was pressed
            catch { return ChangeResult.Failed; }
            if (exit == 3) return ChangeResult.InUse;
            return exit == 0 && changed() ? ChangeResult.Done : ChangeResult.Failed;
        }

        /// <summary>The elevated half of --firewall: open P, close P, move NEW OLD, or clean [ports in use...].</summary>
        public static int Command(string[] a)
        {
            static int? Port(string s) => int.TryParse(s, out int p) && Protocol.PortProblem(s, out _) == null ? p : null;
            int result;
            var inUse = new List<int>();
            if (a.Length == 2 && a[0].Equals("open", StringComparison.OrdinalIgnoreCase) && Port(a[1]) is int open) { result = Apply(true, open); inUse.Add(open); }
            else if (a.Length == 2 && a[0].Equals("close", StringComparison.OrdinalIgnoreCase) && Port(a[1]) is int close)
            {
                // Never the port the service hosts on: that would cut off whoever reaches this PC through it.
                // The window may not know it (the service's settings are only for administrators), this half does.
                if (ServicePort() == close) return 3;
                result = Apply(false, close);
            }
            else if (a.Length == 3 && a[0] == "move" && Port(a[1]) is int to && Port(a[2]) is int from && to != from) { result = Apply(true, to); if (result == 0) result = ServicePort() == from ? 3 : Apply(false, from); inUse.Add(to); }
            else if (a.Length >= 1 && a[0] == "list")
            {
                // Tests: what Kova finds, and what each kind of clean-up would remove, changing nothing.
                foreach (string s in a.Skip(1)) if (Port(s) is int p) inUse.Add(p); else return 2;
                var rules = Scan();
                if (rules == null) return 1;
                var safe = Stale(rules, inUse, thorough: false);
                var all = Stale(rules, inUse, thorough: true);
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "kova-firewall.txt"), rules.Select(r =>
                    (all.Contains(r) ? safe.Contains(r) ? "stale  " : "unused " : "keep   ") + r.Name + " | " + (r.Allow ? "allow" : "block") + " | " + r.Program));
                return 0;
            }
            else if (a.Length >= 1 && a[0] == "clean")
            {
                foreach (string s in a.Skip(1)) if (Port(s) is int p) inUse.Add(p); else return 2;
                return Clean(inUse, thorough: true);
            }
            else return 2; // anything else is refused: a mistyped word must never close a port
            // Already running as administrator: whatever is sure to be stale goes too, as Windows piles it up.
            Clean(inUse, thorough: false);
            return result;
        }

        /// <summary>The elevated half: opens or closes one port. An old TailRemote rule for the port goes either way (Kova's own replaces it).</summary>
        public static int Apply(bool open, int port)
        {
            if (Protocol.PortProblem(port.ToString(), out _) != null) return 1;
            string name = RuleName(port);
            // Already open: left exactly as it is. Deleting and adding again would leave the port closed if the add failed.
            if (!(open && IsOpen(port))) Run(Netsh, "advfirewall firewall delete rule name=\"" + name + "\"");
            if (open && !IsOpen(port))
            {
                int tcp = Run(Netsh, "advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow enable=yes profile=any protocol=TCP localport=" + port);
                int udp = Run(Netsh, "advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow enable=yes profile=any protocol=UDP localport=" + port);
                if (tcp != 0 || udp != 0) return 1;
            }
            // Only once Kova's own rule is in: the old one may be what still lets other PCs in.
            Run(Netsh, "advfirewall firewall delete rule name=\"" + Names.OldFirewallRule(port) + "\"");
            return 0;
        }

        // ================= Kova's rules as Windows Firewall has them =================

        /// <summary>One firewall rule that is Kova's: a port rule by name, or a rule Windows made for a Kova exe.</summary>
        internal sealed record Rule(string Id, string Name, string Program, bool Allow, bool Inbound)
        {
            /// <summary>The port of a "Kova port N" or "TailRemote port N" rule (of any instance); null for a rule for an exe.</summary>
            public int? Port => PortOf(Name, "Kova port ") ?? PortOf(Name, "TailRemote port ");
            /// <summary>A "TailRemote port N" rule.</summary>
            public bool Old => Name.StartsWith("TailRemote port ", StringComparison.Ordinal) && Port != null;
            /// <summary>A port rule of this instance (a test copy's carry its word; the real one's carry none). Every exe rule counts.</summary>
            public bool Ours => Port is not int p || Name == Names.FirewallRule(p) || Name == Names.OldFirewallRule(p);
            public string Exe => Environment.ExpandEnvironmentVariables(Program);
            private static int? PortOf(string name, string prefix)
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;
                var rest = name.AsSpan(prefix.Length);
                int digits = 0;
                while (digits < rest.Length && char.IsAsciiDigit(rest[digits])) digits++;
                return digits > 0 && int.TryParse(rest[..digits], out int p) ? p : null;
            }
        }

        // Rules from every profile, in one pass over each kind of filter: asking rule by rule takes minutes
        // on a PC with hundreds of rules. Read only; needs no administrator rights.
        private const string ScanScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$apps = @{}; foreach ($f in Get-NetFirewallApplicationFilter -All) { $apps[$f.InstanceID] = $f.Program }
$out = @()
foreach ($r in Get-NetFirewallRule -All) {
  $p = [string]$apps[$r.Name]; $n = [string]$r.DisplayName
  if ($p -eq 'Any') { $p = '' }
  if ($n -match '^(Kova|TailRemote) port \d+( [A-Za-z0-9]+)?$' -or ($p.Length -gt 0 -and [IO.Path]::GetFileName($p) -match '^(kova|tailremote)(-(x64|arm64))?\.exe$')) {
    $out += [pscustomobject]@{ Id = [string]$r.Name; Name = $n; Program = $p; Allow = ([string]$r.Action -eq 'Allow'); Inbound = ([string]$r.Direction -eq 'Inbound') }
  }
}
'KOVA-RULES ' + (ConvertTo-Json -InputObject $out -Compress)
";

        /// <summary>Every rule that is Kova's or TailRemote's, or null if Windows could not be asked.</summary>
        public static List<Rule>? Scan()
        {
            string? output = RunScript(ScanScript);
            int at = output?.IndexOf("KOVA-RULES ", StringComparison.Ordinal) ?? -1;
            if (at < 0) return null;
            try
            {
                using var doc = JsonDocument.Parse(output![(at + 11)..].Trim());
                var rules = new List<Rule>();
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    string Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    bool Flag(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
                    var rule = new Rule(Text("Id"), Text("Name"), Text("Program"), Flag("Allow"), Flag("Inbound"));
                    if (rule.Ours) rules.Add(rule); // another instance's port rules are never seen here, so never touched
                }
                return rules;
            }
            catch { return null; }
        }

        /// <summary>
        /// The rules that are no longer needed. Always: rules for a Kova or TailRemote exe that is not there any
        /// more (test copies, old download and build folders), rules that block this very copy, and a "TailRemote
        /// port N" next to a "Kova port N".
        /// thorough (Clean up old firewall rules, asked first): also rules for copies other than this one, the
        /// service's and those running now, rules that block Kova, and old TailRemote port rules for ports not
        /// in use. Kova's own port rules never: those go only when their port is closed.
        /// </summary>
        public static List<Rule> Stale(List<Rule> rules, IReadOnlyCollection<int> inUse, bool thorough)
        {
            var keep = thorough ? CopiesInUse() : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stale = new List<Rule>();
            foreach (var r in rules)
            {
                if (r.Port is int port)
                {
                    if (!r.Old) continue;
                    bool kovaToo = rules.Any(k => !k.Old && k.Port == port);
                    if (kovaToo || (thorough && !inUse.Contains(port))) stale.Add(r);
                    continue;
                }
                string exe = r.Exe;
                bool gone = Gone(r.Program);
                // A rule that blocks this very copy goes every time: Windows makes one when nobody answers its
                // question (a locked PC, an unattended one), and a block beats Kova's own rule, so the port Kova
                // opens would let nobody in.
                bool blocksMe = !r.Allow && r.Inbound && ServiceHost.SameFile(exe, Names.OwnExe);
                if (gone || blocksMe || (thorough && (!keep.Contains(Full(exe)) || (!r.Allow && r.Inbound)))) stale.Add(r);
            }
            return stale;
        }

        /// <summary>
        /// Surely not there any more: only a plain path on this PC's own fixed drive that does not exist. A path with
        /// %VARIABLES% (they expand differently for the administrator), on a network or mapped drive (the elevated
        /// copy may not see it) or a USB stick (it may only be out) is never counted as gone.
        /// </summary>
        private static bool Gone(string program)
        {
            try
            {
                if (program.Contains('%') || program.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(program)) return false;
                if (Path.GetPathRoot(program) is not { Length: > 0 } root || new DriveInfo(root).DriveType != DriveType.Fixed) return false;
                return !File.Exists(program);
            }
            catch { return false; }
        }

        private static string Full(string path)
        {
            try { return Path.GetFullPath(path); } catch { return path; }
        }

        /// <summary>This copy, the service's, and every Kova copy running now: their rules stay.</summary>
        private static HashSet<string> CopiesInUse()
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Names.OwnExe };
            if (ServiceHost.ServiceExe() is string service) keep.Add(service);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (!p.ProcessName.StartsWith("kova", StringComparison.OrdinalIgnoreCase) && !p.ProcessName.StartsWith("tailremote", StringComparison.OrdinalIgnoreCase)) continue;
                        if (p.MainModule?.FileName is string f) keep.Add(Full(f));
                    }
                    catch { } // another account's, or gone already
                }
            }
            return keep;
        }

        /// <summary>The elevated half of clean-up: removes what Stale finds, after giving each port in use Kova's own rule if only an old one let it in.</summary>
        public static int Clean(IReadOnlyCollection<int> inUse, bool thorough)
        {
            var keepPorts = new List<int>(inUse);
            if (ServicePort() is int servicePort) keepPorts.Add(servicePort); // the service's port is in use too
            var rules = Scan();
            if (rules == null) return 1;
            if (thorough)
            {
                bool changed = false;
                foreach (int port in keepPorts.Distinct())
                    if (rules.Any(r => r.Old && r.Port == port) && !rules.Any(r => !r.Old && r.Port == port)) { Apply(true, port); changed = true; }
                if (changed && (rules = Scan()) == null) return 1;
            }
            var stale = Stale(rules, keepPorts, thorough);
            if (stale.Count == 0) return 0;
            // By each rule's own id: names repeat (every copy's rule is called kova.exe), ids never do. An id holds the
            // exe's whole path, which anyone can name with quotes or brackets, so the ids are never written into the
            // script: they go in as base64 and are compared exactly, never as a pattern.
            string ids = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", stale.Select(r => r.Id))));
            string? said = RunScript("$ErrorActionPreference = 'Continue'\n" +
                "$ids = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + ids + "')) -split \"`n\"\n" +
                "Get-NetFirewallRule -All | Where-Object { $ids -ccontains [string]$_.Name } | Remove-NetFirewallRule\n" +
                "'KOVA-DONE'");
            return said?.Contains("KOVA-DONE") == true ? 0 : 1;
        }

        /// <summary>The port the installed service hosts on, or null. Elevated only: its settings are for administrators.</summary>
        private static int? ServicePort() => ServiceHost.IsInstalled() && ServiceHost.LoadConfig() is { } cfg ? cfg.Port : null;

        /// <summary>Runs a PowerShell script with no window and returns what it printed, or null if it would not run.</summary>
        private static string? RunScript(string script)
        {
            try
            {
                // Printed as UTF-8: Windows PowerShell otherwise prints in the old code page, and a folder named
                // in another alphabet came back as question marks, so its rule looked like one for a missing copy.
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" + script));
                using var p = Process.Start(new ProcessStartInfo(PowerShell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
                    { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 })!;
                var errors = p.StandardError.ReadToEndAsync();
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(120_000)) { try { p.Kill(); } catch { } return null; }
                errors.Wait();
                return output;
            }
            catch { return null; }
        }

        private static int Run(string file, string args) => Native.RunHidden(file, args);
    }

    /// <summary>How a change made through an elevated copy of Kova went.</summary>
    /// <summary>InUse: refused, because it would close the port the Kova service hosts on.</summary>
    internal enum ChangeResult { Done, Declined, Failed, InUse }
}
