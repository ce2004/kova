using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace KovaRelay
{
    /// <summary>A host name the relay has pinned to one public key.</summary>
    internal sealed class Pin
    {
        public required string Name;      // as the host first registered it (case kept for showing)
        public required string KeyHash;   // SHA-256 of the SubjectPublicKeyInfo, lower-case hex
        public DateTime FirstSeen;        // UTC
        public DateTime LastSeen;         // UTC
    }

    /// <summary>The relay's settings: the port it listens on, and whether new host names may register.</summary>
    internal sealed record RelaySettings(int Port, bool AllowNewNames)
    {
        public static RelaySettings Default => new(RelayFiles.DefaultPort, true);
    }

    /// <summary>
    /// The relay's files (4.4.1: none of them beside the exe any more).
    ///
    /// settings.json (port, new host names) is in ProgramData\KovaRelay, the same file for the tray and the service.
    /// Only administrators may write it (RelayService.SecureDataDir), so the tray changes it through an elevated copy
    /// of itself, and a user without administrator rights can never change the port the service listens on. Until an
    /// administrator first saves it, the relay uses port 47200.
    ///
    /// hosts.txt is in a folder the relay itself writes: ProgramData\KovaRelay for the service, the user's
    /// LocalAppData\KovaRelay for a relay running in the tray. Every write goes to a temporary file first and then
    /// replaces the real one, so a crash or power cut never leaves half a file. If the folder cannot be written,
    /// the host list is kept in memory and <see cref="Problem"/> says so.
    ///
    /// 4.4.0 kept port.txt, relay-settings.txt and hosts.txt next to the exe: they are read until moved
    /// (RelayService --settings-migrate), and then deleted.
    /// </summary>
    internal sealed class RelayFiles
    {
        public const int DefaultPort = 47200;

        public string HostsFolder { get; }
        public string SettingsFolder { get; }
        public string? OldFolder { get; }
        public string HostsFile => Path.Combine(HostsFolder, "hosts.txt");
        public string SettingsFile => Path.Combine(SettingsFolder, "settings.json");

        /// <summary>What 4.4.0 left: port.txt and relay-settings.txt beside the exe, relay-settings.txt in ProgramData.</summary>
        public string? OldPortFile => OldFolder is null ? null : Path.Combine(OldFolder, "port.txt");
        public string? OldSettingsFile => OldFolder is null ? null : Path.Combine(OldFolder, "relay-settings.txt");
        public string OldDataSettingsFile => Path.Combine(SettingsFolder, "relay-settings.txt");

        /// <summary>Plain words for the status box when the host list cannot be saved; null when all is well.</summary>
        public volatile string? Problem;

        private readonly object _write = new();

        /// <param name="hostsFolder">Where hosts.txt is kept.</param>
        /// <param name="settingsFolder">Where settings.json is (ProgramData\KovaRelay).</param>
        /// <param name="oldFolder">Where 4.4.0 kept port.txt (the exe's folder), to read until it is moved; null for none.</param>
        public RelayFiles(string hostsFolder, string settingsFolder, string? oldFolder)
        {
            HostsFolder = hostsFolder;
            SettingsFolder = settingsFolder;
            OldFolder = oldFolder;
        }

        /// <summary>Whether 4.4.0's settings files are still there to be moved into settings.json.</summary>
        public bool HasOldSettings =>
            (OldPortFile is string p && File.Exists(p)) || (OldSettingsFile is string s && File.Exists(s)) || File.Exists(OldDataSettingsFile);

        /// <summary>
        /// The settings: settings.json if an administrator has saved it, otherwise what 4.4.0 left (port.txt,
        /// relay-settings.txt), otherwise port 47200 with new names allowed. Never writes anything.
        /// </summary>
        public RelaySettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(SettingsFile));
                    var root = doc.RootElement;
                    int port = root.TryGetProperty("Port", out var p) && p.TryGetInt32(out int n) && n >= 1 && n <= 65535 ? n : DefaultPort;
                    bool allow = !root.TryGetProperty("AllowNewHostNames", out var a) || a.ValueKind != JsonValueKind.False;
                    return new RelaySettings(port, allow);
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
            bool oldAllow = OldAllow(OldDataSettingsFile) ?? (OldSettingsFile is string sf ? OldAllow(sf) : null) ?? true;
            return new RelaySettings(oldPort, oldAllow);
        }

        private static bool? OldAllow(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                foreach (string raw in File.ReadAllLines(file))
                {
                    int eq = raw.IndexOf('=');
                    if (eq < 0) continue;
                    string key = raw[..eq].Trim().ToLowerInvariant(), value = raw[(eq + 1)..].Trim().ToLowerInvariant();
                    if (key == "allow-new-host-names") return value is not ("no" or "false" or "0" or "off");
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>Writes settings.json (the elevated copy, and the self-test). False if it could not be written.</summary>
        public bool SaveSettings(RelaySettings settings)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("Port", settings.Port);
                w.WriteBoolean("AllowNewHostNames", settings.AllowNewNames);
                w.WriteEndObject();
            }
            return WriteAll(SettingsFile, Encoding.UTF8.GetString(ms.ToArray()) + "\r\n", setProblem: false);
        }

        /// <summary>hosts.txt: one line per name: name,key hash,first seen,last seen (UTC, ISO 8601). Bad lines are skipped.</summary>
        public Dictionary<string, Pin> LoadPins(int max)
        {
            var pins = new Dictionary<string, Pin>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(HostsFile)) return pins;
                foreach (string raw in File.ReadAllLines(HostsFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    string[] f = line.Split(',');
                    if (f.Length < 4 || !KovaRelayWire.Wire.NameOk(f[0].Trim())) continue;
                    string hash = f[1].Trim().ToLowerInvariant();
                    if (hash.Length != 64 || !IsHex(hash)) continue;
                    DateTime first = ParseTime(f[2]), last = ParseTime(f[3]);
                    string name = f[0].Trim();
                    if (pins.Count >= max) break;
                    pins[KovaRelayWire.Wire.NameKey(name)] = new Pin { Name = name, KeyHash = hash, FirstSeen = first, LastSeen = last };
                }
            }
            catch (Exception) { }
            return pins;
        }

        public bool SavePins(IEnumerable<Pin> pins)
        {
            var sb = new StringBuilder("# Kova Relay host names: name, SHA-256 of the host's public key, first seen, last seen (UTC)\r\n");
            foreach (Pin p in pins)
                sb.Append(p.Name).Append(',').Append(p.KeyHash).Append(',')
                  .Append(p.FirstSeen.ToString("o", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.LastSeen.ToString("o", CultureInfo.InvariantCulture)).Append("\r\n");
            return WriteAll(HostsFile, sb.ToString());
        }

        private static DateTime ParseTime(string s) =>
            DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)
                ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : DateTime.UtcNow;

        private static bool IsHex(string s)
        {
            foreach (char c in s) if (!char.IsAsciiHexDigit(c)) return false;
            return true;
        }

        /// <summary>Temp file then replace. Returns false (and sets Problem) if the folder cannot be written.</summary>
        private bool WriteAll(string path, string text, bool setProblem = true)
        {
            lock (_write)
            {
                string tmp = path + ".tmp";
                try
                {
                    File.WriteAllText(tmp, text, new UTF8Encoding(false));
                    File.Move(tmp, path, overwrite: true);
                    if (setProblem) Problem = null;
                    return true;
                }
                catch (Exception)
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    if (setProblem) Problem = "The relay cannot save its host list, so new host names last only until it closes.";
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
                  .Append("  ").Append(_dropped).Append(" more lines were left out because too much was happening at once.\r\n");
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
