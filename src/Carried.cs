using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TailRemote
{
    /// <summary>How a copy's window shows when it is opened by itself (Carried).</summary>
    internal enum HideMode { None = 0, Tray = 1, Minimized = 2 }

    /// <summary>
    /// What a copy of Kova does by itself when it is opened (4.9): connect to one PC, or host the PC it is opened on.
    /// It travels inside the exe (Carried), so the exe alone is all another PC needs.
    /// </summary>
    internal sealed class LaunchPlan
    {
        /// <summary>True: host the PC it is opened on. False: connect to Address.</summary>
        public bool Host { get; set; }

        /// <summary>Whether the window shows, hides to the notification area, or starts minimised, once it is opened.</summary>
        public HideMode Hide { get; set; }

        // Connecting: as a saved PC has them (an address "@relay:port#tag" means the PC hosting there with this password).
        public string Address { get; set; } = "";
        public int Port { get; set; } = Protocol.DefaultPort;
        public string Name { get; set; } = "";

        // Hosting: as Host this PC asks them.
        public bool Direct { get; set; }
        public bool Relay { get; set; }
        public int HostPort { get; set; } = Protocol.DefaultPort;
        public string RelayAddress { get; set; } = "";
        public int RelayPort { get; set; } = KovaRelayWire.Wire.DefaultPort;
        public string ListenPassword { get; set; } = "";

        /// <summary>The password to connect with, or the one this PC hosts with.</summary>
        public string Password { get; set; } = "";

        /// <summary>The relay as people type it.</summary>
        public string RelayShown => RelayAddress + (RelayPort == KovaRelayWire.Wire.DefaultPort ? "" : ":" + RelayPort);

        /// <summary>The PC it connects to, in words.</summary>
        public string PcShown => Name.Trim().Length > 0 ? Name.Trim()
            : TailRemote.Relay.IsByPassword(Address) ? "the PC on the relay " + TailRemote.Relay.Shown(Address)
            : Port == Protocol.DefaultPort ? Address : Address + ", port " + Port;

        /// <summary>How the window shows, as a sentence ending, or empty when it shows as usual.</summary>
        public string HideShown => Hide switch
        {
            HideMode.Tray => ", and hides to the notification area",
            HideMode.Minimized => ", and starts minimised",
            _ => "",
        };

        /// <summary>What a copy with this plan does when it is opened, as a sentence.</summary>
        public string Does => (!Host ? "connects to " + PcShown
            : "hosts the PC it is opened on, " + (Relay && Direct ? "through the Kova Relay at " + RelayShown + " and directly on port " + HostPort
                : Relay ? "through the Kova Relay at " + RelayShown : "directly on port " + HostPort)) + HideShown;

        /// <summary>Whether it can be used at all (a damaged or hand-made one is ignored rather than half applied).</summary>
        public bool Usable => Password is { Length: > 0 } && Address != null && Name != null && RelayAddress != null && ListenPassword != null
            && (Host ? (Direct || Relay) && HostPort is > 0 and < 65536 && RelayPort is > 0 and < 65536 && (!Relay || RelayAddress.Trim().Length > 0)
                     : Address.Trim().Length > 0 && Port is > 0 and < 65536);
    }

    /// <summary>
    /// A launch plan kept at the very end of the exe itself: the sealed plan, its length (4 bytes), then 8 bytes that
    /// say it is there. Windows and .NET never read past what the exe was built with, so the program runs the same.
    /// The plan holds a password and is only scrambled, not locked: nothing a file carries to any PC by itself can be
    /// (whoever has the file can open it and let it connect). The scrambling keeps the password out of plain sight,
    /// and its check means a cut-off or damaged ending is ignored instead of being read as a plan.
    /// </summary>
    internal static class Carried
    {
        private static readonly byte[] Magic = "KOVAPLN1"u8.ToArray();
        private static readonly byte[] Key = SHA256.HashData("Kova launch plan, scrambled only: not a secret"u8);
        private const int MaxPlan = 64 * 1024;
        private const int Tail = 4 + 8; // the length, then Magic

        private static readonly Lazy<LaunchPlan?> _own = new(() => { try { return Read(Names.OwnExe); } catch { return null; } });

        /// <summary>The plan this running copy carries, or null. Read once.</summary>
        public static LaunchPlan? Own => _own.Value;

        /// <summary>The plan at the end of that exe, or null when it has none (or one that cannot be used).</summary>
        public static LaunchPlan? Read(string exe) => Find(exe).Plan;

        /// <summary>The plan, and how many bytes of the file are the program itself (everything before the plan).</summary>
        private static (LaunchPlan? Plan, long ProgramLength) Find(string exe)
        {
            // The running exe is read while Windows holds it: every kind of sharing is allowed.
            using var f = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long all = f.Length;
            if (all < Tail + 28) return (null, all);
            byte[] tail = new byte[Tail];
            f.Seek(-Tail, SeekOrigin.End);
            f.ReadExactly(tail);
            if (!tail.AsSpan(4).SequenceEqual(Magic)) return (null, all);
            int length = BitConverter.ToInt32(tail, 0);
            if (length < 28 || length > MaxPlan || length + Tail > all) return (null, all);
            byte[] box = new byte[length];
            f.Seek(-Tail - length, SeekOrigin.End);
            f.ReadExactly(box);
            long program = all - Tail - length;
            try
            {
                byte[] plain = new byte[length - 28];
                using (var gcm = new AesGcm(Key, 16)) gcm.Decrypt(box.AsSpan(0, 12), box.AsSpan(28), box.AsSpan(12, 16), plain);
                var plan = JsonSerializer.Deserialize<LaunchPlan>(plain);
                return (plan is { Usable: true } ? plan : null, program);
            }
            // The ending says a plan is there, so it is still not part of the program, even though it cannot be read.
            catch (Exception e) when (e is CryptographicException or JsonException) { return (null, program); }
        }

        private static byte[] Seal(LaunchPlan plan)
        {
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(plan);
            byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], secret = new byte[plain.Length];
            using (var gcm = new AesGcm(Key, 16)) gcm.Encrypt(nonce, plain, secret, tag);
            using var ms = new MemoryStream();
            ms.Write(nonce);
            ms.Write(tag);
            ms.Write(secret);
            ms.Write(BitConverter.GetBytes((int)ms.Length));
            ms.Write(Magic);
            return ms.ToArray();
        }

        /// <summary>
        /// Writes a copy of the program in that exe to another file, carrying that plan (null: carrying none), whatever
        /// the first one carried. Written under a spare name first, so a copy is never left half made.
        /// </summary>
        public static void WriteCopy(string fromExe, string toFile, LaunchPlan? plan)
        {
            long program = Find(fromExe).ProgramLength;
            string part = toFile + ".part";
            try
            {
                using (var src = new FileStream(fromExe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[1 << 16];
                    for (long left = program; left > 0;)
                    {
                        int n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (n <= 0) throw new EndOfStreamException("Kova's own file ended early.");
                        dst.Write(buffer, 0, n);
                        left -= n;
                    }
                    if (plan != null) dst.Write(Seal(plan));
                    dst.Flush(flushToDisk: true);
                }
                File.Move(part, toFile, overwrite: true);
            }
            catch { try { File.Delete(part); } catch { } throw; }
        }

        /// <summary>
        /// An update: the new exe (downloaded and checked, not yet in place) takes on the plan the copy it replaces
        /// carries, so a copy that connects by itself still does after updating. Never throws: an update is not
        /// stopped over it (the problem is written down, and the updated copy opens as plain Kova).
        /// </summary>
        public static void CarryOver(string fromExe, string freshExe)
        {
            try
            {
                if (Read(fromExe) is not LaunchPlan plan) return;
                var (has, program) = Find(freshExe);
                using var f = new FileStream(freshExe, FileMode.Open, FileAccess.Write, FileShare.None);
                if (has != null || program != f.Length) f.SetLength(program);
                f.Seek(0, SeekOrigin.End);
                f.Write(Seal(plan));
                f.Flush(flushToDisk: true);
            }
            catch (Exception e) { DiagLog.Write("the update could not take on what this copy does when opened: " + e.Message); }
        }

        /// <summary>Checked by the self-test: a plan written into a copy of a file is read back the same, replaced, and taken off again.</summary>
        internal static string? SelfTest(string dir)
        {
            string a = Path.Combine(dir, "plan-a.bin"), b = Path.Combine(dir, "plan-b.bin"), c = Path.Combine(dir, "plan-c.bin");
            byte[] program = RandomNumberGenerator.GetBytes(5000);
            File.WriteAllBytes(a, program);
            if (Read(a) != null) return "a file without a plan was read as having one";
            var plan = new LaunchPlan { Host = true, Relay = true, RelayAddress = "relay.example.com", RelayPort = 6968, Password = "passéword", ListenPassword = "listening", Hide = HideMode.Tray };
            WriteCopy(a, b, plan);
            if (Read(b) is not { Host: true, Relay: true, Direct: false, RelayAddress: "relay.example.com", RelayPort: 6968, Password: "passéword", ListenPassword: "listening", Hide: HideMode.Tray })
                return "a hosting plan did not come back as written";
            var other = new LaunchPlan { Address = "office-pc", Port = 47121, Password = "secret1", Name = "Office" };
            WriteCopy(b, c, other); // a copy of a copy: the new plan, not both
            if (Read(c) is not { Host: false, Address: "office-pc", Port: 47121, Password: "secret1", Name: "Office" }) return "a connecting plan did not come back as written";
            if (new FileInfo(c).Length >= new FileInfo(b).Length + 200) return "a copy of a copy kept the old plan as well";
            CarryOver(b, a); // an update: the new file takes on the old one's plan
            if (Read(a) is not { Host: true, RelayPort: 6968 }) return "an update did not take on the plan";
            CarryOver(c, a); // and never two of them
            if (Read(a) is not { Host: false, Address: "office-pc" }) return "an update over a file with a plan did not replace it";
            WriteCopy(a, b, null);
            if (Read(b) != null || !File.ReadAllBytes(b).AsSpan().SequenceEqual(program)) return "taking the plan off did not leave the program as it was";
            byte[] cut = File.ReadAllBytes(c);
            cut[^20] ^= 1; // damage inside the sealed part
            File.WriteAllBytes(c, cut);
            if (Read(c) != null) return "a damaged plan was used";
            if (new LaunchPlan { Host = true, Password = "x" }.Usable) return "a hosting plan with no way in counted as usable";
            return null;
        }
    }
}
