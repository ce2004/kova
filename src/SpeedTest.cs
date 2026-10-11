using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;

using System.Threading;
using System.Threading.Tasks;

namespace TailRemote
{
    /// <summary>
    /// An internet speed test against Cloudflare's speed test servers (speed.cloudflare.com),
    /// the same ones its own speed test page uses: ping and jitter, then 8 seconds of download
    /// and 8 of upload over several connections at once. About 20 seconds in all. The answer is
    /// one "Name: value" line each, for ListViewerForm.
    /// </summary>
    internal static class SpeedTest
    {
        private const string Server = "https://speed.cloudflare.com";
        private static int _running;
        private const long MaxBytes = 750L << 20;

        public static async Task<string> RunAsync()
        {
            if (Interlocked.Exchange(ref _running, 1) == 1) return "Problem: a speed test is already running on this PC.";
            try
            {
                using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 16, AutomaticDecompression = DecompressionMethods.None })
                    { Timeout = Timeout.InfiniteTimeSpan };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Kova/" + Updater.Current);
                var lines = new List<string>();

                // Which Cloudflare data centre answers (an airport code, like FRA), and this PC's
                // internet address, from Cloudflare's trace page: key=value lines.
                string where = "", address = "";
                try
                {
                    var trace = (await http.GetStringAsync(Server + "/cdn-cgi/trace").WaitAsync(TimeSpan.FromSeconds(10)))
                        .Split('\n').Select(l => l.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim());
                    if (trace.TryGetValue("colo", out var colo)) where = colo + (trace.TryGetValue("loc", out var loc) ? ", " + loc : "");
                    if (trace.TryGetValue("ip", out var ip)) address = ip;
                }
                catch { }

                var (ping, jitter) = await Ping(http);
                long used = 0;
                double down = await Measure(6, (token, count) => Download(http, token, count), b => used += b);
                double up = await Measure(4, (token, count) => Upload(http, token, count), b => used += b);

                lines.Add("Download: " + Mbps(down));
                lines.Add("Upload: " + Mbps(up));
                lines.Add("Ping: " + ping + " ms, jitter " + jitter + " ms");
                if (where.Length > 0) lines.Add("Server: Cloudflare's " + where + " data centre");
                if (address.Length > 0) lines.Add("Internet address: " + address);
                lines.Add("Data used: " + FileChannel.Size(used));
                lines.Add("Tested on: " + Environment.MachineName + ", " + DateTime.Now.ToString("t"));
                return string.Join("\n", lines);
            }
            catch (Exception e) { return "Problem: the speed test could not finish: " + e.Message; }
            finally { Volatile.Write(ref _running, 0); }
        }

        private static string Mbps(double bitsPerSecond) =>
            bitsPerSecond >= 1e9 ? (bitsPerSecond / 1e9).ToString("0.00") + " gigabits per second"
            : (bitsPerSecond / 1e6).ToString(bitsPerSecond < 1e7 ? "0.0" : "0") + " megabits per second";

        /// <summary>The middle of 12 tiny requests on a warm connection, and how much they wandered.</summary>
        private static async Task<(int Ping, int Jitter)> Ping(HttpClient http)
        {
            var times = new List<double>();
            Exception? last = null;
            bool warm = false;
            for (int i = 0; i < 13; i++)
            {
                // One lost request on a busy line used to end the whole test: now it is skipped, and
                // the test fails only if fewer than 5 answered.
                try
                {
                    var clock = Stopwatch.StartNew();
                    using var resp = await http.GetAsync(Server + "/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(warm ? 5 : 10));
                    if (warm) times.Add(clock.Elapsed.TotalMilliseconds); // the first answer also opened the connection
                    warm = true;
                }
                catch (Exception e) { last = e; }
            }
            if (times.Count < 5) throw new IOException("Cloudflare's speed test server did not answer" + (last != null ? ": " + last.Message : "."));
            var sorted = times.OrderBy(t => t).ToList();
            double jitter = times.Zip(times.Skip(1), (a, b) => Math.Abs(a - b)).Average();
            return ((int)Math.Round(sorted[sorted.Count / 2]), (int)Math.Round(jitter));
        }

        /// <summary>
        /// Runs 'streams' transfers at once for 8 seconds; the speed is what moved after the first
        /// second (connections take a moment to reach full speed). It stops early at 750 MB, so a
        /// very fast line does not use gigabytes.
        /// </summary>
        private static async Task<double> Measure(int streams, Func<CancellationToken, Action<long>, Task> one, Action<long> total)
        {
            long moved = 0, atOneSecond = -1, baselineMs = -1;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var clock = Stopwatch.StartNew();
            void Count(long n)
            {
                long now = Interlocked.Add(ref moved, n);
                if (now >= MaxBytes) stop.Cancel();
                // The first count after one second is the starting point, and when it really happened is
                // kept too: on a slow line it can be well after one second, and dividing by "all but one
                // second" then counted the bytes over too long and said the line was slower than it is.
                if (Interlocked.Read(ref atOneSecond) < 0 && clock.ElapsedMilliseconds >= 1000 &&
                    Interlocked.CompareExchange(ref atOneSecond, now, -1) == -1)
                    Interlocked.Exchange(ref baselineMs, clock.ElapsedMilliseconds);
            }
            string? problem = null;
            var tasks = Enumerable.Range(0, streams).Select(_ => Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try { await one(stop.Token, Count); }
                    catch when (stop.IsCancellationRequested) { }
                    catch (Exception e) { problem = e.Message; return; } // refused or cut off: this stream stops
                }
            })).ToArray();
            try { await Task.WhenAll(tasks); } catch { }
            double end = clock.Elapsed.TotalSeconds;
            long all = Interlocked.Read(ref moved);
            // Nothing moved is not a speed of zero: the test could not run (blocked, refused, offline).
            if (all < 1 << 20 && problem != null) throw new IOException("Cloudflare's speed test server could not be used: " + problem);
            total(all);
            long baseline = Interlocked.Read(ref atOneSecond), startMs = Interlocked.Read(ref baselineMs);
            // Nothing counted after the first second (it all came at once, or stopped early): the whole time it took.
            if (baseline < 0 || startMs < 0) return all * 8 / Math.Max(0.5, end);
            return (all - baseline) * 8 / Math.Max(0.5, end - startMs / 1000.0);
        }

        private static async Task Download(HttpClient http, CancellationToken token, Action<long> count)
        {
            using var resp = await http.GetAsync(Server + "/__down?bytes=50000000", HttpCompletionOption.ResponseHeadersRead, token);
            resp.EnsureSuccessStatusCode();
            // A sign-in page (hotel, café Wi-Fi) answers in its place: that is not the test.
            if (resp.Content.Headers.ContentType?.MediaType == "text/html") throw new IOException("a web page answered instead (a Wi-Fi sign-in page?)");
            using var s = await resp.Content.ReadAsStreamAsync(token);
            byte[] buf = new byte[64 << 10];
            int n;
            while ((n = await s.ReadAsync(buf, token)) > 0) count(n);
        }

        private static async Task Upload(HttpClient http, CancellationToken token, Action<long> count)
        {
            using var content = new CountingContent(20_000_000, count);
            using var resp = await http.PostAsync(Server + "/__up", content, token);
            resp.EnsureSuccessStatusCode();
        }

        /// <summary>Random bytes for the upload, counted as they go out rather than when the whole post is done.</summary>
        private sealed class CountingContent : HttpContent
        {
            private readonly long _size;
            private readonly Action<long> _count;

            public CountingContent(long size, Action<long> count)
            {
                _size = size;
                _count = count;
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
            {
                byte[] chunk = new byte[64 << 10];
                Random.Shared.NextBytes(chunk);
                for (long sent = 0; sent < _size; sent += chunk.Length)
                {
                    int n = (int)Math.Min(chunk.Length, _size - sent);
                    await stream.WriteAsync(chunk.AsMemory(0, n), token);
                    _count(n);
                }
            }

            protected override bool TryComputeLength(out long length) { length = _size; return true; }
        }
    }
}
