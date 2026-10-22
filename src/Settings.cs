using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TailRemote
{
    /// <summary>A PC in the Saved PCs list; its password is DPAPI-encrypted like the others.</summary>
    internal sealed class SavedPc
    {
        public string Address { get; set; } = "";
        public int Port { get; set; } = Protocol.DefaultPort;
        public string PasswordEnc { get; set; } = "";
        /// <summary>Its network cards (Wake.Describe), learnt while connected: how to wake it. Empty until then.</summary>
        public string Wake { get; set; } = "";
        public override string ToString() => Port == Protocol.DefaultPort ? Address : Address + ", port " + Port;
    }

    /// <summary>Saved in %APPDATA%\TailRemote\settings.json; the passwords are DPAPI-encrypted.</summary>
    internal sealed class Settings
    {
        public bool HostMode { get; set; }
        public string Address { get; set; } = "";
        public int Port { get; set; } = Protocol.DefaultPort;
        public string PasswordEnc { get; set; } = "";
        public string OutputDevice { get; set; } = "";
        /// <summary>Host: the output whose sound is sent ("" = Windows' default).</summary>
        public string CaptureDevice { get; set; } = "";
        /// <summary>Controlling: the microphone sent to the remote PC ("" = Windows' default). Whether it is on is never saved: it starts off.</summary>
        public string MicDevice { get; set; } = "";
        /// <summary>Host: where the controlling PC's microphone plays ("" = Kova picks a virtual cable that is not what it sends).</summary>
        public string MicOutput { get; set; } = "";
        /// <summary>Controlling: the remote NVDA as its sound (0), as text spoken and brailled here with no sound (1), or both (2).</summary>
        public int RemoteNvda { get; set; }
        /// <summary>Where Send files and Send a folder from the other PC are saved; empty: Downloads\TailRemote.</summary>
        public string ReceiveFolder { get; set; } = "";
        public string ListenPasswordEnc { get; set; } = "";
        public bool Logging { get; set; }
        /// <summary>Catch up by speeding up the sound instead of skipping (changes the pitch a little).</summary>
        public bool CatchUpBySpeed { get; set; }
        public bool AnnounceQuality { get; set; }
        public bool AnnounceHealth { get; set; } = true;
        public bool MuteWhenNotControlling { get; set; }
        public bool RideOutStalls { get; set; } = true;
        /// <summary>Piano tones for connecting, clipboard, files and the rest (Sounds.cs).</summary>
        public bool Sounds { get; set; } = true;
        /// <summary>Which sound each event makes (event name to sound name); events not here get the piano.</summary>
        public System.Collections.Generic.Dictionary<string, string> SoundChoices { get; set; } = new();
        /// <summary>The major key the sounds play in, by pitch class (C 0 ... B 11). B flat until changed.</summary>
        public int SoundKey { get; set; } = 10;
        /// <summary>Sound quality: -1 follows the connection (Variable); otherwise the bitrate step it is locked to.</summary>
        public int SoundQuality { get; set; } = -1;
        public System.Collections.Generic.List<SavedPc> SavedPcs { get; set; } = new();

        [JsonIgnore]
        public string ListenPassword
        {
            get => Unprotect(ListenPasswordEnc);
            set => ListenPasswordEnc = Protect(value);
        }

        // Null-safe: a "null" in settings.json (edited by hand, or damaged) used to crash the window at start.
        public static string Protect(string? value) =>
            string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(Native.Protect(Encoding.UTF8.GetBytes(value), true));

        public static string Unprotect(string? enc) => TryUnprotect(enc, out string plain) ? plain : "";

        /// <summary>False when there is a password but it cannot be read here (settings copied from another account or PC).</summary>
        private static bool TryUnprotect(string? enc, out string plain)
        {
            plain = "";
            if (string.IsNullOrEmpty(enc)) return true;
            try { plain = Encoding.UTF8.GetString(Native.Protect(Convert.FromBase64String(enc), false)); return true; }
            catch { return false; }
        }

        /// <summary>
        /// How many saved passwords (the main one, the listen one, Saved PCs') are there but cannot be
        /// read on this Windows account: they act as empty, and a backup would carry them empty. Counted
        /// now, so a password typed in again stops counting.
        /// </summary>
        [JsonIgnore]
        public int UnreadablePasswords
        {
            get
            {
                int n = 0;
                if (!TryUnprotect(PasswordEnc, out _)) n++;
                if (!TryUnprotect(ListenPasswordEnc, out _)) n++;
                foreach (var pc in SavedPcs) if (!TryUnprotect(pc.PasswordEnc, out _)) n++;
                return n;
            }
        }

        /// <summary>
        /// True when settings.json was there but could not be read (held by something for longer than a
        /// couple of seconds, or access refused). These settings are then empty stand-ins, and Save does
        /// nothing: writing them would wipe out every saved PC and password in the real file.
        /// </summary>
        [JsonIgnore]
        public bool SavingDisabled { get; private set; }

        /// <summary>Why the last Save did not reach the disk, in plain words; null when it did.</summary>
        [JsonIgnore]
        public string? SaveProblem { get; private set; }
        /// <summary>Every port TailRemote has used here, so Port editor can still close it later.</summary>
        public System.Collections.Generic.List<int> KnownPorts { get; set; } = new();
        /// <summary>The port this PC last hosted on, in this window or through the service it gave settings to (0: not known yet). When it moves, the old one is offered for closing.</summary>
        public int HostedPort { get; set; }
        /// <summary>What was running when TailRemote last closed: "host", "connect" or "". Used by --resume.</summary>
        public string ResumeState { get; set; } = "";

        // KOVA_SETTINGS_DIR: test copies keep their settings apart from the real ones.
        private static string Dir => Environment.GetEnvironmentVariable("KOVA_SETTINGS_DIR") is { Length: > 0 } test ? test : Names.UserDir;
        internal static string FilePath => Path.Combine(Dir, "settings.json");
        // Save() writes here first, then atomically replaces FilePath, so a crash or power loss
        // mid-write can never leave a half-written settings.json as the live file.
        private static string TempPath => FilePath + ".tmp";

        [JsonIgnore]
        public string Password
        {
            get => Unprotect(PasswordEnc);
            set => PasswordEnc = Protect(value);
        }

        // ---- Backups: everything, for another PC ----
        // The passwords here are locked to this Windows account (DPAPI), so a backup carries them
        // as plain text inside, and the whole file is locked with a password of its own instead:
        // "TRBK1", salt (16), nonce (12), tag (16), then the settings, AES-GCM with a slow PBKDF2 key.

        private static readonly byte[] BackupMagic = "TRBK1"u8.ToArray();

        /// <summary>Whether a file looks like a settings backup (long enough, and starts the way one does), so it can be refused before its password is asked.</summary>
        public static bool LooksLikeBackup(byte[] data) =>
            data != null && data.Length >= BackupMagic.Length + 44 && data.AsSpan(0, BackupMagic.Length).SequenceEqual(BackupMagic);

        public byte[] Export(string password)
        {
            var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
            copy.PasswordEnc = Password;
            copy.ListenPasswordEnc = ListenPassword;
            foreach (var pc in copy.SavedPcs) pc.PasswordEnc = Unprotect(pc.PasswordEnc);
            copy.KnownPorts = new();   // this PC's firewall history, not the next one's
            copy.HostedPort = 0;
            copy.ResumeState = "";
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(copy);
            byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            byte[] nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
            byte[] tag = new byte[16], secret = new byte[plain.Length];
            using (var gcm = new System.Security.Cryptography.AesGcm(BackupKey(password, salt), 16)) gcm.Encrypt(nonce, plain, secret, tag);
            using var ms = new MemoryStream();
            ms.Write(BackupMagic);
            ms.Write(salt);
            ms.Write(nonce);
            ms.Write(tag);
            ms.Write(secret);
            return ms.ToArray();
        }

        /// <summary>The settings in a backup, with their passwords locked to this Windows account again. Throws with a plain message.</summary>
        public static Settings Import(byte[] data, string password)
        {
            int head = BackupMagic.Length;
            if (!LooksLikeBackup(data)) throw new InvalidDataException("That is not a Kova settings backup.");
            byte[] salt = data[head..(head + 16)], nonce = data[(head + 16)..(head + 28)], tag = data[(head + 28)..(head + 44)], secret = data[(head + 44)..];
            byte[] plain = new byte[secret.Length];
            try
            {
                using var gcm = new System.Security.Cryptography.AesGcm(BackupKey(password, salt), 16);
                gcm.Decrypt(nonce, secret, tag, plain);
            }
            // Its own kind, so the window can ask for the password again (the two look the same to AES-GCM).
            catch (System.Security.Cryptography.CryptographicException) { throw new WrongBackupPasswordException(); }
            Settings s;
            // It opened, so the password was right: settings it cannot read come from a Kova that kept them differently.
            try { s = JsonSerializer.Deserialize<Settings>(plain)?.Tidy() ?? throw new InvalidDataException("The backup is empty."); }
            catch (JsonException) { throw new InvalidDataException("That backup was made by a different Kova version and can't be read here."); }
            s.Password = s.PasswordEnc;
            s.ListenPassword = s.ListenPasswordEnc;
            foreach (var pc in s.SavedPcs) pc.PasswordEnc = Protect(pc.PasswordEnc);
            // As Export leaves them out: another PC's firewall history and what it was doing as it closed.
            s.KnownPorts = new();
            s.HostedPort = 0;
            s.ResumeState = "";
            return s;
        }

        private static byte[] BackupKey(string password, byte[] salt) =>
            System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 200_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);

        /// <summary>Takes on every setting of another (a restored backup), keeping this PC's port history.</summary>
        public void CopyFrom(Settings other)
        {
            var ports = KnownPorts;
            int hosted = HostedPort;
            foreach (var p in typeof(Settings).GetProperties())
                if (p.CanWrite && p.CanRead && !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute))) p.SetValue(this, p.GetValue(other));
            foreach (int port in ports) if (!KnownPorts.Contains(port)) KnownPorts.Add(port);
            HostedPort = hosted;
        }

        /// <summary>
        /// The real settings, read at last after they could not be at the start (SavingDisabled): they come in,
        /// but whatever was changed here meanwhile (anything no longer as a fresh start has it) is kept over
        /// them, and Saved PCs, sound choices and ports added meanwhile are added to theirs. Saving works again.
        /// </summary>
        public void RecoverFrom(Settings real)
        {
            var fresh = new Settings();
            foreach (var p in typeof(Settings).GetProperties())
            {
                if (!p.CanWrite || !p.CanRead || Attribute.IsDefined(p, typeof(JsonIgnoreAttribute))) continue;
                object? mine = p.GetValue(this);
                if (mine is System.Collections.ICollection) continue; // the lists: below
                if (Equals(mine, p.GetValue(fresh))) p.SetValue(this, p.GetValue(real));
            }
            static bool Same(SavedPc a, SavedPc b) => string.Equals(a.Address, b.Address, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
            var pcs = new System.Collections.Generic.List<SavedPc>();
            foreach (var pc in real.SavedPcs) pcs.Add(SavedPcs.Find(m => Same(m, pc)) ?? pc); // saved again here meanwhile: that one
            foreach (var pc in SavedPcs) if (!pcs.Contains(pc)) pcs.Add(pc);
            SavedPcs = pcs;
            var choices = new System.Collections.Generic.Dictionary<string, string>(real.SoundChoices);
            foreach (var (k, v) in SoundChoices) choices[k] = v;
            SoundChoices = choices;
            foreach (int port in real.KnownPorts) if (!KnownPorts.Contains(port)) KnownPorts.Add(port);
            SavingDisabled = false;
            SaveProblem = null;
        }

        /// <summary>
        /// A "null" in the file (edited by hand, or damaged) leaves a list or text null, which the window
        /// does not expect anywhere: each goes back to empty, and Saved PCs with nothing in them go.
        /// </summary>
        private Settings Tidy()
        {
            Address ??= ""; PasswordEnc ??= ""; OutputDevice ??= ""; CaptureDevice ??= ""; ReceiveFolder ??= "";
            ListenPasswordEnc ??= ""; ResumeState ??= ""; MicDevice ??= ""; MicOutput ??= "";
            RemoteNvda = Math.Clamp(RemoteNvda, 0, 2);
            SoundChoices ??= new();
            KnownPorts ??= new();
            SavedPcs ??= new();
            SavedPcs.RemoveAll(pc => pc == null);
            foreach (var pc in SavedPcs) { pc.Address ??= ""; pc.PasswordEnc ??= ""; }
            return this;
        }

        private static Settings Parse(string text) => (JsonSerializer.Deserialize<Settings>(text) ?? new Settings()).Tidy();

        /// <summary>The temp file a save left behind, if it is complete and newer than settings.json; else null.</summary>
        private static Settings? NewerTemp()
        {
            try
            {
                if (!File.Exists(TempPath)) return null;
                if (File.Exists(FilePath) && File.GetLastWriteTimeUtc(TempPath) <= File.GetLastWriteTimeUtc(FilePath)) return null;
                return Parse(File.ReadAllText(TempPath));
            }
            catch { return null; } // half written, or another Kova is writing it right now
        }

        /// <summary>Reads settings.json. With note false (the window trying again later) a file still unreadable is not written down again.</summary>
        public static Settings Load(bool note = true)
        {
            // Another Kova saving at this moment holds settings.json for a few milliseconds (File.Replace
            // can even make it briefly not there). That used to count as damage: the settings came back
            // empty, and the next Save wrote that over every saved PC and password. So a file that can't
            // be opened is tried again for about two seconds; only text that isn't settings is damage.
            string? text = null;
            Exception? unreadable = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
                    // Missing can be another Kova's swap in progress: looked at a few more times (longer while
                    // a save's temp file is there) before it counts as the very first start.
                    if (text == null && (attempt < 3 || File.Exists(TempPath) && clock.ElapsedMilliseconds < 500)) { System.Threading.Thread.Sleep(20); continue; }
                    unreadable = null;
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    unreadable = e;
                    if (clock.ElapsedMilliseconds > 2000) break;
                    System.Threading.Thread.Sleep(Math.Min(200, 10 << Math.Min(attempt, 4)));
                }
            }

            // A save that could not swap its temp file in (settings.json read-only, or held) left the
            // newer settings there: they are the ones to use.
            if (NewerTemp() is Settings fresher) return fresher;

            if (unreadable != null)
            {
                // There, but not readable: empty stand-ins that are never saved over the real file.
                if (note) Program.CrashNote("settings.json could not be read, so changes are not saved this time: " + unreadable.Message);
                return new Settings { SavingDisabled = true, SaveProblem = "Kova could not read its settings file (" + unreadable.Message + "), so nothing is saved until it can. It tries again every half minute." };
            }
            if (text == null) return new Settings(); // the very first start

            try { return Parse(text); }
            catch (Exception e)
            {
                if (e is not JsonException)
                {
                    // Not damage that is understood: never risk saving empty settings over it.
                    if (note) Program.CrashNote("settings.json could not be loaded: " + e);
                    return new Settings { SavingDisabled = true, SaveProblem = "Kova could not load its settings file, so nothing is saved until it can. It tries again every half minute." };
                }
                // Damaged (not just missing): kept as settings.json.bad before anything else, so the
                // next Save cannot wipe out every saved PC and password without a trace.
                KeepDamaged(text);
                // A temp file left over from an interrupted save might still be complete: try it before
                // starting fresh, even if it is older.
                try { return Parse(File.ReadAllText(TempPath)); }
                catch { return new Settings(); }
            }
        }

        /// <summary>
        /// A damaged settings.json, kept: the first as settings.json.bad, later ones as settings.json.bad-date-time,
        /// never over an earlier copy (that one may be the only trace of the saved PCs). Only the newest few are kept.
        /// </summary>
        private static void KeepDamaged(string text)
        {
            try
            {
                string first = FilePath + ".bad";
                var copies = new System.Collections.Generic.List<string>(Directory.GetFiles(Dir, "settings.json.bad-*"));
                if (File.Exists(first)) copies.Add(first);
                foreach (string c in copies) { try { if (File.ReadAllText(c) == text) return; } catch { } } // the same damage seen again
                string to = !File.Exists(first) ? first : FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                File.WriteAllText(to, text, new UTF8Encoding(false));
                // The first one stays; of the dated ones only the newest four.
                var dated = new System.Collections.Generic.List<string>(Directory.GetFiles(Dir, "settings.json.bad-*"));
                dated.Sort(StringComparer.Ordinal);
                for (int i = 0; i < dated.Count - 4; i++) { try { File.SetAttributes(dated[i], FileAttributes.Normal); File.Delete(dated[i]); } catch { } }
            }
            catch { }
        }

        /// <summary>
        /// Writes the settings to disk. False, with SaveProblem saying why, if they could not be: a
        /// window can say so, rather than lose every change from then on in silence. Retries: how many more
        /// tries, 150 ms apart; the window's own thread uses few, so a folder that refuses does not freeze it.
        /// </summary>
        public bool Save(int retries = 5)
        {
            if (SavingDisabled) return false;
            Exception? last = null;
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                if (attempt > 0) System.Threading.Thread.Sleep(150); // another Kova saving, or a virus scan: these clear by themselves
                try
                {
                    Directory.CreateDirectory(Dir);
                    string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                    ClearReadOnly(TempPath);
                    // Written all the way to the disk before the swap: Windows can keep the new text in memory
                    // for seconds after the rename itself is on disk, and a power cut in that gap left a
                    // settings.json of zeros, so every saved PC and password was gone at the next start.
                    using (var fs = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        fs.Write(new UTF8Encoding(false).GetBytes(json));
                        fs.Flush(flushToDisk: true);
                    }
                    // A read-only settings.json (copied from a CD or a zip, or set by hand) refused every
                    // save, in silence, for good.
                    ClearReadOnly(FilePath);
                    // Atomic: the live settings file is either the old complete one or the new complete
                    // one, never a half-written mix, even if this process is killed partway through.
                    if (File.Exists(FilePath)) File.Replace(TempPath, FilePath, null);
                    else File.Move(TempPath, FilePath);
                    SaveProblem = null;
                    return true;
                }
                catch (Exception e) { last = e; }
            }
            // A complete temp file may be left: Load takes it when it is newer, so these changes are not lost.
            SaveProblem = "Kova could not save its settings (" + last?.Message + ").";
            return false;
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
    }

    /// <summary>A backup's password was wrong, or the file is damaged (AES-GCM can't tell the two apart): worth asking again.</summary>
    internal sealed class WrongBackupPasswordException : System.Security.Cryptography.CryptographicException
    {
        public WrongBackupPasswordException() : base("Wrong password for that backup, or the file is damaged.") { }
    }
}
