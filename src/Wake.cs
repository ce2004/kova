using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TailRemote
{
    /// <summary>
    /// Waking a sleeping or switched-off PC with Wake-on-LAN: a "magic packet" (its network card's address
    /// sixteen times) broadcast on its own network. The host tells controlling PCs its network cards while
    /// connected, and they keep that with the saved PC. A broadcast only reaches the network it is sent on,
    /// so a PC away from home asks the PCs it is connected to (any of them on the sleeping PC's network)
    /// to send it there too.
    /// </summary>
    internal static class Wake
    {
        /// <summary>
        /// This PC's wired and Wi-Fi cards that are up with an IPv4 address, one per line: "AABBCCDDEEFF 192.168.1.20/24".
        /// Virtual ones (Tailscale, VPNs, Hyper-V) are left out: nothing can wake a PC through them.
        /// </summary>
        public static string Describe()
        {
            var lines = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) continue;
                    string desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
                    if (desc.Contains("virtual") || desc.Contains("tailscale") || desc.Contains("vpn") || desc.Contains("hyper-v") || desc.Contains("vethernet") || desc.Contains("loopback")) continue;
                    byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
                    if (mac.Length != 6 || mac.All(b => b == 0)) continue;
                    foreach (var a in nic.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                            lines.Add(Convert.ToHexString(mac) + " " + a.Address + "/" + a.PrefixLength);
                }
            }
            catch { }
            return string.Join("\n", lines.Distinct());
        }

        /// <summary>The cards in a Describe text: each address, and the broadcast address of its network (or null).</summary>
        public static List<(byte[] Mac, IPAddress? Broadcast)> Parse(string? text)
        {
            var list = new List<(byte[], IPAddress?)>();
            foreach (string line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                byte[] mac;
                try { mac = Convert.FromHexString(parts[0]); } catch { continue; }
                if (mac.Length != 6) continue;
                IPAddress? broadcast = null;
                if (parts.Length > 1 && parts[1].Split('/') is [string ip, string bits] && IPAddress.TryParse(ip, out var addr)
                    && int.TryParse(bits, out int prefix) && prefix is >= 8 and <= 30 && addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    uint a = BitConverter.ToUInt32(addr.GetAddressBytes().Reverse().ToArray(), 0);
                    uint hostBits = prefix == 32 ? 0 : uint.MaxValue >> prefix;
                    broadcast = new IPAddress(BitConverter.GetBytes(a | hostBits).Reverse().ToArray());
                }
                list.Add((mac, broadcast));
            }
            return list;
        }

        /// <summary>Six bytes of 255, then the card's address sixteen times.</summary>
        public static byte[] MagicPacket(byte[] mac)
        {
            byte[] p = new byte[102];
            for (int i = 0; i < 6; i++) p[i] = 0xFF;
            for (int i = 0; i < 16; i++) Buffer.BlockCopy(mac, 0, p, 6 + i * 6, 6);
            return p;
        }

        /// <summary>
        /// Sends the packet everywhere it might be heard on this PC's networks: everyone (255.255.255.255) and
        /// each of this PC's own networks' broadcast addresses, plus any given, on the two usual ports, three
        /// times (one packet can be lost). Returns how many were sent.
        /// </summary>
        public static int Send(byte[] mac, IEnumerable<IPAddress?> also)
        {
            byte[] packet = MagicPacket(mac);
            var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
            foreach (var b in also) if (b != null) targets.Add(b);
            foreach (var (_, b) in Parse(Describe())) if (b != null) targets.Add(b);
            int sent = 0;
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            for (int round = 0; round < 3; round++)
                foreach (var t in targets)
                    foreach (int port in new[] { 9, 7 })
                        try { udp.Send(packet, packet.Length, new IPEndPoint(t, port)); sent++; } catch { }
            return sent;
        }
    }
}
