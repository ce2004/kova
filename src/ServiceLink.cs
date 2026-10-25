using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// When the service hosts, the TailRemote window is not the host, yet it is the one
    /// running as the signed-in user, on their clipboard and in their files. This pipe joins
    /// the two: the window's Send the clipboard, Send files and Stop go out through the
    /// service's connections, and what the controlling PC sends (clipboard text, files, and how
    /// transfers are going) comes to the window, which puts it on the clipboard exactly as
    /// when it hosts itself.
    ///
    /// The service runs as SYSTEM, so it opens files to send as the window's user (the pipe
    /// tells it who that is): nobody can send a file they could not open themselves.
    /// </summary>
    internal static class ServiceLink
    {
        private static string PipeName => Names.FilesPipe;
        // Window to agent:
        private const byte SendText = (byte)'t', SendClipFiles = (byte)'f', SendDownloads = (byte)'d', CancelAll = (byte)'x', ReceiveIn = (byte)'o';
        // Agent to window:
        private const byte GotText = (byte)'T', GotFiles = (byte)'F', Progress = (byte)'P', Controller = (byte)'H';

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint session);

        /// <summary>Test only (--chaostest): the test itself plays the agent, so its pipe is not SYSTEM's.</summary>
        internal static bool TestAnyOwner;

        private static void Write(Stream s, byte type, byte[] payload)
        {
            byte[] m = new byte[5 + payload.Length];
            m[0] = type;
            BitConverter.TryWriteBytes(m.AsSpan(1), payload.Length);
            payload.CopyTo(m, 5);
            s.Write(m);
            s.Flush();
        }

        private static (byte Type, byte[] Payload) Read(Stream s)
        {
            Span<byte> head = stackalloc byte[5];
            Protocol.ReadExactly(s, head);
            int n = BitConverter.ToInt32(head[1..]);
            if (n < 0 || n > 600 << 20) throw new InvalidDataException("a message too big");
            byte[] p = new byte[n];
            Protocol.ReadExactly(s, p);
            return (head[0], p);
        }

        private static byte[] Paths(IReadOnlyList<string> paths) => Encoding.UTF8.GetBytes(string.Join("\n", paths));
        private static string[] Paths(byte[] p) => Encoding.UTF8.GetString(p).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        private static byte[] Pack(FileChannel.Transfer t)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(t.Serial);
            w.Write(t.Outgoing); w.Write(t.Finished); w.Write(t.Failed); w.Write(t.Clipboard); w.Write(t.Cancelled); w.Write(t.Waiting);
            w.Write(t.What); w.Write(t.Peer); w.Write(t.Done); w.Write(t.Total); w.Write(t.BytesPerSecond);
            w.Write(t.Result ?? ""); w.Write(t.Result != null);
            var files = t.Files;
            w.Write(files.Count);
            foreach (var (name, done, total) in files) { w.Write(name); w.Write(done); w.Write(total); }
            return ms.ToArray();
        }

        // ================= The agent's side =================

        public sealed class Server
        {
            private readonly Host _host;
            private readonly List<Window> _windows = new();

            /// <summary>
            /// A connected window, with the session it was in when it linked up. After switching user
            /// the old session's window stays connected (its user is only switched away), and must not
            /// keep getting what arrives for the new user's clipboard and files.
            /// </summary>
            private sealed class Window
            {
                public readonly BlockingCollection<(byte, byte[])> Outbox = new(256);
                public readonly NamedPipeServerStream Pipe;
                public readonly uint Session;
                public Window(NamedPipeServerStream pipe, uint session) { Pipe = pipe; Session = session; }
            }

            private readonly Func<uint> _session;

            /// <summary>session: the session whose TailRemote window may link up (the one at the screen); this process's own if not given.</summary>
            public Server(Host host, Func<uint>? session = null)
            {
                _host = host;
                uint mine = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
                _session = session ?? (() => mine);
                new Thread(AcceptLoop) { IsBackground = true, Name = "Kova service link" }.Start();
            }

            /// <summary>Hands something to every connected window; false if none is connected (the agent then does it itself).</summary>
            public bool Forward(byte type, byte[] payload, bool mayDrop = false)
            {
                Window[] windows;
                lock (_windows) windows = _windows.ToArray();
                uint now = _session();
                int handed = 0;
                // Outside the lock, and never long: a window that has stopped reading must not hold
                // up a transfer (this runs on the file lanes' threads).
                foreach (var w in windows)
                {
                    // A window of a session no longer at the screen (switched away from) is closed, not
                    // told: what arrives is for whoever is at the screen now. Its Serve tidies up.
                    if (w.Session != now) { try { w.Pipe.Dispose(); } catch { } continue; }
                    handed++;
                    try { if (!w.Outbox.TryAdd((type, payload)) && !mayDrop) w.Outbox.TryAdd((type, payload), 250); } catch { } // a window that has just gone
                }
                return handed > 0;
            }

            public bool Text(string text) => Forward(GotText, Encoding.UTF8.GetBytes(text));
            public bool Files(string[] paths) => Forward(GotFiles, Paths(paths));
            public void Transfer(FileChannel.Transfer t) => Forward(Progress, Pack(t), mayDrop: !t.Finished);

            /// <summary>The window's choice of where received files go (null: the usual place). Set by the service.</summary>
            public Action<string?>? FolderChosen;

            /// <summary>
            /// The window's user picked a folder for received files. The service writes there as SYSTEM, so
            /// it is only taken if that user could write there themselves: nobody can use it to put files
            /// where they could not.
            /// </summary>
            private void ChooseFolder(string folder, WindowsIdentity? user)
            {
                if (folder.Length == 0) { FolderChosen?.Invoke(null); return; }
                bool allowed = false;
                if (user != null)
                    WindowsIdentity.RunImpersonated(user.AccessToken, () =>
                    {
                        try
                        {
                            string probe = Path.Combine(folder, ".tailremote-" + Guid.NewGuid().ToString("N"));
                            File.WriteAllBytes(probe, Array.Empty<byte>());
                            File.Delete(probe);
                            allowed = true;
                        }
                        catch { }
                    });
                if (allowed) FolderChosen?.Invoke(folder);
                else ServiceHost.Log("Service link: " + user?.Name + " cannot write to " + folder + ", so received files stay where they were.");
            }

            private void AcceptLoop()
            {
                // SYSTEM, and whoever is signed in at the PC itself: nobody over the network.
                var sec = new PipeSecurity();
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                sec.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                // Owned by SYSTEM, which the window checks: nobody else can pose as the service.
                bool asSystem;
                using (var me = WindowsIdentity.GetCurrent()) asSystem = me.IsSystem;
                if (asSystem) sec.SetOwner(system);
                bool first = true, warned = false, warnedOwner = false;
                // The session last refused, said once: a window left open in a session switched away from asks
                // again every 2 seconds, and each refusal used to be written down, filling the log within hours.
                uint refused = uint.MaxValue;
                while (true)
                {
                    // Claimed before the attempt, win or lose (as AgentLink does): if something squats on the
                    // name at the first try, every retry demanding FirstPipeInstance would fail for as long as
                    // the service runs, even after the squatter is gone. The window still checks that the pipe
                    // is owned by SYSTEM, so a squatter can never pose as the service.
                    bool useFirst = first;
                    first = false;
                    NamedPipeServerStream? pipe = null;
                    try
                    {
                        // Asynchronous: one thread reads while another writes. On a plain pipe Windows makes a
                        // waiting read hold up every write, and nothing ever reached the window. The first
                        // one must be the first of its name: if another program made it first, it is not ours.
                        pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous | (useFirst ? PipeOptions.FirstPipeInstance : 0), 0, 0, sec);
                        // A later instance takes the rules of the name's first one, whoever made it. Not ours
                        // (a program squatted on the name first): its maker could connect here posing as a
                        // window. Let it go and only take a pipe that really is the first of its name again.
                        if (asSystem && !OwnedBySystem(pipe))
                        {
                            pipe.Dispose();
                            if (!warnedOwner) ServiceHost.Log("Service link: another program holds the pipe " + PipeName + "; waiting for it to go.");
                            warnedOwner = true;
                            first = true;
                            Thread.Sleep(1000);
                            continue;
                        }
                        pipe.WaitForConnection();
                        // Only a window in this session (the one at the screen): never another account
                        // that is also signed in, which would otherwise hear what arrives for the clipboard.
                        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint session) || session != _session())
                        {
                            if (session != refused) ServiceHost.Log("Service link: refused a Kova window from another session (" + session + ").");
                            refused = session;
                            pipe.Dispose();
                            continue;
                        }
                        refused = uint.MaxValue;
                        var window = new Window(pipe, session);
                        new Thread(() => Serve(window)) { IsBackground = true, Name = "Kova service link window" }.Start();
                        pipe = null; // Serve's now: it lets it go
                    }
                    catch (Exception e)
                    {
                        // A pipe that never got its window (one that connected and went at once makes the wait
                        // throw) is let go: kept, each held one of the name's 16 places for good, and once they
                        // were all held no Kova window could link up until the service restarted.
                        try { pipe?.Dispose(); } catch { }
                        if (!warned) ServiceHost.Log("Service link: " + e.Message + (useFirst ? " Another program may hold the pipe " + PipeName + "." : ""));
                        warned = true; // said once, not every second
                        Thread.Sleep(1000);
                    }
                }
            }

            /// <summary>Whether a pipe (either end) is owned by SYSTEM, as the service's own pipes are.</summary>
            internal static bool OwnedBySystem(PipeStream pipe)
            {
                try { return (pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true; }
                catch { return false; }
            }

            private void Serve(Window window)
            {
                var pipe = window.Pipe;
                var outbox = window.Outbox;
                WindowsIdentity? user = null;
                try
                {
                    // Who the window runs as: files it sends are opened as that user, never as SYSTEM.
                    pipe.RunAsClient(() => user = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Impersonate | TokenAccessLevels.Duplicate));
                    // Only the user signed in at the screen (4.4): another account started in the same session (runas)
                    // would otherwise hear what arrives for that user's clipboard and choose where received files go.
                    // (Asking Windows who that is needs the service's rights; where it cannot say, as in tests, the
                    // session check above stands alone.)
                    using (var console = NativeService.ConsoleUser())
                        if (console?.User != null && user?.User != null && console.User != user.User)
                        {
                            ServiceHost.Log("Service link: refused a Kova window running as " + user.Name + ", not the user signed in at the screen.");
                            return;
                        }
                    lock (_windows) _windows.Add(window);
                    ServiceHost.Log("Service link: the Kova window of " + user?.Name + " is connected.");
                    new Thread(() =>
                    {
                        try
                        {
                            string? had = null;
                            while (pipe.IsConnected)
                            {
                                if (outbox.TryTake(out var m, 1000)) Write(pipe, m.Item1, m.Item2);
                                // Whether anyone is controlling, and how many PCs control and listen (the window's title).
                                var (controlling, listening) = _host.Connected;
                                byte[] state = new byte[5];
                                state[0] = (byte)(_host.HasController ? 1 : 0);
                                BitConverter.TryWriteBytes(state.AsSpan(1), (ushort)controlling);
                                BitConverter.TryWriteBytes(state.AsSpan(3), (ushort)listening);
                                string key = Convert.ToHexString(state);
                                if (key != had) { had = key; Write(pipe, Controller, state); }
                            }
                        }
                        catch { }
                        try { pipe.Dispose(); } catch { }
                    }) { IsBackground = true, Name = "Kova service link out" }.Start();
                    while (pipe.IsConnected)
                    {
                        var (type, p) = Read(pipe);
                        // Switched away from: this window's user is no longer at the screen, so nothing
                        // more is taken from it either. It links up again when they come back.
                        if (window.Session != _session()) break;
                        // Each send gets a copy of its own: the files go out after this window may have gone,
                        // and this one is let go when it does.
                        WindowsIdentity? AsUser() => user == null ? null : new WindowsIdentity(user.AccessToken.DangerousGetHandle());
                        switch (type)
                        {
                            case SendText: { string text = Encoding.UTF8.GetString(p); _host.SendClipboard(text); break; }
                            case SendClipFiles: _host.SendClipboardFiles(Paths(p), AsUser()); break; // the host lets the copy go when the sends are done
                            case SendDownloads: _host.SendFiles(Paths(p), AsUser()); break;
                            case CancelAll: _host.CancelTransfer(); break;
                            case ReceiveIn: ChooseFolder(Encoding.UTF8.GetString(p), user); break;
                        }
                    }
                }
                catch { }
                finally
                {
                    bool anyLeft;
                    lock (_windows)
                    {
                        _windows.Remove(window);
                        uint now = _session();
                        anyLeft = _windows.Exists(w => w.Session == now);
                    }
                    // The folder this window chose is its user's: with no window of the user at the screen
                    // left, received files go back to the usual place, never on into someone's folder.
                    if (!anyLeft) FolderChosen?.Invoke(null);
                    outbox.CompleteAdding();
                    outbox.Dispose();
                    try { pipe.Dispose(); } catch { }
                    user?.Dispose();
                }
            }
        }

        // ================= The window's side =================

        public sealed class Client : IDisposable
        {
            private volatile NamedPipeClientStream? _pipe;
            private volatile bool _stop;
            private readonly object _writeGate = new();
            private readonly Dictionary<int, FileChannel.Transfer> _transfers = new();

            public event Action<string>? TextArrived;
            public event Action<string[]>? FilesArrived;
            public event Action<FileChannel.Transfer>? TransferProgress;

            public bool Connected => _pipe?.IsConnected == true;
            public bool HasController { get; private set; }
            /// <summary>How many PCs control and listen to the service's host.</summary>
            public (int Controlling, int Listening) Counts { get; private set; }

            public Client() => new Thread(Loop) { IsBackground = true, Name = "Kova service link" }.Start();

            public void Dispose()
            {
                _stop = true;
                try { _pipe?.Dispose(); } catch { }
            }

            private volatile string _folder = "";

            /// <summary>Where the service should save received files (empty: the usual Downloads, TailRemote). Told to it now, and on every connection.</summary>
            public string ReceiveFolder
            {
                get => _folder;
                set { _folder = value ?? ""; Send(ReceiveIn, Encoding.UTF8.GetBytes(_folder)); }
            }

            public void SendClipboard(string text) => Send(SendText, Encoding.UTF8.GetBytes(text));
            public void SendClipboardFiles(IReadOnlyList<string> paths) => Send(SendClipFiles, Paths(paths));
            public void SendFiles(IReadOnlyList<string> paths) => Send(SendDownloads, Paths(paths));
            public void CancelTransfer() => Send(CancelAll, Array.Empty<byte>());

            private void Send(byte type, byte[] payload)
            {
                var pipe = _pipe;
                if (pipe == null) return;
                // Off the window's thread: big clipboard text takes a moment to go through.
                ThreadPool.QueueUserWorkItem(_ => { try { lock (_writeGate) Write(pipe, type, payload); } catch { } });
            }

            /// <summary>Keeps connected to the agent for as long as the window wants it (every 2 seconds while it is not there).</summary>
            private void Loop()
            {
                while (!_stop)
                {
                    try
                    {
                        // Impersonation allowed: the agent opens the files this user sends as this user.
                        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                        pipe.Connect(2000);
                        // Really the service's agent: its pipe is owned by SYSTEM (or administrators). A
                        // program of an ordinary user that made a pipe of this name first would get the
                        // files and clipboard this window sends, and could put anything on this clipboard.
                        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                        if (!TestAnyOwner && owner?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true && owner?.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) != true)
                        {
                            pipe.Dispose();
                            Thread.Sleep(10_000);
                            continue;
                        }
                        _pipe = pipe;
                        // Always, even when empty (the usual place): the service may have restarted since, or
                        // still hold a folder from before, which must not outlive this window's choice.
                        Send(ReceiveIn, Encoding.UTF8.GetBytes(_folder));
                        while (!_stop)
                        {
                            var (type, p) = Read(pipe);
                            switch (type)
                            {
                                case GotText: TextArrived?.Invoke(Encoding.UTF8.GetString(p)); break;
                                case GotFiles: FilesArrived?.Invoke(Paths(p)); break;
                                case Controller when p.Length == 5:
                                    HasController = p[0] == 1;
                                    Counts = (BitConverter.ToUInt16(p, 1), BitConverter.ToUInt16(p, 3));
                                    break;
                                case Progress: TransferProgress?.Invoke(Unpack(p)); break;
                            }
                        }
                    }
                    catch { }
                    try { _pipe?.Dispose(); } catch { }
                    _pipe = null;
                    HasController = false;
                    if (!_stop) Thread.Sleep(2000);
                }
            }

            /// <summary>The same transfer is the same object each time, as the window expects.</summary>
            private FileChannel.Transfer Unpack(byte[] p)
            {
                var r = new BinaryReader(new MemoryStream(p));
                int serial = r.ReadInt32();
                if (!_transfers.TryGetValue(serial, out var t))
                {
                    // Only ever ones still going are kept (finished ones leave below), so this is a safety
                    // net: forgetting one still going would leave it on the window's Files line for good.
                    if (_transfers.Count > 256) _transfers.Clear();
                    _transfers[serial] = t = new FileChannel.Transfer();
                }
                t.Outgoing = r.ReadBoolean(); t.Finished = r.ReadBoolean(); t.Failed = r.ReadBoolean(); t.Clipboard = r.ReadBoolean(); t.Cancelled = r.ReadBoolean(); t.Waiting = r.ReadBoolean();
                t.What = r.ReadString(); t.Peer = r.ReadString(); t.Done = r.ReadInt64(); t.Total = r.ReadInt64(); t.BytesPerSecond = r.ReadDouble();
                string result = r.ReadString();
                t.Result = r.ReadBoolean() ? result : null;
                int n = r.ReadInt32();
                var files = new (string, long, long)[n];
                for (int i = 0; i < n; i++) files[i] = (r.ReadString(), r.ReadInt64(), r.ReadInt64());
                t.Files = files;
                if (t.Finished) _transfers.Remove(serial);
                return t;
            }
        }
    }
}
