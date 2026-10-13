using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// The optional diagnostic log: Enable logging in the main window (or the
    /// service's settings). Writes Kova-log.txt next to the exe (the service:
    /// Kova-service-log.txt), one line per event and one status line a second
    /// from each side, all timestamped to the millisecond. Never what is typed:
    /// key presses are only counted. At 20 MB the file becomes Kova-log.old.txt
    /// and a new one starts. Written once a second, a lot of whole lines at a time.
    /// </summary>
    internal static class DiagLog
    {
        private const long MaxBytes = 20L * 1024 * 1024;
        private static readonly object Gate = new();
        // Lines wait here and go to the file together, once a second (or sooner when there are many):
        // each lot is one write of whole lines, so another Kova appending to the same file can never
        // land in the middle of a line.
        private static FileStream? _file;
        private static readonly StringBuilder Pending = new();
        private static volatile bool _enabled;
        private static Timer? _flush;
        // After a rotation that failed (a viewer holding the log, a read-only .old file), the next try
        // waits for another megabyte or a minute: trying on every line made logging 25 times slower.
        private static long _rotateAfterBytes = MaxBytes;
        private static long _rotateAfterTicks;

        // Next to Kova, unless that folder can't be written (a copy in Program Files run without
        // administrator): then in AppData\Local\Kova, and FilePath says so, rather than claiming to log.
        private static string? _fallback;
        // The service and the window often run the same Kova.exe from the same folder, and with one
        // file name they wrote over each other's lines. The service (started as "Kova.exe --service")
        // keeps its own file.
        private static readonly string FileName = IsService() ? "Kova-service-log.txt" : "Kova-log.txt";
        private static bool IsService()
        {
            try { var a = Environment.GetCommandLineArgs(); return a.Length == 2 && a[1] == "--service"; }
            catch { return false; }
        }
        private static string NextToKova => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, FileName);
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
                        if (_flush == null)
                        {
                            _flush = new Timer(_ => Flush(), null, 1000, 1000);
                            // Up to a second of lines waits for the timer: the last ones before Kova ended were lost.
                            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
                        }
                        WriteLocked("Logging started. Kova " + Updater.Current + ", " +
                            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + " on " +
                            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture + ", " + Environment.OSVersion.VersionString +
                            ", " + Environment.ProcessorCount + " processors.");
                    }
                    else
                    {
                        WriteLocked("Logging stopped.");
                        FlushLocked();
                        _file?.Dispose();
                        _file = null;
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
            if (_file == null) return;
            Pending.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("  ").Append(line).Append(Environment.NewLine);
            if (Pending.Length > 64 * 1024) FlushLocked(); // a burst: not all held until the timer
        }

        private static void FlushLocked()
        {
            if (_file == null || Pending.Length == 0) return;
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(Pending.ToString());
                Pending.Clear();
                _file.Write(bytes); // the stream has no buffer of its own: this is the one write
                long length = _file.Length;
                if (length > _rotateAfterBytes || length > MaxBytes && Environment.TickCount64 >= _rotateAfterTicks) Rotate(length);
            }
            catch { Pending.Clear(); }
        }

        /// <summary>The log has passed 20 MB: it becomes Kova-log.old.txt (replacing the one before) and a new one starts.</summary>
        private static void Rotate(long length)
        {
            _file!.Dispose();
            _file = null; // never left pointing at a closed file, even if what follows fails
            bool moved = false;
            try
            {
                // Another Kova logging to the same file may have just done it: then this one only follows to the new file.
                var now = new FileInfo(FilePath);
                if (!now.Exists || now.Length <= MaxBytes) moved = true;
                else
                {
                    // One rename that replaces the .old file: deleting it first and then failing to rename
                    // (a viewer holding the log) lost the history with nothing gained.
                    string old = Path.ChangeExtension(FilePath, ".old.txt");
                    ClearReadOnly(old);
                    File.Move(FilePath, old, overwrite: true);
                    moved = true;
                }
            }
            catch { /* held without delete sharing, or the like: carry on in the same file and try later */ }
            if (moved) { _rotateAfterBytes = MaxBytes; _rotateAfterTicks = 0; }
            else { _rotateAfterBytes = length + (1 << 20); _rotateAfterTicks = Environment.TickCount64 + 60_000; }
            Open();
            if (_file == null) _enabled = false; // nothing could be reopened: stop claiming logging is on
        }

        private static void ClearReadOnly(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var a = File.GetAttributes(path);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            }
            catch { }
        }

        /// <summary>
        /// Opened for appending only (no right to write anywhere else), so Windows puts every write at
        /// the end of the file as it is then. With an ordinary append each copy of Kova kept its own idea
        /// of where the end was, and two logging to one file wrote over each other's lines.
        /// </summary>
        private static FileStream OpenAppend(string path) =>
            new FileInfo(path).Create(FileMode.Append,
                System.Security.AccessControl.FileSystemRights.AppendData | System.Security.AccessControl.FileSystemRights.Synchronize,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None, null);

        private static void Open()
        {
            try { _file = OpenAppend(FilePath); }
            catch
            {
                _file = null;
                if (_fallback != null) return;
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kova");
                    Directory.CreateDirectory(dir);
                    _fallback = Path.Combine(dir, FileName);
                    _file = OpenAppend(_fallback);
                }
                catch { _file = null; }
            }
        }

        /// <summary>Writes the lines waiting for the timer now (before a crash note, and as Kova exits).</summary>
        internal static void Flush()
        {
            if (_file == null) return;
            lock (Gate) FlushLocked();
        }
    }
}
