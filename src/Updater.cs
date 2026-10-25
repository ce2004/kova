using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace TailRemote
{
    /// <summary>
    /// Updates from the latest GitHub release: downloads this architecture's
    /// exe, checks its SHA-256 against the one GitHub publishes, swaps it in
    /// place (a running exe can be renamed, not overwritten) and restarts.
    /// </summary>
    internal static class Updater
    {
        private const string Repo = "ce2004/kova";

        public sealed record Release(Version Version, string Notes, string Url, string? Sha256);

        public static Version Current
        {
            get
            {
                var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        private static string AssetName =>
            "Kova-" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64") + ".exe";

        // No overall time limit on the client: a 33 MB download over a slow line can take far longer
        // than any fixed limit (five minutes used to mean a slow line could never update). Asking
        // GitHub what is newest has its own limit (ApiLimit), and the download is given up only
        // when nothing at all arrives for StallLimit.
        private static HttpClient Http()
        {
            var h = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Kova/" + Current);
            return h;
        }

        private static readonly TimeSpan ApiLimit = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);
        private const string WebPageInstead = "The internet connection answered with a web page instead of GitHub (a Wi-Fi sign-in page?).";

        /// <summary>
        /// A release tag as a three-part version: "v4.0.0", "V4.0.0", "4.0.0-beta", "4.0.0+build",
        /// "4.0" and "4.0.0.1" all work (the fourth part is dropped, a missing third is 0). Null when
        /// it is not a version at all. Version.Parse alone failed on everything but "v4.0.0".
        /// </summary>
        public static Version? NormalizeTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string t = tag.Trim().TrimStart('v', 'V');
            int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) t = t[..cut];
            if (Version.TryParse(t, out var v)) return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return int.TryParse(t, out int major) && major >= 0 ? new Version(major, 0, 0) : null;
        }

        /// <summary>What GitHub's newest release says: its version, notes, and this PC's download (null if it has none).</summary>
        internal sealed record Parsed(Version Version, string Notes, string? Url, string? Sha256, string? ManifestUrl = null, string? SignatureUrl = null);

        // ---- Signed releases (4.4) ----
        // GitHub's own fingerprint of a download comes from the same place as the download: it catches damage, not a
        // stolen GitHub account or a tampered release, and an update ends up running as the service (SYSTEM). So every
        // release also carries kova-release.json (each file's SHA-256 and the version) and its signature, made by the
        // release build with a private key only it holds. Kova checks the signature against these public keys, built
        // in, before it will install anything: the first is the one in use, the second a spare kept offline in case the
        // first ever has to be replaced.
        internal const string ManifestAsset = "kova-release.json", SignatureAsset = "kova-release.json.sig";
        private static readonly string[] SigningKeys =
        {
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEH9gfZpzO3z0w4ji4pqEX1H9pJ/Bm8raMQU+8XV6znnZuoDe8uc4qdlFB5D2gsqiJRYZm+MRoiwr0GwScojQdUg==",
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEHzZm8nQwnNUp1KMHQpInlorB+ZvobhtcE36LVKFyV7Erl2L0fUg33mmMnsh28cRx68DciHcTuvWAebH4L5u0OA==",
        };

        /// <summary>
        /// The SHA-256 the signed list gives this file, if the list is signed by one of Kova's keys and is for this version;
        /// else null. testKey: tests only (with KOVA_UPDATE_FEED), another public key to accept.
        /// </summary>
        internal static string? SignedHash(byte[] manifest, byte[] signature, Version version, string asset, string? testKey = null)
        {
            bool signed = false;
            foreach (string k in testKey != null ? new[] { testKey } : SigningKeys)
            {
                try
                {
                    using var ec = ECDsa.Create();
                    ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(k), out _);
                    if (ec.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) { signed = true; break; }
                }
                catch (Exception e) when (e is CryptographicException or FormatException) { }
            }
            if (!signed) return null;
            try
            {
                using var doc = JsonDocument.Parse(manifest);
                var root = doc.RootElement;
                if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String || NormalizeTag(v.GetString()) != version) return null;
                if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object) return null;
                if (!files.TryGetProperty(asset, out var h) || h.ValueKind != JsonValueKind.String) return null;
                string hash = h.GetString()!;
                return hash.Length == 64 && hash.All(Uri.IsHexDigit) ? hash : null;
            }
            catch (JsonException) { return null; }
        }

        /// <summary>A small file of the release (the list or its signature): at most 64 KB, within the usual time limit.</summary>
        private static async Task<byte[]> SmallAsync(string url)
        {
            if (TestFeed != null && Path.IsPathFullyQualified(url)) return await File.ReadAllBytesAsync(url);
            using var h = Http();
            using var limit = new System.Threading.CancellationTokenSource(ApiLimit);
            try
            {
                using var resp = await h.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                resp.EnsureSuccessStatusCode();
                await using var s = await resp.Content.ReadAsStreamAsync(limit.Token);
                var ms = new MemoryStream();
                byte[] buf = new byte[8192];
                int n;
                while ((n = await s.ReadAsync(buf, limit.Token)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > 64 << 10) throw new InvalidOperationException("The release's list of files is far too big, so nothing was installed.");
                }
                return ms.ToArray();
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested) { throw new InvalidOperationException("GitHub did not answer. Check the internet connection and try again."); }
            catch (HttpRequestException e) { throw new InvalidOperationException("Could not reach GitHub: " + e.Message, e); }
        }

        /// <summary>
        /// Reads GitHub's answer about the newest release. Anything missing or odd is a plain sentence,
        /// never a raw exception: a Wi-Fi sign-in page answering instead, an empty answer, a release
        /// with no files yet, or notes that are null.
        /// </summary>
        internal static Parsed ParseRelease(string text, string asset)
        {
            if (LooksLikeWebPage(text)) throw new InvalidOperationException(WebPageInstead);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); }
            catch (JsonException) { throw new InvalidOperationException(WebPageInstead); }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("tag_name", out var tagEl) || tagEl.ValueKind != JsonValueKind.String ||
                    NormalizeTag(tagEl.GetString()) is not Version version)
                    throw new InvalidOperationException("GitHub's answer about the newest Kova could not be understood. Try again later.");
                string notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
                string? url = null, sha = null, manifestUrl = null, signatureUrl = null;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                        if (!a.TryGetProperty("browser_download_url", out var u) || u.ValueKind != JsonValueKind.String) continue;
                        string name = n.GetString()!;
                        if (name == ManifestAsset) manifestUrl = u.GetString();
                        else if (name == SignatureAsset) signatureUrl = u.GetString();
                        else if (name == asset && url == null)
                        {
                            url = u.GetString();
                            sha = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is string ds && ds.StartsWith("sha256:") ? ds[7..] : null;
                        }
                    }
                }
                return new Parsed(version, notes, url, sha, manifestUrl, signatureUrl);
            }
        }

        private static bool LooksLikeWebPage(string text)
        {
            string t = text.TrimStart();
            return t.StartsWith("<", StringComparison.Ordinal);
        }

        /// <summary>GitHub's newest release as text, with every way that can fail said in plain words.</summary>
        /// <summary>
        /// Tests only: a file shaped like GitHub's answer about the newest release, whose download is a file on this
        /// PC (still checked against its SHA-256). Lets a test update a copy end to end without a real release.
        /// </summary>
        // A test switch: never honoured by a copy running as administrator or SYSTEM (Names.TestSwitch), which would
        // otherwise install whatever file a program without those rights pointed it at.
        private static readonly string? TestFeed = Names.TestSwitch("KOVA_UPDATE_FEED") is { Length: > 0 } f ? f : null;

        private static async Task<string> ReleaseJsonAsync()
        {
            if (TestFeed != null) return await File.ReadAllTextAsync(TestFeed);
            using var h = Http();
            using var limit = new System.Threading.CancellationTokenSource(ApiLimit);
            try
            {
                using var resp = await h.GetAsync("https://api.github.com/repos/" + Repo + "/releases/latest", limit.Token);
                string text = await resp.Content.ReadAsStringAsync(limit.Token);
                // A sign-in page can answer with any status, so it is recognised first.
                if (resp.Content.Headers.ContentType?.MediaType == "text/html" || LooksLikeWebPage(text)) throw new InvalidOperationException(WebPageInstead);
                // GitHub allows 60 questions an hour from one internet address; past that it answers 403 or 429.
                if ((int)resp.StatusCode is 403 or 429) throw new InvalidOperationException("GitHub is limiting update checks right now. Try again in an hour.");
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException("GitHub answered " + (int)resp.StatusCode + " " + resp.ReasonPhrase + " when asked for the newest Kova. Try again later.");
                return text;
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                throw new InvalidOperationException("GitHub did not answer. Check the internet connection and try again.");
            }
            catch (HttpRequestException e)
            {
                throw new InvalidOperationException("Could not reach GitHub: " + e.Message, e);
            }
        }

        /// <summary>The newest version on GitHub, whatever this copy is.</summary>
        public static async Task<Version> LatestAsync() => ParseRelease(await ReleaseJsonAsync(), AssetName).Version;

        /// <summary>The latest release, or null when this copy is already up to date.</summary>
        public static async Task<Release?> CheckAsync()
        {
            var p = ParseRelease(await ReleaseJsonAsync(), AssetName);
            if (p.Version <= Current) return null;
            if (p.Url == null) throw new InvalidOperationException("Version " + p.Version + " has no " + AssetName + " yet. Try again later.");
            // Signed, or not installed at all (4.4): see SigningKeys.
            if (p.ManifestUrl == null || p.SignatureUrl == null)
                throw new InvalidOperationException("Version " + p.Version + " on GitHub is not signed by Kova's makers, so it was not installed. If it has only just come out, try again in a few minutes.");
            byte[] manifest = await SmallAsync(p.ManifestUrl);
            byte[] signature;
            try { signature = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(await SmallAsync(p.SignatureUrl)).Trim()); }
            catch (FormatException) { signature = Array.Empty<byte>(); }
            string? testKey = TestFeed != null ? Names.TestSwitch("KOVA_UPDATE_TEST_KEY") : null;
            string sha = SignedHash(manifest, signature, p.Version, AssetName, testKey)
                ?? throw new InvalidOperationException("Version " + p.Version + " on GitHub does not carry a valid signature from Kova's makers, so it was not installed. This should never happen: tell Kova's makers.");
            // GitHub's own fingerprint, when it gives one, must agree with the signed one.
            if (p.Sha256 != null && !p.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The file for version " + p.Version + " on GitHub is not the one Kova's makers signed, so it was not installed. This should never happen: tell Kova's makers.");
            return new Release(p.Version, p.Notes, p.Url, sha);
        }

        /// <summary>
        /// Downloads, verifies and swaps in the new exe, then starts it with the arguments
        /// beforeStart returns. beforeStart runs once the new copy is in place, just before it
        /// starts: hosting and the connection carry on through the whole download, and are let
        /// go only then.
        /// </summary>
        public static async Task InstallAsync(Release r, Func<string> beforeStart)
        {
            string exe = Names.OwnExe;
            string fresh = exe + ".new", old = exe + ".old";
            // No checksum from GitHub means nothing to check against: refuse before downloading
            // 33 MB for nothing, rather than after.
            if (r.Sha256 == null) throw new InvalidOperationException("GitHub gave no checksum for the download, so it was not installed. Try again later.");
            try
            {
                await DownloadAsync(r.Url, fresh, r.Sha256);
                // The running copy is swapped for the new one (Windows lets a running program be
                // renamed or replaced, not deleted). If an older leftover cannot be deleted (a
                // TailRemote still running from it, or a virus scan holding it), it is left alone
                // and this copy gets a new name: that used to stop the update with "Access to the
                // path ... .old is denied".
                if (!TryDelete(old)) old = exe + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                // File.Replace (Win32 ReplaceFile) does the swap as one filesystem operation instead
                // of two separate File.Move calls, so a kill between steps can no longer leave no exe
                // at all behind. It deletes/overwrites the backup path itself if one already exists,
                // so it does not need "old" to be absent first.
                // A read-only exe (copied from a CD or a zip that kept the flag) refuses to be replaced.
                ClearReadOnly(exe);
                // A virus scan often opens the brand new file for a moment just as it is written:
                // that is a sharing error that clears by itself, so it is tried a few more times.
                for (int attempt = 1; ; attempt++)
                {
                    // Half done (the old copy already moved aside, the new one not in yet): finish it by hand.
                    if (!File.Exists(exe) && File.Exists(fresh)) { File.Move(fresh, exe); break; }
                    try { File.Replace(fresh, exe, old, ignoreMetadataErrors: true); break; }
                    catch (IOException) when (attempt < 5) { await Task.Delay(500); }
                }
            }
            catch (Exception e)
            {
                // Leave nothing half-done behind: the running exe stays where it was.
                try { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); } catch { }
                try { ClearReadOnly(fresh); File.Delete(fresh); } catch { }
                if (e is UnauthorizedAccessException)
                {
                    string dir = Path.GetDirectoryName(exe)!;
                    // In Program Files, a copy not running as administrator cannot change its own folder,
                    // and "Access to the path ... is denied" says nothing about what to do. But only then:
                    // when the folder can be written, something else holds the file, and running as
                    // administrator would not help.
                    if (!CanWriteIn(dir))
                        throw new InvalidOperationException("Nothing was changed. Kova is in " + dir +
                            ", and this copy could not change it (" + e.Message + "). If Kova is in Program Files, open it with Run as administrator, then update again.", e);
                    throw new InvalidOperationException("Nothing was changed. Kova could not swap in the new copy (" + e.Message +
                        "). Another program, such as a virus scan, may be holding it. Try again in a minute.", e);
                }
                throw;
            }
            string args = beforeStart();
            try
            {
                Process.Start(new ProcessStartInfo(exe, args + " --after-update " + Environment.ProcessId) { UseShellExecute = false });
            }
            catch (Exception e)
            {
                // The swap already succeeded: the exe on disk is the new, valid version. Rolling it
                // back would only undo a working update for no benefit, so it is left as is; the
                // caller just needs a clear, user-facing explanation instead of a raw Process.Start
                // failure or a silent crash.
                throw new InvalidOperationException("Updated to version " + r.Version +
                    ", but it could not restart automatically (" + e.Message + "). Please start Kova yourself.", e);
            }
        }

        /// <summary>
        /// Streams the download into the .new file, hashing as it goes, and checks the hash at the end.
        /// Given up only when nothing arrives for a whole minute: a slow line keeps going as long as
        /// it keeps moving.
        /// </summary>
        internal static async Task DownloadAsync(string url, string fresh, string sha256) // the service's own updates use it too
        {
            if (TestFeed != null && Path.IsPathFullyQualified(url))
            {
                using (var src = File.OpenRead(url))
                using (var dst = new FileStream(fresh, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await src.CopyToAsync(dst);
                    dst.Flush(flushToDisk: true);
                }
                using (var check = File.OpenRead(fresh))
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(check)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The download was damaged. Nothing was changed.");
                return;
            }
            using var h = Http();
            using var stall = new System.Threading.CancellationTokenSource(StallLimit);
            try
            {
                using var resp = await h.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                if (resp.Content.Headers.ContentType?.MediaType == "text/html") throw new InvalidOperationException(WebPageInstead + " Nothing was changed.");
                resp.EnsureSuccessStatusCode();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                ClearReadOnly(fresh); // a read-only leftover from an earlier try cannot be written over
                await using (var src = await resp.Content.ReadAsStreamAsync(stall.Token))
                await using (var dst = new FileStream(fresh, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    byte[] buf = new byte[1 << 16];
                    int n;
                    stall.CancelAfter(StallLimit);
                    while ((n = await src.ReadAsync(buf, stall.Token)) > 0)
                    {
                        stall.CancelAfter(StallLimit); // something arrived: the minute starts again
                        hash.AppendData(buf, 0, n);
                        await dst.WriteAsync(buf.AsMemory(0, n), stall.Token);
                    }
                    // On the disk before it is renamed into place: after a power cut, a rename that landed
                    // before the contents left an exe of zeros (for the service, one that cannot roll back).
                    dst.Flush(flushToDisk: true);
                }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The download was damaged. Nothing was changed.");
            }
            catch (OperationCanceledException) when (stall.IsCancellationRequested)
            {
                throw new IOException("The download stopped: nothing arrived for a minute. Nothing was changed. Try again when the connection is better.");
            }
            catch (HttpRequestException e)
            {
                throw new IOException("The download failed: " + e.Message + " Nothing was changed.", e);
            }
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

        /// <summary>Can this copy make a file in that folder? (Program Files without administrator cannot.)</summary>
        private static bool CanWriteIn(string dir)
        {
            try
            {
                using (new FileStream(Path.Combine(dir, "Kova-write-test-" + Guid.NewGuid().ToString("N") + ".tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// At startup after an update: wait for the old copy to exit, then delete every leftover it can.
        /// stillRunning is called first if the old copy has not gone yet, so NVDA says something
        /// during the wait instead of a silence of up to 15 seconds.
        /// </summary>
        public static void FinishUpdate(int oldPid, Action? stillRunning = null)
        {
            try
            {
                using var p = Process.GetProcessById(oldPid);
                if (!p.WaitForExit(300))
                {
                    stillRunning?.Invoke();
                    p.WaitForExit(15000);
                }
            }
            catch { }
            // In the background: when the service runs the same file, its running copy is one of the
            // leftovers and cannot go until it restarts, and five seconds of retries here held up
            // everything, NVDA's first words included.
            new System.Threading.Thread(() =>
            {
                for (int i = 0; i < 20 && !CleanLeftovers(); i++) System.Threading.Thread.Sleep(250);
            }) { IsBackground = true, Name = "Kova leftovers" }.Start();
        }

        /// <summary>Deletes TailRemote's own leftovers from earlier updates (name.old, name.old-...). True if none are left.</summary>
        public static bool CleanLeftovers()
        {
            string exe = Names.OwnExe;
            // A service update not yet proven: its old copy is what roll back would restore.
            if (File.Exists(exe + ".rollback")) return true;
            bool all = true;
            try
            {
                foreach (string f in Directory.GetFiles(Path.GetDirectoryName(exe)!, Path.GetFileName(exe) + ".old*"))
                    all &= TryDelete(f);
            }
            catch { }
            return all;
        }

        /// <summary>
        /// The single-file exe unpacks its native libraries (NVDA's speech among them) to a folder of
        /// its own per build, %TEMP%\.net\Kova\(a code), and .NET never removes it: every update and
        /// every build left another 8 MB there for good. Those of other builds go, but only when nothing
        /// uses them (a running Kova window has NVDA's library from its folder loaded), and one unpacked
        /// in the last ten minutes is left alone (a copy just starting may not have loaded it yet).
        /// </summary>
        public static void CleanUnpackedCopies()
        {
            try
            {
                // This copy's own folder is in its native library search list; a build that is not a
                // single file has none there, and then nothing is touched.
                if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is not string search) return;
                string? mine = null;
                foreach (string d in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    string full = Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar);
                    string? parent = Path.GetDirectoryName(full);
                    if (parent != null && string.Equals(Path.GetFileName(Path.GetDirectoryName(parent)), ".net", StringComparison.OrdinalIgnoreCase)) { mine = full; break; }
                }
                if (mine == null) return;
                string app = Path.GetDirectoryName(mine)!; // ...\.net\Kova: only this exe name's builds
                foreach (string dir in Directory.GetDirectories(app))
                {
                    if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), mine, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if (DateTime.UtcNow - info.CreationTimeUtc < TimeSpan.FromMinutes(10) || DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromMinutes(10)) continue;
                        // NVDA's library first: every Kova window loads it as it starts, and Windows refuses to
                        // delete a loaded library, so a folder in use stops here with nothing else gone.
                        foreach (string lib in Directory.GetFiles(dir, "nvdaControllerClient*.dll")) { File.SetAttributes(lib, FileAttributes.Normal); File.Delete(lib); }
                        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                        Directory.Delete(dir, true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return true;
                File.SetAttributes(path, FileAttributes.Normal); // a read-only leftover too
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }
    }
}
