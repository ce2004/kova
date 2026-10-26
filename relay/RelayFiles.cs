using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace KovaRelay
{
    /// <summary>The relay's settings: the port it listens on.</summary>
    internal sealed record RelaySettings(int Port)
    {
        public static RelaySettings Default => new(RelayFiles.DefaultPort);
    }

    /// <summary>
    /// The relay's settings file (4.4.1: nothing beside the exe any more).
    ///
    /// settings.json (the port) is in ProgramData\KovaRelay, the same file for the tray and the service. Only
    /// administrators may write it (RelayService.SecureDataDir), so the tray changes it through an elevated copy of
    /// itself, and a user without administrator rights can never change the port the service listens on. Until an
    /// administrator first saves it, the relay uses port 47200. Older versions also wrote AllowNewHostNames there; it
    /// is ignored when read and no longer written.
    ///
    /// The relay keeps no list of host names any more: a name belongs to whichever host holds it right now. A hosts.txt
    /// left by an older version is simply ignored.
    ///
    /// 4.4.0 kept port.txt and relay-settings.txt next to the exe: port.txt is read until moved
    /// (RelayService --settings-migrate), and then both are deleted.
    /// </summary>
    internal sealed class RelayFiles
    {
        public const int DefaultPort = 47200;

        public string SettingsFolder { get; }
        public string? OldFolder { get; }
        public string SettingsFile => Path.Combine(SettingsFolder, "settings.json");

        /// <summary>What 4.4.0 left: port.txt and relay-settings.txt beside the exe, relay-settings.txt in ProgramData.</summary>
        public string? OldPortFile => OldFolder is null ? null : Path.Combine(OldFolder, "port.txt");
        public string? OldSettingsFile => OldFolder is null ? null : Path.Combine(OldFolder, "relay-settings.txt");
        public string OldDataSettingsFile => Path.Combine(SettingsFolder, "relay-settings.txt");

        private readonly object _write = new();

        /// <param name="settingsFolder">Where settings.json is (ProgramData\KovaRelay).</param>
        /// <param name="oldFolder">Where 4.4.0 kept port.txt (the exe's folder), to read until it is moved; null for none.</param>
        public RelayFiles(string settingsFolder, string? oldFolder)
        {
            SettingsFolder = settingsFolder;
            OldFolder = oldFolder;
        }

        /// <summary>The older three-folder form (the first was where hosts.txt lived), still used by RelayFirewall.cs. The first is ignored.</summary>
        public RelayFiles(string unusedHostsFolder, string settingsFolder, string? oldFolder) : this(settingsFolder, oldFolder) { }

        /// <summary>Whether 4.4.0's settings files are still there to be moved into settings.json.</summary>
        public bool HasOldSettings =>
            (OldPortFile is string p && File.Exists(p)) || (OldSettingsFile is string s && File.Exists(s)) || File.Exists(OldDataSettingsFile);

        /// <summary>
        /// The settings: settings.json if an administrator has saved it, otherwise 4.4.0's port.txt, otherwise port
        /// 47200. Never writes anything.
        /// </summary>
        public RelaySettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(SettingsFile));
                    var root = doc.RootElement;
                    int port = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Port", out var p) && p.TryGetInt32(out int n) && n >= 1 && n <= 65535 ? n : DefaultPort;
                    return new RelaySettings(port);
                }
            }
            catch (Exception) { }
            int oldPort = DefaultPort;
            try
            {
                if (OldPortFile is string pf && File.Exists(pf)
                    && int.TryParse(File.ReadAllText(pf).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p >= 1 && p <= 65535)
                    oldPort = p;
            }
            catch (Exception) { }
            return new RelaySettings(oldPort);
        }

        /// <summary>Writes settings.json (the elevated copy, and the self-test). False if it could not be written.</summary>
        public bool SaveSettings(RelaySettings settings)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("Port", settings.Port);
                w.WriteEndObject();
            }
            return WriteAll(SettingsFile, Encoding.UTF8.GetString(ms.ToArray()) + "\r\n");
        }

        /// <summary>Temp file then replace, so a crash or power cut never leaves half a file. False if it could not be written.</summary>
        private bool WriteAll(string path, string text)
        {
            lock (_write)
            {
                string tmp = path + ".tmp";
                try
                {
                    File.WriteAllText(tmp, text, new UTF8Encoding(false));
                    File.Move(tmp, path, overwrite: true);
                    return true;
                }
                catch (Exception)
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    return false;
                }
            }
        }
    }


    /// <summary>
    /// relay-log.txt in the relay's own folder: ProgramData\KovaRelay for the service, the user's LocalAppData\KovaRelay
    /// for the tray (or LocalAppData\KovaRelay anyway, if that folder cannot be written). Lines are
    /// queued and written by one background thread, so the network code never waits on the disk. The queue is
    /// capped (a flood can only cost lines, never memory), and the log is capped at 1 MB, the old one kept as
    /// relay-log.old.txt. Never write session content, keys, tokens or challenges here.
    /// </summary>
    internal sealed class RelayLog : IDisposable
    {
        private const long MaxBytes = 1 << 20;
        private const int MaxQueued = 2000;

        public string Path { get; private set; }
        private readonly Queue<string> _queue = new();
        private readonly object _gate = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _writer;
        private int _dropped;
        private volatile bool _stopping;

        public RelayLog(string folder)
        {
            Path = System.IO.Path.Combine(folder, "relay-log.txt");
            if (!CanWrite(Path))
            {
                string alt = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KovaRelay");
                try { Directory.CreateDirectory(alt); } catch (Exception) { }
                Path = System.IO.Path.Combine(alt, "relay-log.txt");
            }
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Relay log" };
            _writer.Start();
        }

        private static bool CanWrite(string path)
        {
            try { using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } return true; }
            catch (Exception) { return false; }
        }

        public void Write(string line)
        {
            string stamped = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line;
            lock (_gate)
            {
                if (_queue.Count >= MaxQueued) { _dropped++; return; }
                _queue.Enqueue(stamped);
            }
            _wake.Set();
        }

        /// <summary>Writes everything queued now (before opening the log, and on exit).</summary>
        public void Flush()
        {
            lock (_gate) WriteQueued();
        }

        private void WriteLoop()
        {
            while (!_stopping)
            {
                _wake.WaitOne(1000);
                try { lock (_gate) WriteQueued(); } catch (Exception) { }
            }
        }

        // Called holding _gate.
        private void WriteQueued()
        {
            if (_queue.Count == 0 && _dropped == 0) return;
            var sb = new StringBuilder();
            while (_queue.Count > 0) sb.Append(_queue.Dequeue()).Append("\r\n");
            if (_dropped > 0)
            {
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                  .Append("  ").Append(_dropped).Append(" further log lines were not written because too many arrived at once.\r\n");
                _dropped = 0;
            }
            try
            {
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(Path, System.IO.Path.Combine(info.DirectoryName!, "relay-log.old.txt"), overwrite: true);
                using var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                fs.Write(bytes, 0, bytes.Length);
            }
            catch (Exception) { }   // a log that cannot be written must never stop the relay
        }

        public void Dispose()
        {
            _stopping = true;
            _wake.Set();
            _writer.Join(2000);
            Flush();
        }
    }
}
