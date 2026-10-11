using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// The optional diagnostic log: Enable logging in the main window (or the
    /// service's settings). Writes TailRemote-log.txt next to the exe, one line
    /// per event and one status line a second from each side, all timestamped
    /// to the millisecond. Never what is typed: key presses are only counted.
    /// At 20 MB the file becomes TailRemote-log.old.txt and a new one starts.
    /// </summary>
    internal static class DiagLog
    {
        private const long MaxBytes = 20L * 1024 * 1024;
        private static readonly object Gate = new();
        private static StreamWriter? _writer;
        private static volatile bool _enabled;
        private static Timer? _flush;

        // Next to Kova, unless that folder can't be written (a copy in Program Files run without
        // administrator): then in AppData\Local\Kova, and FilePath says so, rather than claiming to log.
        private static string? _fallback;
        private static string NextToKova => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "Kova-log.txt");
        public static string FilePath => _fallback ?? NextToKova;

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                lock (Gate)
                {
                    if (value == _enabled) return;
                    _enabled = value;
                    if (value)
                    {
                        Open();
                        _flush ??= new Timer(_ => Flush(), null, 1000, 1000);
                        WriteLocked("Logging started. Kova " + Updater.Current + ", " +
                            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + " on " +
                            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture + ", " + Environment.OSVersion.VersionString +
                            ", " + Environment.ProcessorCount + " processors.");
                    }
                    else
                    {
                        WriteLocked("Logging stopped.");
                        _writer?.Dispose();
                        _writer = null;
                    }
                }
            }
        }

        public static void Write(string line)
        {
            if (!_enabled) return;
            lock (Gate) WriteLocked(line);
        }

        private static void WriteLocked(string line)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line);
                if (_writer.BaseStream.Length > MaxBytes)
                {
                    _writer.Dispose();
                    _writer = null; // never leave it pointing at a disposed writer, even if rotation below fails
                    try
                    {
                        string old = Path.ChangeExtension(FilePath, ".old.txt");
                        File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    catch { /* rotation failed (file locked, etc.): still try to reopen below, rather than go dark */ }
                    Open();
                    if (_writer == null) _enabled = false; // nothing could be reopened: stop claiming logging is on
                }
            }
            catch { }
        }

        private static void Open()
        {
            try
            {
                var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
            }
            catch
            {
                _writer = null;
                if (_fallback != null) return;
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kova");
                    Directory.CreateDirectory(dir);
                    _fallback = Path.Combine(dir, "Kova-log.txt");
                    var fs = new FileStream(_fallback, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
                }
                catch { _writer = null; }
            }
        }

        private static void Flush()
        {
            lock (Gate) { try { _writer?.Flush(); } catch { } }
        }
    }
}
