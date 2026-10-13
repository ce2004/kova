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

        public static string Unprotect(string? enc)
        {
            if (string.IsNullOrEmpty(enc)) return "";
            try { return Encoding.UTF8.GetString(Native.Protect(Convert.FromBase64String(enc), false)); }
            catch { return ""; }
        }
        /// <summary>Every port TailRemote has used here, so Port editor can still close it later.</summary>
        public System.Collections.Generic.List<int> KnownPorts { get; set; } = new();
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
            catch (System.Security.Cryptography.CryptographicException) { throw new InvalidDataException("Wrong password for that backup, or the file is damaged."); }
            var s = JsonSerializer.Deserialize<Settings>(plain)?.Tidy() ?? throw new InvalidDataException("The backup is empty.");
            s.Password = s.PasswordEnc;
            s.ListenPassword = s.ListenPasswordEnc;
            foreach (var pc in s.SavedPcs) pc.PasswordEnc = Protect(pc.PasswordEnc);
            return s;
        }

        private static byte[] BackupKey(string password, byte[] salt) =>
            System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 200_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);

        /// <summary>Takes on every setting of another (a restored backup), keeping this PC's port history.</summary>
        public void CopyFrom(Settings other)
        {
            var ports = KnownPorts;
            foreach (var p in typeof(Settings).GetProperties())
                if (p.CanWrite && p.CanRead && !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute))) p.SetValue(this, p.GetValue(other));
            foreach (int port in ports) if (!KnownPorts.Contains(port)) KnownPorts.Add(port);
        }

        /// <summary>
        /// A "null" in the file (edited by hand, or damaged) leaves a list or text null, which the window
        /// does not expect anywhere: each goes back to empty, and Saved PCs with nothing in them go.
        /// </summary>
        private Settings Tidy()
        {
            Address ??= ""; PasswordEnc ??= ""; OutputDevice ??= ""; CaptureDevice ??= ""; ReceiveFolder ??= "";
            ListenPasswordEnc ??= ""; ResumeState ??= "";
            SoundChoices ??= new();
            KnownPorts ??= new();
            SavedPcs ??= new();
            SavedPcs.RemoveAll(pc => pc == null);
            foreach (var pc in SavedPcs) { pc.Address ??= ""; pc.PasswordEnc ??= ""; }
            return this;
        }

        public static Settings Load()
        {
            try { return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings()).Tidy(); }
            catch
            {
                // Damaged (not just missing): kept as settings.json.bad before anything else, so the
                // next Save cannot wipe out every saved PC and password without a trace.
                try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bad", true); } catch { }
                // The main file is missing, empty or corrupt - possibly from a crash or power loss
                // mid-write before Save() below wrote atomically. A temp file left over from such an
                // interrupted save might still be a complete, valid settings file: try it once before
                // giving up and starting fresh.
                try { return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(TempPath)) ?? new Settings()).Tidy(); }
                catch { return new Settings(); }
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                // Written all the way to the disk before the swap: Windows can keep the new text in memory
                // for seconds after the rename itself is on disk, and a power cut in that gap left a
                // settings.json of zeros, so every saved PC and password was gone at the next start.
                using (var fs = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(new UTF8Encoding(false).GetBytes(json));
                    fs.Flush(flushToDisk: true);
                }
                // Atomic: the live settings file is either the old complete one or the new complete
                // one, never a half-written mix, even if this process is killed partway through.
                if (File.Exists(FilePath)) File.Replace(TempPath, FilePath, null);
                else File.Move(TempPath, FilePath);
            }
            catch { }
        }
    }
}
