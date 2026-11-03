using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace TailRemote
{
    /// <summary>What an NVDA key asks Kova to do. The numbers are the wire format: never change one, only add.</summary>
    internal enum NvdaKeyCommand : byte
    {
        ConnectSaved = 1,  // Control 1 to 9: the saved PC's number is the argument
        Control = 2,       // Control Shift Enter: start controlling the PC in front
        SendClipboard = 3, // Control B
        GetClipboard = 4,  // Control Shift B
        Disconnect = 5,    // File, Disconnect
        Status = 6,        // which PC is in front and how it is going, as the window title says
        ListSaved = 7,     // the saved PCs and their numbers
        Show = 8,          // the Kova window, to the front
    }

    /// <summary>One NVDA key press: the command and its number (the saved PC for ConnectSaved, else 0).</summary>
    internal readonly record struct NvdaKey(NvdaKeyCommand Command, int Number = 0)
    {
        public const int BodyBytes = 2;

        /// <summary>The body (command, number) if it is a command Kova knows with a number it allows; null for anything else.</summary>
        public static NvdaKey? Parse(ReadOnlySpan<byte> body)
        {
            if (body.Length != BodyBytes) return null;
            byte c = body[0], n = body[1];
            return c switch
            {
                (byte)NvdaKeyCommand.ConnectSaved when n is >= 1 and <= 9 => new NvdaKey(NvdaKeyCommand.ConnectSaved, n),
                >= (byte)NvdaKeyCommand.Control and <= (byte)NvdaKeyCommand.Show when n == 0 => new NvdaKey((NvdaKeyCommand)c),
                _ => null,
            };
        }

        /// <summary>What the add-on writes: u32 length (always 2), then command and number.</summary>
        public byte[] Frame() => new byte[] { BodyBytes, 0, 0, 0, (byte)Command, (byte)Number };
    }

    internal static partial class NvdaHelper
    {
        /// <summary>The NVDA keys pipe: like the speech pipe, one per Windows session, so each person's NVDA finds their own Kova.</summary>
        public const string CommandPipePrefix = "KovaCommand-";
        public static string CommandPipeName(uint session) => CommandPipePrefix + session + Names.TestPipes;

        /// <summary>
        /// Kova's end of the NVDA keys pipe, served by every Kova window (controlling or hosting; never the service, which
        /// has no window to act in). Same rules as the speech pipe (PipeServer), and a trusted NVDA running as SYSTEM (the
        /// sign-in screen's copy) is refused too: nobody signed in should be able to drive Kova.
        ///
        /// One command per connection: u32 length, always 2, then the command byte and its number (NvdaKey). The add-on then
        /// waits for Kova to have read it and closes. A command runs only once the connection has ended cleanly; anything
        /// else (another length, an unknown command or number, a byte more, or too slow: over 2 seconds) is dropped and runs
        /// nothing. At most 5 commands a second are taken, so a stuck key or a runaway program cannot flood the window.
        /// Nothing is ever sent back: Kova says what happened itself.
        /// </summary>
        internal sealed class CommandServer : PipeServer
        {
            private const int MaxConnections = 4;
            internal const int MaxWaitMs = 2000;
            internal const int PerSecond = 5;

            private int _busy;
            private readonly Queue<long> _recent = new();

            /// <summary>Raised on a pipe thread for each command that came in whole; MainForm runs it on the window's thread.</summary>
            public event Action<NvdaKey>? Command;
            /// <summary>Tests: commands taken, and commands left out because they came too fast.</summary>
            public int Taken, TooFast;

            /// <summary>pipeName and trustClient are for tests only (a fixed name, and who counts as NVDA).</summary>
            public CommandServer(Action<string>? log = null, string? pipeName = null, Func<int, bool>? trustClient = null)
                : base("Kova NVDA keys pipe", "NVDA keys: ", CommandPipeName, MaxConnections + 2, 256, log, pipeName,
                    trustClient ?? (pid => IsTrustedNvdaProcess(pid) && !RunsAsSystem(pid)))
                => Start();

            protected override void Talk(NamedPipeServerStream conn)
            {
                if (Interlocked.Increment(ref _busy) > MaxConnections)
                {
                    Interlocked.Decrement(ref _busy);
                    Bad("too many connections at once");
                    return;
                }
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Stopping);
                    deadline.CancelAfter(MaxWaitMs);
                    var head = new byte[4];
                    var body = new byte[NvdaKey.BodyBytes];
                    try
                    {
                        if (!ReadWithin(conn, head, deadline.Token)) { Bad("nothing"); return; }
                        int len = BitConverter.ToInt32(head, 0);
                        if (len != NvdaKey.BodyBytes) { Bad("a frame of " + len + " bytes"); return; }
                        if (!ReadWithin(conn, body, deadline.Token)) { Bad("half a command"); return; }
                        // The end of the connection, and nothing after the command: only then is it taken.
                        if (conn.ReadAsync(new byte[1], deadline.Token).AsTask().GetAwaiter().GetResult() != 0) { Bad("more than one command"); return; }
                    }
                    catch (OperationCanceledException) when (!Stopping.IsCancellationRequested) { Bad("too little, too slowly"); return; }
                    if (NvdaKey.Parse(body) is not NvdaKey key) { Bad("a command Kova does not know (" + body[0] + ", " + body[1] + ")"); return; }
                    if (!Allowed()) { Interlocked.Increment(ref TooFast); LogOnce("too fast", "left out commands that came more than " + PerSecond + " a second"); return; }
                    Interlocked.Increment(ref Taken);
                    try { Command?.Invoke(key); } catch (Exception e) { LogOnce("handler", "a command handler failed: " + e.Message); }
                }
                finally { Interlocked.Decrement(ref _busy); }
            }

            private bool Allowed()
            {
                long now = Environment.TickCount64;
                lock (_recent)
                {
                    while (_recent.Count > 0 && now - _recent.Peek() >= 1000) _recent.Dequeue();
                    if (_recent.Count >= PerSecond) return false;
                    _recent.Enqueue(now);
                    return true;
                }
            }

            private static bool ReadWithin(Stream s, byte[] buf, CancellationToken ct)
            {
                int got = 0;
                while (got < buf.Length)
                {
                    int r = s.ReadAsync(buf.AsMemory(got), ct).AsTask().GetAwaiter().GetResult();
                    if (r <= 0) return false;
                    got += r;
                }
                return true;
            }
        }
    }
}
