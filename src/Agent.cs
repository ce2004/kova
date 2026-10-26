using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace TailRemote
{
    /// <summary>
    /// Hosting as the Windows service, in two halves.
    ///
    /// The service itself (SYSTEM, session 0) hosts: connections, the sound, files, Control Alt
    /// Delete. It starts with Windows, before the sign-in screen, so the controlling PC can be
    /// connected and hear the startup sound, and the connection carries on through signing in,
    /// out, and switching user.
    ///
    /// "TailRemote.exe --agent" runs as SYSTEM in whichever session is at the screen (the service
    /// keeps one there). It types the keys the service hands it, on whichever desktop has the
    /// keyboard (the lock screen and UAC prompts too), and puts on the clipboard what arrives when
    /// no TailRemote window is open to do it.
    /// </summary>
    internal static class Agent
    {
        // ================= In the service =================

        private static Host? _host;
        private static volatile bool _stopping;
        private static System.Threading.Timer? _folders;

        /// <summary>Relay only, and a relay is filled in: otherwise the service hosts directly, or nobody could ever reach it.</summary>
        private static bool RelayOnlyUsable(ServiceHost.Config cfg) => Relay.OnlyUsable(cfg.RelayOnly, cfg.RelayHosting, cfg.RelayAddress, cfg.RelayPort);

        /// <summary>Starts hosting from the service. Retries until it can (the network may not be up yet at boot); never throws.</summary>
        public static void StartHosting()
        {
            var cfg = ServiceHost.LoadConfig();
            DiagLog.Enabled = cfg?.Logging == true;
            if (cfg == null) { ServiceHost.Log("The service's settings are missing or damaged, so it is not hosting. Turn the service off and on again in Kova to fix it."); return; }
            // Read once, before the retries below: a password that cannot be read (settings copied from
            // another PC, or damaged) will never become readable, and retrying it forever hosted nothing
            // and said so only once, in words that sounded like the network.
            // Tried for a minute first: early at boot Windows' protection of saved secrets may not be ready yet,
            // and giving up at once would leave the PC unreachable until someone restarted the service.
            string password = "", listenPassword = "";
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    password = ServiceHost.Config.Open(cfg.PasswordEnc);
                    listenPassword = ServiceHost.Config.Open(cfg.ListenPasswordEnc);
                    break;
                }
                catch (Exception e)
                {
                    if (attempt < 30 && !_stopping) { Thread.Sleep(2000); continue; }
                    ServiceHost.Log("The service's saved passwords could not be read (" + e.Message + "), so it is not hosting. Turn the service off and on again in Kova to fix it.");
                    return;
                }
            }
            if (password.Length == 0) { ServiceHost.Log("The service has no password saved, so it is not hosting. Turn the service off and on again in Kova to fix it."); return; }
            ChooseFolders();
            _folders = new System.Threading.Timer(_ => ChooseFolders(), null, 5000, 5000);
            var agent = new AgentLink.Server();
            Native.KeySink = agent.Key; // keys are typed by the agent, at the screen
            Native.MouseSink = agent.Mouse; // and the mouse moved there
            string? lastError = null;
            // The same lasting identity every start (handed over by the window): controlling PCs know this PC by it.
            var identity = ServiceHost.LoadOrMakeIdentity(cfg);
            while (!_stopping)
            {
                try
                {
                    _host = new Host(cfg.Port, password, listenPassword, ServiceHost.Log,
                        string.IsNullOrEmpty(cfg.CaptureDevice) ? null : cfg.CaptureDevice, identity, relayOnly: RelayOnlyUsable(cfg))
                    {
                        SecureAttention = SendSecureAttention,
                        FileUser = NativeService.ConsoleUser, // file tools with the signed-in user's rights, never SYSTEM's
                        MicDevice = cfg.MicDevice,
                    };
                    break;
                }
                catch (Exception e)
                {
                    if (e.Message != lastError) ServiceHost.Log("Not hosting yet: " + e.Message + " Trying again.");
                    lastError = e.Message;
                    Thread.Sleep(500);
                }
            }
            if (_host == null) return;
            // The Kova Helper NVDA add-on of whoever is signed in talks to the service directly (its pipe follows the
            // session at the screen), so the controlling PC gets that NVDA's speech, braille, beeps and sounds.
            _host.StartNvdaHelper();
            NvdaHelper.UpdateIfNeeded(null, ServiceHost.Log);
            // Also reachable through a Kova Relay, when the window set one up and handed it over.
            // 4.6: on the relay under the code made from the password (no names), as the window does.
            if (cfg.RelayHosting && !string.IsNullOrWhiteSpace(cfg.RelayAddress) && password.Length > 0
                && Relay.Parse(Relay.Channel(password) + "@" + cfg.RelayAddress.Trim(), cfg.RelayPort, out _) is Relay.Target relay)
                _host.UseRelay(relay, listenPassword.Length == 0 ? null
                    : Relay.Parse(Relay.Channel(listenPassword) + "@" + cfg.RelayAddress.Trim(), cfg.RelayPort, out _));
            // The screen is the agent's to see (the service is in session 0): pictures come from it.
            _host.VideoUpdate = agent.Video;
            _host.VideoForget = agent.ForgetVideo;
            var host = _host;
            // The TailRemote window, when open, does the clipboard and sends files through the service
            // (ServiceLink), as the signed-in user; it must be in the session at the screen. Without a
            // window, what the controlling PC sends goes onto the clipboard through the agent.
            var link = new ServiceLink.Server(_host, NativeService.WTSGetActiveConsoleSessionId)
            {
                FolderChosen = folder => { _windowFolderSession = NativeService.WTSGetActiveConsoleSessionId(); _windowFolder = folder; ChooseFolders(); }, // picked in the window, checked as its user
            };
            _host.ClipboardReceived += text => { if (!link.Text(text)) agent.Text(text); };
            _host.ClipboardFilesReceived += paths => { if (!link.Files(paths)) agent.Files(paths); };
            _host.TransferProgress += t => link.Transfer(t);
            _host.UpdateRequested += ServiceHost.UpdateFor;
            // A controller pulled this PC's clipboard. The service runs in session 0 and cannot read a
            // user's clipboard, so the agent at the screen reads it and sends it back, and the service
            // fans it out to the controllers exactly as Send the clipboard does. The files are opened as the
            // signed-in user, never as SYSTEM: a list on the clipboard could name files that user may not
            // read. Nobody signed in: nothing is sent (the host lets the identity go when the sends are done).
            _host.ClipboardRequested += () => agent.GetClipboard();
            agent.ClipboardText = text => _host?.SendClipboard(text);
            agent.ClipboardFiles = paths => { if (NativeService.ConsoleUser() is { } user) _host?.SendClipboardFiles(paths, user); };
            ServiceHost.Log("Hosting on port " + cfg.Port + ", from the service.");
            ServiceHost.HostingStarted(); // only now can a new version count as good
            // Audio settings and device names are never touched here: only Set up audio device, when pressed.
        }

        public static void StopHosting(byte why = Protocol.LeavingStopped)
        {
            _stopping = true;
            _host?.Leave(why);
            _host?.Dispose();
        }

        /// <summary>The service is about to restart into a new version: the connected PCs wait for it quietly.</summary>
        public static void LeaveForUpdate(string version) => _host?.Leave(Protocol.LeavingUpdating, version);

        /// <summary>Only a service may send Control Alt Delete; this is the service.</summary>
        private static bool SendSecureAttention()
        {
            try { NativeService.SendSAS(false); ServiceHost.Log("Sent Control Alt Delete."); return true; }
            catch { return false; }
        }

        /// <summary>Received files go into the folders of whoever is signed in at the screen (Public before anyone is).</summary>
        private static void ChooseFolders()
        {
            uint session = NativeService.WTSGetActiveConsoleSessionId();
            // A folder the window chose belongs to the user who chose it: once someone else is at the
            // screen (switched user, signed out), their files must not go on landing in it.
            if (_windowFolder != null && session != _windowFolderSession) _windowFolder = null;
            string? profile = NativeService.UserProfile(session);
            string root = profile ?? Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public");
            // The signed-in user's own folders, wherever they really are (Downloads moved to another
            // drive or OneDrive): the service runs as SYSTEM, whose own folders are deep inside Windows
            // and which the user's Explorer could not paste from.
            string local = (profile != null ? NativeService.UserFolder(session, NativeService.FolderLocalAppData) : null) ?? Path.Combine(root, "AppData", "Local");
            string downloads = (profile != null ? NativeService.UserFolder(session, NativeService.FolderDownloads) : null) ?? Path.Combine(root, "Downloads");
            FileChannel.StagingOverride = Path.Combine(local, "Kova", "Clipboard");
            FileChannel.DownloadsOverride = _windowFolder ?? Path.Combine(downloads, "Kova");
        }

        /// <summary>Where the TailRemote window says received files go; null: the signed-in user's Downloads\TailRemote.</summary>
        private static volatile string? _windowFolder;
        /// <summary>The session at the screen when the window chose _windowFolder.</summary>
        private static uint _windowFolderSession = uint.MaxValue;

        // ================= In the session at the screen (--agent) =================

        /// <summary>servicePid: the service that started this agent; the agent ends when it does. 0: not watched.</summary>
        public static int Run(int servicePid = 0)
        {
            // Tied to the service that started it. A service that crashed or was killed used to leave its
            // agent behind, which then fought the next service's agent for the pipe (the newest one to
            // connect takes it), so keys went now to one, now to the other.
            if (servicePid != 0)
            {
                System.Diagnostics.Process service;
                try
                {
                    service = System.Diagnostics.Process.GetProcessById(servicePid);
                    // Started after this agent: the service is gone and its number was given to another process.
                    using var me = System.Diagnostics.Process.GetCurrentProcess();
                    if (service.StartTime > me.StartTime) return 1;
                }
                catch { return 1; } // already gone
                new Thread(() =>
                {
                    try { service.WaitForExit(); } catch { }
                    Environment.Exit(0);
                }) { IsBackground = true, Name = "Kova agent watches the service" }.Start();
            }
            // Keys go to whichever desktop has the keyboard: the lock screen, sign-in and UAC prompts too.
            Native.FollowInputDesktop = true;
            var clip = new ClipboardSetter();
            var reader = new ClipboardReader();
            // The remote screen is taken here, at the screen: the service in session 0 cannot see it. Any of
            // the PC's screens, as each viewer chooses.
            var screens = new ScreenSource();
            AgentLink.Run((vk, scan, up, ext) => Native.SendKey(vk, scan, up, ext), (kind, a, b) => Native.SendMouse(kind, a, b), clip.Arrived, clip.FilesArrived, reader.Read,
                new ScreenVideo(screens.Take, screens.Screens, screens.Idle)); // Idle: nothing of the screen is held while nobody watches
            return 0;
        }

        /// <summary>
        /// Puts what arrives onto this PC's clipboard when no TailRemote window is open to do it.
        /// On a thread of its own, set up for the clipboard (STA): the main thread is not, and every
        /// clipboard call made there used to fail without a word (nothing could be pasted).
        /// </summary>
        private sealed class ClipboardSetter
        {
            private readonly System.Collections.Concurrent.BlockingCollection<Action> _jobs = new(16);

            public ClipboardSetter()
            {
                var t = new Thread(() => { foreach (var job in _jobs.GetConsumingEnumerable()) try { job(); } catch { } })
                    { IsBackground = true, Name = "Kova agent clipboard" };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }

            // Nothing never replaces what is on the clipboard here: an empty one sent from the other PC wiped this one.
            public void Arrived(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                _jobs.TryAdd(() => Set(new DataObject(DataFormats.UnicodeText, text)));
            }

            public void FilesArrived(string[] paths) => _jobs.TryAdd(() =>
            {
                if (paths.Length == 0) return;
                var list = new System.Collections.Specialized.StringCollection();
                list.AddRange(paths);
                var data = new DataObject();
                data.SetFileDropList(list);
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(2))); // paste moves them out of the holding folder: never twice the space
                Set(data);
            });

            private static void Set(DataObject data)
            {
                for (int i = 0; ; i++)
                {
                    try { Clipboard.SetDataObject(data, true, 2, 50); return; }
                    catch (Exception e)
                    {
                        if (i == 4) { ServiceHost.Log("Could not put it on the clipboard: " + e.Message); return; }
                        Thread.Sleep(100); // another program has it open
                    }
                }
            }
        }

        /// <summary>
        /// The mirror of ClipboardSetter: reads this PC's clipboard when a controller pulls it and no
        /// TailRemote window is open to do it. On a thread of its own, set up for the clipboard (STA),
        /// for the same reason the setter is. Only text and real files are sent (what Send the clipboard
        /// sends); anything else, or an empty clipboard, sends nothing, so the controller's stays as it was.
        /// </summary>
        private sealed class ClipboardReader
        {
            private readonly System.Collections.Concurrent.BlockingCollection<Action> _jobs = new(16);

            public ClipboardReader()
            {
                var t = new Thread(() => { foreach (var job in _jobs.GetConsumingEnumerable()) try { job(); } catch { } })
                    { IsBackground = true, Name = "Kova agent clipboard read" };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }

            public void Read(Action<string> onText, Action<string[]> onFiles) => _jobs.TryAdd(() =>
            {
                for (int i = 0; ; i++)
                {
                    try
                    {
                        if (Clipboard.ContainsFileDropList())
                        {
                            var list = Clipboard.GetFileDropList();
                            var files = new string[list.Count];
                            list.CopyTo(files, 0);
                            // Files cut and pasted elsewhere stay listed though they are gone: drop those,
                            // the same as Send the clipboard, so nothing arrives as an empty set.
                            files = Array.FindAll(files, f => File.Exists(f) || Directory.Exists(f));
                            if (files.Length > 0) onFiles(files);
                        }
                        else if (Clipboard.ContainsText())
                        {
                            string text = Clipboard.GetText();
                            // Skip over-size text, the same cap as Send the clipboard (SendClipboard): too much to carry.
                            if (!string.IsNullOrEmpty(text) && !string.IsNullOrWhiteSpace(text) && (long)text.Length * 3 <= FileChannel.MaxText) onText(text);
                        }
                        return;
                    }
                    catch (Exception e)
                    {
                        if (i == 4) { ServiceHost.Log("Could not read the clipboard to send it: " + e.Message); return; }
                        Thread.Sleep(100); // another program has it open
                    }
                }
            });
        }
    }
}
