namespace TailRemote
{
    /// <summary>Kova's firewall profile (Firewall.cs and PortEditorForm.cs are shared with Kova Relay, which has its own).</summary>
    internal static class KovaFirewall
    {
        public static FirewallApp Profile => new()
        {
            Name = "Kova",
            RuleName = Names.FirewallRule,
            RulePrefix = "Kova port ",
            OldRuleName = Names.OldFirewallRule,
            OldRulePrefix = "TailRemote port ",
            OldName = "TailRemote",
            RuleNamePattern = @"^(Kova|TailRemote) port \d+( [A-Za-z0-9]+)?$",
            ExeNamePattern = @"^(kova|tailremote)(-(x64|arm64))?\.exe$",
            ProcessNames = new[] { "kova", "tailremote" },
            OwnExe = Names.OwnExe,
            ElevatedArgsPrefix = Names.InstanceArgs,
            ServiceExe = ServiceHost.ServiceExe,
            PortInUse = () => ServiceHost.IsInstalled() && ServiceHost.LoadConfig() is { } cfg ? cfg.Port : null,
            PortProblem = s => Protocol.PortProblem(s, out _),
            ListFile = "kova-firewall.txt",
        };

        public static PortBook Book(Settings settings) => new()
        {
            Known = () => settings.KnownPorts,
            Remember = port => Remember(settings, port),
            Forget = port => Forget(settings, port),
            SaveProblem = () => settings.SaveProblem,
            Speak = (text, interrupt) => Speech.Speak(text, interrupt),
            ChangePortHint = "change Port in the main window first",
        };

        /// <summary>Adds a port to the record of ports Kova has used. False if that could not be saved (Settings.SaveProblem says why).</summary>
        public static bool Remember(Settings settings, int port)
        {
            if (settings.KnownPorts.Contains(port)) return true;
            settings.KnownPorts.Add(port);
            try { return settings.Save(retries: 1); } catch { return false; } // on the window's thread: not frozen for long
        }

        /// <summary>Takes a closed port off the record. False if that could not be saved.</summary>
        public static bool Forget(Settings settings, int port)
        {
            if (!settings.KnownPorts.Remove(port)) return true;
            try { return settings.Save(retries: 1); } catch { return false; }
        }
    }
}
