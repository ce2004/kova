using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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

    /// <summary>
    /// The relay's files, all next to the exe: port.txt, relay-settings.txt, hosts.txt. Every write goes to a
    /// temporary file first and then replaces the real one, so a crash or power cut never leaves half a file.
    /// If the folder cannot be written, everything is kept in memory and <see cref="Problem"/> says so.
    /// </summary>
    internal sealed class RelayFiles
    {
        public const int DefaultPort = 47200;

        public string Folder { get; }
        private readonly string _portFolder;
        public string PortFile => Path.Combine(_portFolder, "port.txt");
        public string SettingsFile => Path.Combine(Folder, "relay-settings.txt");
        public string HostsFile => Path.Combine(Folder, "hosts.txt");

        /// <summary>Plain words for the status box when the folder cannot be written; null when all is well.</summary>
        public volatile string? Problem;

        private readonly object _write = new();

        /// <param name="portFolder">Where port.txt is, if not in <paramref name="folder"/> (the service: port.txt beside
        /// the exe, everything else in ProgramData).</param>
        public RelayFiles(string folder, string? portFolder = null) { Folder = folder; _portFolder = portFolder ?? folder; }

        /// <summary>
        /// Reads port.txt. If it is missing, empty or not a port, writes the default there and sets
        /// <paramref name="made"/> so the tray can say so once.
        /// </summary>
        public int ReadPort(out bool made)
        {
            made = false;
            try
            {
                if (File.Exists(PortFile))
                {
                    string text = File.ReadAllText(PortFile).Trim();
                    if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p >= 1 && p <= 65535)
                        return p;
                }
            }
            catch (Exception) { }
            made = WritePort(DefaultPort);
            return DefaultPort;
        }

        /// <summary>The port in port.txt, or the default; never writes anything.</summary>
        public int ReadPortQuietly()
        {
            try
            {
                if (int.TryParse(File.ReadAllText(PortFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p >= 1 && p <= 65535) return p;
            }
            catch (Exception) { }
            return DefaultPort;
        }

        public bool WritePort(int port) => WriteAll(PortFile, port.ToString(CultureInfo.InvariantCulture) + "\r\n");

        public bool LoadAllowNewNames()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return true;
                foreach (string raw in File.ReadAllLines(SettingsFile))
                {
                    int eq = raw.IndexOf('=');
                    if (eq < 0) continue;
                    string key = raw[..eq].Trim().ToLowerInvariant(), value = raw[(eq + 1)..].Trim().ToLowerInvariant();
                    if (key == "allow-new-host-names") return value is not ("no" or "false" or "0" or "off");
                }
            }
            catch (Exception) { }
            return true;
        }

        public bool SaveAllowNewNames(bool allow) =>
            WriteAll(SettingsFile, "allow-new-host-names=" + (allow ? "yes" : "no") + "\r\n");

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
        private bool WriteAll(string path, string text)
        {
            lock (_write)
            {
                string tmp = path + ".tmp";
                try
                {
                    File.WriteAllText(tmp, text, new UTF8Encoding(false));
                    File.Move(tmp, path, overwrite: true);
                    Problem = null;
                    return true;
                }
                catch (Exception)
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    Problem = "The relay cannot save files in its folder, so changes to settings and host names last only until it closes.";
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// relay-log.txt next to the exe (or %LOCALAPPDATA%\KovaRelay if the folder cannot be written). Lines are
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
