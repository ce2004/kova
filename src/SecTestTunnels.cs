using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Security tests (--securitytest Tunnels): Reach a port on the remote PC. Only a PC with the full password, still
    /// connected, with a lane proven by its session key, reaches the port its proof covers; the host holds a bounded number.
    /// Each check runs the real host and controller in this process over loopback; null when every one passes.
    /// </summary>
    internal static class SecTestTunnels
    {
        private const int Port = 47371;

        internal static string? Run()
        {
            int delay = Host.WrongPasswordDelayMs;
            Host.WrongPasswordDelayMs = 0;
            string pipes = Names.TestPipes;
            Names.TestPipes = pipes + "SecTunnels";
            var locked = Host.TestLocked;
            Host.TestLocked = false; // this PC's own console may be locked; check 4d locks it on purpose
            try { return Checks(); }
            catch (Exception e) { return "Tunnels: the test itself failed: " + e; }
            finally
            {
                Host.TestLocked = locked;
                Host.WrongPasswordDelayMs = delay;
                Names.TestPipes = pipes;
            }
        }

        private static Player Silent() => new(Player.NoDevice, _ => { });

        /// <summary>What a tunnel lane got from the host: its first message's code, or -1 when the host closed it without a word.</summary>
        private static int Answer((TcpClient Tcp, SecureLink Link)? lane)
        {
            if (lane == null) return -1;
            var (tcp, link) = lane.Value;
            using (tcp)
            using (link)
            {
                try
                {
                    var s = tcp.GetStream();
                    s.ReadTimeout = 6000; // longer than the host's wait for a lane's session (2 s)
                    byte[] m = link.Receive(s);
                    return m.Length >= 2 && m[0] == Protocol.TunnelOpened ? m[1] : -2;
                }
                catch { return -1; }
            }
        }

        private static string? Checks()
        {
            using var echo = new TunnelTest.Echo();
            using var host = new Host(Port, "secret-password", "listen-password", _ => { });
            var ctrl = Client.Connect("127.0.0.1", Port, "secret-password", Silent(), _ => { });
            try
            {
                using var lis = Client.Connect("127.0.0.1", Port, "listen-password", Silent(), _ => { });
                for (int i = 0; i < 100 && (ctrl.HostVersion == null || lis.HostVersion == null); i++) Thread.Sleep(20);
                if (!ctrl.CanTunnel) return "the host did not offer tunnels to a controller";

                // The real thing works, so the refusals below mean something.
                if (Answer(ctrl.TestTunnelLane(echo.Port, null)) != Protocol.TunnelOk) return "a proper tunnel lane was not carried";

                // 1. Listen-only: never offered, and a lane on its session (proven with its own key) is closed without a word.
                if (lis.CanTunnel) return "a listen-only connection was offered tunnels";
                int before = Volatile.Read(ref echo.Accepted);
                if (Answer(lis.TestTunnelLane(echo.Port, null)) != -1) return "a listen-only connection's tunnel lane was answered";
                if (Volatile.Read(ref echo.Accepted) != before) return "a listen-only connection reached a port on the host";

                // 2. A wrong lane proof.
                if (Answer(ctrl.TestTunnelLane(echo.Port, (a, _) => a[Protocol.LaneProofAt + 3] ^= 1)) != -1) return "a tunnel lane with a wrong proof was answered";
                // 3. The port changed on the way (after the proof was made): refused, so nobody in between can point it elsewhere.
                int other = echo.Port == 65535 ? echo.Port - 1 : echo.Port + 1;
                if (Answer(ctrl.TestTunnelLane(echo.Port, (a, _) => BitConverter.TryWriteBytes(a.AsSpan(12, 2), (ushort)other))) != -1) return "a tunnel lane whose port was changed on the way was answered";
                // 4. Something after the port, with a proof that matches it (only the session's own PC could make one):
                // still refused, as the field carries the port and nothing else.
                byte[] key = ctrl.TestLaneKey;
                if (Answer(ctrl.TestTunnelLane(echo.Port, (a, nonce) =>
                {
                    a[20] = 1;
                    Protocol.LaneProof(key, nonce, a.AsSpan(12, 16), a.AsSpan(28, 16), Protocol.TunnelProofLabel).CopyTo(a, Protocol.LaneProofAt);
                })) != -1) return "a tunnel lane with something after its port was answered";
                if (Volatile.Read(ref echo.Accepted) != before) return "a refused tunnel lane still reached the port (" + (echo.Accepted - before) + ")";
                // 4b. The same done properly (the field untouched, the proof made again) is carried: check 4 was refused for the field alone.
                if (Answer(ctrl.TestTunnelLane(echo.Port, (a, nonce) => Protocol.LaneProof(key, nonce, a.AsSpan(12, 16), a.AsSpan(28, 16), Protocol.TunnelProofLabel).CopyTo(a, Protocol.LaneProofAt))) != Protocol.TunnelOk)
                    return "a tunnel lane with its proof made again was not carried (check 4 proves nothing)";
                before = Volatile.Read(ref echo.Accepted);

                // 4c. Relabelled on the way (a relay can change the magic and mend the check): a file lane's proof on a tunnel
                // lane is refused, and a tunnel lane turned into a file lane leaves the PC's file channel alone.
                if (Answer(ctrl.TestTunnelLane(echo.Port, (a, nonce) => Protocol.LaneProof(key, nonce, a.AsSpan(12, 16), a.AsSpan(28, 16)).CopyTo(a, Protocol.LaneProofAt))) != -1)
                    return "a tunnel lane with a file lane's proof was answered";
                TunnelTest.Until(() => host.ControllerFiles != null, 3000);
                var filesBefore = host.ControllerFiles;
                var relabelled = ctrl.TestTunnelLane(echo.Port, (a, _) => Protocol.FileMagic.CopyTo(a, 0));
                Thread.Sleep(2500); // past the host's wait for a lane's session
                if (relabelled is { } r) { r.Tcp.Dispose(); r.Link.Dispose(); }
                if (filesBefore != null && host.ControllerFiles != filesBefore) return "a tunnel lane relabelled as a file lane replaced the PC's file channel";
                if (Volatile.Read(ref echo.Accepted) != before) return "a refused tunnel lane still reached the port (" + (echo.Accepted - before) + ")";
                // 4d. A locked PC still carries ports (the owner's choice), unlike Get files.
                Host.TestLocked = true;
                try { if (Answer(ctrl.TestTunnelLane(echo.Port, null)) != Protocol.TunnelOk) return "a tunnel lane was refused while the remote PC was locked"; }
                finally { Host.TestLocked = false; }
                before = Volatile.Read(ref echo.Accepted);
                // 5. Port 0 is no port.
                if (ctrl.TestTunnelLane(0, null) != null) return "a tunnel lane to port 0 was even tried";
                if (Answer(ctrl.TestTunnelLane(echo.Port, (a, nonce) => { a[12] = a[13] = 0; Protocol.LaneProof(key, nonce, a.AsSpan(12, 16), a.AsSpan(28, 16), Protocol.TunnelProofLabel).CopyTo(a, Protocol.LaneProofAt); })) != -1)
                    return "a properly proven tunnel lane to port 0 was answered";
                if (Volatile.Read(ref echo.Accepted) != before) return "a refused tunnel lane still reached the port (" + (echo.Accepted - before) + ")";

                // 6. Bounded: past MaxTunnelsPerSession at once, refused with words; all of them close when the session ends.
                var held = new System.Collections.Generic.List<(TcpClient Tcp, SecureLink Link)>();
                if (!TunnelTest.Until(() => host.TestTunnels == 0, 5000)) return "the host still counted " + host.TestTunnels + " tunnel connections after the ones above closed";
                try
                {
                    for (int i = 0; i < Protocol.MaxTunnelsPerSession; i++)
                    {
                        var lane = ctrl.TestTunnelLane(echo.Port, null);
                        if (lane == null) return "tunnel lane " + (i + 1) + " could not be opened";
                        var s = lane.Value.Tcp.GetStream();
                        s.ReadTimeout = 6000;
                        byte[] m = lane.Value.Link.Receive(s);
                        if (m.Length < 2 || m[1] != Protocol.TunnelOk) return "tunnel lane " + (i + 1) + " of " + Protocol.MaxTunnelsPerSession + " was refused";
                        held.Add(lane.Value);
                    }
                    if (host.TestTunnels != Protocol.MaxTunnelsPerSession) return "the host counted " + host.TestTunnels + " tunnel connections, not " + Protocol.MaxTunnelsPerSession;
                    if (Answer(ctrl.TestTunnelLane(echo.Port, null)) != Protocol.TunnelTooMany) return "a tunnel lane past the limit was not refused as too many";
                    int ended = Volatile.Read(ref echo.Ended);
                    ctrl.Dispose(); // the session ends
                    if (!TunnelTest.Until(() => host.TestTunnels == 0, 15_000)) return "the host still holds " + host.TestTunnels + " tunnel connections after the controller's session ended";
                    if (!TunnelTest.Until(() => Volatile.Read(ref echo.Ended) - ended >= Protocol.MaxTunnelsPerSession, 5000)) return "connections to the host's port stayed open after the session ended";
                    foreach (var (tcp, link) in held)
                    {
                        try
                        {
                            var s = tcp.GetStream();
                            s.ReadTimeout = 3000;
                            link.Receive(s);
                            return "a tunnel lane carried on after its session ended";
                        }
                        catch { }
                    }
                }
                finally { foreach (var (tcp, link) in held) { tcp.Dispose(); link.Dispose(); } }

                // 7. After the session ended: its token and key open nothing.
                before = Volatile.Read(ref echo.Accepted);
                if (Answer(ctrl.TestTunnelLane(echo.Port, null)) != -1) return "a tunnel lane was answered after its session ended";
                if (Volatile.Read(ref echo.Accepted) != before) return "a tunnel lane reached the port after its session ended";

                // 8. The host's end only ever dials this PC itself: there is no address on the wire to choose another.
                using (var probe = TunnelPipe.ConnectHere(echo.Port))
                    if (probe == null || !System.Net.IPAddress.IsLoopback(((System.Net.IPEndPoint)probe.RemoteEndPoint!).Address)) return "the host's end did not dial this PC's own loopback";
                return null;
            }
            finally { ctrl.Dispose(); }
        }
    }
}
