using System;
using System.IO;
using System.Threading;

namespace MonMP.Plugin.Diagnostics
{
    // Diagnostic output only. Keep at most one waiting report while a slow disk write finishes.
    // Game objects and report construction stay on the calling thread.
    internal sealed class LatestReportWriter
    {
        private readonly Action<string, string> write;
        private readonly object gate = new object();
        private readonly ManualResetEventSlim idle = new ManualResetEventSlim(true);
        private (string Path, string Text)? pending;
        private bool active;
        private long completed, replaced, failures, lastWriteTicks;

        internal LatestReportWriter(Action<string, string> write) { this.write = write; }
        internal long Completed => Interlocked.Read(ref completed);
        internal long Replaced => Interlocked.Read(ref replaced);
        internal long Failures => Interlocked.Read(ref failures);
        internal double LastWriteMs => Interlocked.Read(ref lastWriteTicks) * 1000.0 / global::System.Diagnostics.Stopwatch.Frequency;

        internal void Queue(string path, string text)
        {
            lock (gate)
            {
                if (pending.HasValue) Interlocked.Increment(ref replaced);
                pending = (path, text);
                if (active) return;
                active = true;
                idle.Reset();
                ThreadPool.QueueUserWorkItem(_ => Drain());
            }
        }

        private void Drain()
        {
            while (true)
            {
                (string Path, string Text) report;
                lock (gate)
                {
                    if (!pending.HasValue) { active = false; idle.Set(); return; }
                    report = pending.Value;
                    pending = null;
                }
                long start = global::System.Diagnostics.Stopwatch.GetTimestamp();
                try { write(report.Path, report.Text); Interlocked.Increment(ref completed); }
                // Diagnostic IO must not terminate a game or start an unbounded retry loop.
                catch (Exception) { Interlocked.Increment(ref failures); }
                finally { Interlocked.Exchange(ref lastWriteTicks, global::System.Diagnostics.Stopwatch.GetTimestamp() - start); }
            }
        }

        internal bool WaitForIdle(int milliseconds) => idle.Wait(milliseconds);
    }
}
